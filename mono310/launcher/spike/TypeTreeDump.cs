// Editor tool: instantiates every MonoBehaviour / ScriptableObject class named in a script list and builds one
// AssetBundle (with type trees) so the serialized field layout of each script can be read back.
// Env: TT_SCRIPTS = json [["Assembly-CSharp.dll","Namespace","Class"], ...]   TT_OUT = output directory
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class TypeTreeDump
{
    static Type Find(string asm, string ns, string cls)
    {
        string an = asm.EndsWith(".dll") ? asm.Substring(0, asm.Length - 4) : asm;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.GetName().Name != an) continue;
            return a.GetType(string.IsNullOrEmpty(ns) ? cls : ns + "." + cls, false);
        }
        return null;
    }

    public static void Run()
    {
        var list = SimpleJson(File.ReadAllText(Environment.GetEnvironmentVariable("TT_SCRIPTS")));
        var outDir = Environment.GetEnvironmentVariable("TT_OUT");
        Directory.CreateDirectory(outDir);
        const string dir = "Assets/_tt";
        if (Directory.Exists(dir)) AssetDatabase.DeleteAsset(dir);
        AssetDatabase.CreateFolder("Assets", "_tt");
        var assets = new List<string>();
        var report = new List<string>();
        int mb = 0, so = 0, skipped = 0;
        foreach (var e in list)
        {
            var t = Find(e[0], e[1], e[2]);
            string tag = e[0] + "|" + e[1] + "|" + e[2];
            if (t == null) { report.Add("MISSING " + tag); skipped++; continue; }
            if (t.IsAbstract || t.IsGenericTypeDefinition) { report.Add("ABSTRACT " + tag); skipped++; continue; }
            try
            {
                string path = dir + "/" + (mb + so) + ".asset";
                if (typeof(ScriptableObject).IsAssignableFrom(t))
                {
                    var inst = ScriptableObject.CreateInstance(t);
                    inst.name = tag;
                    AssetDatabase.CreateAsset(inst, path); assets.Add(path); so++;
                }
                else if (typeof(MonoBehaviour).IsAssignableFrom(t))
                {
                    var go = new GameObject(tag);
                    go.AddComponent(t);
                    path = dir + "/" + (mb + so) + ".prefab";
                    PrefabUtility.SaveAsPrefabAsset(go, path);
                    UnityEngine.Object.DestroyImmediate(go);
                    assets.Add(path); mb++;
                }
                else { report.Add("NOTSCRIPT " + tag); skipped++; }
            }
            catch (Exception ex) { report.Add("ERROR " + tag + " " + ex.GetType().Name); skipped++; }
        }
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        var builds = new[] { new AssetBundleBuild { assetBundleName = "typetrees", assetNames = assets.ToArray() } };
        BuildPipeline.BuildAssetBundles(outDir, builds, BuildAssetBundleOptions.None, BuildTarget.Android);
        File.WriteAllLines(Path.Combine(outDir, "report.txt"), report);
        Debug.Log("[TTDUMP] monobehaviours=" + mb + " scriptableobjects=" + so + " skipped=" + skipped);
        EditorApplication.Exit(0);
    }

    // minimal parser for [["a","b","c"],...]
    static List<string[]> SimpleJson(string s)
    {
        var res = new List<string[]>(); var cur = new List<string>(); var sb = new System.Text.StringBuilder(); bool inStr = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr) { if (c == '\\') { sb.Append(s[++i]); } else if (c == '"') { inStr = false; cur.Add(sb.ToString()); sb.Length = 0; } else sb.Append(c); }
            else if (c == '"') inStr = true;
            else if (c == ']' && cur.Count > 0) { res.Add(cur.ToArray()); cur = new List<string>(); }
        }
        return res;
    }
}
