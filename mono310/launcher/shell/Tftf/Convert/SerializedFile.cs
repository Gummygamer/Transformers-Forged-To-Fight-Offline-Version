// Newly authored. Reader and writer for Unity serialized files of format 15 (Unity 5.3), the format of the original
// player data and of the 5.3.5 asset bundles. Objects keep their raw bytes; the converter edits them in place.
using System;
using System.Collections.Generic;
using System.IO;

namespace Tftf.Convert
{
    public sealed class SerType
    {
        public int ClassId;                 // negative for MonoBehaviour types (one per script)
        public byte[] ScriptId;             // 16 bytes when ClassId < 0
        public byte[] OldTypeHash;          // 16 bytes
        public TreeNode Node;
    }

    public sealed class SerObject
    {
        public long PathId;
        public int TypeId;                  // == ClassId of the type entry in format 15
        public int ClassId;
        public int ScriptTypeIndex;
        public int Stripped;
        public byte[] Data;
        public int Index;                   // position among the file's objects, for lookups
    }

    public sealed class ScriptRef
    {
        public int FileIndex;
        public long PathId;
    }

    public sealed class External
    {
        public string TempEmpty = "";
        public byte[] Guid = new byte[16];
        public int Type;
        public string Path = "";
    }

    public sealed class SerializedFile
    {
        public string UnityVersion = "5.3.5p8";
        public int Platform = 13;
        public bool HasTypeTree;
        public readonly List<SerType> Types = new List<SerType>();
        public readonly List<SerObject> Objects = new List<SerObject>();
        public readonly List<ScriptRef> ScriptTypes = new List<ScriptRef>();
        public readonly List<External> Externals = new List<External>();
        public string UserInfo = "";
        public int Version;

        public SerType TypeOf(SerObject o)
        {
            foreach (var t in Types) if (t.ClassId == o.TypeId) return t;
            return null;
        }

        public SerObject Find(long pathId)
        {
            foreach (var o in Objects) if (o.PathId == pathId) return o;
            return null;
        }

        public static SerializedFile Read(byte[] data)
        {
            var r = new ByteReader(data);
            uint metaSize = r.U32BE(), fileSize = r.U32BE();
            var f = new SerializedFile { Version = (int)r.U32BE() };
            uint dataOffset = r.U32BE();
            if (f.Version != 15) throw new NotSupportedException("serialized file format " + f.Version);
            int bigEndian = r.U8();
            if (bigEndian != 0) throw new NotSupportedException("big-endian serialized file");
            r.Bytes(3);
            f.UnityVersion = r.CStr();
            f.Platform = r.I32();
            f.HasTypeTree = r.U8() != 0;
            int nt = r.I32();
            for (int i = 0; i < nt; i++)
            {
                var t = new SerType { ClassId = r.I32() };
                if (t.ClassId < 0) t.ScriptId = r.Bytes(16);
                t.OldTypeHash = r.Bytes(16);
                if (f.HasTypeTree) t.Node = ReadBlob(r);
                f.Types.Add(t);
            }
            int no = r.I32();
            for (int i = 0; i < no; i++)
            {
                r.Align(4);
                var o = new SerObject { PathId = r.I64(), Index = i };
                uint start = r.U32(), size = r.U32();
                o.TypeId = r.I32();
                o.ClassId = r.U16();
                o.ScriptTypeIndex = r.I16();
                o.Stripped = r.U8();
                o.Data = new byte[size];
                Buffer.BlockCopy(data, (int)(dataOffset + start), o.Data, 0, (int)size);
                f.Objects.Add(o);
            }
            int ns = r.I32();
            for (int i = 0; i < ns; i++)
            {
                var s = new ScriptRef { FileIndex = r.I32() };
                r.Align(4);
                s.PathId = r.I64();
                f.ScriptTypes.Add(s);
            }
            int ne = r.I32();
            for (int i = 0; i < ne; i++)
                f.Externals.Add(new External { TempEmpty = r.CStr(), Guid = r.Bytes(16), Type = r.I32(), Path = r.CStr() });
            f.UserInfo = r.CStr();
            return f;
        }

        static TreeNode ReadBlob(ByteReader r)
        {
            int count = r.I32(), strSize = r.I32();
            int nodesStart = r.Pos;
            r.Pos += count * 24;
            int strStart = r.Pos;
            r.Pos += strSize;
            var nodes = new List<TreeNode>();
            var stack = new List<TreeNode>();
            TreeNode root = null;
            int p = nodesStart;
            var nr = new ByteReader(r.Data, p);
            for (int i = 0; i < count; i++)
            {
                var n = new TreeNode { Version = (short)nr.I16(), Level = (byte)nr.U8(), TypeFlags = (byte)nr.U8() };
                int to = nr.I32(), no = nr.I32();
                n.ByteSize = nr.I32(); n.Index = nr.I32(); n.MetaFlag = nr.I32();
                n.Type = BlobString(r.Data, strStart, to);
                n.Name = BlobString(r.Data, strStart, no);
                while (stack.Count > n.Level) stack.RemoveAt(stack.Count - 1);
                if (stack.Count > 0) stack[stack.Count - 1].Children.Add(n); else root = n;
                stack.Add(n);
            }
            return root;
        }

        static string BlobString(byte[] d, int start, int off)
        {
            if ((off & unchecked((int)0x80000000)) != 0) return "?common" + (off & 0x7FFFFFFF);   // original files carry none
            int s = start + off, e = s;
            while (d[e] != 0) e++;
            return System.Text.Encoding.UTF8.GetString(d, s, e - s);
        }

        public byte[] Write()
        {
            var meta = new ByteWriter();
            meta.CStr(UnityVersion);
            meta.I32(Platform);
            meta.U8(HasTypeTree ? 1 : 0);
            meta.I32(Types.Count);
            foreach (var t in Types)
            {
                meta.I32(t.ClassId);
                if (t.ClassId < 0) meta.Bytes(t.ScriptId);
                meta.Bytes(t.OldTypeHash);
                if (HasTypeTree) TreeBlob.Write(meta, t.Node);
            }
            var objs = new List<SerObject>(Objects);
            objs.Sort((a, b) => a.PathId.CompareTo(b.PathId));
            var data = new ByteWriter();
            meta.I32(objs.Count);
            foreach (var o in objs)
            {
                meta.Align(4);
                meta.I64(o.PathId);
                meta.I32(data.Length);
                meta.I32(o.Data.Length);
                meta.I32(o.TypeId);
                meta.I16(o.ClassId);
                meta.I16(o.ScriptTypeIndex);
                meta.U8(o.Stripped);
                data.Bytes(o.Data);
                data.Align(8);
            }
            meta.I32(ScriptTypes.Count);
            foreach (var s in ScriptTypes) { meta.I32(s.FileIndex); meta.Align(4); meta.I64(s.PathId); }
            meta.I32(Externals.Count);
            foreach (var e in Externals) { meta.CStr(e.TempEmpty); meta.Bytes(e.Guid); meta.I32(e.Type); meta.CStr(e.Path); }
            meta.CStr(UserInfo);

            int metaSize = meta.Length;
            int dataOffset = 20 + metaSize;
            dataOffset += (16 - dataOffset % 16) % 16;
            var w = new ByteWriter();
            w.U32BE((uint)metaSize);
            w.U32BE((uint)(dataOffset + data.Length));
            w.U32BE(15);
            w.U32BE((uint)dataOffset);
            w.U8(0);
            w.Bytes(new byte[3]);
            w.Bytes(meta.ToArray());
            w.Align(16);
            w.Bytes(data.ToArray());
            return w.ToArray();
        }
    }
}
