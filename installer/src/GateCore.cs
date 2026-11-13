// Shared by the packer and the installer. Compiled against .NET Framework 4.x, which every supported Windows has.
//
// The content key is kept by whoever packs releases and reused for every release. It is stored only wrapped under a key
// derived from a retail install, so the package alone decrypts nothing. Each file is compressed, then encrypted with a key
// and IV derived from the content key and the file's own hash, so an unchanged file gives the same blob in every release
// and itch.io's patching only sends what changed. Anyone who owns the game can derive the same key: this makes the game a real requirement, it does
// not stop a determined attacker, and it does not change who holds the rights to the content.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ReleaseGate
{
    public sealed class Entry
    {
        public string Path;        // destination, relative to the install folder
        public long Size;          // plaintext size
        public string Blob;        // package file holding the ciphertext; empty when copied from retail
        public string BlobSha256;  // hex, of the whole blob
        public string RetailPath;  // source inside the retail install; empty when decrypted from the package
        public string Sha256;      // hex, of the plaintext
    }

    /// <summary>
    /// A v3 vault's lock: the release id and where to ask for its share (docs/keyserver-design.md). The share is
    /// HMAC-SHA256(master secret, "btucu-share|" + hex id); only the packer and the unlock server hold the master secret.
    /// </summary>
    public sealed class VaultLock
    {
        public byte[] ReleaseId;
        public string Url;
        public string Id { get { return Gate.Hex(ReleaseId); } }
    }

    /// <summary>A v3 vault couldn't be unlocked; Message is for the player.</summary>
    public sealed class UnlockException : Exception
    {
        public UnlockException(string message) : base(message) { }
    }

    public static class Gate
    {
        public const uint AppId = 637090;
        public const string VaultName = "package.vault";

        /// <summary>
        /// True when the player's game code sets SteamAppId itself (transplant patch 101, 2026-11-12), so it needs no
        /// steam_appid.txt. Older players need the file, or they relaunch retail through Steam. Looks for the
        /// "SteamGameId" string literal (UTF-16) that the patch adds to Assembly-CSharp.dll.
        /// </summary>
        public static bool SetsAppIdInCode(string player)
        {
            string dll = Path.Combine(player, "BattleTech_Data", "Managed", "Assembly-CSharp.dll");
            if (!File.Exists(dll)) return false;
            byte[] b = File.ReadAllBytes(dll), w = Encoding.Unicode.GetBytes("SteamGameId");
            for (int i = 0; i + w.Length <= b.Length; i++)
            {
                int k = 0;
                while (k < w.Length && b[i + k] == w[k]) k++;
                if (k == w.Length) return true;
            }
            return false;
        }

        /// <summary>True when a player starts as BATTLETECH's app ID: from its code, or from steam_appid.txt.</summary>
        public static bool HasAppId(string player)
        {
            string f = Path.Combine(player, "steam_appid.txt");
            return SetsAppIdInCode(player) || (File.Exists(f) && File.ReadAllText(f).Trim() == AppId.ToString());
        }
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("BTU6GAT2");
        static readonly byte[] Magic3 = Encoding.ASCII.GetBytes("BTU6GAT3");
        const int Window = 1 << 20;

        static bool Starts(byte[] head, byte[] magic)
        {
            if (head == null || head.Length < magic.Length) return false;
            for (int i = 0; i < magic.Length; i++)
                if (head[i] != magic[i]) return false;
            return true;
        }

        /// <summary>False when a vault starts with another format's mark: a newer installer is needed to read it.</summary>
        public static bool IsVault(byte[] head)
        {
            return Starts(head, Magic) || Starts(head, Magic3);
        }

        /// <summary>A release's share: what the unlock server hands out for it.</summary>
        public static byte[] Share(byte[] masterSecret, byte[] releaseId)
        {
            using (var h = new HMACSHA256(masterSecret))
                return h.ComputeHash(Encoding.UTF8.GetBytes("btucu-share|" + Hex(releaseId)));
        }

        static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }

        /// <summary>The lock of a v3 vault, or null for a v2 one. Reads only the header.</summary>
        public static VaultLock PeekLock(Stream f)
        {
            using (var r = new BinaryReader(f, Encoding.UTF8, true))
            {
                byte[] magic = r.ReadBytes(Magic3.Length);
                if (Starts(magic, Magic)) return null;
                if (!Starts(magic, Magic3)) throw new InvalidDataException("Not a package vault");
                return new VaultLock { ReleaseId = r.ReadBytes(16), Url = r.ReadString() };
            }
        }

        // Files from the base depot that mods leave alone. Managed DLLs are deliberately absent: mod tools rewrite them.
        // BattleTech.exe is whole because its code is Unity's stock launcher; only the whole file is BATTLETECH's.
        static readonly string[] WholeFiles = { "BattleTech.exe" };
        static readonly string[] SampledFiles =
        {
            "BattleTech_Data/globalgamemanagers.assets",
            "BattleTech_Data/resources.assets",
            "BattleTech_Data/resources.assets.resS",
            "BattleTech_Data/sharedassets0.assets",
            "BattleTech_Data/level10",
            "BattleTech_Data/level10.resS",
        };

        public static string Hex(byte[] b)
        {
            var s = new StringBuilder(b.Length * 2);
            foreach (byte x in b) s.Append(x.ToString("x2"));
            return s.ToString();
        }

        public static byte[] FromHex(string s)
        {
            if (s.Length % 2 != 0) throw new FormatException("Odd-length hex");
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }

        public static SHA256 NewSha() { return new SHA256Cng(); }

        public static string HashFile(string path)
        {
            using (var h = NewSha())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                return Hex(h.ComputeHash(f));
        }

        /// <summary>Reads the retail files the key depends on. A missing file throws, naming it.</summary>
        public static byte[] Fingerprint(string retail)
        {
            using (var h = NewSha())
            using (var ms = new MemoryStream())
            {
                foreach (string rel in WholeFiles)
                {
                    string p = Path.Combine(retail, rel);
                    if (!File.Exists(p)) throw new FileNotFoundException("Retail file not found", rel);
                    Add(ms, rel, h.ComputeHash(File.ReadAllBytes(p)));
                }
                foreach (string rel in SampledFiles)
                {
                    string p = Path.Combine(retail, rel);
                    if (!File.Exists(p)) throw new FileNotFoundException("Retail file not found", rel);
                    using (var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        long len = f.Length;
                        var buf = new byte[Window];
                        foreach (long at in new[] { 0L, Math.Max(0, len / 2 - Window / 2), Math.Max(0, len - Window) })
                        {
                            f.Position = at;
                            int got = 0, n;
                            while (got < Window && (n = f.Read(buf, got, Window - got)) > 0) got += n;
                            Add(ms, rel + "@" + at + "/" + len, h.ComputeHash(buf, 0, got));
                        }
                    }
                }
                return h.ComputeHash(ms.ToArray());
            }
        }

        static void Add(Stream s, string label, byte[] digest)
        {
            byte[] l = Encoding.UTF8.GetBytes(label.ToLowerInvariant());
            s.Write(l, 0, l.Length);
            s.Write(digest, 0, digest.Length);
        }

        /// <summary>HKDF-SHA256 (RFC 5869), one output block.</summary>
        public static byte[] Derive(byte[] secret, byte[] salt, string info)
        {
            byte[] prk;
            using (var h = new HMACSHA256(salt)) prk = h.ComputeHash(secret);
            byte[] i = Encoding.UTF8.GetBytes(info);
            var t = new byte[i.Length + 1];
            Buffer.BlockCopy(i, 0, t, 0, i.Length);
            t[i.Length] = 1;
            using (var h = new HMACSHA256(prk)) return h.ComputeHash(t);
        }

        public static Aes NewAes(byte[] key, byte[] iv)
        {
            var a = new AesCryptoServiceProvider { KeySize = 256, Mode = CipherMode.CBC, Padding = PaddingMode.PKCS7 };
            a.Key = key;
            a.IV = iv;
            return a;
        }

        public static byte[] Random(int n)
        {
            var b = new byte[n];
            using (var r = new RNGCryptoServiceProvider()) r.GetBytes(b);
            return b;
        }

        static byte[] Seal(byte[] plain, byte[] secret, string purpose)
        {
            byte[] salt = Random(16), iv = Random(16), ct;
            using (var a = NewAes(Derive(secret, salt, purpose + "|enc"), iv))
            using (var e = a.CreateEncryptor())
                ct = e.TransformFinalBlock(plain, 0, plain.Length);
            using (var ms = new MemoryStream())
            {
                ms.Write(salt, 0, 16);
                ms.Write(iv, 0, 16);
                ms.Write(ct, 0, ct.Length);
                byte[] body = ms.ToArray();
                using (var h = new HMACSHA256(Derive(secret, salt, purpose + "|mac")))
                {
                    byte[] tag = h.ComputeHash(body);
                    ms.Write(tag, 0, tag.Length);
                }
                return ms.ToArray();
            }
        }

        /// <summary>Returns null when the secret is wrong or the data was changed.</summary>
        static byte[] Open(byte[] sealedData, byte[] secret, string purpose)
        {
            if (sealedData.Length < 16 + 16 + 16 + 32) return null;
            var salt = new byte[16];
            var iv = new byte[16];
            Buffer.BlockCopy(sealedData, 0, salt, 0, 16);
            Buffer.BlockCopy(sealedData, 16, iv, 0, 16);
            int bodyLen = sealedData.Length - 32;
            byte[] want;
            using (var h = new HMACSHA256(Derive(secret, salt, purpose + "|mac")))
                want = h.ComputeHash(sealedData, 0, bodyLen);
            int diff = 0;
            for (int i = 0; i < 32; i++) diff |= want[i] ^ sealedData[bodyLen + i];
            if (diff != 0) return null;
            using (var a = NewAes(Derive(secret, salt, purpose + "|enc"), iv))
            using (var d = a.CreateDecryptor())
                return d.TransformFinalBlock(sealedData, 32, bodyLen - 32);
        }

        /// <summary>One slot per accepted retail build, so a new retail version needs a new slot, not a new pack.</summary>
        public static void WriteVault(string path, byte[] packageKey, IEnumerable<byte[]> fingerprints, List<Entry> entries)
        {
            WriteVault(path, packageKey, fingerprints, entries, null, null);
        }

        /// <summary>
        /// With a lock and its share, writes a v3 vault: each retail slot is a pair, a check sealed under the fingerprint
        /// alone (so a wrong install is refused before the server is asked) and the package key sealed under fingerprint
        /// plus share. The manifest is sealed under a key from the package key and the share, so an old package key alone
        /// doesn't reveal a new release's file hashes, and with them its new blobs' keys.
        /// </summary>
        public static void WriteVault(string path, byte[] packageKey, IEnumerable<byte[]> fingerprints, List<Entry> entries, VaultLock lk, byte[] share)
        {
            using (var f = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(f))
            {
                var slots = new List<byte[]>();
                if (lk == null)
                {
                    w.Write(Magic);
                    foreach (byte[] fp in fingerprints) slots.Add(Seal(packageKey, fp, "slot"));
                }
                else
                {
                    w.Write(Magic3);
                    w.Write(lk.ReleaseId);
                    w.Write(lk.Url);
                    foreach (byte[] fp in fingerprints)
                    {
                        slots.Add(Seal(Encoding.ASCII.GetBytes(RetailOk), fp, "retail3"));
                        slots.Add(Seal(packageKey, Concat(fp, share), "slot3"));
                    }
                }
                w.Write(slots.Count);
                foreach (byte[] s in slots) { w.Write(s.Length); w.Write(s); }
                byte[] m = Seal(Encoding.UTF8.GetBytes(WriteManifest(entries)), lk == null ? packageKey : Derive(packageKey, share, "manifest3"), "manifest");
                w.Write(m.Length);
                w.Write(m);
            }
        }

        const string RetailOk = "btucu-retail-ok";

        /// <summary>Returns the package key, or null when no slot accepts this retail install.</summary>
        public static byte[] ReadVault(string path, byte[] fingerprint, out List<Entry> entries)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read))
                return ReadVault(f, fingerprint, out entries);
        }

        /// <summary>Reads the vault from any stream, read front to back (a zip entry works).</summary>
        public static byte[] ReadVault(Stream f, byte[] fingerprint, out List<Entry> entries)
        {
            return ReadVault(f, fingerprint, null, out entries);
        }

        /// <summary>
        /// As above; a v3 vault calls share once the retail install is accepted. share returns the release's share or
        /// throws UnlockException; without it a v3 vault throws UnlockException.
        /// </summary>
        public static byte[] ReadVault(Stream f, byte[] fingerprint, Func<VaultLock, byte[]> share, out List<Entry> entries)
        {
            entries = null;
            using (var r = new BinaryReader(f, Encoding.UTF8, true))
            {
                byte[] magic = r.ReadBytes(Magic.Length);
                bool v3 = Starts(magic, Magic3);
                if (!v3 && !Starts(magic, Magic)) throw new InvalidDataException("Not a package vault");
                VaultLock lk = v3 ? new VaultLock { ReleaseId = r.ReadBytes(16), Url = r.ReadString() } : null;
                byte[] key = null, slot3 = null;
                int slots = r.ReadInt32();
                for (int i = 0; i < slots; i += v3 ? 2 : 1)
                {
                    byte[] s = r.ReadBytes(r.ReadInt32());
                    if (!v3) { key = key ?? Open(s, fingerprint, "slot"); continue; }
                    byte[] sealedKey = r.ReadBytes(r.ReadInt32());
                    if (slot3 == null && Open(s, fingerprint, "retail3") != null) slot3 = sealedKey;
                }
                if (v3 && slot3 == null) return null;
                byte[] manifestKey = key;
                if (v3)
                {
                    if (share == null) throw new UnlockException("This package needs an access code.");
                    byte[] s = share(lk);
                    key = Open(slot3, Concat(fingerprint, s), "slot3");
                    if (key == null) throw new UnlockException("The unlock server's answer doesn't fit this release (release " + lk.Id + ").");
                    manifestKey = Derive(key, s, "manifest3");
                }
                if (key == null) return null;
                byte[] m = Open(r.ReadBytes(r.ReadInt32()), manifestKey, "manifest");
                if (m == null) throw new InvalidDataException("Package manifest failed its check");
                entries = ReadManifest(Encoding.UTF8.GetString(m));
                return key;
            }
        }

        static string WriteManifest(List<Entry> entries)
        {
            var s = new StringBuilder();
            foreach (Entry e in entries)
                s.Append(e.Path).Append('\t').Append(e.Size).Append('\t').Append(e.Blob).Append('\t')
                 .Append(e.BlobSha256).Append('\t').Append(e.RetailPath).Append('\t').Append(e.Sha256).Append('\n');
            return s.ToString();
        }

        static List<Entry> ReadManifest(string text)
        {
            var list = new List<Entry>();
            foreach (string line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                string[] c = line.Split('\t');
                list.Add(new Entry { Path = c[0], Size = long.Parse(c[1]), Blob = c[2], BlobSha256 = c[3], RetailPath = c[4], Sha256 = c[5] });
            }
            return list;
        }

        public static byte[] FileKey(byte[] packageKey, string blob, string purpose)
        {
            return Derive(packageKey, Encoding.UTF8.GetBytes(blob), purpose);
        }

        /// <summary>The blob a file is stored in. It depends only on the content key and the file's hash.</summary>
        public static string BlobFor(byte[] contentKey, string plainSha)
        {
            return "data/" + Hex(Derive(contentKey, Encoding.ASCII.GetBytes(plainSha), "blob-name")).Substring(0, 32) + ".pak";
        }

        public static byte[] BlobIv(byte[] contentKey, string plainSha)
        {
            var iv = new byte[16];
            Buffer.BlockCopy(Derive(contentKey, Encoding.ASCII.GetBytes(plainSha), "blob-iv"), 0, iv, 0, 16);
            return iv;
        }

        /// <summary>Finds the install through Steam's own library files, without needing Steam to be running.</summary>
        public static string FindRetail()
        {
            string steam = null;
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                if (k != null) steam = k.GetValue("SteamPath") as string;
            if (string.IsNullOrEmpty(steam)) return null;
            var libraries = new List<string> { steam };
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (string line in File.ReadAllLines(vdf))
                {
                    string v = VdfValue(line, "path");
                    if (v != null) libraries.Add(v);
                }
            foreach (string lib in libraries)
            {
                string acf = Path.Combine(lib, "steamapps", "appmanifest_" + AppId + ".acf");
                if (!File.Exists(acf)) continue;
                foreach (string line in File.ReadAllLines(acf))
                {
                    string v = VdfValue(line, "installdir");
                    if (v == null) continue;
                    string dir = Path.Combine(lib, "steamapps", "common", v);
                    if (Directory.Exists(dir)) return dir;
                }
            }
            return null;
        }

        static string VdfValue(string line, string key)
        {
            string[] p = line.Split('"');
            if (p.Length >= 4 && string.Equals(p[1], key, StringComparison.OrdinalIgnoreCase))
                return p[3].Replace(@"\\", @"\");
            return null;
        }
    }
}
