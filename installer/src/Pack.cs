// Packer: turns a built player into a package. Never ship this tool.
//   ReleasePack --player <dir> --retail <dir> --key <file> --out <new dir> [--extra-fingerprint <hex>]...
//               [--master-secret <file> --unlock-url <url>]   a v3 package, unlocked by the unlock server (docs/keyserver-design.md)
//               [--unity <Editor folder>]   marks files byte-equal to that Unity Editor's player files as "unity" in the public
//                                           manifest (PublicManifest.cs); default: the player's own version under Unity Hub
//   ReleasePack --fingerprint <retail dir>     prints a retail install's fingerprint, for adding a slot
// The key file holds the content key, which every release reuses so unchanged files keep the same blob. It is created on
// first use. Keep it out of the repository and out of packages; a lost key only means the next release is uploaded whole.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ReleaseGate
{
    public sealed class PackProgress
    {
        public long Done;    // bytes of player files finished
        public long Total;   // bytes of player files to pack
    }

    public sealed class OutputExistsException : IOException
    {
        public OutputExistsException(string output)
            : base("The package folder already exists: " + output + ". Move or delete the old package first, or pick another folder.") { }
    }

    public sealed class PackResult
    {
        public int Files, Encrypted, Blobs, FromRetail;
        public long SealedBytes, PackedBytes, CopiedBytes, LargestBlob;
        public string Summary;
        public string ReleaseId;   // v3 only: the id the unlock server is asked about
        public string ManifestSha256;   // of ucu-manifest.tsv, for the release notes
    }

    public static class Pack
    {
        // Development and per-machine files that are not part of a release.
        static readonly string[] SkipDirs = { "Mods", "ModSaves", "BTASaves", "DumpBox", "Mods_sfx", "Music", "BattleTech_Data/StreamingAssets/aa" };
        static readonly string[] SkipFiles = { "MapProbe.dll", "DlcProbe.dll", "doorstop_config.probe.ini", "doorstop_config.modded.ini", "HarmonyFileLog.log", "BattleTechPortBootstrap.log",
            // The mod bootstrap needs Mods/, which a vanilla release leaves out.
            "winhttp.dll", "doorstop_config.ini", "BattleTechPortBootstrap.dll", "doorstop_config.off.ini", ".doorstop_version",
            // Probe left in full-6 by the visual track's build checks.
            "VisualBuildProbe.dll" };
        // Present in a dev retail folder but not in Steam's depot, so they can't be copied from a user's install.
        static readonly string[] NeverFromRetail = { "steam_appid.txt" };

        static int Main(string[] a)
        {
            try
            {
                if (a.Length == 2 && a[0] == "--fingerprint")
                {
                    Console.WriteLine(Gate.Hex(Gate.Fingerprint(a[1])));
                    return 0;
                }
                string player = null, retail = null, output = null, keyFile = null, masterFile = null, unlockUrl = null, unity = null;
                var extra = new List<byte[]>();
                for (int i = 0; i + 1 < a.Length; i += 2)
                {
                    if (a[i] == "--player") player = a[i + 1];
                    else if (a[i] == "--retail") retail = a[i + 1];
                    else if (a[i] == "--out") output = a[i + 1];
                    else if (a[i] == "--key") keyFile = a[i + 1];
                    else if (a[i] == "--extra-fingerprint") extra.Add(FromHex(a[i + 1]));
                    else if (a[i] == "--master-secret") masterFile = a[i + 1];
                    else if (a[i] == "--unlock-url") unlockUrl = a[i + 1];
                    else if (a[i] == "--unity") unity = a[i + 1];
                    else throw new ArgumentException("Unknown option " + a[i]);
                }
                if (player == null || retail == null || output == null || keyFile == null) throw new ArgumentException("Need --player, --retail, --key and --out");
                if (Directory.Exists(output)) throw new IOException("Output exists: " + output);
                if ((masterFile == null) != (unlockUrl == null)) throw new ArgumentException("--master-secret and --unlock-url go together");
                byte[] master = masterFile == null ? null : LoadMaster(masterFile, output);
                PackResult r = Run(player, retail, output, LoadKey(keyFile, output, Console.WriteLine), extra, new PackProgress(), CancellationToken.None, master, unlockUrl, unity);
                Console.WriteLine(r.Summary);
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("FAILED: " + e.Message);
                return 1;
            }
        }

        public static byte[] FromHex(string s)
        {
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }

        public static byte[] LoadKey(string keyFile, string output, Action<string> log)
        {
            keyFile = Path.GetFullPath(keyFile);
            if (keyFile.StartsWith(Path.GetFullPath(output).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Keep the key file outside the package");
            if (File.Exists(keyFile))
            {
                byte[] k = FromHex(File.ReadAllText(keyFile).Trim());
                if (k.Length != 32) throw new InvalidDataException("The key file doesn't hold a 32-byte hex key: " + keyFile);
                return k;
            }
            byte[] made = Gate.Random(32);
            File.WriteAllText(keyFile, Gate.Hex(made));
            log("Created a new content key at " + keyFile + ". Keep it for every later release.");
            return made;
        }

        /// <summary>
        /// The unlock server's master secret (32 bytes, hex). Never created here: a new one must also be set on the server,
        /// so the Release Manager creates it on request and says so.
        /// </summary>
        public static byte[] LoadMaster(string file, string output)
        {
            file = Path.GetFullPath(file);
            if (output != null && file.StartsWith(Path.GetFullPath(output).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Keep the master secret outside the package");
            if (!File.Exists(file)) throw new FileNotFoundException("The master secret file doesn't exist: " + file);
            byte[] k = FromHex(File.ReadAllText(file).Trim());
            if (k.Length != 32) throw new InvalidDataException("The master secret file doesn't hold a 32-byte hex key: " + file);
            return k;
        }

        static string Rel(string root, string path)
        {
            return path.Substring(root.TrimEnd('\\', '/').Length + 1).Replace('\\', '/');
        }

        /// <summary>True when a file at this player-relative path goes into a release.</summary>
        public static bool Ships(string rel)
        {
            rel = rel.Replace('\\', '/');
            if (SkipDirs.Any(d => rel.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase))) return false;
            return !SkipFiles.Contains(Path.GetFileName(rel), StringComparer.OrdinalIgnoreCase);
        }

        public static PackResult Run(string player, string retail, string output, byte[] key, List<byte[]> extra, PackProgress progress, CancellationToken stop,
            byte[] masterSecret = null, string unlockUrl = null, string unity = null)
        {
            var clock = Stopwatch.StartNew();
            player = Path.GetFullPath(player);
            retail = Path.GetFullPath(retail);
            // A package is never written over another: the old one's blobs, vault and installer would mix with the new.
            if (Directory.Exists(output)) throw new OutputExistsException(Path.GetFullPath(output));
            if (!File.Exists(Path.Combine(player, "BattleTech.exe"))) throw new FileNotFoundException("The player has no BattleTech.exe. See Package a release in the README.");
            if (!Gate.HasAppId(player))
                throw new FileNotFoundException("The player doesn't set the Steam app ID in its code (it predates patch 101) and has no steam_appid.txt holding 637090. See Package a release in the README.");
            byte[] fingerprint = Gate.Fingerprint(retail);
            if (unity == null) unity = PublicManifest.FindUnity(player);
            var stock = new PublicManifest.UnityIndex(unity);
            VaultLock lk = null;
            if (masterSecret != null)
            {
                Uri u;
                if (!Uri.TryCreate(unlockUrl ?? "", UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttps)
                    throw new ArgumentException("The unlock URL must be an https address: " + unlockUrl);
                lk = new VaultLock { ReleaseId = Gate.Random(16), Url = unlockUrl };
            }

            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in Directory.EnumerateFiles(retail, "*", SearchOption.AllDirectories))
            {
                string rel = Rel(retail, f);
                if (rel.StartsWith("DumpBox/", StringComparison.OrdinalIgnoreCase)) continue;
                string k = Path.GetFileName(f) + "|" + new FileInfo(f).Length;
                List<string> l;
                if (!index.TryGetValue(k, out l)) index[k] = l = new List<string>();
                l.Add(rel);
            }

            var files = new List<string>();
            foreach (string f in Directory.EnumerateFiles(player, "*", SearchOption.AllDirectories))
            {
                string rel = Rel(player, f);
                if (Ships(rel)) files.Add(rel);
            }
            progress.Total = files.Sum(r => new FileInfo(Path.Combine(player, r)).Length);

            Directory.CreateDirectory(Path.Combine(output, "data"));
            var entries = new Entry[files.Count];
            var rows = new ManifestRow[files.Count];
            long copied = 0, sealedBytes = 0, blobBytes = 0, largest = 0;
            // Files with the same content share one blob.
            var blobs = new ConcurrentDictionary<string, Lazy<string>>();
            Parallel.ForEach(Enumerable.Range(0, files.Count), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount / 2), CancellationToken = stop }, i =>
            {
                string rel = files[i];
                string src = Path.Combine(player, rel);
                long size = new FileInfo(src).Length;
                string sha, sha512;
                bool exec;
                PublicManifest.HashBoth(src, out sha, out sha512, out exec);
                var e = new Entry { Path = rel, Size = size, Blob = "", BlobSha256 = "", RetailPath = "", Sha256 = sha };
                List<string> candidates;
                if (!NeverFromRetail.Contains(Path.GetFileName(rel), StringComparer.OrdinalIgnoreCase)
                    && index.TryGetValue(Path.GetFileName(rel) + "|" + size, out candidates))
                    foreach (string c in candidates)
                        if (Gate.HashFile(Path.Combine(retail, c)) == sha) { e.RetailPath = c; break; }
                if (e.RetailPath.Length > 0)
                    Interlocked.Add(ref copied, size);
                else
                {
                    e.Blob = Gate.BlobFor(key, sha);
                    string blob = e.Blob;
                    e.BlobSha256 = blobs.GetOrAdd(sha, _ => new Lazy<string>(() =>
                    {
                        string dst = Path.Combine(output, blob);
                        string s = Seal(src, dst, key, blob, sha);
                        long n = new FileInfo(dst).Length;
                        Interlocked.Add(ref blobBytes, n);
                        long seen;
                        while (n > (seen = Interlocked.Read(ref largest)) && Interlocked.CompareExchange(ref largest, n, seen) != seen) { }
                        return s;
                    })).Value;
                    Interlocked.Add(ref sealedBytes, size);
                }
                entries[i] = e;
                string origin = e.RetailPath.Length > 0 ? PublicManifest.Retail
                    : stock.IsStock(Path.GetFileName(rel), size, sha) ? PublicManifest.Unity
                    : exec ? PublicManifest.Built : PublicManifest.Data;
                rows[i] = new ManifestRow { Path = rel, Size = size, Sha512 = sha512, Origin = origin, Exec = exec };
                Interlocked.Add(ref progress.Done, size);
            });

            var slots = new List<byte[]> { fingerprint };
            slots.AddRange(extra);
            Gate.WriteVault(Path.Combine(output, Gate.VaultName), key, slots, entries.ToList(), lk, lk == null ? null : Gate.Share(masterSecret, lk.ReleaseId));
            string setup = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BattleTechUCUSetup.exe");
            if (!File.Exists(setup)) throw new FileNotFoundException("Keep BattleTechUCUSetup.exe beside ReleasePack.exe", setup);
            byte[] manifest = PublicManifest.Write(rows);
            File.WriteAllBytes(Path.Combine(output, PublicManifest.FileName), manifest);
            File.Copy(setup, Path.Combine(output, "BattleTechUCUSetup.exe"));
            // The itch app shows "play" as its main Launch button, so players start the installer from the itch app.
            File.WriteAllText(Path.Combine(output, ".itch.toml"),
                "[[actions]]\nname = \"play\"\npath = \"BattleTechUCUSetup.exe\"\n");
            var result = new PackResult
            {
                Files = files.Count,
                Encrypted = entries.Count(x => x.RetailPath.Length == 0),
                Blobs = blobs.Count,
                FromRetail = entries.Count(x => x.RetailPath.Length > 0),
                SealedBytes = sealedBytes,
                PackedBytes = blobBytes,
                CopiedBytes = copied,
                LargestBlob = largest,
                ReleaseId = lk == null ? null : lk.Id,
                ManifestSha256 = PublicManifest.Sha256Of(manifest),
            };
            string origins = string.Join(", ", rows.GroupBy(x => x.Origin).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => string.Format("{0} {1} ({2} exec)", g.Key, g.Count(), g.Count(x => x.Exec))));
            result.Summary = string.Format("files {0}; encrypted {1}, {2:F2} GiB in {3} blobs, {4:F2} GiB packed, largest blob {5:F2} GiB; copied from retail at install {6}, {7:F2} GiB; {8:F0} s",
                result.Files, result.Encrypted, sealedBytes / 1073741824.0, result.Blobs, blobBytes / 1073741824.0, largest / 1073741824.0,
                result.FromRetail, copied / 1073741824.0, clock.Elapsed.TotalSeconds)
                + (lk == null ? "; no unlock server (v2)" : "; unlock server release " + lk.Id)
                + "; public manifest " + origins + (unity == null ? " (the player's Unity Editor wasn't found and no --unity was given: stock Unity files count as built or data)" : " (Unity files from " + unity + ")")
                + ", SHA-256 " + result.ManifestSha256;
            return result;
        }

        // Blob = IV, then AES-CBC of the deflated file. Key and IV come from the content key and the file's hash, so the
        // same file always gives the same bytes.
        static string Seal(string src, string dst, byte[] key, string blob, string plainSha)
        {
            byte[] iv = Gate.BlobIv(key, plainSha);
            using (var hash = Gate.NewSha())
            using (var a = Gate.NewAes(Gate.FileKey(key, blob, "file"), iv))
            {
                using (var o = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                using (var hashed = new CryptoStream(o, hash, CryptoStreamMode.Write))
                {
                    hashed.Write(iv, 0, iv.Length);
                    using (var enc = new CryptoStream(hashed, a.CreateEncryptor(), CryptoStreamMode.Write))
                    using (var z = new DeflateStream(enc, CompressionLevel.Optimal))
                    using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                        i.CopyTo(z, 1 << 20);
                }
                return Gate.Hex(hash.Hash);
            }
        }
    }
}
