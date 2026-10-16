// Editor check: loads the converted bundle and instantiates the subtrees of one asset one by one, logging each.
// Env: CHECK_BUNDLE, CHECK_ASSET (asset path inside the bundle)
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class InstantiateCheck
{
    static void Walk(Transform t, int depth, int maxDepth)
    {
        Debug.Log("[ICHECK] try " + new string(' ', depth) + t.name + " comps=" + t.GetComponents<Component>().Length);
        try
        {
            var c = UnityEngine.Object.Instantiate(t.gameObject);
            UnityEngine.Object.DestroyImmediate(c);
        }
        catch (Exception e) { Debug.Log("[ICHECK] EXC " + t.name + " " + e.GetType().Name + ": " + e.Message); }
        if (depth < maxDepth) foreach (Transform ch in t) Walk(ch, depth + 1, maxDepth);
    }

    public static void Run()
    {
        var b = AssetBundle.LoadFromFile(Environment.GetEnvironmentVariable("CHECK_BUNDLE"));
        var go = b.LoadAsset<GameObject>(Environment.GetEnvironmentVariable("CHECK_ASSET"));
        Debug.Log("[ICHECK] loaded " + (go == null ? "null" : go.name));
        if (go != null && Environment.GetEnvironmentVariable("CHECK_WHOLE") == "1")
        {
            Debug.Log("[ICHECK] whole instantiate");
            var c = UnityEngine.Object.Instantiate(go);
            Debug.Log("[ICHECK] whole ok comps=" + c.GetComponentsInChildren<Component>(true).Length);
        }
        else if (go != null) foreach (Transform ch in go.transform) Walk(ch, 0, 1);
        Debug.Log("[ICHECK] done");
        EditorApplication.Exit(0);
    }
}
