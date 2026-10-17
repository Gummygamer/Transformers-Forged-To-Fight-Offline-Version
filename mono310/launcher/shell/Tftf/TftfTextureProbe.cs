// Newly authored. Developer probe: if tftf/probe.txt lists texture names (one per line, a part of the name is enough),
// the pixels Unity actually holds for each matching texture are written to tftf/probe/<name>.png once. Used to tell a
// bad conversion from a bad draw. Does nothing when probe.txt does not exist.
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public sealed class TftfTextureProbe : MonoBehaviour
{
    float _next;
    bool _reportedShaders;
    readonly HashSet<int> _done = new HashSet<int>();
    string _dir;

    public static void Start(string dir)
    {
        var go = new GameObject("TftfTextureProbe");
        DontDestroyOnLoad(go);
        go.AddComponent<TftfTextureProbe>()._dir = dir;
    }

    // What the game's shader asset paths resolve to (the loading screen asks for "UI/Shaders/Opaque" by path).
    void ReportShaders()
    {
        _reportedShaders = true;
        foreach (var path in new[] { "UI/Shaders/Opaque", "UI/Shaders/Blend_HardClip", "UI/Shaders/Blend_Colored" })
        {
            var sh = TftfResources.Load(path, typeof(Shader)) as Shader;
            Debug.Log("[TFTF] probe shader " + path + " -> " + (sh == null ? "null" : "'" + sh.name + "' supported=" + sh.isSupported));
        }
        int n = 0;
        if (TftfResources.Bundle != null)
            foreach (var name in TftfResources.Bundle.GetAllAssetNames())
                if (name.StartsWith("ui/shaders/", System.StringComparison.Ordinal) && n++ < 6)
                {
                    var stub = TftfResources.Bundle.LoadAsset<Shader>(name);
                    Debug.Log("[TFTF] probe bundle asset " + name + " -> " + (stub == null ? "null" : "'" + stub.name + "'"));
                }
    }

    readonly HashSet<int> _widgets = new HashSet<int>();

    static object Prop(object o, string name)
    {
        if (o == null) return null;
        var p = o.GetType().GetProperty(name);
        if (p != null) return p.GetValue(o, null);
        var f = o.GetType().GetField(name);
        return f != null ? f.GetValue(o) : null;
    }

    // The widgets that show a probed texture: which shader they ask for and which one their draw call really uses.
    void ReportWidgets(List<string> wanted)
    {
        foreach (var ut in Object.FindObjectsOfType<UITexture>())
        {
            if (ut.mainTexture == null || _widgets.Contains(ut.GetInstanceID())) continue;
            bool match = false;
            foreach (var w in wanted) if (ut.mainTexture.name.IndexOf(w, System.StringComparison.OrdinalIgnoreCase) >= 0) match = true;
            if (!match) continue;
            _widgets.Add(ut.GetInstanceID());
            var dc = Prop(ut, "drawCall");
            var mat = Prop(dc, "dynamicMaterial") as Material;
            Debug.Log("[TFTF] probe UITexture tex=" + ut.mainTexture.name + " asks shader=" + (ut.shader == null ? "null" : "'" + ut.shader.name + "'") +
                      " drawCall shader=" + (mat == null || mat.shader == null ? "none" : "'" + mat.shader.name + "'") +
                      " uvRect=" + ut.uvRect + " size=" + ut.width + "x" + ut.height);
        }
    }

    static object Field(object o, string name)
    {
        if (o == null) return null;
        var f = o.GetType().GetField(name);
        return f != null ? f.GetValue(o) : null;
    }

    static string Dump(System.Array a, int max)
    {
        var sb = new System.Text.StringBuilder("[n=" + a.Length + "]");
        for (int i = 0; i < a.Length && i < max; i++) sb.Append(' ').Append(a.GetValue(i));
        return sb.ToString();
    }

    static string Dump(object betterList, int max)
    {
        if (betterList == null) return "null";
        var buffer = Field(betterList, "buffer") as System.Array;
        object size = Field(betterList, "size");
        if (buffer == null || size == null) return "?";
        var sb = new System.Text.StringBuilder("[n=" + size + "]");
        for (int i = 0; i < System.Math.Min(max, (int)size); i++) sb.Append(' ').Append(buffer.GetValue(i));
        return sb.ToString();
    }

    readonly HashSet<int> _geometry = new HashSet<int>();

    // The mesh data of a probed widget once it has been built: vertices and texture coordinates.
    void ReportGeometry(List<string> wanted)
    {
        foreach (var ut in Object.FindObjectsOfType<UITexture>())
        {
            if (ut.mainTexture == null || _geometry.Contains(ut.GetInstanceID())) continue;
            bool match = false;
            foreach (var w in wanted) if (ut.mainTexture.name.IndexOf(w, System.StringComparison.OrdinalIgnoreCase) >= 0) match = true;
            if (!match) continue;
            var geo = Field(ut, "geometry");
            if (geo == null || !(bool)Prop(geo, "hasVertices")) continue;
            _geometry.Add(ut.GetInstanceID());
            Debug.Log("[TFTF] probe geometry tex=" + ut.mainTexture.name + " " + ut.mainTexture.width + "x" + ut.mainTexture.height + " verts " + Dump(Field(geo, "verts"), 4) +
                      " uvs " + Dump(Field(geo, "uvs"), 4) + " type=" + Prop(ut, "type") + " flip=" + Prop(ut, "flip") + " border=" + Prop(ut, "border"));
        }
    }

    // Experiment: while tftf/probe_hide.txt exists, the widgets that show a probed texture are switched off, to see whether
    // what is on screen comes from them or from something else (a screen effect, another widget).
    void HideWidgets(List<string> wanted)
    {
        if (!File.Exists(Path.Combine(_dir, "probe_hide.txt"))) return;
        foreach (var ut in Object.FindObjectsOfType<UITexture>())
        {
            if (ut.mainTexture == null) continue;
            foreach (var w in wanted)
                if (ut.mainTexture.name.IndexOf(w, System.StringComparison.OrdinalIgnoreCase) >= 0 && ut.gameObject.activeSelf)
                {
                    ut.gameObject.SetActive(false);
                    Debug.Log("[TFTF] probe hid widget " + ut.name + " (" + ut.mainTexture.name + ")");
                }
        }
    }

    static string LabelMat(UIWidget ui)
    {
        var m = Prop(ui, "material") as Material;
        if (m == null) return "null";
        var t = m.mainTexture as Texture2D;
        return m.name + " shader=" + (m.shader == null ? "-" : TftfResources.NameOf(m.shader)) + " tex=" + (m.mainTexture == null ? "null" : m.mainTexture.name + " " + m.mainTexture.width + "x" + m.mainTexture.height + (t != null ? " " + t.format : "")) +
               " color=" + (m.HasProperty("_Color") ? m.GetColor("_Color").ToString() : "-");
    }

    readonly HashSet<int> _dumped = new HashSet<int>();

    static string PathOf(Transform t)
    {
        string p = t.name;
        for (var q = t.parent; q != null; q = q.parent) p = q.name + "/" + p;
        return p;
    }

    // probe.txt line "WIDGETS:<part of a hierarchy path>": log every widget below a matching path once (size, type, sprite, atlas,
    // texture), to see what a screen is really made of.
    void DumpWidgets(List<string> wanted)
    {
        foreach (var w in wanted)
        {
            if (!w.StartsWith("WIDGETS:", System.StringComparison.Ordinal)) continue;
            string part = w.Substring(8);
            foreach (var ui in Object.FindObjectsOfType<UIWidget>())
            {
                if (_dumped.Contains(ui.GetInstanceID())) continue;
                string path = PathOf(ui.transform);
                if (path.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                _dumped.Add(ui.GetInstanceID());
                var atlas = Prop(ui, "atlas");
                if (ui.GetType().Name == "UILabel")
                    Debug.Log("[TFTF] probe label " + ui.name + " text='" + Prop(ui, "text") + "' color=" + Prop(ui, "color") + " alpha=" + Prop(ui, "finalAlpha") + " effect=" + Prop(ui, "effectStyle") + " effectColor=" + Prop(ui, "effectColor") +
                              " gradient=" + Prop(ui, "applyGradient") + " top=" + Prop(ui, "gradientTop") + " bottom=" + Prop(ui, "gradientBottom") + " font=" + (Prop(ui, "trueTypeFont") as Object != null ? "ttf:" + ((Object)Prop(ui, "trueTypeFont")).name : Prop(ui, "bitmapFont") as Object != null ? "bmp:" + ((Object)Prop(ui, "bitmapFont")).name : "none") +
                              " size=" + Prop(ui, "fontSize") +
                              " mat=" + LabelMat(ui) + " code=" + (((string)Prop(ui, "text") ?? "").Length > 0 ? ((int)((string)Prop(ui, "text"))[0]).ToString("X") : "-"));
                Debug.Log("[TFTF] probe widget " + path + " type=" + ui.GetType().Name + " size=" + ui.width + "x" + ui.height +
                          " sprite=" + Prop(ui, "spriteName") + " atlas=" + (atlas as Object != null ? ((Object)atlas).name : "-") +
                          " tex=" + (ui.mainTexture == null ? "-" : ui.mainTexture.name) + " active=" + ui.gameObject.activeInHierarchy +
                          " pos=" + ui.transform.position);
            }
        }
    }

    readonly HashSet<int> _drawCalls = new HashSet<int>();

    // What the draw call of a probed widget is bound to once it exists: the material's shader and texture at draw time.
    void ReportDrawCalls(List<string> wanted)
    {
        foreach (var ut in Object.FindObjectsOfType<UITexture>())
        {
            if (ut.mainTexture == null || _drawCalls.Contains(ut.GetInstanceID())) continue;
            bool match = false;
            foreach (var w in wanted) if (ut.mainTexture.name.IndexOf(w, System.StringComparison.OrdinalIgnoreCase) >= 0) match = true;
            if (!match) continue;
            var dc = Prop(ut, "drawCall");
            if (dc == null) continue;
            var mat = Prop(dc, "dynamicMaterial") as Material;
            if (mat == null) continue;
            _drawCalls.Add(ut.GetInstanceID());
            var tex = mat.mainTexture;
            Debug.Log("[TFTF] probe drawcall shader='" + (mat.shader == null ? "none" : mat.shader.name) + "' supported=" + (mat.shader != null && mat.shader.isSupported) +
                      " _MainTex=" + (tex == null ? "null" : tex.name + " " + tex.width + "x" + tex.height + " " + tex.GetType().Name) +
                      " queue=" + mat.renderQueue + " passes=" + mat.passCount + " tris=" + Prop(dc, "triangles") + " color=" + (mat.HasProperty("_Color") ? mat.GetColor("_Color").ToString() : "-") +
                      " keywords=[" + string.Join(",", mat.shaderKeywords) + "]");
        }
    }

    float _meshNext;

    // Every 2 s: the live mesh the draw call of a probed UITexture really draws (vertices, uvs), plus the widget's uvRect/drawRegion.
    void ReportLiveMesh(List<string> wanted)
    {
        if (Time.unscaledTime < _meshNext) return;
        _meshNext = Time.unscaledTime + 2f;
        foreach (var ut in Object.FindObjectsOfType<UITexture>())
        {
            if (ut.mainTexture == null || !ut.gameObject.activeInHierarchy || ut.name != "BackgroundImage") continue;
            var dc = Prop(ut, "drawCall") as Component;
            var mf = dc == null ? null : dc.GetComponent<MeshFilter>();
            var mesh = mf == null ? null : mf.sharedMesh;
            var mr = dc == null ? null : dc.GetComponent<MeshRenderer>();
            string chain = "";
            for (var q = ut.transform; q != null; q = q.parent) chain += q.name + "(" + q.localScale.x.ToString("0.###") + "," + q.localScale.y.ToString("0.###") + ") ";
            Debug.Log("[TFTF] probe scales " + chain);
            foreach (var c in ut.GetComponents<Component>())
            {
                if (c == null) continue;
                string fl = "";
                foreach (var f in c.GetType().GetFields())
                    if (f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType == typeof(Vector2) || f.FieldType == typeof(Vector3))
                        fl += f.Name + "=" + f.GetValue(c) + " ";
                Debug.Log("[TFTF] probe comp " + c.GetType().FullName + " " + (c.GetType().Name.StartsWith("UIStretch") || c.GetType().Name.StartsWith("Tween") || c.GetType().Name.Contains("Fit") ? fl : ""));
            }
            var pn = Prop(ut, "panel") as Component;
            if (pn != null) Debug.Log("[TFTF] probe panel " + pn.name + " clip=" + Prop(pn, "clipping") + " region=" + Prop(pn, "finalClipRegion") + " ws=" + pn.transform.lossyScale);
            Debug.Log("[TFTF] probe live t=" + Time.unscaledTime.ToString("F1") + " uvRect=" + Prop(ut, "uvRect") + " drawRegion=" + Prop(ut, "drawRegion") +
                      " alpha=" + Prop(ut, "finalAlpha") + " depth=" + Prop(ut, "depth") + " mesh=" + (mesh == null ? "null" : "v=" + mesh.vertexCount + " " + Dump(mesh.vertices, 4) + " uv " + Dump(mesh.uv, 4)) +
                      " renderer=" + (mr == null ? "null" : mr.sharedMaterial.shader.name + " tex=" + (mr.sharedMaterial.mainTexture == null ? "null" : mr.sharedMaterial.mainTexture.name) +
                      " ST=" + mr.sharedMaterial.mainTextureScale + "/" + mr.sharedMaterial.mainTextureOffset));
        }
    }

    // While tftf/probe_scene.txt exists (the probe deletes it afterwards): log what the scene is drawn with, one line per distinct
    // shader (renderer count, materials, biggest bounds) and the large renderers by name. Push the file while a screen is up.
    void ReportScene()
    {
        string f = Path.Combine(_dir, "probe_scene.txt");
        if (!File.Exists(f)) return;
        try { File.Delete(f); } catch (System.Exception) { }
        var groups = new Dictionary<string, int>();
        var big = new List<string>();
        foreach (var r in Object.FindObjectsOfType<Renderer>())
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) continue;
                string key = (m.shader == null ? "none" : TftfResources.NameOf(m.shader)) + (r is SkinnedMeshRenderer ? " [skinned]" : "");
                int n; groups.TryGetValue(key, out n); groups[key] = n + 1;
                if (r.bounds.size.magnitude > 15f && big.Count < 25) big.Add(r.name + " mat=" + m.name + " sh=" + (m.shader == null ? "none" : TftfResources.NameOf(m.shader)) + " size=" + r.bounds.size.magnitude.ToString("F0"));
            }
        }
        foreach (var l in Object.FindObjectsOfType<Light>())
            Debug.Log("[TFTF] scene light " + l.name + " type=" + l.type + " en=" + l.enabled + " active=" + l.gameObject.activeInHierarchy + " shadows=" + l.shadows + " int=" + l.intensity + " mask=" + l.cullingMask + " rot=" + l.transform.eulerAngles);
        foreach (var c in Camera.allCameras)
            Debug.Log("[TFTF] scene camera " + c.name + " path=" + c.actualRenderingPath + " mask=" + c.cullingMask + " depth=" + c.depth + " clear=" + c.clearFlags + " hdr=" + c.allowHDR + " ortho=" + c.orthographic + " fov=" + c.fieldOfView + " near=" + c.nearClipPlane + " far=" + c.farClipPlane + " pos=" + c.transform.position + " fwd=" + c.transform.forward + " rt=" + (c.targetTexture != null));
        Debug.Log("[TFTF] scene ambient mode=" + RenderSettings.ambientMode + " color=" + RenderSettings.ambientLight + " fog=" + RenderSettings.fog + " globalDir=" + Shader.GetGlobalVector("_EBDirectionalLightDirection") + " tftfDir=" + Shader.GetGlobalVector("_TftfLightDir") + " pixLights=" + QualitySettings.pixelLightCount + " shadowDist=" + QualitySettings.shadowDistance);
        foreach (var kv in groups) Debug.Log("[TFTF] scene shader " + kv.Key + " x" + kv.Value);
        foreach (var b in big) Debug.Log("[TFTF] scene big " + b);
        Debug.Log("[TFTF] scene done");
    }

    readonly HashSet<int> _swapped = new HashSet<int>();
    Texture2D _pngTexture;

    // Experiments on a probed widget, each switched on by a file in tftf/:
    //   probe_swap_shader.txt  -> the widget gets Sprites/Default instead of its shader
    //   probe_swap_tex.txt     -> the widget gets the (PNG) title background instead of its texture
    void SwapWidgets(List<string> wanted)
    {
        bool shaderSwap = File.Exists(Path.Combine(_dir, "probe_swap_shader.txt"));
        bool texSwap = File.Exists(Path.Combine(_dir, "probe_swap_tex.txt"));
        if (!shaderSwap && !texSwap) return;
        foreach (var ut in Object.FindObjectsOfType<UITexture>())
        {
            if (ut.mainTexture == null || _swapped.Contains(ut.GetInstanceID())) continue;
            bool match = false;
            foreach (var w in wanted) if (ut.mainTexture.name.IndexOf(w, System.StringComparison.OrdinalIgnoreCase) >= 0) match = true;
            if (!match) continue;
            _swapped.Add(ut.GetInstanceID());
            if (shaderSwap) { ut.shader = Shader.Find("Sprites/Default"); Debug.Log("[TFTF] probe swapped shader of " + ut.name); }
            if (texSwap)
            {
                if (_pngTexture == null)
                {
                    string f = Path.Combine(_dir, "base/assets/ui/titles/title_background.png");
                    if (File.Exists(f)) { _pngTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false); ImageConversion.LoadImage(_pngTexture, File.ReadAllBytes(f)); }
                }
                if (_pngTexture != null) { ut.mainTexture = _pngTexture; Debug.Log("[TFTF] probe swapped texture of " + ut.name + " to a PNG " + _pngTexture.width + "x" + _pngTexture.height); }
            }
        }
    }

    void Update()
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 0.5f;
        string list = Path.Combine(_dir, "probe.txt");
        TftfResources.EnableTrace(File.Exists(list));
        ReportScene();
        if (!File.Exists(list)) return;
        var wanted = new List<string>();
        foreach (var line in File.ReadAllLines(list)) if (line.Trim().Length > 0) wanted.Add(line.Trim());
        if (!_reportedShaders) ReportShaders();
        ReportWidgets(wanted);
        ReportGeometry(wanted);
        ReportDrawCalls(wanted);
        ReportLiveMesh(wanted);
        HideWidgets(wanted);
        SwapWidgets(wanted);
        DumpWidgets(wanted);
        foreach (var tex in Resources.FindObjectsOfTypeAll<Texture2D>())
        {
            if (tex == null || _done.Contains(tex.GetInstanceID())) continue;
            bool match = false;
            foreach (var w in wanted) if (tex.name.IndexOf(w, System.StringComparison.OrdinalIgnoreCase) >= 0) match = true;
            if (!match) continue;
            _done.Add(tex.GetInstanceID());
            try
            {
                var rt = RenderTexture.GetTemporary(512, 512, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(tex, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var copy = new Texture2D(512, 512, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, 512, 512), 0, 0);
                copy.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                Directory.CreateDirectory(Path.Combine(_dir, "probe"));
                string file = Path.Combine(_dir, "probe/" + tex.name.Replace('/', '_') + "_" + tex.format + "_" + tex.width + "x" + tex.height + ".png");
                File.WriteAllBytes(file, ImageConversion.EncodeToPNG(copy));
                Destroy(copy);
                Debug.Log("[TFTF] probe wrote " + file + " (format " + tex.format + ", mips " + tex.mipmapCount + ", filter " + tex.filterMode + ", wrap " + tex.wrapMode + ")");
            }
            catch (System.Exception e) { Debug.LogWarning("[TFTF] probe failed for " + tex.name + ": " + e.Message); }
        }
    }
}
