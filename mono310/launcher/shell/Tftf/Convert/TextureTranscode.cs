// Newly authored. The PC player cannot sample Android's ETC1 textures (TextureFormat.ETC_RGB4 = 34): it shows the raw
// blocks as noise. ETC1 and DXT1/BC1 (TextureFormat.DXT1 = 10) both store a 4x4 block in 8 bytes, so each block is
// decoded and re-encoded in place and only the format field changes: object layout, mip chain and stream offsets stay
// as they are. Works on Texture2D objects of serialized files with type trees (bundles, formats 15 to 22) and on the
// player data the converter reads with the tree database.
using System;
using System.Collections.Generic;
using System.Text;

namespace Tftf.Convert
{
    public static class TextureTranscode
    {
        public const int EtcRgb4 = 34, Dxt1 = 10;
        const int ClsTexture2D = 28;

        /// <summary>Set by the installer on Windows; the phone player samples ETC1 natively.</summary>
        public static bool EtcToBc1;

        // ---- Texture2D objects -------------------------------------------------------------------------------------

        /// <summary>Transcodes one Texture2D object (tree = its type tree) in place. Streamed pixels are found through
        /// resource(path), which returns the mutable bytes of the stream (or null). Returns true if it changed.</summary>
        public static bool Transcode(TreeNode tree, byte[] obj, int objStart, int objLength, Func<string, byte[]> resource)
        {
            var data = new byte[objLength];
            Buffer.BlockCopy(obj, objStart, data, 0, objLength);
            int fs, fe;
            if (!TypeWalker.FindRange(tree, data, out fs, out fe, "m_TextureFormat")) return false;
            if (BitConverter.ToInt32(data, fs) != EtcRgb4) return false;
            int s, e;
            if (!TypeWalker.FindRange(tree, data, out s, out e, "image data")) return false;
            int inline = BitConverter.ToInt32(data, s);
            if (inline > 0) Etc1ToBc1(data, s + 4, inline);
            else
            {
                int os, oe, ss, se;
                string path = TypeWalker.ReadString(tree, data, "m_StreamData", "path");
                if (string.IsNullOrEmpty(path) || !TypeWalker.FindRange(tree, data, out os, out oe, "m_StreamData", "offset")
                    || !TypeWalker.FindRange(tree, data, out ss, out se, "m_StreamData", "size")) return false;
                long offset = oe - os == 8 ? BitConverter.ToInt64(data, os) : BitConverter.ToUInt32(data, os);
                int size = (int)BitConverter.ToUInt32(data, ss);
                var stream = resource(path);
                if (stream == null || offset + size > stream.Length) return false;
                Etc1ToBc1(stream, (int)offset, size);
            }
            data[fs] = Dxt1; data[fs + 1] = 0; data[fs + 2] = 0; data[fs + 3] = 0;
            Buffer.BlockCopy(data, 0, obj, objStart, objLength);
            return true;
        }

