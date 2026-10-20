using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

public static class TftfShaders
{
    public static AssetBundle Bundle { get; private set; }

    public static void EnsureLoaded()
    {
        if (Bundle != null) return;

        // Try load from local file next to app/data
        string localPath = Path.Combine(Application.dataPath, "shaders.bundle");
        if (File.Exists(localPath))
        {
            try
            {
                Bundle = AssetBundle.LoadFromFile(localPath);
                if (Bundle != null)
                {
                    Debug.Log("[TFTF] Shaders loaded from local file: " + localPath);
                    return;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TFTF] Local load failed: " + e.Message);
            }
        }

        // Try load from APK zip entry "assets/shaders.bundle" directly in memory
        try
        {
            string apkPath = Application.dataPath;
            if (File.Exists(apkPath))
            {
                using (FileStream fs = File.OpenRead(apkPath))
                using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry entry = zip.GetEntry("assets/shaders.bundle");
                    if (entry != null)
                    {
                        using (Stream s = entry.Open())
                        using (MemoryStream ms = new MemoryStream((int)entry.Length))
                        {
                            s.CopyTo(ms);
                            Bundle = AssetBundle.LoadFromMemory(ms.ToArray());
                            Debug.Log("[TFTF] Shaders loaded from APK zip memory: " + (Bundle != null));
                            return;
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[TFTF] APK zip load failed: " + e.Message);
        }
    }
}

public static class ShaderFallback
{
    public static Shader Find(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        TftfShaders.EnsureLoaded();

        // 1. Text & Font shaders (Must be before Lit to avoid "Unlit" matching "Lit"!)
        if (name.IndexOf("Text", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Font", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (name.IndexOf("SoftClip", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Shader sClip = Shader.Find("Hidden/Unlit/Text (SoftClip)");
                if (sClip == null) sClip = Shader.Find("Unlit/Text (SoftClip)");
                if (sClip == null) sClip = Shader.Find("GUI/Text Shader (SoftClip)");
                if (sClip == null) sClip = Shader.Find("Hidden/GUI/Text Shader (SoftClip)");
                if (sClip == null) sClip = Shader.Find("TFTF/Text (SoftClip)");
                if (sClip == null) sClip = Shader.Find("Hidden/EBG/UI/BlendColored (SoftClip)");
                if (sClip == null) sClip = Shader.Find("EBG/UI/BlendColored (SoftClip)");
                if (sClip != null) return sClip;
            }

            Shader sText = Shader.Find("Unlit/Text");
            if (sText == null) sText = Shader.Find("GUI/Text Shader");
            if (sText == null) sText = Shader.Find("TFTF/Text");
            if (sText == null) sText = Shader.Find("EBG/UI/BlendColored");
            if (sText != null) return sText;
        }

        // 2. UI shaders
        if (name.IndexOf("UI", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (name.IndexOf("SoftClip", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Shader sUIClip = Shader.Find("Hidden/EBG/UI/BlendColored (SoftClip)");
                if (sUIClip == null) sUIClip = Shader.Find("EBG/UI/BlendColored (SoftClip)");
                if (sUIClip != null) return sUIClip;
            }

            Shader sUI = Shader.Find("EBG/UI/BlendColored");
            if (sUI != null) return sUI;
            sUI = Shader.Find("UI/Default");
            if (sUI != null) return sUI;
        }

        // 3. Terrain & Specialized shaders
        if (name.IndexOf("PBRTerrain", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Terrain", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Shader s = Shader.Find("EB/PBRTerrain");
            if (s != null) return s;
        }

        if (name.IndexOf("GridReveal", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Shader s = Shader.Find("Hidden/GridReveal");
            if (s != null) return s;
        }

        // 4. PBR & 3D Lit shaders (Strictly exclude Unlit!)
        if (name.IndexOf("PBR", StringComparison.OrdinalIgnoreCase) >= 0 ||
            (name.IndexOf("Lit", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("Unlit", StringComparison.OrdinalIgnoreCase) < 0))
        {
            Shader s = Shader.Find("TFTF/LitTex");
            if (s != null) return s;
        }

        // 5. Particles
        if (name.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Shader sPart = Shader.Find("Particles/Additive");
            if (sPart != null) return sPart;
        }

        // 6. UI / 2D universal fallback
        if (name.IndexOf("UI", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Text", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Font", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Sprite", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("NGUI", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Shader sUI = Shader.Find("EBG/UI/BlendColored");
            if (sUI != null) return sUI;
        }

        // 7. 3D default fallback
        Shader sDef = Shader.Find("TFTF/LitTex");
        if (sDef != null) return sDef;

        sDef = Shader.Find("EB/PBRTerrain");
        if (sDef != null) return sDef;

        sDef = Shader.Find("Sprites/Default");
        if (sDef != null) return sDef;

        return null;
    }
}
