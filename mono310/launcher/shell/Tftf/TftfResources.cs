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
            if (IsComponent(type))
            {
                // Resources.Load<SomeComponent>(prefab path) returns the component on the prefab
                var go = Bundle.LoadAsset<GameObject>(Key(path));
                if (go != null) o = go.GetComponent(type);
            }
            else o = type == typeof(UnityEngine.Object) ? Bundle.LoadAsset(Key(path)) : Bundle.LoadAsset(Key(path), type);
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

    static bool IsComponent(Type t)
    {
        return t != null && typeof(Component).IsAssignableFrom(t);
    }

    public static TftfRequest LoadAsync(string path, Type type)
    {
        if (Bundle != null && Bundle.Contains(Key(path)))
        {
            if (IsComponent(type)) return new TftfRequest(Bundle.LoadAssetAsync(Key(path), typeof(GameObject)), type);
            return new TftfRequest(Bundle.LoadAssetAsync(Key(path), type));
        }
        return new TftfRequest(Resources.LoadAsync(path, type));
    }

    public static TftfRequest LoadAsync<T>(string path) where T : UnityEngine.Object
    {
        return LoadAsync(path, typeof(T));
    }

    // Resources.LoadAll(folder): every asset below the folder (or the single asset at that path).
    public static UnityEngine.Object[] LoadAll(string path)
    {
        return LoadAll(path, typeof(UnityEngine.Object));
    }

    public static UnityEngine.Object[] LoadAll(string path, Type type)
    {
        if (Bundle != null)
        {
            string key = Key(path), prefix = key.Length == 0 ? "" : key + "/";
            var found = new System.Collections.Generic.List<UnityEngine.Object>();
            foreach (var name in Bundle.GetAllAssetNames())
            {
                if (name != key && !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                UnityEngine.Object o;
                if (IsComponent(type)) { var go = Bundle.LoadAsset<GameObject>(name); o = go == null ? null : go.GetComponent(type); }
                else o = type == typeof(UnityEngine.Object) ? Bundle.LoadAsset(name) : Bundle.LoadAsset(name, type);
                if (o != null) found.Add(o);
            }
            if (found.Count > 0) return found.ToArray();
        }
        return Resources.LoadAll(path, type);
    }

    public static T[] LoadAll<T>(string path) where T : UnityEngine.Object
    {
        var all = LoadAll(path, typeof(T));
        var result = new T[all.Length];
        for (int i = 0; i < all.Length; i++) result[i] = (T)all[i];
        return result;
    }

    // The converter drops the old compiled shaders and stores each material's shader name as a keyword.
    public static void FixMaterialShaders(AssetBundle bundle)
    {
        FixMaterials(bundle.LoadAllAssets<Material>());
    }

    public static void FixMaterials(Material[] materials)
    {
        int fixedCount = 0, missing = 0;
        foreach (var m in materials)
        {
            foreach (var kw in m.shaderKeywords)
            {
                if (!kw.StartsWith(ShaderKeywordPrefix, StringComparison.Ordinal)) continue;
                var shader = ShaderFallback.Find(kw.Substring(ShaderKeywordPrefix.Length));
                m.DisableKeyword(kw);
                if (shader != null) { m.shader = shader; fixedCount++; } else missing++;
            }
        }
        if (fixedCount + missing > 0) Debug.Log("[TFTF] material shaders fixed=" + fixedCount + " unresolved=" + missing);
    }
}

// Stand-in for ResourceRequest: the request may be served by the converted bundle or by the player's Resources.
public class TftfRequest : CustomYieldInstruction
{
    readonly AssetBundleRequest _bundle;
    readonly ResourceRequest _resources;
    readonly System.Type _component;

    public TftfRequest(AssetBundleRequest r) { _bundle = r; }
    public TftfRequest(AssetBundleRequest r, System.Type component) { _bundle = r; _component = component; }
    public TftfRequest(ResourceRequest r) { _resources = r; }

    public override bool keepWaiting { get { return _bundle != null ? !_bundle.isDone : !_resources.isDone; } }
    public UnityEngine.Object asset
    {
        get
        {
            if (_bundle == null) return _resources.asset;
            var a = _bundle.asset;
            var go = a as GameObject;
            return _component != null && go != null ? go.GetComponent(_component) : a;
        }
    }
    public bool isDone { get { return !keepWaiting; } }
}

// Materials inside prefabs are only loaded when the prefab is: keep giving newly loaded ones their shader.
public class TftfMaterialFixer : MonoBehaviour
{
    float _next;

    void Update()
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 0.2f;
        TftfResources.FixMaterials(Resources.FindObjectsOfTypeAll<Material>());
    }
}