        /// <summary>Transcodes every ETC1 Texture2D of a serialized file (in place). nodes maps bundle node names to
        /// their bytes, for textures streamed into the bundle's .resS node. Returns the number of textures changed.</summary>
        public static int TranscodeSerializedFile(byte[] sf, Dictionary<string, byte[]> nodes)
        {
            int p = 8;
            int version = (int)U32BE(sf, ref p);
            if (version < 15 || version > 22 || sf[16] != 0) return 0;     // little-endian metadata only
            long dataOffset;
            if (version >= 22) { p = 32; dataOffset = (long)U32BE(sf, ref p) << 32; dataOffset |= U32BE(sf, ref p); p = 48; }
            else { p = 12; dataOffset = U32BE(sf, ref p); p = 20; }
            var r = new ByteReader(sf, p);
            r.CStr(); r.I32();
            if (r.U8() == 0) return 0;                                     // no type trees: nothing to walk
            int typeCount = r.I32();
            var classOf = new int[typeCount];
            var trees = new TreeNode[typeCount];
            for (int i = 0; i < typeCount; i++)
            {
                int cid = r.I32();
                classOf[i] = cid;
                if (version >= 16) r.U8();
                int scriptIndex = version >= 17 ? r.I16() : -1;
                if ((version < 16 && cid < 0) || (version >= 16 && (cid == 114 || scriptIndex >= 0))) r.Bytes(16);
                r.Bytes(16);
                trees[i] = ReadTree(r, version >= 19 ? 32 : 24);
                if (version >= 21) { int deps = r.I32(); r.Pos += 4 * deps; }
            }
            int count = r.I32(), changed = 0;
            Func<string, byte[]> resource = path =>
            {
                byte[] b;
                int slash = path.LastIndexOf('/');
                return nodes.TryGetValue(slash < 0 ? path : path.Substring(slash + 1), out b) ? b : null;
            };
            for (int i = 0; i < count; i++)
            {
                r.Align(4);
                r.I64();
                long start = version >= 22 ? r.I64() : r.U32();
                int size = (int)r.U32();
                int typeId = r.I32();
                int cls = typeId;
                if (version < 16) { cls = r.U16(); r.I16(); r.U8(); }      // classId, script type index, stripped
                TreeNode tree = null;
                if (version >= 16) { if (typeId < 0 || typeId >= typeCount) continue; cls = classOf[typeId]; tree = trees[typeId]; }
                else for (int t = 0; t < typeCount; t++) if (classOf[t] == typeId) tree = trees[t];
                if (cls != ClsTexture2D || tree == null) continue;
                if (Transcode(tree, sf, (int)(dataOffset + start), size, resource)) changed++;
            }
            return changed;
        }

        // Type-tree blob of a serialized file: node records (24 bytes; 32 from format 19) and a string buffer. Offsets
        // with the high bit set index Unity's built-in common strings.
        static TreeNode ReadTree(ByteReader r, int recordSize)
        {
            int count = r.I32(), strSize = r.I32();
            int nodesAt = r.Pos, strAt = nodesAt + count * recordSize;
            TreeNode root = null;
            var stack = new List<TreeNode>();
            for (int i = 0; i < count; i++)
            {
                var nr = new ByteReader(r.Data, nodesAt + i * recordSize);
                var n = new TreeNode { Version = (short)nr.I16(), Level = (byte)nr.U8(), TypeFlags = (byte)nr.U8() };
                int to = nr.I32(), no = nr.I32();
                n.ByteSize = nr.I32(); n.Index = nr.I32(); n.MetaFlag = nr.I32();
                n.Type = Str(r.Data, strAt, to); n.Name = Str(r.Data, strAt, no);
                while (stack.Count > n.Level) stack.RemoveAt(stack.Count - 1);
                if (stack.Count > 0) stack[stack.Count - 1].Children.Add(n); else root = n;
                stack.Add(n);
            }
            r.Pos = strAt + strSize;
            return root;
        }

        static string Str(byte[] d, int strAt, int off)
        {
            if (off < 0) return Common((off & 0x7FFFFFFF));
            int s = strAt + off, e = s;
            while (d[e] != 0) e++;
            return Encoding.UTF8.GetString(d, s, e - s);
        }

