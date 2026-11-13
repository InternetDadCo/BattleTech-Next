// The public manifest (ucu-manifest.tsv): every file a release installs, with its size, SHA-512, where it comes from and
// whether it is executable. It is published in the clear beside the encrypted package, so anyone can see what a release
// installs and check an install against it (verify.ps1, or the installer's Verify install).
//
// SHA-512, never SHA-256: blob keys and IVs derive from each file's plaintext SHA-256, and v3 seals those values under the
// release share (docs/keyserver-design.md, gbrain lessons/release-public-hashes-not-sha256). Publishing them would let an
// old package key open a newer release's changed files.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReleaseGate
{
    public sealed class ManifestRow
    {
        public string Path;     // relative to the install folder, forward slashes
        public long Size;
        public string Sha512;   // hex, lower case
        public string Origin;   // retail | unity | built | data
        public bool Exec;       // starts with "MZ": a Windows executable or DLL
    }

    public static class PublicManifest
    {
        public const string FileName = "ucu-manifest.tsv";
        public const string Header = "# BattleTech UCU public manifest v1";

        /// <summary>Byte-equal to a file in the user's own retail install; copied from it at install time.</summary>
        public const string Retail = "retail";
        /// <summary>Byte-equal to a file shipped with the Unity Editor version that built the player.</summary>
        public const string Unity = "unity";
        /// <summary>An executable we build (HBS assemblies recompiled, Unity package assemblies, our own).</summary>
        public const string Built = "built";
        /// <summary>Anything else: game data, configuration.</summary>
        public const string Data = "data";

        /// <summary>SHA-256 and SHA-512 of a file in one read, both as lower-case hex; also whether it starts with "MZ".</summary>
        public static void HashBoth(string path, out string sha256, out string sha512, out bool exec)
        {
            using (var a = Gate.NewSha())
            using (var b = new SHA512Cng())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            {
                var buf = new byte[1 << 20];
                int n;
                bool first = true;
                exec = false;
                while ((n = f.Read(buf, 0, buf.Length)) > 0)
                {
                    if (first) { exec = n >= 2 && buf[0] == (byte)'M' && buf[1] == (byte)'Z'; first = false; }
                    a.TransformBlock(buf, 0, n, null, 0);
                    b.TransformBlock(buf, 0, n, null, 0);
                }
                a.TransformFinalBlock(buf, 0, 0);
                b.TransformFinalBlock(buf, 0, 0);
                sha256 = Gate.Hex(a.Hash);
                sha512 = Gate.Hex(b.Hash);
            }
        }

        public static string Sha512File(string path)
        {
            using (var h = new SHA512Cng())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                return Gate.Hex(h.ComputeHash(f));
        }

        /// <summary>SHA-256 of the manifest file itself: the value the release notes carry, so readers can tie the two.</summary>
        public static string Sha256Of(byte[] manifest)
        {
            using (var h = Gate.NewSha()) return Gate.Hex(h.ComputeHash(manifest));
        }

        public static byte[] Write(IEnumerable<ManifestRow> rows)
        {
            var list = rows.OrderBy(r => r.Path, StringComparer.Ordinal).ToList();
            var s = new StringBuilder();
            s.Append(Header).Append('\n');
            s.Append("# ").Append(list.Count).Append(" files. Columns: path, size, SHA-512, origin (")
             .Append(Retail).Append(" = byte-equal to your own retail file, copied from it at install; ")
             .Append(Unity).Append(" = byte-equal to the Unity Editor's stock file; ")
             .Append(Built).Append(" = an executable we build; ")
             .Append(Data).Append(" = anything else), exec (a Windows executable or DLL).\n");
            foreach (var g in list.GroupBy(r => r.Origin).OrderBy(g => g.Key, StringComparer.Ordinal))
                s.Append("# ").Append(g.Key).Append(": ").Append(g.Count()).Append(" files, ").Append(g.Count(r => r.Exec)).Append(" executable\n");
            foreach (var r in list)
                s.Append(r.Path).Append('\t').Append(r.Size).Append('\t').Append(r.Sha512).Append('\t').Append(r.Origin).Append('\t').Append(r.Exec ? "exec" : "-").Append('\n');
            return new UTF8Encoding(false).GetBytes(s.ToString());
        }

        /// <summary>Parses a manifest; throws InvalidDataException when it isn't one.</summary>
        public static Dictionary<string, ManifestRow> Read(byte[] bytes)
        {
            string text = new UTF8Encoding(false).GetString(bytes);
            if (!text.StartsWith(Header + "\n", StringComparison.Ordinal)) throw new InvalidDataException("Not a BattleTech UCU public manifest.");
            var d = new Dictionary<string, ManifestRow>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in text.Split('\n'))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] c = line.Split('\t');
                long size;
                if (c.Length != 5 || !long.TryParse(c[1], out size) || c[2].Length != 128) throw new InvalidDataException("Bad manifest line: " + line);
                if (d.ContainsKey(c[0])) throw new InvalidDataException("Manifest lists a file twice: " + c[0]);
                d[c[0]] = new ManifestRow { Path = c[0], Size = size, Sha512 = c[2].ToLowerInvariant(), Origin = c[3], Exec = c[4] == "exec" };
            }
            return d;
        }

        /// <summary>The first way the vault's file list differs from the manifest, or null when every path and size agree.</summary>
        public static string Mismatch(IList<Entry> entries, Dictionary<string, ManifestRow> manifest)
        {
            foreach (Entry e in entries)
            {
                ManifestRow r;
                if (!manifest.TryGetValue(e.Path, out r)) return e.Path + " is not in the manifest";
                if (r.Size != e.Size) return e.Path + " has a different size";
            }
            if (manifest.Count != entries.Count)
            {
                var have = new HashSet<string>(entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
                return manifest.Keys.First(k => !have.Contains(k)) + " is in the manifest but not the package";
            }
            return null;
        }

#if PACK || MANAGER
        // Where a Unity Editor keeps the files its Windows Mono player build copies in.
        static readonly string[] UnityPlayerDirs =
        {
            @"Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_player_nondevelopment_mono",
            @"Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations\mono",
            @"Editor\Data\MonoBleedingEdge",
        };

        /// <summary>
        /// The Unity Editor that built this player, from UnityPlayer.dll's product version ("6000.6.8f1 (hash)"), where Unity
        /// Hub installs it; null when it isn't installed there.
        /// </summary>
        public static string FindUnity(string player)
        {
            string dll = System.IO.Path.Combine(player, "UnityPlayer.dll");
            if (!File.Exists(dll)) return null;
            string version = (System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).ProductVersion ?? "").Split(' ')[0];
            if (version.Length == 0) return null;
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (string dir in new[] { System.IO.Path.Combine(pf, @"Unity\Hub\Editor", version), System.IO.Path.Combine(pf, "Unity " + version) })
                if (File.Exists(System.IO.Path.Combine(dir, @"Editor\Unity.exe"))) return dir;
            return null;
        }

        /// <summary>Name|size -> SHA-256s of the Editor's player files, to recognise stock files. Empty when unity is null.</summary>
        public sealed class UnityIndex
        {
            readonly Dictionary<string, List<string>> _bySize = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string, string> _sha = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public int Files { get { return _bySize.Values.Sum(l => l.Count); } }

            public UnityIndex(string unity)
            {
                if (string.IsNullOrEmpty(unity)) return;
                if (!File.Exists(System.IO.Path.Combine(unity, @"Editor\Unity.exe")))
                    throw new FileNotFoundException("That isn't a Unity Editor folder (no Editor\\Unity.exe): " + unity);
                foreach (string rel in UnityPlayerDirs)
                {
                    string dir = System.IO.Path.Combine(unity, rel);
                    if (!Directory.Exists(dir)) continue;
                    foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        string k = System.IO.Path.GetFileName(f) + "|" + new FileInfo(f).Length;
                        List<string> l;
                        if (!_bySize.TryGetValue(k, out l)) _bySize[k] = l = new List<string>();
                        l.Add(f);
                    }
                }
            }

            /// <summary>True when a file of this name, size and SHA-256 ships with the Editor.</summary>
            public bool IsStock(string name, long size, string sha256)
            {
                List<string> l;
                if (!_bySize.TryGetValue(name + "|" + size, out l)) return false;
                foreach (string f in l)
                {
                    string h;
                    lock (_sha)
                        if (!_sha.TryGetValue(f, out h)) h = null;
                    if (h == null)
                    {
                        h = Gate.HashFile(f);
                        lock (_sha) _sha[f] = h;
                    }
                    if (h == sha256) return true;
                }
                return false;
            }
        }
#endif
    }
}
