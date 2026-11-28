// Newly authored. Lets the Windows player draw the donor (9.2.0) shaders with OpenGL Core. The APK's bundles carry their
// shader programs for GLES2 and GLES3 only, so on Direct3D every donor material needs a stand-in. The GLES3 programs are
// GLSL ES 3.00 text that compiles unchanged as desktop GLSL 4.30 once the version line says so, so this rewrites each
// Shader object of a 2020.3 bundle (serialized format 22) on the user's machine:
//   - platform GLES3 (9) becomes OpenGLCore (15) in the shader's platform lists;
//   - GLES3/GLES31 program types (4, 3, 2) become GLCore43 (8), in the parsed form and in each blob subprogram;
//   - "#version 300 es" / "#version 31x es" / "#version 320 es" becomes "#version 430" / "#version 450";
//   - the platform's blob segments are recompressed (LZ4) and the object and file re-laid out with the new sizes.
// Nothing else in the program (parameters, bindings, keywords) changes. Other platforms are copied as they are.
using System;
using System.Collections.Generic;
using System.Text;

namespace Tftf.Convert
{
    public static class ShaderGlCore
    {
        /// <summary>Set by the installer on Windows.</summary>
        public static bool Enabled;
        public static int Converted;

        const int ClsShader = 48, PlatGles3 = 9, PlatGlCore = 15, TypeGlCore43 = 8, SubProgramVersion = 201806140;

        static bool IsGles3Type(int t) { return t == 2 || t == 3 || t == 4; }

        // ---- serialized file ---------------------------------------------------------------------------------------

        /// <summary>Rewrites every Shader object of a format-22 serialized file; returns the file unchanged when there is
        /// nothing to do.</summary>
        public static byte[] ConvertSerializedFile(byte[] sf)
        {
            int p = 8;
            int version = (int)U32BE(sf, ref p);
            if (version != 22 || sf[16] != 0) return sf;
            p = 32;
            long dataOffset = (long)U32BE(sf, ref p) << 32; dataOffset |= U32BE(sf, ref p);
            var r = new ByteReader(sf, 48);
            r.CStr(); r.I32();
            if (r.U8() == 0) return sf;
            int typeCount = r.I32();
            var classOf = new int[typeCount];
            var trees = new TreeNode[typeCount];
            for (int i = 0; i < typeCount; i++)
            {
                int cid = r.I32();
                classOf[i] = cid;
                r.U8();
                int scriptIndex = r.I16();
                if (cid == 114 || scriptIndex >= 0) r.Bytes(16);
                r.Bytes(16);
                trees[i] = TextureTranscode.ReadTree(r, 32);
                int deps = r.I32(); r.Pos += 4 * deps;
            }
            int count = r.I32();
            var entryAt = new int[count];
            var start = new long[count];
            var size = new int[count];
            var replaced = new byte[count][];
            bool any = false;
            for (int i = 0; i < count; i++)
            {
                r.Align(4);
                entryAt[i] = r.Pos;
                r.I64();
                start[i] = r.I64();
                size[i] = (int)r.U32();
                int typeId = r.I32();
                if (typeId < 0 || typeId >= typeCount || classOf[typeId] != ClsShader || trees[typeId] == null) continue;
                var obj = new byte[size[i]];
                Buffer.BlockCopy(sf, (int)(dataOffset + start[i]), obj, 0, size[i]);
                byte[] nb = ConvertShader(trees[typeId], obj);
                if (nb != null) { replaced[i] = nb; any = true; Converted++; }
            }
            if (!any) return sf;

            // re-lay the data section: objects in their original order, each at an 8-byte boundary
            var order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;
            Array.Sort(order, (a, b) => start[a].CompareTo(start[b]));
            var w = new ByteWriter();
            w.Bytes(sf, 0, (int)dataOffset);
            var newStart = new long[count];
            var newSize = new int[count];
            foreach (int i in order)
            {
                while ((w.Length - dataOffset) % 8 != 0) w.U8(0);
                newStart[i] = w.Length - dataOffset;
                if (replaced[i] != null) { w.Bytes(replaced[i]); newSize[i] = replaced[i].Length; }
                else { w.Bytes(sf, (int)(dataOffset + start[i]), size[i]); newSize[i] = size[i]; }
            }
            var o = w.ToArray();
            for (int i = 0; i < count; i++)
            {
                PutI64(o, entryAt[i] + 8, newStart[i]);
                PutI32(o, entryAt[i] + 16, newSize[i]);
            }
            PutI64BE(o, 24, o.Length);                                   // 64-bit file size of format 22
            return o;
        }

