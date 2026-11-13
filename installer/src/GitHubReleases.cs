// GitHub Releases for the Release Manager: the REST API, and the two-repository upload and publish (README.md, 3. GitHub
// Releases). The main repository's release carries only the installer; the data repository's release of the same tag
// carries package.vault, the data parts and a copy of the installer.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace ReleaseGate
{
    /// <summary>GitHub's REST API with a fine-grained token read from a file on every call. The token is never logged.</summary>
    sealed class GitHub
    {
        readonly string _repo, _tokenFile;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        /// <summary>GitHub's API; tests point it at a local stand-in.</summary>
        internal static string ApiBase = "https://api.github.com/repos/";

        public GitHub(string repo, string tokenFile) { _repo = repo.Trim().Trim('/'); _tokenFile = tokenFile; }

        HttpWebRequest Request(string method, string url)
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.Method = method;
            r.UserAgent = "BattleTechUCU-ReleaseManager";
            r.Accept = "application/vnd.github+json";
            r.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            r.Headers["Authorization"] = "Bearer " + File.ReadAllText(_tokenFile).Trim();
            return r;
        }

        object Call(string method, string path, object body)
        {
            HttpWebRequest r = Request(method, path.StartsWith("https://") ? path : ApiBase + _repo + path);
            if (body != null)
            {
                byte[] b = Encoding.UTF8.GetBytes(Json.Serialize(body));
                r.ContentType = "application/json";
                r.ContentLength = b.Length;
                using (Stream s = r.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            return Read(r);
        }

        static object Read(HttpWebRequest r)
        {
            try
            {
                using (var resp = (HttpWebResponse)r.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string text = sr.ReadToEnd();
                    return text.Length == 0 ? null : Json.DeserializeObject(text);
                }
            }
            catch (WebException e)
            {
                var resp = e.Response as HttpWebResponse;
                if (resp == null) throw;
                string text;
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) text = sr.ReadToEnd();
                string message = text;
                try
                {
                    var d = Json.DeserializeObject(text) as Dictionary<string, object>;
                    if (d != null && d.ContainsKey("message")) message = d["message"] + (d.ContainsKey("errors") ? " " + Json.Serialize(d["errors"]) : "");
                }
                catch (ArgumentException) { }
                throw new IOException(string.Format("GitHub {0} {1}: {2}", (int)resp.StatusCode, resp.StatusDescription, message));
            }
        }

        public Dictionary<string, object> Repo() { return (Dictionary<string, object>)Call("GET", "", null); }

        /// <summary>Every release, drafts included, newest first as GitHub lists them.</summary>
        public List<Dictionary<string, object>> Releases()
        {
            var all = new List<Dictionary<string, object>>();
            for (int page = 1; page < 50; page++)
            {
                var list = Call("GET", "/releases?per_page=100&page=" + page, null) as object[];
                if (list == null || list.Length == 0) break;
                all.AddRange(list.OfType<Dictionary<string, object>>());
            }
            return all;
        }

        /// <summary>Drafts included, which the tags endpoint doesn't return.</summary>
        public Dictionary<string, object> FindRelease(string tag)
        {
            for (int page = 1; page < 50; page++)
            {
                var list = Call("GET", "/releases?per_page=100&page=" + page, null) as object[];
                if (list == null || list.Length == 0) return null;
                foreach (Dictionary<string, object> r in list)
                    if (string.Equals(r["tag_name"] as string, tag, StringComparison.Ordinal)) return r;
            }
            return null;
        }

        public Dictionary<string, object> CreateRelease(string tag, string name, string body, bool draft, bool prerelease)
        {
            return (Dictionary<string, object>)Call("POST", "/releases", new Dictionary<string, object>
            {
                { "tag_name", tag }, { "name", name }, { "body", body }, { "draft", draft }, { "prerelease", prerelease },
            });
        }

        public void DeleteAsset(object id) { Call("DELETE", "/releases/assets/" + id, null); }

        /// <summary>
        /// True when this repository holds a build attestation for a file with this SHA-256 (hex): its CI built those exact
        /// bytes (ci/installer.yml). Anyone can check the same with gh attestation verify.
        /// </summary>
        public bool HasAttestation(string sha256)
        {
            try
            {
                var d = Call("GET", "/attestations/sha256:" + sha256.ToLowerInvariant(), null) as Dictionary<string, object>;
                object list;
                return d != null && d.TryGetValue("attestations", out list) && list is object[] && ((object[])list).Length > 0;
            }
            catch (IOException e) when (e.Message.StartsWith("GitHub 404")) { return false; }
        }

        /// <summary>Adds a paragraph to a release's notes, keeping what is there.</summary>
        public Dictionary<string, object> AppendToBody(Dictionary<string, object> release, string paragraph)
        {
            string body = ((release["body"] as string) ?? "").TrimEnd();
            return (Dictionary<string, object>)Call("PATCH", "/releases/" + release["id"], new Dictionary<string, object>
            {
                { "body", (body.Length == 0 ? "" : body + "\n\n") + paragraph },
            });
        }

        /// <summary>A repository with no commit yet, where GitHub can't make the tag a release needs.</summary>
        public bool IsEmpty()
        {
            try { Call("GET", "/commits?per_page=1", null); return false; }
            catch (IOException e) when (e.Message.StartsWith("GitHub 409")) { return true; }
        }

        /// <summary>Gives an empty repository its first commit: a README.</summary>
        public void AddReadme(string text)
        {
            Call("PUT", "/contents/README.md", new Dictionary<string, object>
            {
                { "message", "Add README" }, { "content", Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) },
            });
        }

        public Dictionary<string, object> Publish(Dictionary<string, object> release)
        {
            return (Dictionary<string, object>)Call("PATCH", "/releases/" + release["id"], new Dictionary<string, object> { { "draft", false } });
        }

        public void Upload(Dictionary<string, object> release, string file, Action<long> sent, CancellationToken stop)
        {
            string url = Regex.Replace((string)release["upload_url"], "\\{.*\\}$", "") + "?name=" + Uri.EscapeDataString(Path.GetFileName(file));
            HttpWebRequest r = Request("POST", url);
            r.ContentType = "application/octet-stream";
            r.ContentLength = new FileInfo(file).Length;
            r.AllowWriteStreamBuffering = false;
            r.SendChunked = false;
            r.Timeout = Timeout.Infinite;
            r.ReadWriteTimeout = 10 * 60 * 1000;
            using (stop.Register(r.Abort))
            {
                try
                {
                    using (var i = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                    using (Stream o = r.GetRequestStream())
                    {
                        var buf = new byte[1 << 20];
                        int n;
                        while ((n = i.Read(buf, 0, buf.Length)) > 0)
                        {
                            stop.ThrowIfCancellationRequested();
                            o.Write(buf, 0, n);
                            sent(n);
                        }
                    }
                    Read(r);
                }
                catch (WebException) when (stop.IsCancellationRequested) { throw new OperationCanceledException(); }
            }
        }

        public static IEnumerable<Dictionary<string, object>> Assets(Dictionary<string, object> release)
        {
            object a;
            return release.TryGetValue("assets", out a) && a is object[] ? ((object[])a).Cast<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();
        }
    }

    static class GitHubReleases
    {
        // Players should never need the data repository, so its README only sends them to the main one.
        internal const string DataReadme = "# BattleTech UCU data\n\nThere's nothing to download here. Get BattleTech: Unofficial Community Update (UCU) " +
            "from {0}\n\nThe installer fetches the files in this repository by itself.\n";

        /// <summary>
        /// Finds or creates the release with this tag, then uploads the files in order, skipping any already there at the same
        /// size. Runs in a job; sent counts bytes for the progress bar.
        /// </summary>
        internal static Dictionary<string, object> UploadRelease(GitHub gh, string repoName, string tag, string name, string body, bool draft, bool pre,
            List<string> files, long[] sent, Action<string> current, CancellationToken token, ref int uploaded, ref int skipped, string mainName, Action<string> log)
        {
            if (gh.IsEmpty())
            {
                gh.AddReadme(string.Format(DataReadme, "https://github.com/" + mainName));
                log("Added a README to " + repoName + ", which had no commit for the release's tag.");
            }
            var rel = gh.FindRelease(tag);
            if (rel == null)
            {
                rel = gh.CreateRelease(tag, name, body, draft, pre);
                log("Created release " + tag + " in " + repoName + (draft ? " (draft)" : ""));
            }
            else log("Adding to the existing release " + tag + " in " + repoName + (true.Equals(rel["draft"]) ? " (draft)" : " (published)"));
            var have = GitHub.Assets(rel).ToDictionary(a => (string)a["name"], StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                token.ThrowIfCancellationRequested();
                string fn = Path.GetFileName(f);
                long len = new FileInfo(f).Length;
                Dictionary<string, object> a;
                if (have.TryGetValue(fn, out a))
                {
                    if (Convert.ToInt64(a["size"]) == len && (a["state"] as string) == "uploaded" && SameDigest(a, f, len))
                    {
                        Interlocked.Add(ref sent[0], len);
                        skipped++;
                        continue;
                    }
                    // A different or broken earlier upload of this file.
                    gh.DeleteAsset(a["id"]);
                    log("Replacing " + fn + " in " + repoName);
                }
                current(fn);
                gh.Upload(rel, f, n => Interlocked.Add(ref sent[0], n), token);
                uploaded++;
                log(string.Format("  uploaded {0} to {1}  {2}", fn, repoName, Checks.Gib(len)));
            }
            return rel;
        }

        /// <summary>
        /// A rebuilt installer or a repacked vault can keep its size, so size alone would skip it. Files under 256 MiB are
        /// checked against GitHub's sha256 digest when the asset has one; data parts (~2 GiB each) stay on size alone.
        /// </summary>
        static bool SameDigest(Dictionary<string, object> asset, string file, long len)
        {
            object d;
            string digest = asset.TryGetValue("digest", out d) ? d as string : null;
            if (len >= 256L << 20 || digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return true;
            using (var sha = Gate.NewSha())
            using (var s = File.OpenRead(file))
                return string.Equals(digest.Substring(7), BitConverter.ToString(sha.ComputeHash(s)).Replace("-", ""), StringComparison.OrdinalIgnoreCase);
        }

        internal static string PublishBoth(GitHub main, GitHub data, string mainName, string dataName, string tag, Action<string> log)
        {
            var d = data.FindRelease(tag);
            var m = main.FindRelease(tag);
            if (d == null || m == null)
                throw new IOException("There's no release " + tag + " in " + (d == null ? dataName : mainName) + " to publish. Press Upload to GitHub first: it creates " +
                    "both releases and the tag (as drafts while Draft is ticked), then Publish makes them public. Nothing was published.");
            if (!GitHub.Assets(d).Any(a => string.Equals(a["name"] as string, Gate.VaultName, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("The data release has no " + Gate.VaultName + " yet: its upload didn't finish. Upload again; nothing was published.");
            // Data first: the installer reads the data release, and a player who opens the main release should find it ready.
            if (true.Equals(d["draft"])) { d = data.Publish(d); log("Published " + tag + " in " + dataName); }
            if (true.Equals(m["draft"])) { m = main.Publish(m); log("Published " + tag + " in " + mainName); }
            return "Release " + tag + " is public. " + m["html_url"];
        }

        /// <summary>
        /// GitHub's download counts, which its release pages don't show. Per release: downloads of the installer from the main
        /// repository, and reads of package.vault from the data repository (one per online install, update or check, by any
        /// installer). Counts are requests, not people; the unlock server's admin.ps1 steam counts people. Data parts aren't
        /// listed: the installer fetches each in many ranged requests, so their counts mean nothing.
        /// </summary>
        internal static string DownloadCounts(GitHub main, GitHub data, string mainName, string dataName, Action<string> log)
        {
            var vaultReads = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var r in data.Releases())
                vaultReads[(string)r["tag_name"]] = Count(r, Gate.VaultName);
            long installers = 0, reads = 0;
            var tags = new HashSet<string>(StringComparer.Ordinal);
            log(string.Format("Download counts ({0} installer, {1} {2}):", mainName, dataName, Gate.VaultName));
            foreach (var r in main.Releases())
            {
                string tag = (string)r["tag_name"];
                tags.Add(tag);
                long exe = Count(r, Remote.InstallerName), vault;
                vaultReads.TryGetValue(tag, out vault);
                installers += exe;
                reads += vault;
                log(string.Format("  {0,-16} {1,-9} {2,7:N0} installer downloads  {3,7:N0} online installs or checks  {4}", tag,
                    true.Equals(r["draft"]) ? "draft" : true.Equals(r["prerelease"]) ? "pre" : "published", exe, vault, Day(r)));
            }
            foreach (var kv in vaultReads.Where(kv => !tags.Contains(kv.Key)))
            {
                reads += kv.Value;
                log(string.Format("  {0,-16} {1,-9} {2,7}                       {3,7:N0} online installs or checks (data only)", kv.Key, "", "-", kv.Value));
            }
            return string.Format("{0:N0} installer downloads and {1:N0} online installs or checks across all releases. These count requests, not people; " +
                ".\\admin.ps1 steam counts the Steam accounts that unlocked.", installers, reads);
        }

        static long Count(Dictionary<string, object> release, string asset)
        {
            var a = GitHub.Assets(release).FirstOrDefault(x => string.Equals(x["name"] as string, asset, StringComparison.OrdinalIgnoreCase));
            object n;
            return a != null && a.TryGetValue("download_count", out n) && n != null ? Convert.ToInt64(n) : 0;
        }

        static string Day(Dictionary<string, object> release)
        {
            object at;
            string s = release.TryGetValue("published_at", out at) ? at as string : null;
            return s != null && s.Length >= 10 ? "published " + s.Substring(0, 10) : "";
        }
    }
}
