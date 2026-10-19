// Newly authored. Restamps the build target of a UnityFS asset bundle (format 6/7, stored or LZ4/LZ4HC blocks) so a
// player of another platform accepts it: the Windows player refuses the APKs' Android bundles ("Unknown error occurred
// while loading 'archive:/CAB-...'"). The result is an uncompressed format-6 bundle (UnityFsWriter) with the same nodes.
using System;
using System.Collections.Generic;
using System.Text;

namespace Tftf.Convert
{
    public static class BundleRetarget
    {
        public static byte[] Retarget(byte[] b, int platform)
        {
            if (b.Length < 8 || Encoding.ASCII.GetString(b, 0, 8) != "UnityFS\0") return b;   // not a bundle: nothing to restamp
            int p = 8;
            uint fmt = U32(b, ref p);
            CStr(b, ref p);
            string engine = CStr(b, ref p);
            p += 8;                                                     // total size
            int ci = (int)U32(b, ref p), ui = (int)U32(b, ref p);
            uint flags = U32(b, ref p);
            if (fmt >= 7) p = (p + 15) & ~15;
            int infoAt = (flags & 0x80) != 0 ? b.Length - ci : p;
            int dataAt = (flags & 0x80) != 0 ? p : p + ci;
            if ((flags & 0x200) != 0) dataAt = (dataAt + 15) & ~15;     // block-info padding (2019.4+ bundles)
            byte[] info = Unpack(b, infoAt, ci, ui, flags & 0x3f);

            int q = 16;                                                 // data hash
            int blocks = (int)U32(info, ref q);
            var bu = new int[blocks]; var bc = new int[blocks]; var bf = new int[blocks];
            long total = 0;
            for (int i = 0; i < blocks; i++)
            {
                bu[i] = (int)U32(info, ref q); bc[i] = (int)U32(info, ref q); bf[i] = (info[q] << 8 | info[q + 1]) & 0x3f; q += 2;
                total += bu[i];
            }
            var data = new byte[total];
            int src = dataAt, dst = 0;
            for (int i = 0; i < blocks; i++)
            {
                Buffer.BlockCopy(Unpack(b, src, bc[i], bu[i], (uint)bf[i]), 0, data, dst, bu[i]);
                src += bc[i]; dst += bu[i];
            }

            int count = (int)U32(info, ref q);
            var nodes = new List<FsNode>();
            for (int i = 0; i < count; i++)
            {
                long off = I64(info, ref q), size = I64(info, ref q);
                uint nf = U32(info, ref q);
                var node = new FsNode { Name = CStr(info, ref q), Flags = nf, Data = new byte[size] };
                Buffer.BlockCopy(data, (int)off, node.Data, 0, (int)size);
                if ((nf & 4) != 0) StampPlatform(node.Data, platform);
                nodes.Add(node);
            }
            return UnityFsWriter.Write(nodes, engine);
        }

        // SerializedFile header: big-endian sizes and version, endianness byte at 16, then the metadata (in that
        // endianness) starting with the engine version string and the target platform. Version 22 and later insert 64-bit
        // metadata size, file size and data offset fields first (28 bytes).
        static void StampPlatform(byte[] sf, int platform)
        {
            int p = 8;
            uint version = U32(sf, ref p);
            if (version < 9 || version > 23) throw new Exception("unsupported serialized file version " + version);
            bool little = sf[16] == 0;
            p = version >= 22 ? 48 : 20;
            CStr(sf, ref p);
            for (int i = 0; i < 4; i++) sf[p + i] = (byte)(platform >> (little ? 8 * i : 8 * (3 - i)));
        }

        static byte[] Unpack(byte[] b, int at, int csize, int usize, uint kind)
        {
            var o = new byte[usize];
            if (kind == 0) { Buffer.BlockCopy(b, at, o, 0, usize); return o; }
            if (kind != 2 && kind != 3) throw new Exception("unsupported bundle compression " + kind);
            int s = at, end = at + csize, d = 0;                        // LZ4 block format
            while (s < end)
            {
                int token = b[s++];
                int lit = token >> 4;
                if (lit == 15) { int x; do { x = b[s++]; lit += x; } while (x == 255); }
                Buffer.BlockCopy(b, s, o, d, lit); s += lit; d += lit;
                if (s >= end) break;
                int back = b[s] | b[s + 1] << 8; s += 2;
                int len = token & 15;
                if (len == 15) { int x; do { x = b[s++]; len += x; } while (x == 255); }
                len += 4;
                for (int k = 0; k < len; k++, d++) o[d] = o[d - back];
            }
            if (d != usize) throw new Exception("LZ4 block size mismatch");
            return o;
        }

        static string CStr(byte[] b, ref int p) { int s = p; while (b[p] != 0) p++; return Encoding.UTF8.GetString(b, s, p++ - s); }
        static uint U32(byte[] b, ref int p) { uint v = (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]); p += 4; return v; }
        static long I64(byte[] b, ref int p) { long hi = U32(b, ref p); return hi << 32 | U32(b, ref p); }
    }
}
