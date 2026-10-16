// Newly authored. Walks serialized object bytes with a type tree: skips values, finds the byte range of a named
// field and reads simple fields. Alignment rules follow Unity's: a node with the align flag pads to 4 after its
// value; arrays of aligned elements pad once after the array.
using System;
using System.Text;

namespace Tftf.Convert
{
    public static class TypeWalker
    {
        static int PrimitiveSize(string type)
        {
            switch (type)
            {
                case "SInt8": case "UInt8": case "char": case "bool": return 1;
                case "short": case "SInt16": case "unsigned short": case "UInt16": return 2;
                case "int": case "SInt32": case "unsigned int": case "UInt32": case "Type*": case "float": return 4;
                case "long long": case "SInt64": case "unsigned long long": case "UInt64": case "FileSize": case "double": return 8;
                default: return 0;
            }
        }

        public static void Skip(TreeNode n, ByteReader r)
        {
            SkipBody(n, r);
            if (n.Aligned) r.Align(4);
        }

        static void SkipBody(TreeNode n, ByteReader r)
        {
            int p = PrimitiveSize(n.Type);
            if (p > 0) { r.Pos += p; return; }
            if (n.Type == "string") { int len = r.I32(); if (len > 0) { r.Pos += len; r.Align(4); } return; }
            if (n.Type == "TypelessData") { int len = r.I32(); r.Pos += len; return; }
            if (n.Type == "pair") { Skip(n.Children[0], r); Skip(n.Children[1], r); return; }
            if (n.Children.Count > 0 && n.Children[0].Type == "Array")
            {
                bool align = n.Children[0].Aligned;
                int size = r.I32();
                var sub = n.Children[0].Children[1];
                if (sub.Aligned)
                {
                    for (int i = 0; i < size; i++) SkipBody(sub, r);
                    r.Align(4);
                }
                else
                {
                    for (int i = 0; i < size; i++) Skip(sub, r);
                }
                if (align) r.Align(4);
                return;
            }
            foreach (var c in n.Children) Skip(c, r);
        }

        /// <summary>Byte range [start, end) of the field at the given path of child names below the root.</summary>
        public static bool FindRange(TreeNode root, byte[] data, out int start, out int end, params string[] path)
        {
            var r = new ByteReader(data);
            return Find(root, r, path, 0, out start, out end);
        }

        static bool Find(TreeNode n, ByteReader r, string[] path, int depth, out int start, out int end)
        {
            foreach (var c in n.Children)
            {
                if (c.Name == path[depth])
                {
                    if (depth == path.Length - 1) { start = r.Pos; Skip(c, r); end = r.Pos; return true; }
                    return Find(c, r, path, depth + 1, out start, out end);
                }
                Skip(c, r);
            }
            start = end = -1;
            return false;
        }

        public static string ReadString(TreeNode root, byte[] data, params string[] path)
        {
            int s, e;
            if (!FindRange(root, data, out s, out e, path)) return null;
            return new ByteReader(data, s).AlignedString();
        }

        public static bool ReadInt(TreeNode root, byte[] data, out int value, params string[] path)
        {
            int s, e;
            value = 0;
            if (!FindRange(root, data, out s, out e, path)) return false;
            value = new ByteReader(data, s).I32();
            return true;
        }

        public static bool ReadLong(TreeNode root, byte[] data, out long value, params string[] path)
        {
            int s, e;
            value = 0;
            if (!FindRange(root, data, out s, out e, path)) return false;
            value = new ByteReader(data, s).I64();
            return true;
        }

        public static byte[] Replace(byte[] data, int start, int end, byte[] with)
        {
            var w = new ByteWriter();
            w.Bytes(data, 0, start);
            w.Bytes(with);
            w.Bytes(data, end, data.Length - end);
            return w.ToArray();
        }
    }
}