        // ---- one Shader object -------------------------------------------------------------------------------------

        sealed class Leaf { public string Path; public int Pos; }

        static byte[] ConvertShader(TreeNode tree, byte[] obj)
        {
            var leaves = new List<Leaf>();
            var rd = new ByteReader(obj);
            foreach (var c in tree.Children) Visit(c, "/" + c.Name, rd, leaves);

            var platforms = new List<int>(); var platformAt = new List<int>();
            var offs = new List<int>(); var clens = new List<int>(); var dlens = new List<int>();
            foreach (var l in leaves)
            {
                if (l.Path == "/platforms/Array/data") { platforms.Add(BitConverter.ToInt32(obj, l.Pos)); platformAt.Add(l.Pos); }
            }
            int pi = platforms.IndexOf(PlatGles3);
            if (pi < 0 || platforms.Contains(PlatGlCore)) return null;

            // segment tables: one inner list per platform
            var segOff = Tables(tree, obj, "offsets"); var segC = Tables(tree, obj, "compressedLengths"); var segD = Tables(tree, obj, "decompressedLengths");
            if (segOff == null || segC == null || segD == null || segOff.Count != platforms.Count) return null;
            int bs, be;
            if (!TypeWalker.FindRange(tree, obj, out bs, out be, "compressedBlob")) return null;
            int blobLen = BitConverter.ToInt32(obj, bs);
            int blobAt = bs + 4;

            // decompress the GLES3 segments and rewrite their subprograms
            int nseg = segOff[pi].Values.Count;
            var raw = new byte[nseg][];
            for (int s = 0; s < nseg; s++)
                raw[s] = Lz4.Decompress(obj, blobAt + segOff[pi].Values[s], segC[pi].Values[s], segD[pi].Values[s]);
            byte[][] newRaw = RewriteSegments(raw);
            if (newRaw == null) return null;

            // new blob: every platform's segments in order, the GLES3 ones recompressed
            var blob = new ByteWriter();
            var nOff = new List<int>[platforms.Count]; var nC = new List<int>[platforms.Count]; var nD = new List<int>[platforms.Count];
            for (int q = 0; q < platforms.Count; q++)
            {
                nOff[q] = new List<int>(); nC[q] = new List<int>(); nD[q] = new List<int>();
                for (int s = 0; s < segOff[q].Values.Count; s++)
                {
                    nOff[q].Add(blob.Length);
                    if (q == pi)
                    {
                        byte[] c = Lz4.Compress(newRaw[s]);
                        blob.Bytes(c); nC[q].Add(c.Length); nD[q].Add(newRaw[s].Length);
                    }
                    else
                    {
                        blob.Bytes(obj, blobAt + segOff[q].Values[s], segC[q].Values[s]);
                        nC[q].Add(segC[q].Values[s]); nD[q].Add(segD[q].Values[s]);
                    }
                }
            }

            // fixed-size patches first (they all sit before the blob), then the blob itself
            var o = (byte[])obj.Clone();
            PutI32(o, platformAt[pi], PlatGlCore);
            foreach (var l in leaves)
            {
                if (l.Path.EndsWith("/m_Passes/Array/data/m_Platforms/Array/data", StringComparison.Ordinal) && o[l.Pos] == PlatGles3) o[l.Pos] = PlatGlCore;
                else if (l.Path.EndsWith("/m_SubPrograms/Array/data/m_GpuProgramType", StringComparison.Ordinal) && IsGles3Type((sbyte)o[l.Pos])) o[l.Pos] = TypeGlCore43;
            }
            for (int q = 0; q < platforms.Count; q++)
                for (int s = 0; s < nOff[q].Count; s++)
                {
                    PutI32(o, segOff[q].At[s], nOff[q][s]);
                    PutI32(o, segC[q].At[s], nC[q][s]);
                    PutI32(o, segD[q].At[s], nD[q][s]);
                }
            var nb = new ByteWriter();
            byte[] blobBytes = blob.ToArray();
            nb.I32(blobBytes.Length);
            nb.Bytes(blobBytes);
            while ((bs + nb.Length) % 4 != 0) nb.U8(0);                 // compressedBlob is an aligned array
            int oldEnd = blobAt + blobLen;
            while (oldEnd % 4 != 0) oldEnd++;
            return TypeWalker.Replace(o, bs, oldEnd, nb.ToArray());
        }

        sealed class Table { public readonly List<int> Values = new List<int>(), At = new List<int>(); }

