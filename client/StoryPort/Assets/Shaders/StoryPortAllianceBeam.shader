Shader "StoryPort/AllianceBeam"
{
    Properties
    {
        _EmissionTex ("Emissive Gradient", 2D) = "black" {}
        _TintColor ("Particle Tint", Color) = (1,1,1,1)
        _EmissionColor ("Emission Intensity", Color) = (1,1,1,1)
        _EmissionBoost ("Emission Boost", Range(0,4)) = 1
    }

    SubShader
    {
        Tags { "Queue"="Transparent+100" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha One
        ZWrite Off
        ZTest LEqual
        Cull Off
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _EmissionTex;
            float4 _EmissionTex_ST;
            fixed4 _TintColor;
            fixed4 _EmissionColor;
            half _EmissionBoost;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _EmissionTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 gradient = tex2D(_EmissionTex, i.uv);
                fixed3 color = gradient.rgb * _TintColor.rgb * _EmissionColor.rgb * _EmissionBoost;
                half alpha = gradient.a * _TintColor.a;
                return fixed4(color, alpha);
            }
            ENDCG
        }
    }
}
