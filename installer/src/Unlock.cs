// Asks the unlock server for a v3 release's share (docs/keyserver-design.md). With no access code, the installer proves
// ownership through Steam (SteamTicket.cs) and the server confirms the ticket with Valve; an access code is the manual
// route for anyone Steam can't confirm. The share is never written to disk: every install or update asks again. The
// access code is remembered for this Windows user only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace ReleaseGate
{
    public static class Unlock
    {
        /// <summary>The player's access code: --code, --code-file, the window, or the remembered one.</summary>
        public static string Code;
        /// <summary>--share-file: unlocks with the share itself, with no server. For the Release Manager's test installs.</summary>
        public static byte[] ShareOverride;
        /// <summary>--unlock-url: asks this server instead of the one the vault names (a local Worker in tests).</summary>
        public static string UrlOverride;
        /// <summary>The BATTLETECH folder being checked: its steam_api64.dll gets the Steam ticket.</summary>
        public static string Retail;
        /// <summary>How the last share was obtained: "steam", "code" or "share".</summary>
        public static string LastRoute;

        const string Agent = "BattleTechUCU-Setup";
        public const int CodeLength = 20;
        static readonly Dictionary<string, byte[]> Cache = new Dictionary<string, byte[]>();
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        static Unlock()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public static string SavedCodeFile
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BattleTechUCU", "access.txt"); }
        }

        public static string LoadSaved()
        {
            try { return File.Exists(SavedCodeFile) ? File.ReadAllText(SavedCodeFile).Trim() : null; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        public static void Save(string code)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavedCodeFile));
                File.WriteAllText(SavedCodeFile, Normalize(code));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        /// <summary>Upper case, with dashes, spaces and anything else that isn't a letter or digit dropped.</summary>
        public static string Normalize(string code)
        {
            return new string((code ?? "").ToUpperInvariant().Where(ch => (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')).ToArray());
        }

        /// <summary>Reads a share or code file: its text, trimmed.</summary>
        public static string ReadFile(string path) { return File.ReadAllText(path).Trim(); }

        /// <summary>The share for this release, from the server. Throws UnlockException with a message for the player.</summary>
        public static byte[] Get(VaultLock lk)
        {
            if (ShareOverride != null) { LastRoute = "share"; return ShareOverride; }
            string code = Normalize(Code);
            if (code.Length > 0 && code.Length != CodeLength) throw new UnlockException("An access code has " + CodeLength + " letters and digits (dashes don't count).");
            string route = code.Length > 0 ? "code" : "steam";
            string cacheKey = lk.Id + "|" + (code.Length > 0 ? code : "steam");
            lock (Cache)
            {
                byte[] hit;
                if (Cache.TryGetValue(cacheKey, out hit)) { LastRoute = route; return hit; }
            }
            string url = (UrlOverride ?? lk.Url).TrimEnd('/') + "/v1/unlock";
            byte[] share = code.Length > 0
                ? Ask(url, new Dictionary<string, object> { { "release", lk.Id }, { "code", code }, { "installer", Version } })
                : SteamTicket.With(Retail, t => Ask(url, new Dictionary<string, object> { { "release", lk.Id }, { "steam", t.Hex }, { "steamKind", t.Kind }, { "installer", Version } }));
            lock (Cache) Cache[cacheKey] = share;
            LastRoute = route;
            return share;
        }

        static byte[] Ask(string url, Dictionary<string, object> request)
        {
            byte[] body = Encoding.UTF8.GetBytes(Json.Serialize(request));
            int status;
            Dictionary<string, object> answer;
            try { answer = Post(url, body, out status); }
            catch (WebException e) when (e.Response == null)
            {
                throw new UnlockException("Couldn't reach the unlock server (" + e.Message + "). Check your internet connection and try again.");
            }
            object v;
            string error = answer != null && answer.TryGetValue("error", out v) ? v as string : null;
            string message = answer != null && answer.TryGetValue("message", out v) ? v as string : null;
            if (status == 200 && answer != null && answer.TryGetValue("share", out v) && v is string)
            {
                byte[] share;
                try { share = Gate.FromHex((string)v); }
                catch (Exception e) when (e is FormatException || e is ArgumentException) { share = null; }
                if (share == null || share.Length != 32) throw new UnlockException("The unlock server's answer couldn't be read.");
                return share;
            }
            if (error == "code") throw new UnlockException("This access code isn't valid, or it was revoked.");
            if (error == "steam") throw new UnlockException((message ?? "Steam didn't confirm that you own BATTLETECH.") + " If you were given an access code, enter it instead.");
            if (error == "steam-borrowed") throw new UnlockException("This copy of BATTLETECH is borrowed through Steam Family Sharing. Sign in to the account that owns it, or enter an access code.");
            if (error == "steam-blocked") throw new UnlockException("This Steam account can't unlock releases. If you think that's a mistake, ask for an access code.");
            if (error == "steam-off") throw new UnlockException(string.IsNullOrEmpty(message) ? "Unlocking through Steam is switched off for now. Enter an access code instead." : message);
            if (error == "steam-down") throw new UnlockException("Steam couldn't be asked to confirm your copy right now. Try again later.");
            if (error == "release") throw new UnlockException("This release has been withdrawn. Look for a newer one.");
            if (error == "closed") throw new UnlockException(string.IsNullOrEmpty(message) ? "Installing is switched off for now." : message);
            if (error == "slow-down") throw new UnlockException("Too many tries. Wait a while and try again.");
            throw new UnlockException("The unlock server answered " + status + (message != null ? ": " + message : "") + ". Try again later.");
        }

        /// <summary>
        /// Checks a server's Steam route without a real ticket: a dummy ticket must get as far as Valve and be refused
        /// there, which shows the route is on and the server's Web API key works. Null when it does; else what's wrong.
        /// </summary>
        public static string SteamRouteProblem(string baseUrl)
        {
            var request = new Dictionary<string, object> { { "release", new string('0', 32) }, { "steam", new string('0', 40) }, { "steamKind", "webapi" }, { "installer", Version } };
            int status;
            Dictionary<string, object> answer;
            try { answer = Post(baseUrl.TrimEnd('/') + "/v1/unlock", Encoding.UTF8.GetBytes(Json.Serialize(request)), out status); }
            catch (WebException e) when (e.Response == null) { return "the server can't be reached (" + e.Message + ")"; }
            object v;
            string error = answer != null && answer.TryGetValue("error", out v) ? v as string : null;
            string message = answer != null && answer.TryGetValue("message", out v) ? v as string : null;
            if (status == 403 && error == "steam") return null;
            if (error == "steam-off") return "its Steam route is off (no STEAM_WEBAPI_KEY secret, or admin.ps1 steam-off)";
            if (error == "steam-down") return "it couldn't use Valve's Web API" + (message != null ? " (" + message + ")" : "") + "; check its STEAM_WEBAPI_KEY secret";
            if (error == "closed") return "installing is switched off (admin.ps1 open)";
            if (status == 400) return "it has no Steam route yet; deploy the current keyserver";
            return "it answered " + status + (error != null ? " " + error : "");
        }

        static string Version
        {
            get { return typeof(Unlock).Assembly.GetName().Version.ToString(); }
        }

        static Dictionary<string, object> Post(string url, byte[] body, out int status)
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.Method = "POST";
            r.ContentType = "application/json";
            r.UserAgent = Agent;
            r.Timeout = 30000;
            r.ReadWriteTimeout = 30000;
            using (Stream s = r.GetRequestStream()) s.Write(body, 0, body.Length);
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)r.GetResponse(); }
            catch (WebException e) when (e.Response is HttpWebResponse) { resp = (HttpWebResponse)e.Response; }
            using (resp)
            {
                status = (int)resp.StatusCode;
                string text;
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) text = rd.ReadToEnd();
                try { return Json.Deserialize<Dictionary<string, object>>(text); }
                catch (ArgumentException) { return null; }
                catch (InvalidOperationException) { return null; }
            }
        }
    }
}
