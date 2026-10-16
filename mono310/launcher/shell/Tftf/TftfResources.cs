// Newly authored. Serves Resources.Load from the converted resources bundle (see launcher/convert).
// The game's Resources.Load call sites are redirected here by repair P40 in scripts/compat310.py.
using System;
using UnityEngine;

public static class TftfResources
{
    public const string ShaderKeywordPrefix = "TFTF_SHADER=";
    public static AssetBundle Bundle;

    public static void Mount(AssetBundle bundle)
    {
        Bundle = bundle;
    }

    static string Key(string path)
    {
        return (path ?? string.Empty).Replace('\\', '/').Trim('/').ToLowerInvariant();
    }

    public static UnityEngine.Object Load(string path)
    {
        return Load(path, typeof(UnityEngine.Object));
    }

    public static UnityEngine.Object Load(string path, Type type)
    {
        UnityEngine.Object o = null;
        if (Bundle != null)
        {
            o = type == typeof(UnityEngine.Object) ? Bundle.LoadAsset(Key(path)) : Bundle.LoadAsset(Key(path), type);
        }
        return o != null ? o : Resources.Load(path, type);       // authored Shader310 and friends live in the player
    }

    public static T Load<T>(string path) where T : UnityEngine.Object
    {
        return (T)Load(path, typeof(T));
    }

    public static TftfRequest LoadAsync(string path)
    {
        return LoadAsync(path, typeof(UnityEngine.Object));
    }

    public static TftfRequest LoadAsync(string path, Type type)
    {
        if (Bundle != null && Bundle.Contains(Key(path)))
            return new TftfRequest(Bundle.LoadAssetAsync(Key(path), type));
        return new TftfRequest(Resources.LoadAsync(path, type));
    }

    public static TftfRequest LoadAsync<T>(string path) where T : UnityEngine.Object
    {
        return LoadAsync(path, typeof(T));
    }

    public static UnityEngine.Object[] LoadAll(string path)
    {
        return Resources.LoadAll(path);
    }

    public static UnityEngine.Object[] LoadAll(string path, Type type)
    {
        return Resources.LoadAll(path, type);
    }

    public static T[] LoadAll<T>(string path) where T : UnityEngine.Object
    {
        return Resources.LoadAll<T>(path);
    }

    // The converter drops the old compiled shaders and stores each material's shader name as a keyword.
    public static void FixMaterialShaders(AssetBundle bundle)
    {
        int fixedCount = 0, missing = 0;
        foreach (var m in bundle.LoadAllAssets<Material>())
        {
            foreach (var kw in m.shaderKeywords)
            {
                if (!kw.StartsWith(ShaderKeywordPrefix, StringComparison.Ordinal)) continue;
                var shader = ShaderFallback.Find(kw.Substring(ShaderKeywordPrefix.Length));
                m.DisableKeyword(kw);
                if (shader != null) { m.shader = shader; fixedCount++; } else missing++;
            }
        }
        Debug.Log("[TFTF] material shaders fixed=" + fixedCount + " unresolved=" + missing);
    }
}

// Stand-in for ResourceRequest: the request may be served by the converted bundle or by the player's Resources.
public class TftfRequest : CustomYieldInstruction
{
    readonly AssetBundleRequest _bundle;
    readonly ResourceRequest _resources;

    public TftfRequest(AssetBundleRequest r) { _bundle = r; }
    public TftfRequest(ResourceRequest r) { _resources = r; }

    public override bool keepWaiting { get { return _bundle != null ? !_bundle.isDone : !_resources.isDone; } }
    public UnityEngine.Object asset { get { return _bundle != null ? _bundle.asset : _resources.asset; } }
    public bool isDone { get { return !keepWaiting; } }
}
