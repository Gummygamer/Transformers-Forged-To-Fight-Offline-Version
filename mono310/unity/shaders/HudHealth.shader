// Replacement for the fight HUD health bar shader (the original cannot be recovered from the rip).
// The bar is a textureless quad (UIVectorArt) whose uv.x runs 0..1 across it and whose vertex colour carries the state:
//   a = remaining health (0..1), g = trailing "recent damage" amount (0..1), r = hit flash (0..1).
// Fills from the inner edge (uv.x = 1) towards the outer edge; the part between the trail and the health is the
// damage that was just taken; the rest is transparent so the bar's rim shows through.
Shader "TFTF/HudHealth"
{
    Properties
    {
        _MainTex ("Unused", 2D) = "white" {}
        _Health ("Health colour", Color) = (0.15, 0.95, 0.25, 1)
        _Damage ("Damage trail colour", Color) = (1.0, 0.55, 0.10, 1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" }
        Cull Off Lighting Off ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata_t { float4 vertex : POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; };
            fixed4 _Health, _Damage;
            v2f vert(appdata_t i)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(i.vertex);
                o.color = i.color;
                o.uv = i.uv;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float fromInner = 1.0 - i.uv.x;          // 0 at the inner edge, 1 at the outer edge
                float health = i.color.a;
                float trail = max(i.color.g, health);
                if (fromInner > trail) discard;
                fixed3 c = (fromInner <= health) ? _Health.rgb : _Damage.rgb;
                c = lerp(c, fixed3(1, 1, 1), saturate(i.color.r));
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
