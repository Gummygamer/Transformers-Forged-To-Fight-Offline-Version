#!/usr/bin/env python3
"""Generate replacement shaders (correct Shader.Find names, simple unlit CG) for every name
the 3.1.0 code looks up. The ripped Shader/*.shader files are dummy exports named "" and
cannot be found by name. Usage: gen_shaders.py <out dir>"""
import sys, re
from pathlib import Path

names = """EBG/UI/BlendColored|EBG/UI/Opaque|EBG/UI/OpaqueFlat|EBG/UI/BlendFlat|EBG/UI/BlendColoredMask
EB/UI/Ember/Transparent/Blend Color No Fog|Unlit/Transparent Colored|Diffuse
EB/Misc/ContactShadow|EB/Light/PointLight|EB/Light/TubeLight|EB/Light/SpotLight|EB/Light/AreaLight|EB/PBRTerrain
Hidden/Light/Flare|Hidden/EB/RBInit|Hidden/EB/Blit|Hidden/EB/UIScreenCapture|Hidden/EB/RBSphere
Hidden/EB/RBParticleTaperedCylinderEmitter|Hidden/EB/RBParticleSphereEmitter|Hidden/EB/RBParticleSphere
Hidden/EB/RBParticleSpawn|Hidden/EB/RBParticlePlaneEmitter|Hidden/EB/RBParticleLooping|Hidden/EB/RBParticleIntegrator
Hidden/EB/RBParticleInit|Hidden/EB/RBParticleHemisphereEmitter|Hidden/EB/RBParticleCircleEmitter|Hidden/EB/RBParticleBoxEmitter
Hidden/EB/RBIntegrator|Hidden/EB/RBCollisionReaction|Hidden/EB/RBBoundary
Hidden/EB/PostFx/LowResParticleAlphaReplacement|Hidden/EB/PostFx/LowResCompReplacement|Hidden/EB/PostFx/DepthReplacement
Hidden/EB/PostFx/BloomLowResParticleComp|Hidden/EB/PostFX/ToneMapBlit|Hidden/EB/PostFX/NoToneMapBlit|Hidden/EB/PostFX/FXAA
Hidden/EB/LensFlare|Hidden/EB/DepthBlur|Hidden/EB/CharacterDamage|Hidden/EB/Blur
GUI/Text Shader (SoftClip)|TFTF/Text|Particles/Alpha Blended|Particles/Additive
Hidden/EB/PostFX/Generated/ParticleLowResVignetteBloomWarpFXAA""".replace("\n", "|").split("|")
names = list(dict.fromkeys(n.strip() for n in names if n.strip()))
TEXT = {"GUI/Text Shader", "GUI/Text Shader (SoftClip)", "TFTF/Text"}
MULTIPASS = 8  # Hidden/EB/* effects index passes up to ~6 (Blur/DepthBlur/FXAA...)
OPAQUE = {"EBG/UI/Opaque", "EBG/UI/OpaqueFlat", "Diffuse"}

PASS = '''        Pass
        {{
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata_t {{ float4 vertex : POSITION; fixed4 color : COLOR; float2 texcoord : TEXCOORD0; }};
            struct v2f {{ float4 vertex : SV_POSITION; fixed4 color : COLOR; float2 texcoord : TEXCOORD0; }};
            sampler2D _MainTex; fixed4 _Color; float4 _MainTex_ST;
            v2f vert(appdata_t i) {{ v2f o; o.vertex = UnityObjectToClipPos(i.vertex); o.texcoord = i.texcoord; o.color = i.color * _Color; return o; }}
            fixed4 frag(v2f i) : SV_Target {{ return tex2D(_MainTex, i.texcoord) * i.color; }}
            ENDCG
        }}
'''
PASS = PASS.replace('{{', '{').replace('}}', '}')  # PASS is substituted as a value, not formatted
TPL = '''Shader "{name}"
{{
    Properties
    {{
        _MainTex ("Texture", 2D) = "white" {{}}
        _Color ("Tint", Color) = (1,1,1,1)
    }}
    SubShader
    {{
        Tags {{ "Queue"="{queue}" "IgnoreProjector"="True" "RenderType"="{rt}" }}
        Cull Off Lighting Off ZWrite {zw} Blend {blend}
{passes}    }}
    Fallback Off
}}
'''
TEXT_FRAG = "return fixed4(i.color.rgb, i.color.a * tex2D(_MainTex, i.texcoord).a);"
out = Path(sys.argv[1]); out.mkdir(parents=True, exist_ok=True)
for n in names:
    op = n in OPAQUE
    one = PASS.replace("return tex2D(_MainTex, i.texcoord) * i.color;", TEXT_FRAG) if n in TEXT else PASS
    count = MULTIPASS if n.startswith(("Hidden/EB", "EB/")) else 1
    f = out / (re.sub(r"[^A-Za-z0-9]+", "_", n) + ".shader")
    f.write_text(TPL.format(name=n, queue="Geometry" if op else "Transparent", rt="Opaque" if op else "Transparent",
                            passes=one * count, zw="On" if op else "Off",
                            blend="Off" if op else "SrcAlpha OneMinusSrcAlpha"))
