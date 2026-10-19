using System;
using UnityEngine;

public static class TestTftfFix
{
    public static void FixMaterials(Material[] materials)
    {
        if (materials == null) return;
        TftfShaders.EnsureLoaded();

        int fixedCount = 0;
        int unresolvedCount = 0;

        for (int i = 0; i < materials.Length; i++)
        {
            Material mat = materials[i];
            if (mat == null) continue;

            Texture mainTex = mat.HasProperty("_MainTex") ? mat.mainTexture : null;
            Texture2D ft = mainTex as Texture2D;
            bool isAlpha8Font = ft != null && ft.format == TextureFormat.Alpha8;
            string texName = (mainTex != null && mainTex.name != null) ? mainTex.name : string.Empty;
            string mName = (mat.name != null) ? mat.name : string.Empty;
            bool isFontNamed = texName.IndexOf("Font", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               mName.IndexOf("Font", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isAlpha8Font || isFontNamed)
            {
                string sName = (mat.shader != null && mat.shader.name != null) ? mat.shader.name : string.Empty;
                if (mat.shader == null || 
                    sName == "Hidden/Internal-Colored" || 
                    sName == "Hidden/InternalErrorShader" ||
                    sName == "TFTF/LitTex" ||
                    sName == "Sprites/Default" ||
                    sName.IndexOf("Text", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    Shader ts = ShaderFallback.Find("Unlit/Text");
                    if (ts != null) { mat.shader = ts; fixedCount++; }
                }
                if (mat.HasProperty("_Color"))
                {
                    mat.SetColor("_Color", Color.white);
                }
            }

            string[] keywords = mat.shaderKeywords;
            if (keywords != null)
            {
                for (int j = 0; j < keywords.Length; j++)
                {
                    string kw = keywords[j];
                    if (kw != null && kw.StartsWith("TFTF_SHADER=", StringComparison.Ordinal))
                    {
                        string shaderName = kw.Substring("TFTF_SHADER=".Length);
                        Shader s = ShaderFallback.Find(shaderName);
                        mat.DisableKeyword(kw);
                        if (s != null)
                        {
                            mat.shader = s;
                            fixedCount++;
                        }
                        else
                        {
                            unresolvedCount++;
                        }
                    }
                }
            }

            Shader curShader = mat.shader;
            if (curShader == null || curShader.name == "Hidden/Internal-Colored" || curShader.name == "Hidden/InternalErrorShader")
            {
                string matName = mat.name;
                if (matName != null && (matName.IndexOf("terrain", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        matName.IndexOf("ground", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    Shader ts = Shader.Find("EB/PBRTerrain");
                    if (ts != null) { mat.shader = ts; fixedCount++; }
                }
                else
                {
                    Shader ls = Shader.Find("TFTF/LitTex");
                    if (ls != null) { mat.shader = ls; fixedCount++; }
                }
            }
        }

        if (fixedCount + unresolvedCount > 0)
        {
            Debug.Log("[TFTF] material shaders fixed=" + fixedCount + " unresolved=" + unresolvedCount);
        }
    }
}
