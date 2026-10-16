// Newly authored. Minimal UnityFS (bundle format 6) writer: stored 128 KB blocks, any number of nodes.
using System;

namespace Tftf.Convert
{
    public sealed class FsNode
    {
        public string Name;
        public byte[] Data;
        public uint Flags;          // 4 = serialized file, 0 = raw resource
    }

    public static class UnityFsWriter
    {
        public static byte[] Write(System.Collections.Generic.List<FsNode> nodes, string engine = "5.3.5p8")
        {
            long total = 0;
            foreach (var n in nodes) total += n.Data.Length;
            var info = new ByteWriter();
            info.Bytes(new byte[16]);                                       // data hash, unused
            const int block = 0x20000;
            int blocks = (int)((total + block - 1) / block);
            if (blocks == 0) blocks = 1;
            info.U32BE((uint)blocks);
            long left = total;
            for (int i = 0; i < blocks; i++)
            {
                int n = (int)Math.Min(block, left);
                left -= n;
                info.U32BE((uint)n); info.U32BE((uint)n); info.U16BE(0);
            }
            info.U32BE((uint)nodes.Count);
            long off = 0;
            foreach (var n in nodes)
            {
                info.I64BE(off); info.I64BE(n.Data.Length); info.U32BE(n.Flags); info.CStr(n.Name);
                off += n.Data.Length;
            }
            var infoBytes = info.ToArray();
            var head = new ByteWriter();
            head.CStr("UnityFS");
            head.U32BE(6);
            head.CStr("5.x.x");
            head.CStr(engine);
            long size = head.Length + 8 + 4 + 4 + 4 + infoBytes.Length + total;
            var w = new ByteWriter();
            w.Bytes(head.ToArray());
            w.I64BE(size);
            w.U32BE((uint)infoBytes.Length);
            w.U32BE((uint)infoBytes.Length);
            w.U32BE(0x40);                                                  // directory info present, no compression
            w.Bytes(infoBytes);
            foreach (var n in nodes) w.Bytes(n.Data);
            return w.ToArray();
        }
    }
}
