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

    // Move files of the 9.2.0 characters (the 3.1.0 moves bundle lacks them): the launcher copies the 9.2.0 APK's
    // characters/moves.assetbundle next to the converted bundle; "movedata92/<move name>" resolves against it.
    static System.Collections.Generic.Dictionary<string, TextAsset> _donorMoves;

    public static void MountDonorMoves(AssetBundle bundle)
    {
        _donorMoves = new System.Collections.Generic.Dictionary<string, TextAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var ta in bundle.LoadAllAssets<TextAsset>()) _donorMoves[ta.name] = ta;
        // The 3.1.0 'moves' bundle has the same identity: unload this one (the text assets stay) so both can load.
        bundle.Unload(false);
        Debug.Log("[TFTF] donor moves mounted: " + _donorMoves.Count);
    }

    // Shader assets the game loads by path ("UI/Shaders/Opaque"): the converted stub shaders have no usable name, so the
    // converter writes "path<TAB>declared name" lines and the real (authored) shader is looked up by that name.
    static System.Collections.Generic.Dictionary<string, string> _shaderNames;

    public static void MountShaderMap(string file)
    {
        if (!System.IO.File.Exists(file)) return;
        _shaderNames = new System.Collections.Generic.Dictionary<string, string>();
        foreach (var line in System.IO.File.ReadAllLines(file))
        {
            int tab = line.IndexOf('\t');
            if (tab > 0) _shaderNames[Key(line.Substring(0, tab))] = line.Substring(tab + 1);
        }
        // The stub shader objects of the bundle have no usable name: remember which object is which declared shader.
        _stubNames = new System.Collections.Generic.Dictionary<int, string>();
        if (Bundle != null)
            foreach (var kv in _shaderNames)
            {
                var stub = Bundle.LoadAsset<Shader>(kv.Key);
                if (stub != null) _stubNames[stub.GetInstanceID()] = kv.Value;
            }
        Debug.Log("[TFTF] shader names mounted: " + _shaderNames.Count + " (stubs identified: " + _stubNames.Count + ")");
    }

    static System.Collections.Generic.Dictionary<int, string> _stubNames;

    /// <summary>The declared name of a shader: stub shaders of the converted bundle have an empty Unity name.</summary>
    public static string NameOf(Shader shader)
    {
        if (shader == null) return null;
        string name;
        if (_stubNames != null && _stubNames.TryGetValue(shader.GetInstanceID(), out name)) return name;
        return shader.name;
    }

    public static Shader ResolveShader(string path)
    {
        string name;
        if (_shaderNames == null || !_shaderNames.TryGetValue(Key(path), out name)) return null;
        var sh = Shader.Find(name);
        if (sh == null) sh = Resources.Load<Shader>("Shader310/" + System.Text.RegularExpressions.Regex.Replace(name, "[^A-Za-z0-9]+", "_"));
        return sh;
    }

    // Dev trace (only active while tftf/probe.txt exists): how loads of the probed shader paths are answered.
    static bool _trace;
    static void Trace(string how, string path, Type type, UnityEngine.Object result)
    {
        if (!_trace || path == null || path.IndexOf("shaders/opaque", StringComparison.OrdinalIgnoreCase) < 0) return;
        var sh = result as Shader;
        Debug.Log("[TFTF] trace " + how + " '" + path + "' as " + (type == null ? "?" : type.Name) + " -> " + (result == null ? "null" : result.GetType().Name + " '" + result.name + "'" + (sh != null ? " supported=" + sh.isSupported : "")));
    }

    public static void EnableTrace(bool on) { _trace = on; }

    // Experiment/fix: ETC1 (ETC_RGB4) textures of the converted data draw as vertical streaks in the UI (the pixels in memory
    // are right); hand the game an uncompressed copy instead.
    static readonly System.Collections.Generic.Dictionary<int, Texture2D> _etc1Copies = new System.Collections.Generic.Dictionary<int, Texture2D>();

    public static bool Etc1Fix;      // opt-in (tftf/etc1fix.txt): it did not cure the loading-screen streaks, so it is off by default

    static UnityEngine.Object Decompress(UnityEngine.Object o)
    {
        if (!Etc1Fix) return o;
        var tex = o as Texture2D;
        if (tex == null || tex.format != TextureFormat.ETC_RGB4) return o;
        Texture2D copy;
        if (_etc1Copies.TryGetValue(tex.GetInstanceID(), out copy) && copy != null) return copy;
        var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(tex, rt);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
        copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
        copy.Apply(false, false);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        copy.name = tex.name;
        copy.wrapMode = tex.wrapMode;
        copy.filterMode = tex.filterMode;
        _etc1Copies[tex.GetInstanceID()] = copy;
        Debug.Log("[TFTF] ETC1 texture " + tex.name + " handed out as RGBA32 (" + tex.width + "x" + tex.height + ")");
        return copy;
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
        if (_shaderNames != null && (type == typeof(Shader) || type == typeof(UnityEngine.Object)))
        {
            var real = ResolveShader(path);
            Trace("Load(map)", path, type, real);
            if (real != null) return real;
        }
        if (_donorMoves != null && path != null && path.StartsWith("movedata92/", StringComparison.Ordinal))
        {
            TextAsset ta;
            return _donorMoves.TryGetValue(path.Substring("movedata92/".Length), out ta) ? ta : null;
        }
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
        var final = o != null ? o : Resources.Load(path, type);       // authored Shader310 and friends live in the player
        Trace("Load(fallthrough)", path, type, final);
        return Decompress(final);
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
        if (_shaderNames != null && (type == typeof(Shader) || type == typeof(UnityEngine.Object)))
        {
            var real = ResolveShader(path);
            Trace("LoadAsync(map)", path, type, real);
            if (real != null) return new TftfRequest(real);          // already available: the request is complete at once
        }
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
        if (_trace) Debug.Log("[TFTF] trace LoadAll '" + path + "' as " + (type == null ? "?" : type.Name));
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
            // A dynamic font's material that ended up on a colour shader draws its alpha-only font texture as black
            // glyphs (an Alpha8 texture samples as rgb 0 in Unity 2020): give it the text shader, which takes the colour from the tint.
            var ft = m.HasProperty("_MainTex") ? m.mainTexture as Texture2D : null;     // mainTexture logs an error on a material without _MainTex
            bool isAlpha8Font = ft != null && ft.format == TextureFormat.Alpha8;
            bool isFontNamed = (ft != null && ft.name != null && (ft.name.IndexOf("font", StringComparison.OrdinalIgnoreCase) >= 0 || ft.name.IndexOf("arial", StringComparison.OrdinalIgnoreCase) >= 0))
                            || (m.name != null && m.name.IndexOf("font", StringComparison.OrdinalIgnoreCase) >= 0);
            if ((isAlpha8Font || isFontNamed) && m.shader != null && m.shader.name != "TFTF/Text" && !m.shader.name.StartsWith("GUI/Text"))
            {
                var ts = ShaderFallback.Find("TFTF/Text");
                if (ts != null) { m.shader = ts; fixedCount++; }
            }
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

    readonly UnityEngine.Object _ready;

    public TftfRequest(UnityEngine.Object ready) { _ready = ready; }
    public TftfRequest(AssetBundleRequest r) { _bundle = r; }
    public TftfRequest(AssetBundleRequest r, System.Type component) { _bundle = r; _component = component; }
    public TftfRequest(ResourceRequest r) { _resources = r; }

    public override bool keepWaiting { get { return _ready != null ? false : _bundle != null ? !_bundle.isDone : !_resources.isDone; } }
    public UnityEngine.Object asset
    {
        get
        {
            if (_ready != null) return _ready;
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
