// N5, second half: a one-time copy of BATTLETECH's Steam Cloud folder before the port first runs. Retail and the port are the
// same Steam app (637090) and share that folder (saves and settings), so a port bug that damages a save would damage the
// retail one too. The installer does it, before first run (2026-11-14).
//   After an install that succeeds, each Steam account on this PC with a 637090 folder that has no backup yet is copied to
//   Documents\BattleTech UCU save backup\<account>\. An account already backed up is never copied again, so a later update
//   keeps the first copy (the one from before the port ran).
//   Steam's folder is only read. The copy is written beside its final name, checked file by file (size and SHA-256), and
//   only then renamed into place, so a half copy never looks like a backup.
//   BattleTechUCUSetup --backup-saves [--steam-root <folder>] [--backup-root <folder>]   runs it alone, with no window
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ReleaseGate
{
    static class SaveBackup
    {
        public const string AppId = "637090";
        public const string FolderName = "BattleTech UCU save backup";
        /// <summary>Tests: a Steam folder (holding userdata\) instead of Steam's own.</summary>
        public static string SteamRootOverride;
        /// <summary>Tests: where backups go instead of Documents\BattleTech UCU save backup.</summary>
        public static string BackupRootOverride;

        public static string BackupRoot
        {
            get { return BackupRootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), FolderName); }
        }

        static string SteamRoot()
        {
            if (SteamRootOverride != null) return SteamRootOverride;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string p = k == null ? null : k.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(p)) return p.Replace('/', '\\');
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException || e is UnauthorizedAccessException || e is IOException) { }
            return null;
        }

        /// <summary>Each account's 637090\remote folder, keyed by the account folder's name.</summary>
        static List<KeyValuePair<string, string>> Sources(string steam)
        {
            var l = new List<KeyValuePair<string, string>>();
            string userdata = Path.Combine(steam, "userdata");
            if (!Directory.Exists(userdata)) return l;
            foreach (string account in Directory.GetDirectories(userdata).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string remote = Path.Combine(account, AppId, "remote");
                if (Directory.Exists(remote) && Directory.EnumerateFiles(remote, "*", SearchOption.AllDirectories).Any())
                    l.Add(new KeyValuePair<string, string>(Path.GetFileName(account), remote));
            }
            return l;
        }

        /// <summary>
        /// Backs up every account not backed up yet. Returns one sentence for the install message, or null when there was
        /// nothing to do (no Steam folder, no BATTLETECH saves, or every account already has its backup). Never throws for
        /// I/O: a failed copy is reported and removed, and the install result stands.
        /// </summary>
        public static string Run()
        {
            string steam = SteamRoot();
            if (steam == null || !Directory.Exists(steam)) return null;
            List<KeyValuePair<string, string>> sources;
            try { sources = Sources(steam); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return "Your BATTLETECH saves couldn't be backed up: " + e.Message; }
            string root = BackupRoot;
            var done = new List<string>();
            var failed = new List<string>();
            int files = 0;
            long bytes = 0;
            foreach (var s in sources)
            {
                string final = Path.Combine(root, s.Key);
                if (Directory.Exists(final)) continue;
                string problem = Copy(s.Value, final, ref files, ref bytes);
                if (problem == null) done.Add(final); else failed.Add(problem);
            }
            var m = new StringBuilder();
            if (done.Count > 0)
                m.AppendFormat("Your BATTLETECH saves and settings were backed up ({0} file{1}, {2:F0} MB) to {3}.", files, files == 1 ? "" : "s", bytes / 1048576.0, root);
            foreach (string f in failed) m.Append(m.Length > 0 ? " " : "").Append("Your BATTLETECH saves couldn't be backed up: ").Append(f);
            return m.Length > 0 ? m.ToString() : null;
        }

        static string Copy(string remote, string final, ref int files, ref long bytes)
        {
            string part = final + ".partial";
            try
            {
                if (Directory.Exists(part)) Directory.Delete(part, true);
                string[] all = Directory.GetFiles(remote, "*", SearchOption.AllDirectories);
                long need = all.Sum(f => new FileInfo(f).Length);
                Directory.CreateDirectory(part);
                long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(part))).AvailableFreeSpace;
                if (free < need + (need / 10) + 64L * 1048576)
                {
                    Directory.Delete(part, true);
                    return string.Format("{0:F0} MB needed and only {1:F0} MB free on that drive.", need / 1048576.0, free / 1048576.0);
                }
                var index = new StringBuilder();
                using (var sha = SHA256.Create())
                    foreach (string f in all)
                    {
                        string rel = f.Substring(remote.Length).TrimStart('\\');
                        string to = Path.Combine(part, "remote", rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(to));
                        File.Copy(f, to, false);
                        File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(f));
                        string a = Hash(sha, f), b = Hash(sha, to);
                        long len = new FileInfo(to).Length;
                        if (a != b || len != new FileInfo(f).Length) throw new IOException("the copy of " + rel + " doesn't match the original");
                        index.Append(rel).Append('\t').Append(len).Append('\t').Append(b).Append("\r\n");
                    }
                File.WriteAllText(Path.Combine(part, "files.tsv"), index.ToString());
                File.WriteAllText(Path.Combine(part, "README.txt"), string.Format(
                    "BATTLETECH save backup, made by the BattleTech UCU installer on {0:yyyy-MM-dd HH:mm}, the first time it\r\n" +
                    "installed or updated BattleTech UCU for this Steam account on this PC.\r\n" +
                    "\r\n" +
                    "BATTLETECH and BattleTech UCU share one Steam Cloud folder for saves and settings. This is a copy of it:\r\n" +
                    "  {1}\r\n" +
                    "{2} files, {3:F0} MB; files.tsv lists each file with its size and SHA-256.\r\n" +
                    "\r\n" +
                    "To put it back: quit the game and Steam, copy the contents of the remote folder here over the folder above,\r\n" +
                    "then start Steam. Steam Cloud may ask which copy to keep: choose the one on this computer.\r\n" +
                    "The installer makes this copy once and never changes it.\r\n",
                    DateTime.Now, remote, all.Length, need / 1048576.0));
                Directory.Move(part, final);
                files += all.Length;
                bytes += need;
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                try { if (Directory.Exists(part)) Directory.Delete(part, true); } catch (Exception) { }
                return e.Message;
            }
        }

        static string Hash(SHA256 sha, string path)
        {
            using (FileStream s = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "");
        }
    }
}
