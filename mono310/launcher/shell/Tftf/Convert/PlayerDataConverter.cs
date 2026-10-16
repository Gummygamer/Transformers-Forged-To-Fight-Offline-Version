// Newly authored. Converts the player data of the original 3.1.0 APK (Unity 5.3.5: serialized files without type
// trees) into one AssetBundle that a Unity 2020 player loads. See launcher/convert/convert_player_data.py, the
// prototype this is a port of, and mono310/ANDROID_LAUNCHER.md for the findings behind each step.
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Tftf.Convert
{
    public interface IPlayerDataSource
    {
        /// <summary>Contents of a file of assets/bin/Data, or null if absent.</summary>
        byte[] Read(string name);
    }

    public sealed class ConvertStats
    {
        public int Files, MissingScript, DroppedShaders, Materials, Audio, StreamedTextures, Roots, Resources;
        public override string ToString()
        {
            return "files=" + Files + " resources=" + Resources + " sceneRoots=" + Roots + " missingScript=" + MissingScript +
                   " droppedShaders=" + DroppedShaders + " materials=" + Materials + " audio=" + Audio +
                   " streamedTextures=" + StreamedTextures;
        }
    }

    public static class PlayerDataConverter
    {
        public const string ShaderMark = "TFTF_SHADER=";
        const int StreamTexturesOver = 64 * 1024;
        const int ClsGameObject = 1, ClsTransform = 4, ClsMaterial = 21, ClsTexture2D = 28, ClsShader = 48, ClsAudioClip = 83,
                  ClsMonoBehaviour = 114, ClsMonoScript = 115, ClsAssetBundle = 142, ClsResourceManager = 147, ClsCgProgram = 109;
        // Type hash of the AssetBundle class in 5.3.5 bundles (taken from a genuine bundle).
        static readonly byte[] AssetBundleTypeHash = HexToBytes("f284abb6f2df17ebedae2b6b50436810");

        static byte[] HexToBytes(string h)
        {
            var b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = System.Convert.ToByte(h.Substring(i * 2, 2), 16);
            return b;
        }

        static string Cab(string name)
        {
            using (var md5 = MD5.Create())
            {
                var h = md5.ComputeHash(Encoding.UTF8.GetBytes(name));
                var sb = new StringBuilder("CAB-");
                foreach (var x in h) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        static string Base(string path) { int i = path.LastIndexOf('/'); return i < 0 ? path : path.Substring(i + 1); }

        sealed class Entry { public string Path, File; public long PathId; }

        public static byte[] Convert(IPlayerDataSource src, TreeDb db, ConvertStats stats, Action<string> log)
        {
            log = log ?? (s => { });
            // ---- Resources path map
            var ggm = SerializedFile.Read(src.Read("globalgamemanagers"));
            var rmObj = ggm.Objects.Find(o => o.ClassId == ClsResourceManager);
            var rmTree = db.Engine[ClsResourceManager];
            int cs, ce;
            TypeWalker.FindRange(rmTree, rmObj.Data, out cs, out ce, "m_Container");
            var cr = new ByteReader(rmObj.Data, cs);
            int count = cr.I32();
            var container = new List<Entry>();
            for (int i = 0; i < count; i++)
            {
                string p = cr.AlignedString();
                int fid = cr.I32();
                long pid = cr.I64();
                if (fid > 0) container.Add(new Entry { Path = p, File = Base(ggm.Externals[fid - 1].Path), PathId = pid });
            }
            stats.Resources = container.Count;

            var nameSet = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            foreach (var c in container) nameSet[c.File] = true;
            nameSet["globalgamemanagers.assets"] = true; nameSet["sharedassets0.assets"] = true; nameSet["level0"] = true;
            var names = new List<string>(nameSet.Keys);
            var loaded = new Dictionary<string, SerializedFile>();
            foreach (var n in names) loaded[n] = SerializedFile.Read(src.Read(n));
            stats.Files = names.Count;

            string mainNode = Cab("main");
            var nodeOf = new Dictionary<string, string>();
            foreach (var n in names) nodeOf[n] = Cab(n);

            // ---- boot scene roots become loose objects the loader instantiates
            var lv = loaded["level0"];
            foreach (var o in lv.Objects)
            {
                if (o.ClassId != ClsTransform) continue;
                long father, go;
                TypeWalker.ReadLong(db.Engine[ClsTransform], o.Data, out father, "m_Father", "m_PathID");
                if (father != 0) continue;
                TypeWalker.ReadLong(db.Engine[ClsTransform], o.Data, out go, "m_GameObject", "m_PathID");
                var goObj = lv.Find(go);
                string nm = TypeWalker.ReadString(db.Engine[ClsGameObject], goObj.Data, "m_Name") ?? "";
                container.Add(new Entry { Path = "scene/level0/" + go + "/" + nm, File = "level0", PathId = go });
                stats.Roots++;
            }

            // ---- shader names for materials (the declared name is what Shader.Find uses)
            var shaderName = new Dictionary<string, string>();
            var declared = new Regex("^\\s*Shader\\s+\"([^\"]+)\"");
            foreach (var n in names)
                foreach (var o in loaded[n].Objects)
                {
                    if (o.ClassId != ClsShader) continue;
                    string script = TypeWalker.ReadString(db.Engine[ClsShader], o.Data, "m_Script") ?? "";
                    var m = declared.Match(script);
                    shaderName[n + "|" + o.PathId] = m.Success ? m.Groups[1].Value : (TypeWalker.ReadString(db.Engine[ClsShader], o.Data, "m_Name") ?? "");
                }

            // ---- streamed resource node: audio keeps its offsets, big textures are appended
            var resBlob = new ByteWriter();
            for (int i = 0; ; i++)
            {
                var part = src.Read("sharedassets0.resource.split" + i);
                if (part == null) break;
                resBlob.Bytes(part);
            }
            string resNode = Cab("sharedassets0.resource") + ".resource";
            string resPath = "archive:/" + mainNode + "/" + resNode;

            var scriptKeys = new Dictionary<string, string>();
            Func<SerializedFile, string, int, long, string> scriptKey = (sf, fileName, fileIdx, pid) =>
            {
                var owner = fileIdx == 0 ? sf : loaded[Base(sf.Externals[fileIdx - 1].Path)];
                string cacheKey = (fileIdx == 0 ? fileName : Base(sf.Externals[fileIdx - 1].Path)) + "|" + pid;
                string k;
                if (!scriptKeys.TryGetValue(cacheKey, out k))
                {
                    var so = owner.Find(pid);
                    var t = db.Engine[ClsMonoScript];
                    k = TreeDb.ScriptKey(TypeWalker.ReadString(t, so.Data, "m_AssemblyName"),
                                         TypeWalker.ReadString(t, so.Data, "m_Namespace"),
                                         TypeWalker.ReadString(t, so.Data, "m_ClassName"));
                    scriptKeys[cacheKey] = k;
                }
                return k;
            };

            var fsNodes = new List<FsNode>();
            foreach (var n in names)
            {
                var sf = loaded[n];
                sf.HasTypeTree = true;
                foreach (var t in sf.Types)
                {
                    if (t.ClassId < 0)
                    {
                        string key = null;
                        var first = sf.Objects.Find(o => o.TypeId == t.ClassId);
                        if (first != null && first.ScriptTypeIndex >= 0 && first.ScriptTypeIndex < sf.ScriptTypes.Count)
                        {
                            var sr = sf.ScriptTypes[first.ScriptTypeIndex];
                            key = scriptKey(sf, n, sr.FileIndex, sr.PathId);
                        }
                        TreeNode tree;
                        if (key != null && db.Scripts.TryGetValue(key, out tree)) t.Node = tree;
                        else { t.Node = db.Engine[ClsMonoBehaviour]; stats.MissingScript++; }
                    }
                    else t.Node = db.Engine[t.ClassId];
                }
                var kept = new List<SerObject>();
                foreach (var o in sf.Objects)
                {
                    if (o.ClassId == ClsShader || o.ClassId == ClsCgProgram) { stats.DroppedShaders++; continue; }
                    kept.Add(o);
                    if (o.ClassId == ClsMaterial) FixMaterial(sf, n, o, db, shaderName, stats);
                    else if (o.ClassId == ClsAudioClip) FixAudio(o, db, resPath, stats);
                    else if (o.ClassId == ClsTexture2D) FixTexture(o, db, resBlob, resPath, stats);
                }
                sf.Objects.Clear();
                sf.Objects.AddRange(kept);
                foreach (var e in sf.Externals)
                {
                    string b = Base(e.Path);
                    if (nodeOf.ContainsKey(b)) { e.Path = ("archive:/" + mainNode + "/" + nodeOf[b]).ToLowerInvariant(); e.Guid = new byte[16]; e.Type = 0; }
                    else if (e.Path.ToLowerInvariant().EndsWith("unity_builtin_extra") || e.Path == "0000000000000000f000000000000000")
                    { e.Path = "resources/unity_builtin_extra"; e.Guid = HexToBytes("00000000000000000f00000000000000"); e.Type = 0; }
                    else if (e.Path.ToLowerInvariant().Contains("unity default resources"))
                    { e.Path = "library/unity default resources"; e.Guid = HexToBytes("00000000000000000e00000000000000"); e.Type = 0; }
                }
                fsNodes.Add(new FsNode { Name = nodeOf[n], Data = sf.Write(), Flags = 4 });
                log("converted " + n);
                if (n != "globalgamemanagers.assets" && n != "sharedassets0.assets")
                    loaded[n] = null;       // the converted file is serialized; free the parsed copy (scripts live in the two shared files)
            }

            // ---- main node: AssetBundle object with the Resources container
            var idOf = new Dictionary<string, int>();
            for (int i = 0; i < names.Count; i++) idOf[names[i]] = i + 1;
            var ab = new ByteWriter();
            ab.AlignedString("tftf/resources");
            ab.I32(0);                                                       // m_PreloadTable
            ab.I32(container.Count);
            foreach (var c in container)
            {
                ab.AlignedString(c.Path.ToLowerInvariant());
                ab.I32(0); ab.I32(0);                                        // preloadIndex, preloadSize
                ab.I32(idOf[c.File]); ab.I64(c.PathId);
            }
            ab.I32(0); ab.I32(0); ab.I32(0); ab.I64(0);                      // m_MainAsset
            ab.I32(1);                                                       // m_RuntimeCompatibility
            ab.AlignedString("tftf/resources");
            ab.I32(0);                                                       // m_Dependencies
            ab.U8(0); ab.Align(4);                                           // m_IsStreamedSceneAssetBundle

            var main = new SerializedFile { HasTypeTree = true };
            main.Types.Add(new SerType { ClassId = ClsAssetBundle, OldTypeHash = AssetBundleTypeHash, Node = db.Engine[ClsAssetBundle] });
            main.Objects.Add(new SerObject { PathId = 1, TypeId = ClsAssetBundle, ClassId = ClsAssetBundle, ScriptTypeIndex = -1, Data = ab.ToArray() });
            foreach (var n in names)
                main.Externals.Add(new External { Path = ("archive:/" + mainNode + "/" + nodeOf[n]).ToLowerInvariant() });

            var all = new List<FsNode> { new FsNode { Name = mainNode, Data = main.Write(), Flags = 4 } };
            all.AddRange(fsNodes);
            all.Add(new FsNode { Name = resNode, Data = resBlob.ToArray(), Flags = 0 });
            return UnityFsWriter.Write(all);
        }

        static void FixMaterial(SerializedFile sf, string file, SerObject o, TreeDb db, Dictionary<string, string> shaderName, ConvertStats stats)
        {
            var tree = db.Engine[ClsMaterial];
            int fid; long pid;
            if (!TypeWalker.ReadInt(tree, o.Data, out fid, "m_Shader", "m_FileID")) return;
            TypeWalker.ReadLong(tree, o.Data, out pid, "m_Shader", "m_PathID");
            string owner = fid == 0 ? file : Base(sf.Externals[fid - 1].Path);
            string nm;
            if (!shaderName.TryGetValue(owner + "|" + pid, out nm) || string.IsNullOrEmpty(nm)) return;
            int s, e;
            TypeWalker.FindRange(tree, o.Data, out s, out e, "m_ShaderKeywords");
            string kw = new ByteReader(o.Data, s).AlignedString().Trim();
            var w = new ByteWriter();
            w.AlignedString((kw + " " + ShaderMark + nm).Trim());
            o.Data = TypeWalker.Replace(o.Data, s, e, w.ToArray());
            stats.Materials++;
        }

        static void FixAudio(SerObject o, TreeDb db, string resPath, ConvertStats stats)
        {
            var tree = db.Engine[ClsAudioClip];
            string src = TypeWalker.ReadString(tree, o.Data, "m_Resource", "m_Source");
            if (src == null || !src.EndsWith("sharedassets0.resource", StringComparison.Ordinal)) return;
            int s, e;
            TypeWalker.FindRange(tree, o.Data, out s, out e, "m_Resource", "m_Source");
            var w = new ByteWriter();
            w.AlignedString(resPath);
            o.Data = TypeWalker.Replace(o.Data, s, e, w.ToArray());
            stats.Audio++;
        }

        static void FixTexture(SerObject o, TreeDb db, ByteWriter resBlob, string resPath, ConvertStats stats)
        {
            var tree = db.Engine[ClsTexture2D];
            int s, e;
            if (!TypeWalker.FindRange(tree, o.Data, out s, out e, "image data")) return;
            var r = new ByteReader(o.Data, s);
            int size = r.I32();
            if (size < StreamTexturesOver) return;
            string path = TypeWalker.ReadString(tree, o.Data, "m_StreamData", "path");
            if (!string.IsNullOrEmpty(path)) return;
            long offset = resBlob.Length;
            resBlob.Bytes(o.Data, s + 4, size);
            var w = new ByteWriter();
            w.I32(0);                                                        // image data is now empty
            w.I32((int)offset); w.I32(size); w.AlignedString(resPath);       // m_StreamData
            o.Data = TypeWalker.Replace(o.Data, s, o.Data.Length, w.ToArray());
            stats.StreamedTextures++;
        }
    }
}
