using UnityEngine;
using UnityEditor;
public static class BundleCheck
{
    public static void Run()
    {
        var path = System.Environment.GetEnvironmentVariable("CHECK_BUNDLE");
        var b = AssetBundle.LoadFromFile(path);
        if (b == null) { Debug.Log("[BCHECK] LoadFromFile returned null"); EditorApplication.Exit(1); return; }
        foreach (var n in b.GetAllAssetNames())
        {
            var t = b.LoadAsset<TextAsset>(n);
            Debug.Log("[BCHECK] " + n + " len=" + (t == null ? -1 : t.text.Length) + " head=" + (t == null ? "" : t.text.Substring(0, Mathf.Min(40, t.text.Length)).Replace("\n", " ")));
        }
        Debug.Log("[BCHECK] done");
        EditorApplication.Exit(0);
    }
}
