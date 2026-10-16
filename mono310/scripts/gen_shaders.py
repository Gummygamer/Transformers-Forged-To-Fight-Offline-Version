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
GUI/Text Shader (SoftClip)|Particles/Alpha Blended|Particles/Additive
Hidden/EB/PostFX/Generated/ParticleLowResVignetteBloomWarpFXAA""".replace("\n", "|").split("|")
names = list(dict.fromkeys(n.strip() for n in names if n.strip()))
TEXT = {"GUI/Text Shader", "GUI/Text Shader (SoftClip)"}
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