LIT = '''Shader "TFTF/LitTex"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} _Color ("Tint", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Cull Back ZWrite On
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata_t { float4 vertex : POSITION; float3 normal : NORMAL; float2 texcoord : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float3 n : TEXCOORD1; float2 uv : TEXCOORD0; float3 wp : TEXCOORD2; };
            sampler2D _MainTex; fixed4 _Color; float4 _MainTex_ST;
            v2f vert(appdata_t i) { v2f o; o.vertex = UnityObjectToClipPos(i.vertex); o.uv = TRANSFORM_TEX(i.texcoord, _MainTex); o.n = UnityObjectToWorldNormal(i.normal); o.wp = mul(unity_ObjectToWorld, i.vertex).xyz; return o; }
            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.n);
                float3 L1 = normalize(float3(0.4, 0.8, -0.45));
                float3 V = normalize(_WorldSpaceCameraPos - i.wp);
                float d = saturate(dot(n, L1)) * 0.75 + 0.35 + 0.25 * saturate(dot(n, float3(-0.5, 0.2, 0.6)));
                float rim = pow(1.0 - saturate(dot(n, V)), 3.0) * 0.25;
                fixed4 c = tex2D(_MainTex, i.uv) * _Color;
                return fixed4(c.rgb * d + rim, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
'''
(out / "TFTF_LitTex.shader").write_text(LIT)