        // vector<vector<unsigned int>>: values and their byte positions, one Table per outer element
        static List<Table> Tables(TreeNode tree, byte[] obj, string name)
        {
            int s, e;
            if (!TypeWalker.FindRange(tree, obj, out s, out e, name)) return null;
            var r = new ByteReader(obj, s);
            int n = r.I32();
            var list = new List<Table>();
            for (int i = 0; i < n; i++)
            {
                var t = new Table();
                int m = r.I32();
                for (int j = 0; j < m; j++) { t.At.Add(r.Pos); t.Values.Add(r.I32()); }
                list.Add(t);
            }
            return list;
        }

        // Records the position of every primitive leaf (by path; array elements as "/Array/data"). Same alignment rules
        // as TypeWalker.
        static void Visit(TreeNode n, string path, ByteReader r, List<Leaf> leaves)
        {
            VisitBody(n, path, r, leaves);
            if (n.Aligned) r.Align(4);
        }

        static void VisitBody(TreeNode n, string path, ByteReader r, List<Leaf> leaves)
        {
            int ps = PrimitiveSize(n.Type);
            if (ps > 0) { leaves.Add(new Leaf { Path = path, Pos = r.Pos }); r.Pos += ps; return; }
            if (n.Type == "string") { int len = r.I32(); if (len > 0) { r.Pos += len; r.Align(4); } return; }
            if (n.Type == "TypelessData") { int len = r.I32(); r.Pos += len; return; }
            if (n.Children.Count > 0 && n.Children[0].Type == "Array")
            {
                var arr = n.Children[0];
                int size = r.I32();
                var sub = arr.Children[1];
                string sp = path + "/Array/data";
                int psub = PrimitiveSize(sub.Type);
                if (psub > 0 && sub.Children.Count == 0 && !NeedsLeaves(sp)) r.Pos += psub * size;   // byte blobs: skip fast
                else if (sub.Aligned) { for (int i = 0; i < size; i++) VisitBody(sub, sp, r, leaves); r.Align(4); }
                else for (int i = 0; i < size; i++) Visit(sub, sp, r, leaves);
                if (arr.Aligned) r.Align(4);
                return;
            }
            foreach (var c in n.Children) Visit(c, path + "/" + c.Name, r, leaves);
        }

        static bool NeedsLeaves(string arrayPath)
        {
            return arrayPath == "/platforms/Array/data" || arrayPath.EndsWith("/m_Platforms/Array/data", StringComparison.Ordinal);
        }

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

        // ---- blob segments -----------------------------------------------------------------------------------------

        // Segment 0 starts with the subprogram table: count, then (offset, length, segment) per subprogram (2019.3+).
        // Every subprogram is rewritten; each segment keeps its own bytes between and around them.
        static byte[][] RewriteSegments(byte[][] raw)
        {
            var hr = new ByteReader(raw[0]);
            int n = hr.I32();
            var eo = new int[n]; var el = new int[n]; var es = new int[n];
            for (int i = 0; i < n; i++) { eo[i] = hr.I32(); el[i] = hr.I32(); es[i] = hr.I32(); if (es[i] < 0 || es[i] >= raw.Length) return null; }
            var outSeg = new byte[raw.Length][];
            var no = new int[n]; var nl = new int[n];
            for (int s = 0; s < raw.Length; s++)
            {
                var idx = new List<int>();
                for (int i = 0; i < n; i++) if (es[i] == s) idx.Add(i);
                idx.Sort((a, b) => eo[a].CompareTo(eo[b]));
                var w = new ByteWriter();
                int at = 0;
                foreach (int i in idx)
                {
                    if (eo[i] < at) return null;                               // overlapping entries: leave the shader alone
                    w.Bytes(raw[s], at, eo[i] - at);
                    byte[] sp = RewriteSubProgram(raw[s], eo[i], el[i]);
                    if (sp == null) return null;
                    no[i] = w.Length; nl[i] = sp.Length;
                    w.Bytes(sp);
                    at = eo[i] + el[i];
                }
                w.Bytes(raw[s], at, raw[s].Length - at);
                outSeg[s] = w.ToArray();
            }
            for (int i = 0; i < n; i++) { PutI32(outSeg[0], 4 + 12 * i, no[i]); PutI32(outSeg[0], 8 + 12 * i, nl[i]); }
            return outSeg;
        }