        // Unity's built-in type-tree string table (names at their byte offsets).
        const string CommonStrings =
            "AABB\0AnimationClip\0AnimationCurve\0AnimationState\0Array\0Base\0BitField\0bitset\0bool\0char\0" +
            "ColorRGBA\0Component\0data\0deque\0double\0dynamic_array\0FastPropertyName\0first\0float\0Font\0" +
            "GameObject\0Generic Mono\0GradientNEW\0GUID\0GUIStyle\0int\0list\0long long\0map\0Matrix4x4f\0" +
            "MdFour\0MonoBehaviour\0MonoScript\0m_ByteSize\0m_Curve\0m_EditorClassIdentifier\0m_EditorHideFlags\0" +
            "m_Enabled\0m_ExtensionPtr\0m_GameObject\0m_Index\0m_IsArray\0m_IsStatic\0m_MetaFlag\0m_Name\0" +
            "m_ObjectHideFlags\0m_PrefabInternal\0m_PrefabParentObject\0m_Script\0m_StaticEditorFlags\0m_Type\0" +
            "m_Version\0Object\0pair\0PPtr<Component>\0PPtr<GameObject>\0PPtr<Material>\0PPtr<MonoBehaviour>\0" +
            "PPtr<MonoScript>\0PPtr<Object>\0PPtr<Prefab>\0PPtr<Sprite>\0PPtr<TextAsset>\0PPtr<Texture>\0" +
            "PPtr<Texture2D>\0PPtr<Transform>\0Prefab\0Quaternionf\0Rectf\0RectInt\0RectOffset\0second\0set\0" +
            "short\0size\0SInt16\0SInt32\0SInt64\0SInt8\0staticvector\0string\0TextAsset\0TextMesh\0Texture\0" +
            "Texture2D\0Transform\0TypelessData\0UInt16\0UInt32\0UInt64\0UInt8\0unsigned int\0" +
            "unsigned long long\0unsigned short\0vector\0Vector2f\0Vector3f\0Vector4f\0" +
            "m_ScriptingClassIdentifier\0Gradient\0Type*\0int2_storage\0int3_storage\0BoundsInt\0" +
            "m_CorrespondingSourceObject\0m_PrefabInstance\0m_PrefabAsset\0FileSize\0";

        static string Common(int off)
        {
            if (off >= CommonStrings.Length) return "?common" + off;
            int e = CommonStrings.IndexOf('\0', off);
            return CommonStrings.Substring(off, e - off);
        }

        static uint U32BE(byte[] b, ref int p) { uint v = (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]); p += 4; return v; }

        // ---- ETC1 -> BC1 -------------------------------------------------------------------------------------------

        static readonly int[,] EtcModifiers =
        {
            { 2, 8, -2, -8 }, { 5, 17, -5, -17 }, { 9, 29, -9, -29 }, { 13, 42, -13, -42 },
            { 18, 60, -18, -60 }, { 24, 80, -24, -80 }, { 33, 106, -33, -106 }, { 47, 183, -47, -183 },
        };

        /// <summary>Rewrites length bytes of ETC1 blocks at offset as BC1 blocks (8 bytes each, same pixel order).</summary>
        public static void Etc1ToBc1(byte[] d, int offset, int length)
        {
            var px = new int[16 * 3];
            for (int b = offset; b + 8 <= offset + length; b += 8)
            {
                DecodeEtc1(d, b, px);
                EncodeBc1(px, d, b);
            }
        }