PBR = r"""Shader "TFTF/PbrLite"
{
    // Stand-in for the donor PBR family (Hidden/PBR...): albedo, normal map, metallic/roughness, AO and emissive maps of the robots
    // under one directional light plus a fake sky/ground reflection. Fed by SwapPbr in TftfDiag (compat310.py).
    Properties
    {
        _MainTex ("Albedo", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _NormalTex ("Normal", 2D) = "bump" {}
        _MetalTex ("Metallic", 2D) = "black" {}
        _RoughTex ("Roughness", 2D) = "white" {}
        _AoTex ("AO", 2D) = "white" {}
        _EmisTex ("Emissive", 2D) = "black" {}
        _EmisColor ("Emissive colour", Color) = (0,0,0,0)
        _Flags ("has normal, metal, rough, ao", Vector) = (0,0,0,0)
        _MetalAmt ("Metallic scale", Float) = 1
        _RoughAmt ("Roughness scale", Float) = 1
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Cull Back ZWrite On
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            struct appdata_t { float4 vertex : POSITION; float3 normal : NORMAL; float2 texcoord : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float3 n : TEXCOORD1; float2 uv : TEXCOORD0; float3 wp : TEXCOORD2; };
            sampler2D _MainTex, _NormalTex, _MetalTex, _RoughTex, _AoTex, _EmisTex;
            fixed4 _Color, _EmisColor; float4 _MainTex_ST, _Flags; float _MetalAmt, _RoughAmt; float4 _EBDirectionalLightDirection;
            v2f vert(appdata_t i)
            {
                v2f o; o.vertex = UnityObjectToClipPos(i.vertex); o.uv = TRANSFORM_TEX(i.texcoord, _MainTex);
                o.n = UnityObjectToWorldNormal(i.normal); o.wp = mul(unity_ObjectToWorld, i.vertex).xyz; return o;
            }
            // normal mapping without tangents: cotangent frame from screen-space derivatives
            float3 perturb(float3 n, float3 wp, float2 uv, float3 tn)
            {
                float3 dp1 = ddx(wp), dp2 = ddy(wp); float2 duv1 = ddx(uv), duv2 = ddy(uv);
                float3 dp2perp = cross(dp2, n), dp1perp = cross(n, dp1);
                float3 T = dp2perp * duv1.x + dp1perp * duv2.x, B = dp2perp * duv1.y + dp1perp * duv2.y;
                float inv = rsqrt(max(dot(T, T), dot(B, B)) + 1e-12);
                return normalize(T * inv * tn.x + B * inv * tn.y + n * tn.z);
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.n);
                if (_Flags.x > 0.5)
                {
                    float3 tn = tex2D(_NormalTex, i.uv).xyz * 2 - 1; tn.xy *= 0.8; tn.z = sqrt(saturate(1 - dot(tn.xy, tn.xy)));
                    n = perturb(n, i.wp, i.uv, tn);
                }
                float3 V = normalize(_WorldSpaceCameraPos - i.wp);
                // the scene's own sun (EBLight pushes its up vector as the direction towards the light); a fixed one before it is set
                float3 L = _EBDirectionalLightDirection.xyz;
                L = dot(L, L) < 0.01 ? float3(0.4, 0.8, -0.45) : L;
                L = normalize(L.y < 0 ? -L : L);
                float3 L2 = normalize(float3(-L.x, 0.15, -L.z));
                float3 alb = tex2D(_MainTex, i.uv).rgb * _Color.rgb;
                float metal = _Flags.y > 0.5 ? saturate(tex2D(_MetalTex, i.uv).r * _MetalAmt) : saturate(_MetalAmt);
                float rough = _Flags.z > 0.5 ? saturate(tex2D(_RoughTex, i.uv).r * _RoughAmt) : saturate(_RoughAmt);
                float ao = _Flags.w > 0.5 ? tex2D(_AoTex, i.uv).r : 1;
                rough = clamp(rough, 0.08, 1);
                float3 spec0 = lerp(0.04, alb, metal), diff = alb * (1 - 0.55 * metal);
                float aoL = lerp(0.45, 1, ao);
                float ndl = dot(n, L);
                float nl = saturate((ndl + 0.2) / 1.2), nl2 = saturate(dot(n, L2));      // slightly wrapped key light
                float3 H = normalize(L + V);
                float a2 = pow(rough, 4), nh = saturate(dot(n, H));
                float dd = nh * nh * (a2 - 1) + 1, D = a2 / (3.14159 * dd * dd + 1e-5);
                float3 F = spec0 + (1 - spec0) * pow(1 - saturate(dot(V, H)), 5);
                float3 specular = D * F * 0.3 * saturate(ndl);
                float hemi = saturate(n.y * 0.5 + 0.5);
                float3 ambient = lerp(float3(0.20, 0.19, 0.19), float3(0.52, 0.58, 0.70), hemi);     // dark underside, bluish sky
                float3 r = reflect(-V, n);
                float3 env = lerp(float3(0.22, 0.21, 0.20), float3(0.70, 0.78, 0.92), saturate(r.y * 0.5 + 0.5)) * (1 - rough * 0.6);
                float3 col = diff * (nl * 1.05 + nl2 * 0.18 + ambient) * aoL + specular + env * F * aoL * (0.25 + metal * 0.75);
                col += tex2D(_EmisTex, i.uv).rgb * _EmisColor.rgb;
                col = 1 - exp(-1.3 * col);      // soft shoulder: bright albedo (snow) keeps its shape instead of clipping
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
"""
(out / "TFTF_PbrLite.shader").write_text(PBR)

FX = r"""Shader "TFTF/AddFx"
{
    // Stand-in for the donor PBR materials that were authored as transparent (glow cards, trails): additive, so black is nothing.
    Properties { _MainTex ("Texture", 2D) = "white" {} _Color ("Tint", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" }
        Cull Off Lighting Off ZWrite Off Blend One One
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata_t { float4 vertex : POSITION; float2 texcoord : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };
            sampler2D _MainTex; fixed4 _Color; float4 _MainTex_ST;
            v2f vert(appdata_t i) { v2f o; o.vertex = UnityObjectToClipPos(i.vertex); o.uv = TRANSFORM_TEX(i.texcoord, _MainTex); return o; }
            fixed4 frag(v2f i) : SV_Target { fixed4 c = tex2D(_MainTex, i.uv); return fixed4(c.rgb * _Color.rgb * c.a * 0.6, 1); }
            ENDCG
        }
    }
    Fallback Off
}
"""
(out / "TFTF_AddFx.shader").write_text(FX)
print(len(names), "shaders ->", out)

# --- also rewrite AssetRipper dummy shaders (white-output placeholders) in place, keeping GUIDs ---
if len(sys.argv) > 2:
    n = 0
    for f in Path(sys.argv[2]).rglob("*.shader"):
        t = f.read_text(errors="replace")
        if "DummyShaderTextExporter" in t:
            f.write_text(TPL.format(name="Recovered/" + re.sub(r"[^A-Za-z0-9]+", "_", f.stem),
                                    queue="Transparent", rt="Transparent", passes=PASS, zw="Off", blend="SrcAlpha OneMinusSrcAlpha"))
            n += 1
    print("rewrote", n, "dummy shaders")
