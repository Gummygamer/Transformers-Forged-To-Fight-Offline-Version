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
        public int Files, MissingScript, DroppedShaders, Materials, Audio, StreamedTextures, Roots, Resources, Preload, Endpoints;
        public override string ToString()
        {
            return "files=" + Files + " resources=" + Resources + " sceneRoots=" + Roots + " missingScript=" + MissingScript +
                   " droppedShaders=" + DroppedShaders + " materials=" + Materials + " audio=" + Audio +
                   " streamedTextures=" + StreamedTextures + " preload=" + Preload + " endpoints=" + Endpoints;
        }
    }

    public static class PlayerDataConverter
    {
        public const string ShaderMark = "TFTF_SHADER=";
        const int StreamTexturesOver = 64 * 1024;
        const int ClsGameObject = 1, ClsTransform = 4, ClsMaterial = 21, ClsTexture2D = 28, ClsShader = 48, ClsAudioClip = 83,
                  ClsTextAsset = 49, ClsMonoBehaviour = 114, ClsMonoScript = 115, ClsAssetBundle = 142, ClsResourceManager = 147, ClsCgProgram = 109;
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

        public static byte[] Convert(IPlayerDataSource src, TreeDb db, ConvertStats stats, Action<string> log, string serverUrl = "http://127.0.0.1:8080")
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
            var loaded = new Dictionary<string, SerializedFile>();
            // Include every file reachable through external references: prefabs keep their materials and atlases in
            // files the Resources map never names.
            var pending = new Queue<string>(nameSet.Keys);
            while (pending.Count > 0)
            {
                string n = pending.Dequeue();
                if (loaded.ContainsKey(n)) continue;
                var bytes = src.Read(n);
                if (bytes == null) continue;
                var sfile = SerializedFile.Read(bytes);
                loaded[n] = sfile;
                foreach (var e in sfile.Externals)
                {
                    string b = Base(e.Path);
                    if (!loaded.ContainsKey(b) && !nameSet.ContainsKey(b) && b != "unity_builtin_extra" && !b.StartsWith("0000", StringComparison.Ordinal))
                        if (src.Read(b) != null) { nameSet[b] = true; pending.Enqueue(b); }
                }
            }
            var names = new List<string>(loaded.Keys);
            names.Sort(StringComparer.Ordinal);
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
            // Shaders with no readable name (baked in the 5.3 player data) are still known by their Resources path:
            // its last segment (ui/shaders/hud_health -> hud_health) is what ShaderFallback matches against Shader310/.
            foreach (var c in container)
            {
                string key = c.File + "|" + c.PathId, nm;
                if (shaderName.TryGetValue(key, out nm) && string.IsNullOrEmpty(nm))
                    shaderName[key] = c.Path.Substring(c.Path.LastIndexOf('/') + 1);
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

            var idOfName = new Dictionary<string, int>();
            for (int i = 0; i < names.Count; i++) idOfName[names[i]] = i;
            var deps = new Dictionary<long, Ref[]>[names.Count];
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
                var myDeps = new Dictionary<long, Ref[]>();
                deps[idOfName[n]] = myDeps;
                foreach (var o in sf.Objects)
                {
                    if (o.ClassId == ClsCgProgram) { stats.DroppedShaders++; continue; }
                    if (o.ClassId == ClsShader)
                    {
                        // Components keep their Shader references (UITexture.mShader decides blend vs opaque): keep a
                        // named stub; the game resolves the real shader by name.
                        string stubName;
                        if (!shaderName.TryGetValue(n + "|" + o.PathId, out stubName) || string.IsNullOrEmpty(stubName)) { stats.DroppedShaders++; log("nameless shader " + n + "|" + o.PathId + " script=" + (TypeWalker.ReadString(db.Engine[ClsShader], o.Data, "m_PathName") ?? "")); continue; }
                        StubShader(o, db, stubName);
                    }
                    kept.Add(o);
                    myDeps[o.PathId] = CollectDeps(sf, n, idOfName, o, db);
                    if (o.ClassId == ClsMaterial) FixMaterial(sf, n, o, db, shaderName, stats, log);
                    else if (o.ClassId == ClsAudioClip) FixAudio(o, db, resPath, stats);
                    else if (o.ClassId == ClsTextAsset) FixEndpoint(o, db, serverUrl, stats);
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
            // Each asset's preload range lists everything it needs, dependencies first; the player loads a range
            // before the asset, which is what links behaviours to their scripts.
            var preload = new List<Ref>();
            var ranges = new int[container.Count][];
            for (int ci = 0; ci < container.Count; ci++)
            {
                int start = preload.Count;
                EmitClosure(deps, idOfName[container[ci].File], container[ci].PathId, preload);
                ranges[ci] = new[] { start, preload.Count - start };
            }
            stats.Preload = preload.Count;
            var ab = new ByteWriter();
            ab.AlignedString("tftf/resources");
            ab.I32(preload.Count);                                           // m_PreloadTable
            foreach (var pr in preload) { ab.I32(pr.File + 1); ab.I64(pr.Path); }
            ab.I32(container.Count);
            for (int ci = 0; ci < container.Count; ci++)
            {
                var c = container[ci];
                ab.AlignedString(c.Path.ToLowerInvariant());
                ab.I32(ranges[ci][0]); ab.I32(ranges[ci][1]);                // preloadIndex, preloadSize
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

        struct Ref { public int File; public long Path; }

        static Ref[] CollectDeps(SerializedFile sf, string file, Dictionary<string, int> idOfName, SerObject o, TreeDb db)
        {
            var list = new List<Ref>();
            TreeNode tree = null;
            foreach (var t in sf.Types) if (t.ClassId == o.TypeId) { tree = t.Node; break; }
            if (tree == null) return list.ToArray();
            int self = idOfName[file];
            TypeWalker.CollectPPtrs(tree, new ByteReader(o.Data), (fid, pid) =>
            {
                if (pid == 0) return;
                int gf;
                if (fid == 0) gf = self;
                else if (fid - 1 < sf.Externals.Count && idOfName.TryGetValue(Base(sf.Externals[fid - 1].Path), out gf)) { }
                else return;                                  // builtin resources are not part of the bundle
                list.Add(new Ref { File = gf, Path = pid });
            });
            return list.ToArray();
        }

        /// <summary>Post-order walk of the dependency graph: every object appears after the objects it needs.</summary>
        static void EmitClosure(Dictionary<long, Ref[]>[] deps, int file, long path, List<Ref> output)
        {
            var seen = new HashSet<long>();
            var stack = new Stack<KeyValuePair<Ref, int>>();
            Func<Ref, long> key = r => ((long)r.File << 48) ^ r.Path;
            var root = new Ref { File = file, Path = path };
            seen.Add(key(root));
            stack.Push(new KeyValuePair<Ref, int>(root, 0));
            while (stack.Count > 0)
            {
                var top = stack.Pop();
                Ref[] d;
                if (deps[top.Key.File] == null || !deps[top.Key.File].TryGetValue(top.Key.Path, out d)) d = new Ref[0];
                if (top.Value < d.Length)
                {
                    stack.Push(new KeyValuePair<Ref, int>(top.Key, top.Value + 1));
                    var next = d[top.Value];
                    if (seen.Add(key(next))) stack.Push(new KeyValuePair<Ref, int>(next, 0));
                }
                else output.Add(top.Key);
            }
        }

        static void StubShader(SerObject o, TreeDb db, string name)
        {
            var w = new ByteWriter();
            w.AlignedString(name);
            w.AlignedString("Shader \"" + name + "\" { SubShader { Pass { } } }");
            w.AlignedString("");
            w.I32(0);          // decompressedSize
            w.I32(0);          // m_SubProgramBlob
            w.I32(0);          // m_Dependencies
            w.U8(0); w.U8(0); w.U8(0); w.U8(0);   // m_ShaderIsBaked, padded
            o.Data = w.ToArray();
        }

        static void FixMaterial(SerializedFile sf, string file, SerObject o, TreeDb db, Dictionary<string, string> shaderName, ConvertStats stats, Action<string> log = null)
        {
            var tree = db.Engine[ClsMaterial];
            int fid; long pid;
            if (!TypeWalker.ReadInt(tree, o.Data, out fid, "m_Shader", "m_FileID")) return;
            TypeWalker.ReadLong(tree, o.Data, out pid, "m_Shader", "m_PathID");
            string owner = fid == 0 ? file : Base(sf.Externals[fid - 1].Path);
            string nm;
            if (!shaderName.TryGetValue(owner + "|" + pid, out nm) || string.IsNullOrEmpty(nm))
            {
                // Font materials point at the engine's built-in text shader (id 10101 in the 5.3 default resources).
                if (owner == "unity default resources" && pid == 10101) nm = "GUI/Text Shader";
                else { if (log != null) log("material without shader name: " + (TypeWalker.ReadString(tree, o.Data, "m_Name") ?? "?") + " -> " + owner + "|" + pid); return; }
            }
            if (log != null && (TypeWalker.ReadString(tree, o.Data, "m_Name") ?? "").IndexOf("health", StringComparison.OrdinalIgnoreCase) >= 0) log("health material: " + TypeWalker.ReadString(tree, o.Data, "m_Name") + " shader=" + nm);
            int s, e;
            TypeWalker.FindRange(tree, o.Data, out s, out e, "m_ShaderKeywords");
            string kw = new ByteReader(o.Data, s).AlignedString().Trim();
            var w = new ByteWriter();
            w.AlignedString((kw + " " + ShaderMark + nm).Trim());
            o.Data = TypeWalker.Replace(o.Data, s, e, w.ToArray());
            stats.Materials++;
        }

        // The game reads its server address from a baked "endpointconfig" text asset; point it at the local server.
        static void FixEndpoint(SerObject o, TreeDb db, string url, ConvertStats stats)
        {
            var tree = db.Engine[ClsTextAsset];
            string script = TypeWalker.ReadString(tree, o.Data, "m_Script");
            if (script == null || script.IndexOf("\"Default\"", StringComparison.Ordinal) < 0 || script.IndexOf("\"Prod\"", StringComparison.Ordinal) < 0) return;
            int s, e;
            TypeWalker.FindRange(tree, o.Data, out s, out e, "m_Script");
            var w = new ByteWriter();
            w.AlignedString("{\"Default\":{\"Prod\":\"" + url + "\"}}");
            o.Data = TypeWalker.Replace(o.Data, s, e, w.ToArray());
            stats.Endpoints++;
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