        // version, program type, 12 bytes of statistics, 4 more (5.5+), global keywords, local keywords, program code
        // (int32 length + bytes, aligned to 4), then parameters and bindings, copied unchanged.
        static byte[] RewriteSubProgram(byte[] d, int at, int len)
        {
            var r = new ByteReader(d, at);
            int ver = r.I32();
            int type = r.I32();
            if (ver != SubProgramVersion) return null;
            r.Pos += 16;
            for (int list = 0; list < 2; list++)
            {
                int k = r.I32();
                for (int i = 0; i < k; i++) { int sl = r.I32(); r.Pos += sl; r.Align(4); }
            }
            int codeLenAt = r.Pos;
            int codeLen = r.I32();
            int codeAt = r.Pos;
            int tailAt = codeAt + codeLen;
            int rel = tailAt - at;
            while (rel % 4 != 0) { tailAt++; rel++; }
            if (tailAt > at + len) return null;
            byte[] code = new byte[codeLen];
            Buffer.BlockCopy(d, codeAt, code, 0, codeLen);
            if (IsGles3Type(type) && codeLen > 0) code = RewriteGlsl(code);
            var w = new ByteWriter();
            w.Bytes(d, at, codeLenAt - at);
            w.I32(code.Length);
            w.Bytes(code);
            w.Align(4);
            w.Bytes(d, tailAt, at + len - tailAt);
            var o = w.ToArray();
            if (IsGles3Type(type)) PutI32(o, 4, TypeGlCore43);
            return o;
        }

        static byte[] RewriteGlsl(byte[] code)
        {
            string s = Encoding.UTF8.GetString(code);
            s = s.Replace("#version 300 es", "#version 430")
                 .Replace("#version 310 es", "#version 430")
                 .Replace("#version 320 es", "#version 450");
            return Encoding.UTF8.GetBytes(s);
        }

        // ---- little helpers ----------------------------------------------------------------------------------------

        static uint U32BE(byte[] b, ref int p) { uint v = (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]); p += 4; return v; }
        static void PutI32(byte[] b, int p, int v) { b[p] = (byte)v; b[p + 1] = (byte)(v >> 8); b[p + 2] = (byte)(v >> 16); b[p + 3] = (byte)(v >> 24); }
        static void PutI64(byte[] b, int p, long v) { PutI32(b, p, (int)v); PutI32(b, p + 4, (int)(v >> 32)); }
        static void PutI64BE(byte[] b, int p, long v) { for (int i = 0; i < 8; i++) b[p + i] = (byte)(v >> (56 - 8 * i)); }
    }

    /// <summary>LZ4 block format: a greedy compressor (hash of 4-byte sequences, 64 KiB window) and the decoder.</summary>
    public static class Lz4
    {
        public static byte[] Decompress(byte[] b, int at, int csize, int usize)
        {
            var o = new byte[usize];
            int s = at, end = at + csize, d = 0;
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

        const int MinMatch = 4, LastLiterals = 5, MfLimit = 12, HashBits = 16;

        public static byte[] Compress(byte[] src)
        {
            var w = new ByteWriter();
            int n = src.Length, anchor = 0, i = 0;
            var table = new int[1 << HashBits];
            for (int k = 0; k < table.Length; k++) table[k] = -1;
            int matchLimit = n - LastLiterals;
            while (n >= MfLimit && i < n - MfLimit)
            {
                uint seq = (uint)(src[i] | src[i + 1] << 8 | src[i + 2] << 16 | src[i + 3] << 24);
                int h = (int)((seq * 2654435761u) >> (32 - HashBits));
                int cand = table[h];
                table[h] = i;
                if (cand < 0 || i - cand > 65535 || src[cand] != src[i] || src[cand + 1] != src[i + 1] || src[cand + 2] != src[i + 2] || src[cand + 3] != src[i + 3]) { i++; continue; }
                int len = MinMatch;
                while (i + len < matchLimit && src[cand + len] == src[i + len]) len++;
                Emit(w, src, anchor, i - anchor, i - cand, len);
                i += len;
                anchor = i;
            }
            // last literals
            int litLen = n - anchor;
            w.U8((Math.Min(litLen, 15)) << 4);
            if (litLen >= 15) WriteLen(w, litLen - 15);
            w.Bytes(src, anchor, litLen);
            return w.ToArray();
        }

        static void Emit(ByteWriter w, byte[] src, int litAt, int litLen, int offset, int matchLen)
        {
            int ml = matchLen - MinMatch;
            w.U8(Math.Min(litLen, 15) << 4 | Math.Min(ml, 15));
            if (litLen >= 15) WriteLen(w, litLen - 15);
            w.Bytes(src, litAt, litLen);
            w.U8(offset); w.U8(offset >> 8);
            if (ml >= 15) WriteLen(w, ml - 15);
        }

        static void WriteLen(ByteWriter w, int v)
        {
            while (v >= 255) { w.U8(255); v -= 255; }
            w.U8(v);
        }
    }
}
