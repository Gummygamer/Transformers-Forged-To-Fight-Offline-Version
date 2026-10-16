// Editor check: loads a converted resources bundle, instantiates every GameObject asset and reports missing scripts.
// Env: CHECK_BUNDLE = bundle path, CHECK_LIMIT = optional max assets
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class ResourcesCheck
{
    public static void Run()
    {
        var path = Environment.GetEnvironmentVariable("CHECK_BUNDLE");
        int limit = int.Parse(Environment.GetEnvironmentVariable("CHECK_LIMIT") ?? "100000");
        var b = AssetBundle.LoadFromFile(path);
        if (b == null) { Debug.Log("[RCHECK] LoadFromFile returned null"); EditorApplication.Exit(1); return; }
        var names = b.GetAllAssetNames();
        Debug.Log("[RCHECK] assets=" + names.Length);
        int go = 0, text = 0, other = 0, nulls = 0, withMissing = 0, comps = 0, missingComps = 0, n = 0;
        var kinds = new Dictionary<string, int>();
        foreach (var name in names)
        {
            if (++n > limit) break;
            UnityEngine.Object o = null;
            try { o = b.LoadAsset(name); } catch (Exception e) { Debug.Log("[RCHECK] EXC " + name + " " + e.GetType().Name); }
            if (o == null) { nulls++; Debug.Log("[RCHECK] NULL " + name); continue; }
            string k = o.GetType().Name; int c; kinds.TryGetValue(k, out c); kinds[k] = c + 1;
            var g = o as GameObject;
            if (g != null)
            {
                go++;
                var cs = g.GetComponentsInChildren<Component>(true);
                comps += cs.Length;
                int miss = cs.Count(x => x == null);
                missingComps += miss;
                if (miss > 0) { withMissing++; if (withMissing <= 15) Debug.Log("[RCHECK] MISSING " + name + " x" + miss); }
            }
            else if (o is TextAsset) text++; else other++;
        }
        Debug.Log("[RCHECK] kinds " + string.Join(", ", kinds.Select(kv => kv.Key + "=" + kv.Value).ToArray()));
        Debug.Log("[RCHECK] gameobjects=" + go + " text=" + text + " other=" + other + " nullLoads=" + nulls + " prefabsWithMissingScripts=" + withMissing + " components=" + comps + " missing=" + missingComps);
        Debug.Log("[RCHECK] done");
        EditorApplication.Exit(0);
    }
}
