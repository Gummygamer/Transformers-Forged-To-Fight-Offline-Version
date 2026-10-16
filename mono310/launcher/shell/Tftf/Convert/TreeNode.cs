// Newly authored. Type-tree nodes, the database file written by launcher/convert/export_tree_db.py, and the
// type-tree blob format of serialized files (format 15).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Tftf.Convert
{
    public sealed class TreeNode
    {
        public short Version = 1;
        public byte Level;
        public byte TypeFlags;
        public string Type = "", Name = "";
        public int ByteSize = -1, Index, MetaFlag;
        public readonly List<TreeNode> Children = new List<TreeNode>();

        public const int AlignBytes = 0x4000;
        public bool Aligned { get { return (MetaFlag & AlignBytes) != 0; } }

        public IEnumerable<TreeNode> Flatten()
        {
            yield return this;
            foreach (var c in Children) foreach (var n in c.Flatten()) yield return n;
        }

        public TreeNode Child(string name)
        {
            foreach (var c in Children) if (c.Name == name) return c;
            return null;
        }
    }

    public sealed class TreeDb
    {
        public readonly Dictionary<int, TreeNode> Engine = new Dictionary<int, TreeNode>();
        public readonly Dictionary<string, TreeNode> Scripts = new Dictionary<string, TreeNode>();

        public static string ScriptKey(string asm, string ns, string cls) { return asm + "|" + ns + "|" + cls; }

        public static TreeDb Load(byte[] data)
        {
            var r = new ByteReader(data);
            if (Encoding.ASCII.GetString(r.Bytes(4)) != "TFTT") throw new InvalidDataException("not a tree database");
            r.I32();
            var db = new TreeDb();
            int ne = r.I32();
            for (int i = 0; i < ne; i++) { int cid = r.I32(); db.Engine[cid] = ReadTree(r); }
            int ns = r.I32();
            for (int i = 0; i < ns; i++)
            {
                string a = Str(r), n = Str(r), c = Str(r);
                db.Scripts[ScriptKey(a, n, c)] = ReadTree(r);
            }
            return db;
        }

        static string Str(ByteReader r) { int n = r.U16(); return Encoding.UTF8.GetString(r.Bytes(n)); }

        static TreeNode ReadTree(ByteReader r)
        {
            int count = r.I32();
            TreeNode root = null;
            var stack = new List<TreeNode>();
            for (int i = 0; i < count; i++)
            {
                var n = new TreeNode { Version = (short)r.I16(), Level = (byte)r.U8(), TypeFlags = (byte)r.U8() };
                n.Type = Str(r); n.Name = Str(r);
                n.ByteSize = r.I32(); n.Index = r.I32(); n.MetaFlag = r.I32();
                while (stack.Count > n.Level) stack.RemoveAt(stack.Count - 1);
                if (stack.Count > 0) stack[stack.Count - 1].Children.Add(n); else root = n;
                stack.Add(n);
            }
            return root;
        }
    }

    public static class TreeBlob
    {
        /// <summary>Serialized-file type-tree blob (format 15): node count, string buffer size, nodes, strings.</summary>
        public static void Write(ByteWriter w, TreeNode root)
        {
            var nodes = new List<TreeNode>(root.Flatten());
            var strings = new ByteWriter();
            var offsets = new Dictionary<string, int>();
            Func<string, int> off = s =>
            {
                int o;
                if (!offsets.TryGetValue(s, out o)) { o = strings.Length; strings.CStr(s); offsets[s] = o; }
                return o;
            };
            var nb = new ByteWriter();
            foreach (var n in nodes)
            {
                nb.I16(n.Version); nb.U8(n.Level); nb.U8(n.TypeFlags);
                nb.I32(off(n.Type)); nb.I32(off(n.Name));
                nb.I32(n.ByteSize); nb.I32(n.Index); nb.I32(n.MetaFlag);
            }
            w.I32(nodes.Count);
            w.I32(strings.Length);
            w.Bytes(nb.ToArray());
            w.Bytes(strings.ToArray());
        }
    }
}
