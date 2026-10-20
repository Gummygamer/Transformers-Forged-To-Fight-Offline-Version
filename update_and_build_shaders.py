import os
from pathlib import Path

shaders_dir = Path(r"E:\Agent\TFTF-blender\toolchain\unity_build_project\Assets\Shaders")
shaders_dir.mkdir(parents=True, exist_ok=True)

# 1. Standard Unclipped Text Shader Template
text_unlit_template = '''Shader "{shader_name}"
{{
    Properties
    {{
        [PerRendererData] _MainTex ("Alpha (A)", 2D) = "white" {{}}
        _Color ("Tint", Color) = (1,1,1,1)
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }}
    SubShader
    {{
        LOD 200
        Tags
        {{
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
        }}
        Stencil
        {{
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }}
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {{
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Color;

            struct appdata_t
            {{
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            }};

            struct v2f
            {{
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            }};

            v2f vert(appdata_t v)
            {{
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                fixed4 tint = (_Color.a > 0.001 ? _Color : fixed4(1,1,1,1));
                o.color = v.color * tint;
                o.texcoord = v.texcoord;
                return o;
            }}

            fixed4 frag(v2f i) : SV_Target
            {{
                fixed4 col = i.color;
                col.a *= tex2D(_MainTex, i.texcoord).a;
                return col;
            }}
            ENDCG
        }}
    }}
    Fallback Off
}}
'''

# 2. SoftClip Text Shader Template (supports NGUI legacy mainTextureOffset/Scale clipping)
text_softclip_template = '''Shader "{shader_name}"
{{
    Properties
    {{
        [PerRendererData] _MainTex ("Alpha (A)", 2D) = "white" {{}}
        _Color ("Tint", Color) = (1,1,1,1)
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }}
    SubShader
    {{
        LOD 200
        Tags
        {{
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
        }}
        Stencil
        {{
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }}
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {{
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Color;
            float4 _MainTex_ST;
            float2 _ClipSharpness = float2(1000.0, 1000.0);

            struct appdata_t
            {{
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            }};

            struct v2f
            {{
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float2 worldPos : TEXCOORD1;
            }};

            v2f vert(appdata_t v)
            {{
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                fixed4 tint = (_Color.a > 0.001 ? _Color : fixed4(1,1,1,1));
                o.color = v.color * tint;
                o.texcoord = v.texcoord;
                o.worldPos = TRANSFORM_TEX(v.vertex.xy, _MainTex);
                return o;
            }}

            fixed4 frag(v2f i) : SV_Target
            {{
                float2 factor = (float2(1.0, 1.0) - abs(i.worldPos)) * _ClipSharpness;
                float f = clamp(min(factor.x, factor.y), 0.0, 1.0);
                fixed4 col = i.color;
                col.a *= tex2D(_MainTex, i.texcoord).a * f;
                return col;
            }}
            ENDCG
        }}
    }}
    Fallback Off
}}
'''

# Write all text unclipped variants
text_unclipped_names = [
    ("Unlit_Text.shader", "Unlit/Text"),
    ("GUI_Text_Shader.shader", "GUI/Text Shader"),
    ("TFTF_Text.shader", "TFTF/Text"),
]
for fname, sname in text_unclipped_names:
    (shaders_dir / fname).write_text(text_unlit_template.format(shader_name=sname), encoding="utf-8")
    print(f"Wrote unclipped text shader: {fname} -> '{sname}'")

# Write all text softclip variants
text_softclip_names = [
    ("Hidden_Unlit_Text_SoftClip.shader", "Hidden/Unlit/Text (SoftClip)"),
    ("Unlit_Text_SoftClip.shader", "Unlit/Text (SoftClip)"),
    ("GUI_Text_Shader_SoftClip_.shader", "GUI/Text Shader (SoftClip)"),
    ("Hidden_GUI_Text_Shader_SoftClip.shader", "Hidden/GUI/Text Shader (SoftClip)"),
    ("TFTF_Text_SoftClip.shader", "TFTF/Text (SoftClip)"),
]
for fname, sname in text_softclip_names:
    (shaders_dir / fname).write_text(text_softclip_template.format(shader_name=sname), encoding="utf-8")
    print(f"Wrote softclip text shader: {fname} -> '{sname}'")


