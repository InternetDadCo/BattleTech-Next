// Install logic, with no user interface. The form and the command line both call this.
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ReleaseGate
{
    public enum InstallResult { Installed = 0, Failed = 1, NoRetail = 2, RetailRefused = 3, Damaged = 4, Cancelled = 5 }

    /// <summary>The outcome of checking a BATTLETECH folder against this package.</summary>
    public sealed class RetailCheck
    {
        public string Folder;       // the install root, after tidying what the user typed
        public byte[] Key;          // null when refused
        public List<Entry> Entries;
        public string Problem;      // what to show the user; null when accepted
        public bool CodeProblem;    // Problem came from unlocking a v3 package: the access code may need changing
        public byte[] ManifestBytes;    // the public manifest (PublicManifest.cs); null for a package packed before it existed
        public Dictionary<string, ManifestRow> Manifest;
        public string ManifestSha256 { get { return ManifestBytes == null ? null : PublicManifest.Sha256Of(ManifestBytes); } }
        public long Need { get { return Entries == null ? 0 : Entries.Sum(e => e.Size); } }
    }

    public sealed class InstallProgress
    {
        public long Done;
        public long Total;
    }

    public static class Installer
    {
        public static string PackageFolder { get { return AppDomain.CurrentDomain.BaseDirectory; } }

        // Package files come loose from the installer's folder (itch app, extracted zip), or straight out of zips: data
        // parts beside the installer (GitHub), or the zip itch.io serves for a website download, read in place so the
        // player never extracts it.
        static Dictionary<string, PartBlob> _parts;
        static readonly object PartsLock = new object();
        static string _chosenZip;

        static Dictionary<string, PartBlob> Parts
        {
            get { lock (PartsLock) return _parts ?? (_parts = Volumes.Index(CandidateZips())); }
        }

        /// <summary>A downloaded zip the player chose; its package files are read in place.</summary>
        public static void UseZip(string zip)
        {
            lock (PartsLock) { _chosenZip = zip; _parts = null; }
        }

        static IEnumerable<string> CandidateZips()
        {
            var l = new List<string>();
            try { l.AddRange(Directory.EnumerateFiles(PackageFolder, "*.zip")); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            string z = ZipRunFrom();
            if (z != null) l.Add(z);
            if (_chosenZip != null) l.Add(_chosenZip);
            return l;
        }

        /// <summary>
        /// Double-clicking the installer inside a zip in Explorer runs a copy extracted to %TEMP%\Temp<n>_<zip name>\.
        /// The zip's name comes from that folder; look for it where browsers save downloads.
        /// </summary>
        public static string ZipRunFrom()
        {
            string name = ZipNameRunFrom();
            if (name == null) return null;
            foreach (string dir in DownloadFolders())
            {
                try
                {
                    string p = Path.Combine(dir, name);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        /// <summary>The zip's file name when Explorer ran this installer out of a zip, else null.</summary>
        public static string ZipNameRunFrom()
        {
            var m = System.Text.RegularExpressions.Regex.Match(PackageFolder, @"\\Temp\d+_([^\\]+\.zip)\\", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        public static IEnumerable<string> DownloadFolders()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string shell = null;
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
                    shell = k == null ? null : k.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;
            }
            catch (Exception e) when (e is System.Security.SecurityException || e is UnauthorizedAccessException || e is IOException) { }
            var l = new List<string>();
            if (!string.IsNullOrEmpty(shell)) l.Add(Environment.ExpandEnvironmentVariables(shell));
            l.Add(Path.Combine(profile, "Downloads"));
            l.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            l.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            return l.Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        // A release online (Remote.cs). Its vault replaces any local one; blobs still come from local package files when
        // they have them (a blob's name fixes its content), and the rest are downloaded into the destination first.
        static RemotePackage _remote;
        static string _downloads;

        /// <summary>Blobs downloaded for an install wait here, inside the destination, and are removed once it succeeds.</summary>
        public const string DownloadName = ".btu6-download";

        public static RemotePackage Remote { get { return _remote; } }

        public static void UseRemote(RemotePackage remote) { _remote = remote; }

        /// <summary>True when package.vault is beside the installer, in a zip it can read, or online.</summary>
        public static bool HasPackage
        {
            get { return _remote != null || File.Exists(Path.Combine(PackageFolder, Gate.VaultName)) || Parts.ContainsKey(Gate.VaultName); }
        }

        static Stream OpenVault()
        {
            if (_remote != null) return new MemoryStream(_remote.Vault, false);
            string loose = Path.Combine(PackageFolder, Gate.VaultName);
            if (File.Exists(loose)) return new FileStream(loose, FileMode.Open, FileAccess.Read, FileShare.Read);
            PartBlob p;
            if (Parts.TryGetValue(Gate.VaultName, out p)) return Volumes.OpenBlob(p);
            return null;
        }

        /// <summary>The package's public manifest, from the same place as its vault; null when it has none.</summary>
        static byte[] ReadManifest()
        {
            if (_remote != null) return _remote.Manifest;
            string loose = Path.Combine(PackageFolder, PublicManifest.FileName);
            if (File.Exists(loose)) return File.ReadAllBytes(loose);
            PartBlob p;
            if (!Parts.TryGetValue(PublicManifest.FileName, out p)) return null;
            using (Stream s = Volumes.OpenBlob(p))
            using (var m = new MemoryStream())
            {
                s.CopyTo(m);
                return m.ToArray();
            }
        }

        /// <summary>The package's v3 lock, or null for a v2 package or none found.</summary>
        public static VaultLock PackageLock
        {
            get
            {
                try
                {
                    using (Stream v = OpenVault()) return v == null ? null : Gate.PeekLock(v);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException) { return null; }
            }
        }

        static bool HasLocalBlob(string blob)
        {
            return File.Exists(Path.Combine(PackageFolder, blob)) || Parts.ContainsKey(blob);
        }

        static bool HasBlob(string blob)
        {
            return HasLocalBlob(blob) || (_remote != null && _remote.Blobs.ContainsKey(blob));
        }

        static Stream OpenBlob(string blob)
        {
            string loose = Path.Combine(PackageFolder, blob);
            if (File.Exists(loose)) return new FileStream(loose, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            PartBlob p;
            if (Parts.TryGetValue(blob, out p)) return Volumes.OpenBlob(p);
            string fetched = _downloads == null ? null : Path.Combine(_downloads, blob);
            if (fetched != null && File.Exists(fetched)) return new FileStream(fetched, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            throw new FileNotFoundException("Package file missing", blob);
        }

        /// <summary>Accepts the install folder, its BattleTech_Data folder, or the game's exe.</summary>
        public static string TidyRetail(string typed)
        {
            string p = (typed ?? "").Trim().Trim('"').TrimEnd('\\', '/');
            if (p.Length == 0) return p;
            if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = Path.GetDirectoryName(p) ?? p;
            if (string.Equals(Path.GetFileName(p), "BattleTech_Data", StringComparison.OrdinalIgnoreCase)) p = Path.GetDirectoryName(p) ?? p;
            return p;
        }

        public static RetailCheck Check(string typed)
        {
            var c = new RetailCheck { Folder = TidyRetail(typed) };
            if (c.Folder.Length == 0) { c.Problem = "Choose your BATTLETECH folder."; return c; }
            try
            {
                if (!Directory.Exists(c.Folder)) { c.Problem = "That folder doesn't exist."; return c; }
                byte[] fingerprint;
                try { fingerprint = Gate.Fingerprint(c.Folder); }
                catch (FileNotFoundException e)
                {
                    c.Problem = "That isn't a complete BATTLETECH install: " + e.FileName + " is missing.";
                    return c;
                }
                if (_remote != null && _remote.NeedsNewerInstaller)
                {
                    c.Problem = "Release " + _remote.Version + " needs a newer installer.";
                    return c;
                }
                List<Entry> entries;
                using (Stream vault = OpenVault())
                {
                    if (vault == null) { c.Problem = "The game package wasn't found. Keep the downloaded zip, or package.vault, in the same folder as this installer."; return c; }
                    Unlock.Retail = c.Folder;
                    try { c.Key = Gate.ReadVault(vault, fingerprint, Unlock.Get, out entries); }
                    catch (UnlockException e)
                    {
                        c.Problem = e.Message;
                        c.CodeProblem = true;
                        return c;
                    }
                }
                c.Entries = entries;
                if (c.Key == null)
                    c.Problem = "This package needs an unmodified Steam install of BATTLETECH. Verify the game's files in Steam and try again.";
                else
                {
                    int missing = entries.Where(e => e.Blob.Length > 0).Select(e => e.Blob).Distinct(StringComparer.OrdinalIgnoreCase).Count(b => !HasBlob(b));
                    if (missing > 0 && _remote != null)
                        c.Problem = string.Format("Release {0} is missing {1} package file(s).", _remote.Version, missing);
                    else if (missing > 0)
                        c.Problem = string.Format("{0} package file(s) are missing. Keep every downloaded file (the zip, or each data part) in the same folder as this installer.", missing);
                    else
                        CheckManifest(c);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                c.Problem = "That folder can't be read: " + e.Message;
            }
            return c;
        }

        // A package with a public manifest must list exactly the files the manifest lists, at the same sizes; each file's
        // SHA-512 is then checked as it is written. A package from before manifests installs as before, with no manifest.
        static void CheckManifest(RetailCheck c)
        {
            byte[] bytes = ReadManifest();
            if (bytes == null) return;
            try
            {
                c.Manifest = PublicManifest.Read(bytes);
            }
            catch (InvalidDataException e)
            {
                c.Problem = "This package's public manifest (" + PublicManifest.FileName + ") can't be read: " + e.Message + " Download the package again.";
                return;
            }
            string diff = PublicManifest.Mismatch(c.Entries, c.Manifest);
            if (diff != null)
            {
                c.Problem = "This package doesn't match its public manifest: " + diff + ". Nothing was installed. Download the package again.";
                c.Manifest = null;
                return;
            }
            c.ManifestBytes = bytes;
        }

        /// <summary>
        /// Checks every file the install folder's public manifest lists: present, same size, same SHA-512. Lists what differs
        /// in message. Files the manifest doesn't list (saves, logs, settings, mods) are not checked.
        /// </summary>
        public static InstallResult Verify(string dest, InstallProgress progress, CancellationToken stop, out string message)
        {
            string path = Path.Combine(dest, PublicManifest.FileName);
            if (!File.Exists(path))
            {
                message = "This folder has no " + PublicManifest.FileName + ". Install or update with this installer first.";
                return InstallResult.Failed;
            }
            byte[] bytes = File.ReadAllBytes(path);
            Dictionary<string, ManifestRow> rows;
            try { rows = PublicManifest.Read(bytes); }
            catch (InvalidDataException e)
            {
                message = PublicManifest.FileName + " can't be read: " + e.Message;
                return InstallResult.Failed;
            }
            progress.Total = rows.Values.Sum(r => r.Size);
            var bad = new ConcurrentBag<string>();
            try
            {
                Parallel.ForEach(rows.Values.OrderByDescending(r => r.Size), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stop }, r =>
                {
                    string f = Path.Combine(dest, r.Path);
                    if (!File.Exists(f)) bad.Add("missing " + r.Path);
                    else if (new FileInfo(f).Length != r.Size) bad.Add("size " + r.Path);
                    else if (PublicManifest.Sha512File(f) != r.Sha512) bad.Add("hash " + r.Path);
                    Interlocked.Add(ref progress.Done, r.Size);
                });
            }
            catch (OperationCanceledException)
            {
                message = "Check stopped.";
                return InstallResult.Cancelled;
            }
            string sum = "Manifest SHA-256 " + PublicManifest.Sha256Of(bytes) + ".";
            if (bad.Count > 0)
            {
                var l = bad.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                message = string.Format("{0} of {1} files differ from the public manifest: {2}{3}. Run the installer again to repair them. {4}",
                    l.Count, rows.Count, string.Join(", ", l.Take(5)), l.Count > 5 ? ", ..." : "", sum);
                return InstallResult.Damaged;
            }
            message = string.Format("All {0} files match the public manifest. {1}", rows.Count, sum);
            return InstallResult.Installed;
        }

        /// <summary>Kept free beyond what an install needs, for .part files of files being replaced and the record.</summary>
        const long Headroom = 1L << 30;

        /// <summary>
        /// Returns what is wrong with this destination, or null when it can be used. space is the extra disk space the
        /// install needs there (PlanInstall), so a folder holding part of the game asks only for the rest.
        /// </summary>
        public static string CheckDest(string dest, RetailCheck c, out long space)
        {
            space = 0;
            if (string.IsNullOrWhiteSpace(dest)) return "Choose a folder to install to.";
            string full, root;
            try
            {
                full = Path.GetFullPath(dest.Trim().Trim('"')).TrimEnd('\\') + "\\";
                root = Path.GetPathRoot(full);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                return "That isn't a valid folder.";
            }
            if (!Directory.Exists(root)) return "Drive " + root + " isn't available.";
            string r = Path.GetFullPath(c.Folder).TrimEnd('\\') + "\\";
            if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                return "Install somewhere outside your BATTLETECH folder. The original game stays as it is.";
            string pkg = Path.GetFullPath(PackageFolder).TrimEnd('\\') + "\\";
            if (full.StartsWith(pkg, StringComparison.OrdinalIgnoreCase)) return "Install somewhere outside this package folder.";
            try { space = PlanInstall(c, full).Space; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return "That folder can't be read: " + e.Message;
            }
            long free = new DriveInfo(root).AvailableFreeSpace;
            if (free < space + Headroom)
                return string.Format("{0:F1} GiB {1}needed on {2}, {3:F1} GiB free.", (space + Headroom) / 1073741824.0,
                    space < c.Need ? "more " : "", root, free / 1073741824.0);
            return null;
        }

        /// <summary>Creates the folder and proves it can be written to, so a protected location fails before any copying.</summary>
        public static string ProbeWrite(string dest)
        {
            try
            {
                Directory.CreateDirectory(dest);
                string p = Path.Combine(dest, ".write-test");
                File.WriteAllText(p, "");
                File.Delete(p);
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return "Can't write to that folder. Choose another one, or run this installer as administrator.";
            }
        }

        // Lists what this installer put in the folder: path, size, SHA-256, last write time. A later run skips files that are
        // unchanged and removes files the new version no longer has.
        public const string RecordName = ".btu6-install.tsv";

        static Dictionary<string, string[]> ReadRecord(string dest)
        {
            var r = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            string p = Path.Combine(dest, RecordName);
            if (!File.Exists(p)) return r;
            foreach (string line in File.ReadAllLines(p))
            {
                string[] c = line.Split('\t');
                if (c.Length == 4) r[c[0]] = new[] { c[1], c[2], c[3] };
            }
            return r;
        }

        static void WriteRecord(string dest, IDictionary<string, string[]> record)
        {
            string p = Path.Combine(dest, RecordName);
            File.WriteAllLines(p + ".part", record.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Key + "\t" + string.Join("\t", x.Value)));
            if (File.Exists(p)) File.Delete(p);
            File.Move(p + ".part", p);
        }

        static string[] Stamp(string file, string sha)
        {
            var f = new FileInfo(file);
            return new[] { f.Length.ToString(), sha, f.LastWriteTimeUtc.Ticks.ToString() };
        }

        /// <summary>The file is there as this installer last wrote it, with the hash this entry wants.</summary>
        static bool IsCurrent(string dest, Dictionary<string, string[]> old, Entry e)
        {
            string target = Path.Combine(dest, e.Path);
            string[] had;
            return old.TryGetValue(e.Path, out had) && had[1] == e.Sha256 && File.Exists(target) && Stamp(target, e.Sha256).SequenceEqual(had);
        }

        /// <summary>What an install into dest would write, and which blobs it would download first.</summary>
        public sealed class Plan
        {
            public int Files;           // files to write
            public long WriteBytes;
            public int Same;            // files already up to date
            public int Remove;          // files an earlier version installed that this one doesn't have
            public readonly List<string> Fetch = new List<string>();
            public long FetchBytes;     // bytes still to download (blobs a stopped run already saved don't count)
            public long Space;          // more disk space the install needs than dest already holds for it
        }

        static long Length(string path)
        {
            var f = new FileInfo(path);
            return f.Exists ? f.Length : 0;
        }

        /// <summary>
        /// What an install into dest still has to do. Space counts only what isn't there yet: files already current need
        /// nothing, a file being replaced (or a .part a stopped run left) gives back its own size, and blobs already in the
        /// download folder need no more room. Asking for the whole game again made a run stopped by a full disk unresumable
        /// (GitHub issue 1).
        /// </summary>
        public static Plan PlanInstall(RetailCheck c, string dest)
        {
            var plan = new Plan();
            Dictionary<string, string[]> old = ReadRecord(dest);
            var fetch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string downloads = Path.Combine(dest, DownloadName);
            foreach (Entry e in c.Entries)
            {
                if (IsCurrent(dest, old, e)) { plan.Same++; continue; }
                plan.Files++;
                plan.WriteBytes += e.Size;
                string target = Path.Combine(dest, e.Path);
                plan.Space += Math.Max(0, e.Size - Length(target) - Length(target + ".part"));
                if (_remote == null || e.RetailPath.Length > 0 || HasLocalBlob(e.Blob) || !fetch.Add(e.Blob)) continue;
                plan.Fetch.Add(e.Blob);
                RemoteBlob b = _remote.Blobs[e.Blob];
                if (Length(Path.Combine(downloads, e.Blob)) == b.Size) continue;   // saved by a stopped run; Download skips it
                plan.FetchBytes += b.Length;
                plan.Space += b.Size;
            }
            var keep = new HashSet<string>(c.Entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
            plan.Remove = old.Keys.Count(p => !keep.Contains(p));
            return plan;
        }

        /// <summary>Downloads the blobs the plan names into dest's download folder; Install then reads them from there.</summary>
        public static InstallResult Fetch(Plan plan, string dest, InstallProgress progress, CancellationToken stop, out string message)
        {
            message = null;
            // Download counts blobs a stopped run already saved as done, so the total includes them.
            progress.Total = plan.Fetch.Sum(b => _remote.Blobs[b].Length);
            _downloads = Path.Combine(dest, DownloadName);
            if (plan.Fetch.Count == 0) return InstallResult.Installed;
            long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dest))).AvailableFreeSpace;
            long need = plan.Space + Headroom;
            if (free < need)
            {
                message = string.Format("{0:F1} GiB more needed to download and install, {1:F1} GiB free.", need / 1073741824.0, free / 1073741824.0);
                return InstallResult.Failed;
            }
            try
            {
                global::ReleaseGate.Remote.Download(_remote, plan.Fetch, _downloads, progress, stop);
                return InstallResult.Installed;
            }
            catch (OperationCanceledException)
            {
                message = "Download stopped. Run this again to continue it.";
                return InstallResult.Cancelled;
            }
            catch (Exception e) when (e is IOException || e is System.Net.WebException || e is UnauthorizedAccessException)
            {
                message = "Download failed: " + e.Message + " Run this again to continue it.";
                return InstallResult.Failed;
            }
        }

        // Mod support (tools/mod-bootstrap/README.md): a release ships ModSupport\ inert, because Doorstop only reads files beside
        // the exe. Copying these three there turns it on. They aren't in the install record, so an update never reverts them.
        static readonly string[] ModLoaderFiles = { "winhttp.dll", "BattleTechPortBootstrap.dll", "doorstop_config.ini" };
        public const string ModsOffName = ".ucu-mods-off";

        /// <summary>The install's last choice: on unless the player turned it off. A new install starts on.</summary>
        public static bool ModsWanted(string dest)
        {
            try { return !File.Exists(Path.Combine(dest, ModsOffName)); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is IOException) { return true; }
        }

        /// <summary>
        /// Turns mod support on or off after an install. On copies the loader beside the exe, replacing any other copy (BTA's
        /// installer leaves ModTek's Doorstop there, which crashes on D3D12). Off removes only files identical to ModSupport's,
        /// and remembers the choice. Null when the release has no ModSupport folder.
        /// </summary>
        public static string ApplyMods(string dest, bool on)
        {
            string src = Path.Combine(dest, "ModSupport");
            if (!ModLoaderFiles.All(f => File.Exists(Path.Combine(src, f)))) return null;
            string off = Path.Combine(dest, ModsOffName);
            try
            {
                if (on)
                {
                    foreach (string f in ModLoaderFiles) File.Copy(Path.Combine(src, f), Path.Combine(dest, f), true);
                    if (File.Exists(off)) File.Delete(off);
                    return "Mod support is on: ModTek mods in the Mods folder load when the game starts.";
                }
                foreach (string f in ModLoaderFiles)
                {
                    string target = Path.Combine(dest, f);
                    if (File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(Path.Combine(src, f)))) File.Delete(target);
                }
                File.WriteAllText(off, "Mod support was turned off in the installer. Run the installer with it ticked to turn it on.\r\n");
                return "Mod support is off.";
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return "Mod support couldn't be changed: " + e.Message;
            }
        }

        /// <summary>Stops after the files in progress when cancelled. Finished files stay, and a later run skips them.</summary>
        public static InstallResult Install(RetailCheck c, string dest, InstallProgress progress, CancellationToken stop, out string message)
        {
            message = null;
            byte[] key = c.Key;
            string retail = c.Folder;
            progress.Total = c.Need;
            var bad = new List<string>();
            Directory.CreateDirectory(dest);
            Dictionary<string, string[]> old = ReadRecord(dest);
            var now = new ConcurrentDictionary<string, string[]>(old, StringComparer.OrdinalIgnoreCase);
            int same = 0;
            string text = null;
            InstallResult result;
            try
            {
                result = Run();
            }
            finally
            {
                WriteRecord(dest, now);
            }
            message = text;
            return result;

            InstallResult Run()
            {
                try
                {
                    // Largest first, so one big file doesn't run alone at the end.
                    Parallel.ForEach(c.Entries.OrderByDescending(e => e.Size), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stop }, e =>
                    {
                        string target = Path.Combine(dest, e.Path);
                        if (IsCurrent(dest, old, e))
                        {
                            Interlocked.Add(ref progress.Done, e.Size);
                            Interlocked.Increment(ref same);
                            return;
                        }
                        string[] gone;
                        now.TryRemove(e.Path, out gone);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        string part = target + ".part";
                        bool ok;
                        try
                        {
                            string src = e.RetailPath.Length > 0 ? Path.Combine(retail, e.RetailPath) : null;
                            string sha512 = c.Manifest == null ? null : c.Manifest[e.Path].Sha512;
                            ok = src != null ? File.Exists(src) && Copy(src, part, e.Sha256, sha512, progress, stop) : Open(part, key, e, sha512, progress, stop);
                        }
                        catch (OperationCanceledException)
                        {
                            File.Delete(part);
                            throw;
                        }
                        if (ok)
                        {
                            if (File.Exists(target)) File.Delete(target);
                            File.Move(part, target);
                            now[e.Path] = Stamp(target, e.Sha256);
                        }
                        else
                        {
                            File.Delete(part);
                            lock (bad) bad.Add(e.Path);
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    text = "Install cancelled. Run this again to finish it.";
                    return InstallResult.Cancelled;
                }
                catch (AggregateException a)
                {
                    if (a.Flatten().InnerExceptions.All(x => x is OperationCanceledException))
                    {
                        text = "Install cancelled. Run this again to finish it.";
                        return InstallResult.Cancelled;
                    }
                    text = "Install failed: " + a.Flatten().InnerExceptions[0].Message;
                    return InstallResult.Failed;
                }
                if (bad.Count > 0)
                {
                    text = string.Format("{0} file(s) failed their check, first {1}. Verify BATTLETECH in Steam or download this package again.", bad.Count, bad[0]);
                    return InstallResult.Damaged;
                }
                // Files an earlier version installed that this one doesn't have.
                var keep = new HashSet<string>(c.Entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
                string root = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
                int removed = 0;
                foreach (string path in old.Keys.Where(p => !keep.Contains(p)))
                {
                    string full = Path.GetFullPath(Path.Combine(dest, path));
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(full)) { File.Delete(full); removed++; }
                    string[] gone;
                    now.TryRemove(path, out gone);
                }
                string downloads = Path.Combine(dest, DownloadName);
                if (Directory.Exists(downloads))
                {
                    try { Directory.Delete(downloads, true); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                }
                // A copy of the public manifest stays with the install, for Verify install and verify.ps1. An install from a
                // package without one removes an older copy, which would no longer describe the folder.
                string kept = Path.Combine(dest, PublicManifest.FileName);
                if (c.ManifestBytes != null) File.WriteAllBytes(kept, c.ManifestBytes);
                else if (File.Exists(kept)) File.Delete(kept);
                text = string.Format("Installed {0} files, {1:F2} GiB.", c.Entries.Count, c.Need / 1073741824.0);
                if (same > 0 || removed > 0)
                    text += string.Format(" {0} were already up to date; {1} old file(s) removed.", same, removed);
                if (c.ManifestBytes != null)
                    text += " Files written match the public manifest, SHA-256 " + c.ManifestSha256 + ".";
                return InstallResult.Installed;
            }
        }

        static void Pump(Stream from, Stream to, InstallProgress progress, CancellationToken stop)
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = from.Read(buf, 0, buf.Length)) > 0)
            {
                stop.ThrowIfCancellationRequested();
                to.Write(buf, 0, n);
                Interlocked.Add(ref progress.Done, n);
            }
        }

        // The written file passes through SHA-512 (when the package has a public manifest) and then to disk.
        static Stream Hashed(Stream o, HashAlgorithm sha512)
        {
            return sha512 == null ? o : new CryptoStream(o, sha512, CryptoStreamMode.Write);
        }

        static bool Matches512(HashAlgorithm h, string want)
        {
            return h == null || Gate.Hex(h.Hash) == want;
        }

        static bool Copy(string src, string dst, string sha, string sha512, InstallProgress progress, CancellationToken stop)
        {
            using (var hash = Gate.NewSha())
            using (HashAlgorithm wide = sha512 == null ? null : new SHA512Cng())
            {
                using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                using (var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                using (var w = Hashed(o, wide))
                using (var h = new CryptoStream(w, hash, CryptoStreamMode.Write))
                    Pump(i, h, progress, stop);
                return Gate.Hex(hash.Hash) == sha && Matches512(wide, sha512);
            }
        }

        static bool Open(string dst, byte[] key, Entry e, string sha512, InstallProgress progress, CancellationToken stop)
        {
            using (var cipherHash = Gate.NewSha())
            using (var plainHash = Gate.NewSha())
            using (HashAlgorithm wide = sha512 == null ? null : new SHA512Cng())
            {
                try
                {
                    using (var i = OpenBlob(e.Blob))
                    using (var hashed = new CryptoStream(i, cipherHash, CryptoStreamMode.Read))
                    {
                        var iv = new byte[16];
                        if (hashed.Read(iv, 0, 16) != 16) return false;
                        using (var a = Gate.NewAes(Gate.FileKey(key, e.Blob, "file"), iv))
                        using (var dec = new CryptoStream(hashed, a.CreateDecryptor(), CryptoStreamMode.Read))
                        using (var z = new DeflateStream(dec, CompressionMode.Decompress))
                        using (var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                        using (var w = Hashed(o, wide))
                        using (var h = new CryptoStream(w, plainHash, CryptoStreamMode.Write))
                        {
                            Pump(z, h, progress, stop);
                            // Read the rest, so the blob's hash covers the whole file.
                            var rest = new byte[1 << 16];
                            while (dec.Read(rest, 0, rest.Length) > 0) { }
                        }
                    }
                }
                catch (CryptographicException) { return false; }
                catch (InvalidDataException) { return false; }
                catch (FileNotFoundException) { return false; }
                return Gate.Hex(cipherHash.Hash) == e.BlobSha256 && Gate.Hex(plainHash.Hash) == e.Sha256 && Matches512(wide, sha512);
            }
        }
    }
}