        // Pixel i = y * 4 + x, RGB in px[3i..3i+2].
        static void DecodeEtc1(byte[] d, int b, int[] px)
        {
            int r1, g1, b1, r2, g2, b2;
            bool diff = (d[b + 3] & 2) != 0, flip = (d[b + 3] & 1) != 0;
            if (diff)
            {
                int rr = d[b] >> 3, gg = d[b + 1] >> 3, bb = d[b + 2] >> 3;
                int dr = (d[b] & 7) << 29 >> 29, dg = (d[b + 1] & 7) << 29 >> 29, db = (d[b + 2] & 7) << 29 >> 29;
                r1 = Ext5(rr); g1 = Ext5(gg); b1 = Ext5(bb);
                r2 = Ext5(rr + dr); g2 = Ext5(gg + dg); b2 = Ext5(bb + db);
            }
            else
            {
                r1 = Ext4(d[b] >> 4); r2 = Ext4(d[b] & 15);
                g1 = Ext4(d[b + 1] >> 4); g2 = Ext4(d[b + 1] & 15);
                b1 = Ext4(d[b + 2] >> 4); b2 = Ext4(d[b + 2] & 15);
            }
            int cw1 = d[b + 3] >> 5, cw2 = (d[b + 3] >> 2) & 7;
            int msb = d[b + 4] << 8 | d[b + 5], lsb = d[b + 6] << 8 | d[b + 7];
            for (int x = 0; x < 4; x++)
                for (int y = 0; y < 4; y++)
                {
                    int i = x * 4 + y;                                             // ETC pixel indices run down columns
                    int idx = ((msb >> i) & 1) << 1 | ((lsb >> i) & 1);
                    bool second = flip ? y >= 2 : x >= 2;
                    int m = EtcModifiers[second ? cw2 : cw1, idx];
                    int o = (y * 4 + x) * 3;
                    px[o] = Clamp((second ? r2 : r1) + m);
                    px[o + 1] = Clamp((second ? g2 : g1) + m);
                    px[o + 2] = Clamp((second ? b2 : b1) + m);
                }
        }

        static int Ext4(int v) { return v << 4 | v; }
        static int Ext5(int v) { return v << 3 | v >> 2; }
        static int Clamp(int v) { return v < 0 ? 0 : v > 255 ? 255 : v; }

        // Bounding-box endpoints inset by 1/16, four-colour mode, nearest palette entry per pixel.
        static void EncodeBc1(int[] px, byte[] d, int b)
        {
            int[] lo = { 255, 255, 255 }, hi = { 0, 0, 0 };
            for (int i = 0; i < 16; i++)
                for (int c = 0; c < 3; c++) { int v = px[i * 3 + c]; if (v < lo[c]) lo[c] = v; if (v > hi[c]) hi[c] = v; }
            for (int c = 0; c < 3; c++) { int inset = (hi[c] - lo[c]) >> 4; lo[c] += inset; hi[c] -= inset; }
            int c0 = To565(hi), c1 = To565(lo);
            if (c0 < c1) { int t = c0; c0 = c1; c1 = t; }
            uint indices = 0;
            if (c0 != c1)
            {
                var pal = new int[12];
                From565(c0, pal, 0); From565(c1, pal, 3);
                for (int c = 0; c < 3; c++) { pal[6 + c] = (2 * pal[c] + pal[3 + c]) / 3; pal[9 + c] = (pal[c] + 2 * pal[3 + c]) / 3; }
                for (int i = 15; i >= 0; i--)
                {
                    int best = 0, bestDist = int.MaxValue;
                    for (int k = 0; k < 4; k++)
                    {
                        int dr = px[i * 3] - pal[k * 3], dg = px[i * 3 + 1] - pal[k * 3 + 1], dbl = px[i * 3 + 2] - pal[k * 3 + 2];
                        int dist = dr * dr + dg * dg + dbl * dbl;
                        if (dist < bestDist) { bestDist = dist; best = k; }
                    }
                    indices = indices << 2 | (uint)best;
                }
            }
            d[b] = (byte)c0; d[b + 1] = (byte)(c0 >> 8); d[b + 2] = (byte)c1; d[b + 3] = (byte)(c1 >> 8);
            d[b + 4] = (byte)indices; d[b + 5] = (byte)(indices >> 8); d[b + 6] = (byte)(indices >> 16); d[b + 7] = (byte)(indices >> 24);
        }

        static int To565(int[] c) { return (c[0] * 31 + 127) / 255 << 11 | (c[1] * 63 + 127) / 255 << 5 | (c[2] * 31 + 127) / 255; }

        static void From565(int v, int[] o, int at)
        {
            int r = v >> 11 & 31, g = v >> 5 & 63, bl = v & 31;
            o[at] = r << 3 | r >> 2; o[at + 1] = g << 2 | g >> 4; o[at + 2] = bl << 3 | bl >> 2;
        }
    }
}