# 3. UI SoftClip Shader Template (for EBG/UI/BlendColored, etc.)
ui_softclip_template = '''Shader "{shader_name}"
{{
    Properties
    {{
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {{}}
        _Color ("Tint", Color) = (1,1,1,1)
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }}
    SubShader
    {{
        Tags
        {{
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }}
        Stencil
        {{
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }}
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {{
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float2 _ClipSharpness = float2(1000.0, 1000.0);

            struct appdata_t
            {{
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            }};

            struct v2f
            {{
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float2 worldPos : TEXCOORD1;
            }};

            v2f vert(appdata_t i)
            {{
                v2f o;
                o.vertex = UnityObjectToClipPos(i.vertex);
                o.texcoord = i.texcoord;
                fixed4 tint = (_Color.a > 0.001 ? _Color : fixed4(1,1,1,1));
                o.color = i.color * tint;
                o.worldPos = TRANSFORM_TEX(i.vertex.xy, _MainTex);
                return o;
            }}

            fixed4 frag(v2f i) : SV_Target
            {{
                float2 factor = (float2(1.0, 1.0) - abs(i.worldPos)) * _ClipSharpness;
                float f = clamp(min(factor.x, factor.y), 0.0, 1.0);
                fixed4 col = tex2D(_MainTex, i.texcoord);
                if (col.r == 0.0 && col.g == 0.0 && col.b == 0.0 && col.a > 0.0)
                {{
                    col = fixed4(i.color.rgb, i.color.a * col.a);
                }}
                else
                {{
                    col *= i.color;
                }}
                col.a *= f;
                return col;
            }}
            ENDCG
        }}
    }}
    Fallback Off
}}
'''

ui_softclip_names = [
    ("Hidden_EBG_UI_BlendColored_SoftClip.shader", "Hidden/EBG/UI/BlendColored (SoftClip)"),
    ("EBG_UI_BlendColored_SoftClip.shader", "EBG/UI/BlendColored (SoftClip)"),
    ("Hidden_Unlit_Transparent_Colored_SoftClip.shader", "Hidden/Unlit/Transparent Colored (SoftClip)"),
]
for fname, sname in ui_softclip_names:
    (shaders_dir / fname).write_text(ui_softclip_template.format(shader_name=sname), encoding="utf-8")
    print(f"Wrote softclip UI shader: {fname} -> '{sname}'")


# 4. Update standard unclipped UI Shaders
ui_unclipped_template = '''Shader "{shader_name}"
{{
    Properties
    {{
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {{}}
        _Color ("Tint", Color) = (1,1,1,1)
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }}
    SubShader
    {{
        Tags
        {{
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }}
        Stencil
        {{
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }}
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {{
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Color;

            struct appdata_t
            {{
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            }};

            struct v2f
            {{
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            }};

            v2f vert(appdata_t i)
            {{
                v2f o;
                o.vertex = UnityObjectToClipPos(i.vertex);
                o.texcoord = i.texcoord;
                fixed4 tint = (_Color.a > 0.001 ? _Color : fixed4(1,1,1,1));
                o.color = i.color * tint;
                return o;
            }}

            fixed4 frag(v2f i) : SV_Target
            {{
                fixed4 col = tex2D(_MainTex, i.texcoord);
                if (col.r == 0.0 && col.g == 0.0 && col.b == 0.0 && col.a > 0.0)
                {{
                    return fixed4(i.color.rgb, i.color.a * col.a);
                }}
                return col * i.color;
            }}
            ENDCG
        }}
    }}
    Fallback Off
}}
'''

ui_unclipped_list = [
    ("EBG_UI_BlendColored.shader", "EBG/UI/BlendColored"),
    ("EBG_UI_BlendColoredMask.shader", "EBG/UI/BlendColoredMask"),
    ("EBG_UI_BlendFlat.shader", "EBG/UI/BlendFlat"),
    ("EBG_UI_Opaque.shader", "EBG/UI/Opaque"),
    ("EBG_UI_OpaqueFlat.shader", "EBG/UI/OpaqueFlat"),
    ("EB_UI_Ember_Opaque.shader", "EB/UI/Ember/Opaque"),
    ("EB_UI_Ember_Transparent_Blend_Color.shader", "EB/UI/Ember/Transparent/Blend Color"),
    ("EB_UI_Ember_Transparent_Blend_Color_No_Fog.shader", "EB/UI/Ember/Transparent/Blend Color No Fog"),
    ("Unlit_Transparent_Colored.shader", "Unlit/Transparent Colored"),
]

for fname, sname in ui_unclipped_list:
    (shaders_dir / fname).write_text(ui_unclipped_template.format(shader_name=sname), encoding="utf-8")
    print(f"Updated unclipped UI shader: {fname} -> '{sname}'")

print("All shaders updated successfully!")
