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
TEXT = {"GUI/Text Shader", "GUI/Text Shader (SoftClip)", "TFTF/Text", "Unlit/Text", "Unlit - Text"}
MULTIPASS = 8  # Hidden/EB/* effects index passes up to ~6 (Blur/DepthBlur/FXAA...)
OPAQUE = {"EBG/UI/Opaque", "EBG/UI/OpaqueFlat", "Diffuse"}
ADDITIVE = {"Hidden/Light/Flare", "Hidden/EB/LensFlare"}  # sun glows: alpha-blended, their dark texels drew a black ring over the sky

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
            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.texcoord);
                if (col.r == 0.0 && col.g == 0.0 && col.b == 0.0 && col.a > 0.0)
                {
                    col.rgb = i.color.rgb;
                    col.a *= i.color.a;
                    return col;
                }
                return col * i.color;
            }
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
                            blend="Off" if op else "One One" if n in ADDITIVE else "SrcAlpha OneMinusSrcAlpha"))
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
        _EmisOver ("donor _emissive_overbright_range", Float) = 120
        _EmisRamp ("donor _emissive_ramp_range", Float) = 1
        _EmisPulse ("donor pulse intensity, time", Vector) = (0,1,0,0)
        _Flags ("has normal, metal, rough, ao", Vector) = (0,0,0,0)
        _MetalAmt ("Metallic scale", Float) = 1
        _RoughAmt ("Roughness scale", Float) = 1
        _IsSkinned ("Robot (skinned mesh)", Float) = 0
        _CompTex ("RAOE composite (R roughness, G AO, B emissive)", 2D) = "black" {}
        _CompOn ("composite present", Float) = 0
        _CompST ("donor _pbr_composite_tex_ST (merged atlas)", Vector) = (1,1,0,0)
        _NormalST ("donor _normal_tex_ST", Vector) = (1,1,0,0)
        _NmMetal ("metallic in the normal map's blue (EB_COMPOSITE_NM)", Float) = 0
        _Base2Tex ("Terrain colour layer", 2D) = "white" {}
        _MaskTex ("Terrain blend mask", 2D) = "white" {}
        _Base2ST ("colour layer tiling", Vector) = (1,1,0,0)
        _MaskST ("mask tiling", Vector) = (1,1,0,0)
        _Blend ("mask offset, remap, colour layer on, world uv", Vector) = (0,1,0,0)
        _LmOn ("baked arena lightmap on uv2 (donor LIGHTMAP_ON)", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Cull Back ZWrite On
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"
            #include "AutoLight.cginc"
            struct appdata_t { float4 vertex : POSITION; float3 normal : NORMAL; float2 texcoord : TEXCOORD0; float2 texcoord1 : TEXCOORD1; };
            struct v2f { float4 pos : SV_POSITION; float3 n : TEXCOORD1; float2 uv : TEXCOORD0; float3 wp : TEXCOORD2; SHADOW_COORDS(3) float2 uvRaw : TEXCOORD4; float2 uvLm : TEXCOORD5; };
            sampler2D _TftfLm; float _TftfLmOn, _LmOn;
            sampler2D _CompTex; float _CompOn; sampler2D _MainTex, _NormalTex, _MetalTex, _RoughTex, _AoTex, _EmisTex, _Base2Tex, _MaskTex; float4 _Base2ST, _MaskST, _Blend, _CompST, _NormalST; float _NmMetal;
            fixed4 _Color, _EmisColor; float4 _MainTex_ST, _Flags; float _MetalAmt, _RoughAmt; float4 _TftfLightDir; float _TftfDebug; float4 _TftfTune; float4 _TftfGrade; float _IsSkinned; float4 _TftfRobot; float4 _TftfSky; float _EmisOver, _EmisRamp, _TftfGlowOn; float4 _EmisPulse; float _TftfEmis;
            v2f vert(appdata_t v)
            {
                v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = TRANSFORM_TEX(v.texcoord, _MainTex); o.uvRaw = v.texcoord; o.uvLm = v.texcoord1;
                o.n = UnityObjectToWorldNormal(v.normal); o.wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                TRANSFER_SHADOW(o)
                return o;
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
                // terrain (EB/PBRTerrain): textures are projected from the world position, tiled per metre
                float2 uv = _Blend.w > 0.5 ? i.wp.xz * _MainTex_ST.xy + _MainTex_ST.zw : i.uv;
                float2 uvR = _Blend.w > 0.5 ? i.wp.xz : i.uvRaw;
                float3 n = normalize(i.n);
                // robots (donor EB_COMPOSITE_*): normal map and RAOE composite are merged atlases addressed through their own ST
                bool world = _Blend.w > 0.5;
                float2 uvN = world ? uv : i.uvRaw * _NormalST.xy + _NormalST.zw;
                float2 uvC = world ? uv : i.uvRaw * _CompST.xy + _CompST.zw;
                float3 nmS = tex2D(_NormalTex, uvN).xyz;
                if (_Flags.x > 0.5)
                {
                    float3 tn = nmS * 2 - 1; tn.xy *= 0.8; tn.z = sqrt(saturate(1 - dot(tn.xy, tn.xy)));
                    n = perturb(n, i.wp, uvN, tn);
                }
                float3 V = normalize(_WorldSpaceCameraPos - i.wp);
                float4 G = _TftfGrade.w > 0 ? _TftfGrade : float4(1, 1, 1, 1);      // all zero until the game's first lighting update
                // key light from the camera's upper left front, rim light from behind: fight cameras face the scene's sun, which lit the
                // bots from the front and made them flat. TftfDiag points the shadow-casting TFTF sun the same way (TftfLightDir).
                float3 camRight = UNITY_MATRIX_V[0].xyz, camBack = UNITY_MATRIX_V[2].xyz;
                float3 L = _TftfLightDir.xyz;
                L = dot(L, L) < 0.01 ? normalize(-0.55 * camRight + float3(0, 0.75, 0) + 0.35 * camBack) : normalize(L);
                float3 L2 = normalize(0.45 * camRight + float3(0, 0.25, 0) - 0.85 * camBack);
                // lit in linear space and tone mapped, like the original PBR pipeline (gamma-space lighting looked flat)
                float3 albS = tex2D(_MainTex, uv).rgb;
                if (_Blend.z > 0.5)      // EB/PBRTerrain: a large-scale colour layer mixed in through a breakup mask
                {
                    float mask = tex2D(_MaskTex, uvR * _MaskST.xy + _MaskST.zw).r;
                    float k = saturate((mask + _Blend.x) * _Blend.y);
                    albS = lerp(albS, tex2D(_Base2Tex, uvR * _Base2ST.xy + _Base2ST.zw).rgb, k);
                }
                float3 alb = pow(albS * _Color.rgb, 2.2);
                float metal = _NmMetal > 0.5 ? nmS.z : _Flags.y > 0.5 ? saturate(tex2D(_MetalTex, uv).r * _MetalAmt) : saturate(_MetalAmt);
                // donor decode: channels squared; R^2 is the GGX alpha^2 (= our rough^4), G^2 AO, B^2 emissive mask
                float3 comp = _CompOn > 0.5 ? tex2D(_CompTex, uvC).rgb : float3(-1, -1, 0);
                float rough = _CompOn > 0.5 ? sqrt(saturate(comp.r)) : (_Flags.z > 0.5 ? saturate(tex2D(_RoughTex, uv).r * _RoughAmt) : saturate(_RoughAmt));
                float ao = _CompOn > 0.5 ? comp.g * comp.g : (_Flags.w > 0.5 ? tex2D(_AoTex, uv).r : 1);
                rough = clamp(rough, 0.12, 1);
                float3 spec0 = lerp(0.04, alb, metal), diff = alb * (1 - metal);
                float ndl = dot(n, L), ndv = saturate(dot(n, V));
                UNITY_LIGHT_ATTENUATION(atten, i, i.wp);      // the TFTF sun light's shadow (1 without one)
                // static arena geometry: the donor's forward lightmap (global _lm of the time of day) holds the baked sun shadow and AO in
                // alpha and the baked local lights in rgb (x4); without it the arenas were lit flat
                float4 lm = _TftfLmOn > 0.5 && _LmOn > 0.5 ? tex2D(_TftfLm, i.uvLm) : float4(0, 0, 0, 1);
                lm.rgb *= _TftfLmOn > 1.5 ? 1 : 0;      // 1: deferred map (only its alpha applies here), 2: forward map
                float nl = saturate(ndl) * atten * lm.a, nl2 = saturate(dot(n, L2)) * lm.a;
                float3 H = normalize(L + V);
                float a2 = rough * rough * rough * rough, nh = saturate(dot(n, H));
                float dd = nh * nh * (a2 - 1) + 1, D = a2 / (3.14159 * dd * dd + 1e-5);
                float vis = 0.25 / max(lerp(ndv, 1, rough * 0.5) * lerp(saturate(ndl), 1, rough * 0.5), 0.05);
                float3 F = spec0 + (1 - spec0) * pow(1 - saturate(dot(V, H)), 5);
                float3 sun = float3(1.0, 0.9, 0.78) * (_TftfTune.y > 0 ? _TftfTune.y : 2.8);      // warm sun
                float3 direct = (diff / 3.14159 * 3.14159 + D * vis * F) * nl * sun;          // diffuse * pi cancels the 1/pi of lambert
                float hemi = saturate(n.y * 0.5 + 0.5);
                float3 skyUp = _TftfSky.a > 0 ? lerp(float3(0.27, 0.30, 0.38), _TftfSky.rgb, _TftfSky.a) : float3(0.27, 0.30, 0.38);      // a = how much of the arena sky colour (1 in fights, less elsewhere)       // the arena's own sky colour (TftfDiag.SampleSky)
                float3 ambient = lerp(skyUp * 0.19, skyUp, hemi) + nl2 * skyUp * 0.5;
                ambient = _IsSkinned > 0.5 ? ambient * 1.9 + skyUp * 0.12 : ambient;      // fill light for the robots' dark side
                float3 r = reflect(-V, n);
                float3 sky = lerp(skyUp * 0.06, skyUp * 2.0, saturate(r.y * 0.5 + 0.5));
                float3 Fenv = spec0 + (max(1 - rough, spec0) - spec0) * pow(1 - ndv, 5);
                float3 env = sky * Fenv * (1 - rough * 0.7);
                float rim = pow(1 - ndv, 4) * 0.35 * (1 - rough * 0.5);
                float aoL = lerp(_IsSkinned > 0.5 ? 0.7 : 0.35, 1, ao);      // robots keep their shadowed parts readable
                float back = pow(saturate(dot(n, L2)), 2) * (0.35 + 0.65 * pow(1 - ndv, 2));
                back *= lm.a; aoL *= lerp(0.25, 1, lm.a); rim *= lm.a;
                if (_TftfDebug > 0.5)      // tftf/dbg.txt: 1 world normal, 2 key light lambert, 3 shadow atten, 4 albedo, 5 light dir, 6 n.v, 7 ambient+rim only, 8 baked lightmap occlusion
                {
                    float dbg = _TftfDebug;
                    float3 o = dbg < 1.5 ? n * 0.5 + 0.5 : dbg < 2.5 ? nl.xxx : dbg < 3.5 ? atten.xxx : dbg < 4.5 ? albS : dbg < 5.5 ? L * 0.5 + 0.5 : dbg < 6.5 ? ndv.xxx : dbg < 7.5 ? ambient : lm.aaa;
                    return fixed4(o, 1);
                }
                float3 col = direct + diff * lm.rgb * 4 * sun * 0.5 + (diff * ambient * G.x + env + rim * G.y * float3(0.20, 0.23, 0.30)) * aoL + back * G.y * float3(0.55, 0.62, 0.80) * (diff + spec0) * 1.6;
                // atmospheric haze: far geometry fades into the arena's sky colour (the original arenas are hazy and moody)
                float fd = distance(_WorldSpaceCameraPos, i.wp);
                float fogK = saturate((fd - 30) / 260) * _TftfTune.z;
                col = lerp(col, skyUp * 1.5 + float3(0.02, 0.015, 0.01), fogK);
                col *= float3(1 + _TftfTune.w, 1, 1 - _TftfTune.w);      // warm/cool grade (tftf/tune.txt: warm)
                bool robot = _IsSkinned > 0.5 && _TftfRobot.x > 0;      // robots outside fights (selection screens) get their own exposure/saturation
                col *= (_TftfTune.x > 0 ? _TftfTune.x : 1.0) * (robot ? _TftfRobot.x : 1.0);      // exposure (tftf/tune.txt: "exposure sun")
                // emissive parts (eyes, lights, glowing panels) are added after the exposure, so a dark fight exposure does not dim them
                // donor (Hidden/PBR ... EB_COMPOSITE_RAOE): pow(B^2 * col^2, ramp) * (1 + sin(t * pulseTime) * pulseAmp) * overbright, in the
                // material's own emissive colour (Primal's eyes red, head lights orange)
                float3 ecol = _EmisColor.rgb / 3;      // TftfDiag stores it * 3
                float pulse = 1 + sin(_Time.z * _EmisPulse.y) * _EmisPulse.x;
                float3 emap = _CompOn > 0.5 ? pow(max(comp.b * comp.b * ecol * ecol, 1e-8), _EmisRamp > 0 ? _EmisRamp : 1) * pulse * _EmisOver * (_TftfEmis > 0 ? _TftfEmis : 1)
                                            : pow(tex2D(_EmisTex, uv).rgb, 2.2) * 2.2 * clamp(log2(max(_EmisOver, 1)) / 6.9, 0.25, 1.4);
                col += emap;
                // alpha = glow mask for TFTF/Bloom on the fight camera (TftfBloom sets _TftfGlowOn only while it renders)
                float glow = saturate(dot(emap, float3(0.3, 0.5, 0.2)) * 0.9 + saturate(dot(col, float3(0.3, 0.5, 0.2)) - 1.6) * 0.25);
                col = (col * (2.51 * col + 0.03)) / (col * (2.43 * col + 0.59) + 0.14);      // ACES approximation
                col = pow(saturate(col), 1 / 2.2);
                float lum = dot(col, float3(0.299, 0.587, 0.114));
                col = lerp(lum.xxx, col, G.z * (robot ? _TftfRobot.y : 1.0));                       // saturation
                col = saturate((col - 0.5) * G.w + 0.5);             // contrast
                return fixed4(col, _TftfGlowOn > 0.5 ? glow : 1);
            }
            ENDCG
        }
        Pass
        {
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual Cull Back
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"
            struct v2f { V2F_SHADOW_CASTER; };
            v2f vert(appdata_base v) { v2f o; TRANSFER_SHADOW_CASTER_NORMALOFFSET(o) return o; }
            float4 frag(v2f i) : SV_Target { SHADOW_CASTER_FRAGMENT(i) }
            ENDCG
        }
    }
    Fallback Off
}
"""
(out / "TFTF_PbrLite.shader").write_text(PBR)

SKY = r"""Shader "TFTF/SkyLite"
{
    // Stand-in for the arenas' sky domes (EB/Transformers/Sky, EB/PBRSky) whose donor shaders do not compile on this GPU: the dome's own
    // texture, unlit, behind everything.
    Properties { _MainTex ("Sky", 2D) = "white" {} _Color ("Tint", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "IgnoreProjector"="True" }
        Cull Off ZWrite Off Lighting Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata_t { float4 vertex : POSITION; float2 texcoord : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };
            sampler2D _MainTex; fixed4 _Color; float4 _MainTex_ST; float _TftfGlowOn;
            v2f vert(appdata_t v) { v2f o; o.vertex = UnityObjectToClipPos(v.vertex); o.uv = TRANSFORM_TEX(v.texcoord, _MainTex); return o; }
            fixed4 frag(v2f i) : SV_Target { return fixed4(tex2D(_MainTex, i.uv).rgb * _Color.rgb, _TftfGlowOn > 0.5 ? 0.12 : 1); }
            ENDCG
        }
    }
    Fallback Off
}
"""
(out / "TFTF_SkyLite.shader").write_text(SKY)

FX = r"""Shader "TFTF/AddFx"
{
    // Stand-in for the donor PBR materials that were authored as transparent (glow cards, trails): additive, so black is nothing.
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _EmisOver ("donor _emissive_overbright_range", Float) = 8
        _EmisRamp ("donor _emissive_ramp_range", Float) = 1
        _UseVC ("particle vertex colour", Float) = 0
    }
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
            struct appdata_t { float4 vertex : POSITION; float2 texcoord : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; float4 vc : COLOR; };
            sampler2D _MainTex; fixed4 _Color; float4 _MainTex_ST; float _EmisOver, _EmisRamp, _UseVC;
            v2f vert(appdata_t i) { v2f o; o.vertex = UnityObjectToClipPos(i.vertex); o.uv = TRANSFORM_TEX(i.texcoord, _MainTex); o.vc = _UseVC > 0.5 ? i.color : float4(1, 1, 1, 1); return o; }
            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                // donor particles: pow(emissive, ramp) * tint * overbright; overbright 1..235 mapped to about 0.5..2.1 (LDR target, TFTF/Bloom spreads the rest)
                float k = 0.6 * (0.8 + 0.35 * log2(max(_EmisOver, 1)));
                float3 e = pow(max(c.rgb, 0), _EmisRamp > 0 ? _EmisRamp : 1) * _Color.rgb * c.a * k * i.vc.rgb * i.vc.a;
                return fixed4(e, saturate(dot(e, float3(0.3, 0.5, 0.2)) * 0.8));      // alpha adds up: glow mask for TFTF/Bloom
            }
            ENDCG
        }
    }
    Fallback Off
}
"""
(out / "TFTF_AddFx.shader").write_text(FX)

BLOOM = r"""Shader "TFTF/Bloom"
{
    // Stand-in for the game's bloom post-FX (EBPostFXBloom runs on stub shaders). TftfBloom on the fight camera: 0 prefilter (rgb * glow mask in
    // alpha, plus a soft threshold on very bright pixels), 1 separable 9-tap gaussian along _BlurDir, 2 composite onto the frame.
    Properties { _MainTex ("Source", 2D) = "black" {} _BloomTex ("Bloom", 2D) = "black" {} _Bloom2Tex ("Bloom wide", 2D) = "black" {} }
    CGINCLUDE
    #include "UnityCG.cginc"
    sampler2D _MainTex, _BloomTex, _Bloom2Tex; float4 _MainTex_TexelSize, _BlurDir, _BloomParams;      // params: threshold, threshold gain, amount
    struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
    v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }
    float3 pre(float2 uv)
    {
        float4 c = tex2D(_MainTex, uv);
        float l = dot(c.rgb, float3(0.3, 0.5, 0.2));
        return c.rgb * (c.a + saturate((l - _BloomParams.x) / (1.0001 - _BloomParams.x)) * _BloomParams.y);
    }
    float4 fragPre(v2f i) : SV_Target
    {
        float2 d = _MainTex_TexelSize.xy;
        return float4((pre(i.uv + d * float2(-1, -1)) + pre(i.uv + d * float2(1, -1)) + pre(i.uv + d * float2(-1, 1)) + pre(i.uv + d * float2(1, 1))) * 0.25, 1);
    }
    float4 fragBlur(v2f i) : SV_Target
    {
        float2 d = _BlurDir.xy;
        float3 c = tex2D(_MainTex, i.uv).rgb * 0.2270270;
        c += (tex2D(_MainTex, i.uv + d * 1.3846154).rgb + tex2D(_MainTex, i.uv - d * 1.3846154).rgb) * 0.3162162;
        c += (tex2D(_MainTex, i.uv + d * 3.2307692).rgb + tex2D(_MainTex, i.uv - d * 3.2307692).rgb) * 0.0702703;
        return float4(c, 1);
    }
    float4 fragComp(v2f i) : SV_Target
    {
        float3 c = tex2D(_MainTex, i.uv).rgb;
        float3 b = (tex2D(_BloomTex, i.uv).rgb * 0.6 + tex2D(_Bloom2Tex, i.uv).rgb * 0.8) * _BloomParams.z;
        return float4(c + b * (1 - c * 0.35), 1);
    }
    ENDCG
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragPre
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragBlur
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragComp
            ENDCG }
    }
    Fallback Off
}
"""
(out / "TFTF_Bloom.shader").write_text(BLOOM)
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
