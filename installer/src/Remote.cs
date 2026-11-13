// Remote packages. A public GitHub release in the data repository holds package.vault, the data parts (Volumes.cs) and a
// copy of BattleTechUCUSetup.exe; the main repository's release of the same tag holds only the installer. Players download
// only the installer. It downloads the vault, checks BATTLETECH and the access code against it, works out
// which blobs the install folder still needs, and fetches only those with HTTP range requests into the parts. No GitHub
// token is involved: the package is encrypted, and only a retail install plus an unlock server share opens it. Blobs are named by their content, so an update downloads what changed and nothing else.
// Any JSON in the shape of GitHub's release API works as a source too; the tests serve a release that way.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ReleaseGate
{
    public sealed class RemoteAsset
    {
        public string Name;
        public long Size;
        public string Url;              // browser_download_url
        public string Sha256;           // hex, from GitHub's digest field; null when the release gives none
        internal volatile string Signed; // the storage URL a download was redirected to; it expires, and is fetched again when refused
    }

    public sealed class RemoteBlob
    {
        public RemoteAsset Part;
        public long Local;      // the entry's local header
        public long End;        // where the next entry, or the zip's directory, starts
        public long Length;     // bytes stored in the zip
        public long Size;       // bytes once extracted
        public bool Deflated;
        public uint Crc;
    }

    /// <summary>A release found online: its vault, and where each package file sits in its data parts.</summary>
    public sealed class RemotePackage
    {
        public string Source;
        public string Version;              // the release's tag
        public byte[] Vault;
        public bool NeedsNewerInstaller;    // the vault is in a format this installer can't read
        public RemoteAsset Installer;       // the release's BattleTechUCUSetup.exe, or null
        public byte[] Manifest;             // the release's public manifest (PublicManifest.cs), or null for an older release
        public readonly Dictionary<string, RemoteBlob> Blobs = new Dictionary<string, RemoteBlob>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>An HTTP status that retrying won't change.</summary>
    public sealed class RemoteRefusedException : IOException
    {
        public RemoteRefusedException(string message) : base(message) { }
    }

    public static class Remote
    {
        /// <summary>
        /// Where release data is published: the data repository, whose releases hold package.vault, the data parts and a copy
        /// of the installer. The main repository's releases carry only the installer, for players. The installer goes here
        /// when no package sits beside it, or with --remote.
        /// </summary>
        public const string DefaultSource = "github:InternetDadCo/BattleTech-UCU-data";
        public const string InstallerName = "BattleTechUCUSetup.exe";
        const string Agent = "BattleTechUCU-Setup";
        const long Coalesce = 1 << 20;  // read through a gap this size rather than start another request
        const int Retries = 5;
        static readonly Regex PartName = new Regex("^" + Volumes.PartPrefix + @".*-data-\d+\.zip$", RegexOptions.IgnoreCase);
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        static Remote()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 16);
        }

        /// <summary>
        /// Reads the release, downloads its vault and indexes its data parts. github:owner/repo is the newest published
        /// release that has a vault, pre-releases included; github:owner/repo@tag a given published one; an http(s) URL is
        /// read as JSON in the shape of GitHub's release API. Failures are IOExceptions whose message is meant for the user.
        /// </summary>
        public static RemotePackage Open(string source, CancellationToken stop)
        {
            Dictionary<string, object> release = FindRelease(source);
            object tag;
            var p = new RemotePackage { Source = source, Version = release.TryGetValue("tag_name", out tag) && tag is string ? (string)tag : "(untagged)" };
            object list;
            var parts = new List<RemoteAsset>();
            RemoteAsset vault = null, manifest = null;
            foreach (object o in release.TryGetValue("assets", out list) && list is object[] ? (object[])list : new object[0])
            {
                var d = o as Dictionary<string, object>;
                object name, size, url, digest;
                if (d == null || !d.TryGetValue("name", out name) || !d.TryGetValue("size", out size)) continue;
                if (!d.TryGetValue("browser_download_url", out url)) continue;
                var a = new RemoteAsset { Name = name as string, Size = Convert.ToInt64(size), Url = url as string };
                if (d.TryGetValue("digest", out digest) && digest is string && ((string)digest).StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    a.Sha256 = ((string)digest).Substring(7);
                if (a.Name == null || a.Url == null) continue;
                if (string.Equals(a.Name, Gate.VaultName, StringComparison.OrdinalIgnoreCase)) vault = a;
                else if (string.Equals(a.Name, InstallerName, StringComparison.OrdinalIgnoreCase)) p.Installer = a;
                else if (string.Equals(a.Name, PublicManifest.FileName, StringComparison.OrdinalIgnoreCase)) manifest = a;
                else if (PartName.IsMatch(a.Name)) parts.Add(a);
            }
            if (vault == null) throw new RemoteRefusedException("Release " + p.Version + " has no " + Gate.VaultName + ".");
            p.Vault = Whole(p, vault, stop);
            p.NeedsNewerInstaller = !Gate.IsVault(p.Vault);
            if (p.NeedsNewerInstaller) return p;
            if (manifest != null) p.Manifest = Whole(p, manifest, stop);
            Each(parts, a => Index(p, a, stop), stop);
            return p;
        }

        static Dictionary<string, object> FindRelease(string source)
        {
            Match m = Regex.Match(source ?? "", @"^github:([\w.-]+/[\w.-]+)(?:@(.+))?$", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                Uri u;
                if (!Uri.TryCreate(source, UriKind.Absolute, out u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
                    throw new RemoteRefusedException("Not a release source: " + source + ". Use github:<owner>/<repo>[@<tag>] or a URL.");
                return Release(GetJson(source));
            }
            string api = "https://api.github.com/repos/" + m.Groups[1].Value;
            if (m.Groups[2].Success) return Release(GetJson(api + "/releases/tags/" + Uri.EscapeDataString(m.Groups[2].Value)));
            // Not /releases/latest, which skips pre-releases. Drafts aren't listed to anyone without push access. The vault
            // is uploaded last, so a release still being uploaded is passed over.
            Dictionary<string, object> newest = Newest(GetJson(api + "/releases?per_page=30") as object[]);
            if (newest == null) throw new RemoteRefusedException("No published release was found in " + m.Groups[1].Value + ".");
            return newest;
        }

        /// <summary>The most recently published release in GitHub's release list that isn't a draft and has a vault, or null.</summary>
        internal static Dictionary<string, object> Newest(object[] list)
        {
            Dictionary<string, object> newest = null;
            foreach (object o in list ?? new object[0])
            {
                var r = o as Dictionary<string, object>;
                if (r == null || true.Equals(r.ContainsKey("draft") ? r["draft"] : null) || !HasVault(r)) continue;
                if (newest == null || string.CompareOrdinal(Text(r, "published_at"), Text(newest, "published_at")) > 0) newest = r;
            }
            return newest;
        }

        static string Text(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v is string ? (string)v : "";
        }

        static bool HasVault(Dictionary<string, object> release)
        {
            object list;
            if (!release.TryGetValue("assets", out list) || !(list is object[])) return false;
            return ((object[])list).OfType<Dictionary<string, object>>().Any(a => string.Equals(Text(a, "name"), Gate.VaultName, StringComparison.OrdinalIgnoreCase));
        }

        static Dictionary<string, object> Release(object json)
        {
            var d = json as Dictionary<string, object>;
            if (d == null) throw new RemoteRefusedException("That address didn't return a release.");
            return d;
        }

        static object GetJson(string url)
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = Agent;
            r.Accept = "application/vnd.github+json";
            r.Timeout = 60000;
            r.ReadWriteTimeout = 60000;
            string host = new Uri(url).Host;
            try
            {
                using (var resp = (HttpWebResponse)r.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return Json.DeserializeObject(sr.ReadToEnd());
            }
            catch (ArgumentException) { throw new RemoteRefusedException(host + " didn't return a release."); }
            catch (WebException e)
            {
                var resp = e.Response as HttpWebResponse;
                if (resp == null) throw new IOException("Can't reach " + host + ": " + e.Message, e);
                int code = (int)resp.StatusCode;
                string why = code + " " + resp.StatusDescription;
                resp.Close();
                if (code == 404) throw new RemoteRefusedException("No published release was found at " + host + ".");
                if (code == 403 || code == 429) throw new IOException(host + " refused the request (" + why + "). Try again later.");
                throw new IOException(host + " answered " + why + ".");
            }
        }

        static byte[] Whole(RemotePackage p, RemoteAsset a, CancellationToken stop)
        {
            if (a.Size > int.MaxValue) throw new RemoteRefusedException(a.Name + " is too large to read into memory.");
            byte[] b = Read(p, a, 0, a.Size, stop);
            if (a.Sha256 != null)
                using (var sha = Gate.NewSha())
                    if (!string.Equals(Gate.Hex(sha.ComputeHash(b)), a.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException(a.Name + " failed its check. Try again.");
            return b;
        }

        /// <summary>count bytes from an asset, starting at from; a dropped connection resumes where it stopped.</summary>
        static byte[] Read(RemotePackage p, RemoteAsset a, long from, long count, CancellationToken stop)
        {
            var b = new byte[count];
            int got = 0, failures = 0;
            while (got < count)
            {
                try
                {
                    HttpWebRequest req;
                    using (HttpWebResponse r = Get(p, a, from + got, from + count - 1, stop, out req))
                    using (stop.Register(req.Abort))
                    using (Stream s = r.GetResponseStream())
                    {
                        int n;
                        while (got < count && (n = s.Read(b, got, (int)Math.Min(count - got, 1 << 20))) > 0) got += n;
                    }
                    if (got < count) throw new IOException("The download of " + a.Name + " ended early.");
                }
                catch (Exception e) { Pause(e, ref failures, stop); }
            }
            return b;
        }

        /// <summary>
        /// A response holding bytes from..to of the asset. Redirects are followed here, not by the framework, so an expired
        /// storage URL is swapped for a fresh one.
        /// </summary>
        static HttpWebResponse Get(RemotePackage p, RemoteAsset a, long from, long to, CancellationToken stop, out HttpWebRequest req)
        {
            for (int hop = 0; ; hop++)
            {
                stop.ThrowIfCancellationRequested();
                string signed = a.Signed;
                string url = signed ?? a.Url;
                req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = Agent;
                req.AllowAutoRedirect = false;
                req.Timeout = 60000;
                req.ReadWriteTimeout = 60000;
                req.AddRange(from, to);
                HttpWebResponse resp;
                using (stop.Register(req.Abort))
                {
                    try { resp = (HttpWebResponse)req.GetResponse(); }
                    catch (WebException e) when (e.Response is HttpWebResponse) { resp = (HttpWebResponse)e.Response; }
                }
                int code = (int)resp.StatusCode;
                if (code >= 300 && code < 400)
                {
                    string location = resp.Headers[HttpResponseHeader.Location];
                    resp.Close();
                    if (location == null || hop >= 5) throw new RemoteRefusedException(a.Name + ": too many redirects.");
                    a.Signed = new Uri(new Uri(url), location).AbsoluteUri;
                    continue;
                }
                if (code == 206)
                {
                    string range = resp.Headers[HttpResponseHeader.ContentRange] ?? "";
                    if (range.StartsWith("bytes " + from + "-", StringComparison.Ordinal)) return resp;
                    resp.Close();
                    throw new IOException(a.Name + ": the server sent the wrong part of the file.");
                }
                if (code == 200 && from == 0 && to == a.Size - 1) return resp;
                string why = code + " " + resp.StatusDescription;
                resp.Close();
                // Storage URLs that GitHub redirects to expire: go back to the asset's own URL for a new one.
                if (signed != null && code >= 400 && code < 500 && hop < 5)
                {
                    a.Signed = null;
                    continue;
                }
                if (code == 200) throw new RemoteRefusedException("The download server doesn't support partial downloads.");
                if (code == 408 || code == 429 || code >= 500) throw new IOException(a.Name + ": " + why);
                throw new RemoteRefusedException(a.Name + ": " + why);
            }
        }

        /// <summary>After a failed transfer: waits and returns to retry it, or throws (cancelled, refused, or out of retries).</summary>
        static void Pause(Exception e, ref int failures, CancellationToken stop)
        {
            if (stop.IsCancellationRequested) throw new OperationCanceledException(stop);
            bool transient = (e is IOException && !(e is RemoteRefusedException)) || e is WebException;
            if (!transient || ++failures > Retries) ExceptionDispatchInfo.Capture(e).Throw();
            if (stop.WaitHandle.WaitOne(1000 << failures)) throw new OperationCanceledException(stop);
        }

        /// <summary>Runs work over items four at a time and rethrows the first real failure as itself.</summary>
        static void Each<T>(IEnumerable<T> items, Action<T> work, CancellationToken stop)
        {
            try
            {
                Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stop }, work);
            }
            catch (AggregateException e)
            {
                var all = e.Flatten().InnerExceptions;
                Exception first = all.FirstOrDefault(x => !(x is OperationCanceledException)) ?? all[0];
                ExceptionDispatchInfo.Capture(first).Throw();
            }
        }

        /// <summary>Reads a part's zip directory with two or three small requests, and adds its package files.</summary>
        static void Index(RemotePackage p, RemoteAsset a, CancellationToken stop)
        {
            int tail = (int)Math.Min(a.Size, 22 + 65535);
            byte[] t = Read(p, a, a.Size - tail, tail, stop);
            int eocd = -1;
            for (int i = t.Length - 22; i >= 0; i--)
                if (BitConverter.ToUInt32(t, i) == 0x06054b50u) { eocd = i; break; }
            if (eocd < 0) throw new RemoteRefusedException(a.Name + " isn't a zip.");
            long count = BitConverter.ToUInt16(t, eocd + 10);
            long cdSize = BitConverter.ToUInt32(t, eocd + 12);
            long cdAt = BitConverter.ToUInt32(t, eocd + 16);
            if (count == 0xffff || cdSize == 0xffffffffL || cdAt == 0xffffffffL)
            {
                if (eocd < 20 || BitConverter.ToUInt32(t, eocd - 20) != 0x07064b50u) throw new RemoteRefusedException(a.Name + ": Zip64 locator missing.");
                byte[] z = Read(p, a, BitConverter.ToInt64(t, eocd - 12), 56, stop);
                if (BitConverter.ToUInt32(z, 0) != 0x06064b50u) throw new RemoteRefusedException(a.Name + ": bad Zip64 end record.");
                count = BitConverter.ToInt64(z, 32);
                cdSize = BitConverter.ToInt64(z, 40);
                cdAt = BitConverter.ToInt64(z, 48);
            }
            if (cdSize > int.MaxValue || cdAt + cdSize > a.Size) throw new RemoteRefusedException(a.Name + ": bad zip directory.");
            byte[] cd = Read(p, a, cdAt, cdSize, stop);
            // Every entry, in file order, so each one's end is where the next begins; package files carry a blob.
            var all = new List<Tuple<long, string, RemoteBlob>>();
            int at = 0;
            for (long i = 0; i < count; i++)
            {
                if (at + 46 > cd.Length || BitConverter.ToUInt32(cd, at) != 0x02014b50u) throw new RemoteRefusedException(a.Name + ": bad zip directory.");
                int flags = BitConverter.ToUInt16(cd, at + 8), method = BitConverter.ToUInt16(cd, at + 10);
                uint crc = BitConverter.ToUInt32(cd, at + 16);
                long csize = BitConverter.ToUInt32(cd, at + 20), usize = BitConverter.ToUInt32(cd, at + 24);
                int n = BitConverter.ToUInt16(cd, at + 28), x = BitConverter.ToUInt16(cd, at + 30), c = BitConverter.ToUInt16(cd, at + 32);
                long local = BitConverter.ToUInt32(cd, at + 42);
                if (at + 46 + n + x + c > cd.Length) throw new RemoteRefusedException(a.Name + ": bad zip directory.");
                string name = ((flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.ASCII).GetString(cd, at + 46, n);
                // Zip64 extra field: 64-bit values for whichever of these were 0xffffffff, in this order.
                for (int e = at + 46 + n, end = e + x; e + 4 <= end; )
                {
                    int id = BitConverter.ToUInt16(cd, e), len = BitConverter.ToUInt16(cd, e + 2), v = e + 4;
                    if (id == 1)
                    {
                        if (usize == 0xffffffffL && v + 8 <= e + 4 + len) { usize = BitConverter.ToInt64(cd, v); v += 8; }
                        if (csize == 0xffffffffL && v + 8 <= e + 4 + len) { csize = BitConverter.ToInt64(cd, v); v += 8; }
                        if (local == 0xffffffffL && v + 8 <= e + 4 + len) local = BitConverter.ToInt64(cd, v);
                    }
                    e += 4 + len;
                }
                at += 46 + n + x + c;
                string key = Volumes.PackageName(name);
                bool usable = key != null && key != Gate.VaultName && key != PublicManifest.FileName && (flags & 1) == 0 && (method == 8 || (method == 0 && csize == usize));
                all.Add(Tuple.Create(local, key, usable ? new RemoteBlob { Part = a, Local = local, Length = csize, Size = usize, Deflated = method == 8, Crc = crc } : null));
            }
            all.Sort((l, r) => l.Item1.CompareTo(r.Item1));
            lock (p.Blobs)
                for (int i = 0; i < all.Count; i++)
                {
                    RemoteBlob b = all[i].Item3;
                    if (b == null) continue;
                    b.End = i + 1 < all.Count ? all[i + 1].Item1 : cdAt;
                    if (b.End - b.Local < 30 + b.Length) throw new RemoteRefusedException(a.Name + ": entries overlap.");
                    p.Blobs[all[i].Item2] = b;
                }
        }

        /// <summary>
        /// Fetches these package files into folder at their package names (data/....pak), so the folder reads as a loose
        /// package. A file already there at its full size is kept: each is written under a .part name and renamed only once
        /// its CRC-32 matches, so a stopped download resumes where it was. Nearby entries share one request.
        /// </summary>
        public static void Download(RemotePackage p, IEnumerable<string> blobs, string folder, InstallProgress progress, CancellationToken stop)
        {
            var todo = new List<KeyValuePair<string, RemoteBlob>>();
            foreach (string name in blobs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                RemoteBlob b = p.Blobs[name];
                var f = new FileInfo(Path.Combine(folder, name));
                if (f.Exists && f.Length == b.Size) Interlocked.Add(ref progress.Done, b.Length);
                else todo.Add(new KeyValuePair<string, RemoteBlob>(name, b));
            }
            var runs = new List<List<KeyValuePair<string, RemoteBlob>>>();
            foreach (var part in todo.GroupBy(kv => kv.Value.Part))
            {
                List<KeyValuePair<string, RemoteBlob>> run = null;
                foreach (var kv in part.OrderBy(kv => kv.Value.Local))
                {
                    if (run == null || kv.Value.Local - run[run.Count - 1].Value.End > Coalesce)
                        runs.Add(run = new List<KeyValuePair<string, RemoteBlob>>());
                    run.Add(kv);
                }
            }
            // Longest first, so one long run doesn't go alone at the end.
            Each(runs.OrderByDescending(r => r[r.Count - 1].Value.End - r[0].Value.Local), run => Fetch(p, run, folder, progress, stop), stop);
        }

        static void Fetch(RemotePackage p, List<KeyValuePair<string, RemoteBlob>> run, string folder, InstallProgress progress, CancellationToken stop)
        {
            RemoteAsset a = run[0].Value.Part;
            var buf = new byte[1 << 16];
            int i = 0, failures = 0;
            while (i < run.Count)
            {
                try
                {
                    long at = run[i].Value.Local, end = run[run.Count - 1].Value.End;
                    HttpWebRequest req;
                    using (HttpWebResponse r = Get(p, a, at, end - 1, stop, out req))
                    using (stop.Register(req.Abort))
                    using (Stream s = r.GetResponseStream())
                    {
                        for (; i < run.Count; i++)
                        {
                            RemoteBlob b = run[i].Value;
                            Skip(s, b.Local - at, buf);
                            byte[] h = Exactly(s, 30);
                            if (BitConverter.ToUInt32(h, 0) != 0x04034b50u) throw new RemoteRefusedException(a.Name + ": no zip entry where " + run[i].Key + " should be.");
                            int n = BitConverter.ToUInt16(h, 26), x = BitConverter.ToUInt16(h, 28);
                            Skip(s, n + x, buf);
                            Save(s, b, Path.Combine(folder, run[i].Key), progress, stop, buf);
                            at = b.Local + 30 + n + x + b.Length;
                            failures = 0;
                        }
                        // Read what's left of a short tail, so the connection can be used again.
                        if (end - at <= Coalesce) Skip(s, end - at, buf);
                    }
                }
                catch (Exception e) { Pause(e, ref failures, stop); }
            }
        }

        static void Save(Stream s, RemoteBlob b, string path, InstallProgress progress, CancellationToken stop, byte[] buf)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string part = path + ".part";
            long counted = 0;
            try
            {
                uint crc = 0xffffffffu;
                long size = 0;
                using (var o = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var window = new Window(s, b.Length, n => { Interlocked.Add(ref progress.Done, n); counted += n; });
                    using (Stream src = b.Deflated ? (Stream)new DeflateStream(window, CompressionMode.Decompress, true) : window)
                    {
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            stop.ThrowIfCancellationRequested();
                            crc = Crc32.Update(crc, buf, n);
                            o.Write(buf, 0, n);
                            size += n;
                        }
                    }
                    while (window.Read(buf, 0, buf.Length) > 0) { }
                }
                if ((crc ^ 0xffffffffu) != b.Crc || size != b.Size) throw new IOException(Path.GetFileName(path) + " arrived damaged.");
                if (File.Exists(path)) File.Delete(path);
                File.Move(part, path);
            }
            catch
            {
                Interlocked.Add(ref progress.Done, -counted);
                try { File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        static void Skip(Stream s, long count, byte[] buf)
        {
            if (count < 0) throw new RemoteRefusedException("Zip entries overlap.");
            while (count > 0)
            {
                int n = s.Read(buf, 0, (int)Math.Min(count, buf.Length));
                if (n == 0) throw new IOException("The download ended early.");
                count -= n;
            }
        }

        static byte[] Exactly(Stream s, int count)
        {
            var b = new byte[count];
            for (int got = 0; got < count; )
            {
                int n = s.Read(b, got, count - got);
                if (n == 0) throw new IOException("The download ended early.");
                got += n;
            }
            return b;
        }

        /// <summary>
        /// Downloads the release's installer to the temp folder and starts it with these arguments. With relay, its output
        /// is copied to this process's console.
        /// </summary>
        public static Process StartNewerInstaller(RemotePackage p, string arguments, bool relay, CancellationToken stop)
        {
            if (p.Installer == null) throw new RemoteRefusedException("Release " + p.Version + " needs a newer installer and has none to download.");
            byte[] exe = Whole(p, p.Installer, stop);
            string folder = Path.Combine(Path.GetTempPath(), "BattleTechUCUSetup-" + string.Concat(p.Version.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, InstallerName);
            File.WriteAllBytes(path, exe);
            var newer = new Process { StartInfo = new ProcessStartInfo(path, arguments) { UseShellExecute = false, RedirectStandardOutput = relay, RedirectStandardError = relay } };
            if (relay)
            {
                newer.OutputDataReceived += (s, e) => { if (e.Data != null) Console.Out.WriteLine(e.Data); };
                newer.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };
            }
            newer.Start();
            if (relay)
            {
                newer.BeginOutputReadLine();
                newer.BeginErrorReadLine();
            }
            return newer;
        }

        /// <summary>A bounded read of a stream it doesn't own; reports each read, and fails if the stream ends early.</summary>
        sealed class Window : Stream
        {
            readonly Stream _s;
            readonly Action<int> _read;
            long _left;
            public Window(Stream s, long length, Action<int> read) { _s = s; _left = length; _read = read; }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_left <= 0) return 0;
                int n = _s.Read(buffer, offset, (int)Math.Min(count, _left));
                if (n == 0) throw new IOException("The download ended early.");
                _left -= n;
                _read(n);
                return n;
            }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
