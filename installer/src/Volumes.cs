// Data parts: the package's data/ blobs bundled into a few uncompressed zip files, for hosts that cap file size or file
// count (GitHub Releases: 2 GiB per file, 1,000 files per release; a package has tens of thousands of blobs).
// The installer reads blobs straight out of parts that sit beside it, so players don't extract anything. The parts are
// ordinary zips, so extracting them all into the installer's folder works too.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace ReleaseGate
{
    public sealed class PartBlob
    {
        public string Zip;
        public long Offset;
        public long Length;      // bytes in the zip (compressed size when Deflated)
        public bool Deflated;
    }

    public static class Volumes
    {
        // Parts are named <prefix>-data-NN.zip. The installer only opens zips whose name starts with this.
        public const string PartPrefix = "BattleTechUCU";
        public const long GitHubFileLimit = 2147483648L;    // GitHub refuses release files of 2 GiB or more
        public const int GitHubAssetLimit = 1000;
        const ushort DosTime = 0, DosDate = (1 << 5) | 1;   // 1980-01-01 00:00, so the same package gives the same parts

        /// <summary>Writes every data/ blob of the package into parts of at most partBytes. Returns the part paths.</summary>
        public static List<string> WriteParts(string package, string outDir, string label, long partBytes, Action<long> done, CancellationToken stop)
        {
            string data = Path.Combine(package, "data");
            var blobs = Directory.EnumerateFiles(data, "*.pak").Select(f => new FileInfo(f)).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
            var groups = new List<List<FileInfo>>();
            List<FileInfo> cur = null;
            long size = 0;
            foreach (FileInfo f in blobs)
            {
                long cost = f.Length + 30 + 46 + 2 * ("data/" + f.Name).Length;
                if (cost + 22 > partBytes) throw new IOException(string.Format("{0} is {1:F2} GiB, more than one part holds", f.Name, f.Length / 1073741824.0));
                if (cur == null || size + cost + 22 > partBytes)
                {
                    groups.Add(cur = new List<FileInfo>());
                    size = 0;
                }
                cur.Add(f);
                size += cost;
            }
            Directory.CreateDirectory(outDir);
            var paths = new List<string>();
            for (int i = 0; i < groups.Count; i++)
            {
                string p = Path.Combine(outDir, string.Format("{0}-{1}-data-{2:D2}.zip", PartPrefix, label, i + 1));
                WriteZip(p, groups[i].Select(f => new KeyValuePair<string, string>("data/" + f.Name, f.FullName)), done, stop);
                paths.Add(p);
            }
            return paths;
        }

        static void WriteZip(string path, IEnumerable<KeyValuePair<string, string>> files, Action<long> done, CancellationToken stop)
        {
            var central = new MemoryStream();
            var cw = new BinaryWriter(central);
            int count = 0;
            string part = path + ".part";
            using (var o = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            using (var w = new BinaryWriter(o))
            {
                var buf = new byte[1 << 20];
                foreach (var kv in files)
                {
                    byte[] name = Encoding.ASCII.GetBytes(kv.Key);
                    long at = o.Position;
                    if (at > uint.MaxValue) throw new IOException("Part too large for a zip without Zip64");
                    w.Write(0x04034b50u); w.Write((ushort)10); w.Write((ushort)0); w.Write((ushort)0);
                    w.Write(DosTime); w.Write(DosDate);
                    long crcAt = o.Position;
                    w.Write(0u); w.Write(0u); w.Write(0u);
                    w.Write((ushort)name.Length); w.Write((ushort)0); w.Write(name);
                    uint crc = 0xffffffffu;
                    long len = 0;
                    using (var i = new FileStream(kv.Value, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                    {
                        int n;
                        while ((n = i.Read(buf, 0, buf.Length)) > 0)
                        {
                            stop.ThrowIfCancellationRequested();
                            crc = Crc32.Update(crc, buf, n);
                            o.Write(buf, 0, n);
                            len += n;
                            if (done != null) done(n);
                        }
                    }
                    crc ^= 0xffffffffu;
                    long end = o.Position;
                    o.Position = crcAt;
                    w.Write(crc); w.Write((uint)len); w.Write((uint)len);
                    o.Position = end;

                    cw.Write(0x02014b50u); cw.Write((ushort)20); cw.Write((ushort)10); cw.Write((ushort)0); cw.Write((ushort)0);
                    cw.Write(DosTime); cw.Write(DosDate);
                    cw.Write(crc); cw.Write((uint)len); cw.Write((uint)len);
                    cw.Write((ushort)name.Length); cw.Write((ushort)0); cw.Write((ushort)0); cw.Write((ushort)0); cw.Write((ushort)0);
                    cw.Write(0u); cw.Write((uint)at); cw.Write(name);
                    count++;
                }
                if (count > ushort.MaxValue) throw new IOException("Too many files for one part");
                long cdAt = o.Position;
                central.Position = 0;
                central.CopyTo(o);
                w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)count); w.Write((ushort)count);
                w.Write((uint)central.Length); w.Write((uint)cdAt); w.Write((ushort)0);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(part, path);
        }

        /// <summary>Finds every blob in the parts in a folder, by its package name (data/....pak).</summary>
        public static Dictionary<string, PartBlob> Index(string folder)
        {
            return Index(Directory.EnumerateFiles(folder, PartPrefix + "*.zip"));
        }

        /// <summary>
        /// Indexes the package files (data/....pak and package.vault) in these zips: our parts, or the zip itch.io serves
        /// for a website download. Entries may sit under a top folder, be stored or Deflate-compressed, and use Zip64.
        /// Zips that aren't readable, or hold no package files, add nothing.
        /// </summary>
        public static Dictionary<string, PartBlob> Index(IEnumerable<string> zips)
        {
            var found = new Dictionary<string, PartBlob>(StringComparer.OrdinalIgnoreCase);
            foreach (string zip in zips.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(z => z, StringComparer.OrdinalIgnoreCase))
            {
                try { IndexOne(zip, found); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException || e is ArgumentException) { }
            }
            return found;
        }

        /// <summary>The package name of a zip entry, or null: data/<blob>.pak or package.vault, under any top folder.</summary>
        internal static string PackageName(string entry)
        {
            entry = entry.Replace('\\', '/');
            int d = entry.StartsWith("data/", StringComparison.OrdinalIgnoreCase) ? 0 : entry.IndexOf("/data/", StringComparison.OrdinalIgnoreCase);
            if (d > 0) d++;
            if (d >= 0 &&entry.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) && entry.IndexOf('/', d + 5) < 0) return entry.Substring(d);
            if (string.Equals(Path.GetFileName(entry), Gate.VaultName, StringComparison.OrdinalIgnoreCase)) return Gate.VaultName;
            if (string.Equals(Path.GetFileName(entry), PublicManifest.FileName, StringComparison.OrdinalIgnoreCase)) return PublicManifest.FileName;
            return null;
        }

        static void IndexOne(string zip, Dictionary<string, PartBlob> found)
        {
            using (var f = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var r = new BinaryReader(f))
            {
                int tail = (int)Math.Min(f.Length, 22 + 65535);
                long tailAt = f.Length - tail;
                f.Position = tailAt;
                byte[] t = r.ReadBytes(tail);
                int eocd = -1;
                for (int i = t.Length - 22; i >= 0; i--)
                    if (BitConverter.ToUInt32(t, i) == 0x06054b50u) { eocd = i; break; }
                if (eocd < 0) throw new InvalidDataException("Not a zip");
                long count = BitConverter.ToUInt16(t, eocd + 10);
                long cdAt = BitConverter.ToUInt32(t, eocd + 16);
                if (count == 0xffff || cdAt == 0xffffffffL)
                {
                    // Zip64: the locator sits just before the end record and points at the Zip64 end record.
                    if (eocd < 20 || BitConverter.ToUInt32(t, eocd - 20) != 0x07064b50u) throw new InvalidDataException("Zip64 locator missing");
                    f.Position = BitConverter.ToInt64(t, eocd - 12);
                    if (r.ReadUInt32() != 0x06064b50u) throw new InvalidDataException("Bad Zip64 end record");
                    byte[] z = r.ReadBytes(52);
                    count = BitConverter.ToInt64(z, 28);
                    cdAt = BitConverter.ToInt64(z, 44);
                }
                f.Position = cdAt;
                var entries = new List<KeyValuePair<string, PartBlob>>();
                var locals = new List<long>();
                for (long i = 0; i < count; i++)
                {
                    if (r.ReadUInt32() != 0x02014b50u) throw new InvalidDataException("Bad zip directory");
                    byte[] h = r.ReadBytes(42);
                    int flags = BitConverter.ToUInt16(h, 4), method = BitConverter.ToUInt16(h, 6);
                    long csize = BitConverter.ToUInt32(h, 16), usize = BitConverter.ToUInt32(h, 20);
                    int n = BitConverter.ToUInt16(h, 24), x = BitConverter.ToUInt16(h, 26), c = BitConverter.ToUInt16(h, 28);
                    long local = BitConverter.ToUInt32(h, 38);
                    byte[] raw = r.ReadBytes(n);
                    string name = ((flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.ASCII).GetString(raw);
                    byte[] extra = r.ReadBytes(x);
                    f.Position += c;
                    // Zip64 extra field: 64-bit values for whichever of these were 0xffffffff, in this order.
                    for (int e = 0; e + 4 <= extra.Length; )
                    {
                        int id = BitConverter.ToUInt16(extra, e), len = BitConverter.ToUInt16(extra, e + 2), at = e + 4;
                        if (id == 1)
                        {
                            if (usize == 0xffffffffL && at + 8 <= e + 4 + len) { usize = BitConverter.ToInt64(extra, at); at += 8; }
                            if (csize == 0xffffffffL && at + 8 <= e + 4 + len) { csize = BitConverter.ToInt64(extra, at); at += 8; }
                            if (local == 0xffffffffL && at + 8 <= e + 4 + len) { local = BitConverter.ToInt64(extra, at); }
                        }
                        e += 4 + len;
                    }
                    string key = PackageName(name);
                    if (key == null || (flags & 1) != 0 || (method != 0 && method != 8) || (method == 0 && csize != usize)) continue;
                    entries.Add(new KeyValuePair<string, PartBlob>(key, new PartBlob { Zip = zip, Length = csize, Deflated = method == 8 }));
                    locals.Add(local);
                }
                for (int i = 0; i < entries.Count; i++)
                {
                    f.Position = locals[i];
                    if (r.ReadUInt32() != 0x04034b50u) continue;
                    byte[] h = r.ReadBytes(26);
                    entries[i].Value.Offset = locals[i] + 30 + BitConverter.ToUInt16(h, 22) + BitConverter.ToUInt16(h, 24);
                    found[entries[i].Key] = entries[i].Value;
                }
            }
        }

        public static Stream OpenBlob(PartBlob b)
        {
            var f = new FileStream(b.Zip, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            f.Position = b.Offset;
            Stream s = new Slice(f, b.Length);
            return b.Deflated ? new DeflateStream(s, CompressionMode.Decompress) : s;
        }

        /// <summary>A read-only window onto a stream; disposing it closes the stream.</summary>
        sealed class Slice : Stream
        {
            readonly Stream _s;
            long _left;
            public Slice(Stream s, long length) { _s = s; _left = length; }
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
                _left -= n;
                return n;
            }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { if (disposing) _s.Dispose(); base.Dispose(disposing); }
        }
    }

    static class Crc32
    {
        static readonly uint[] Table = Make();

        static uint[] Make()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        public static uint Update(uint crc, byte[] b, int n)
        {
            for (int i = 0; i < n; i++) crc = Table[(crc ^ b[i]) & 0xff] ^ (crc >> 8);
            return crc;
        }
    }
}
