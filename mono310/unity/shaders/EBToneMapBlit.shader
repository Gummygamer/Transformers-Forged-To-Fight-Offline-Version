// Inferred: composite material; PostFX stages bind their own props at runtime (unknown, harmless if absent). Pass 0 copy, pass 1 blend. Exposure + Reinhard curve applied; _Exposure defaults 1.
Shader "Hidden/EB/PostFX/ToneMapBlit"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}
		_Exposure ("Exposure", Float) = 1
	}
	SubShader
	{
		Cull Off ZWrite Off ZTest Always
CGINCLUDE
#include "UnityCG.cginc"
sampler2D _MainTex;
struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }
ENDCG
		// Pass 0: opaque copy
		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag0
			#pragma target 2.0
			float _Exposure;
			fixed4 Process(float2 uv) { fixed4 c = tex2D(_MainTex, uv); c.rgb *= _Exposure; c.rgb = c.rgb / (1.0 + c.rgb); return c; }
			fixed4 frag0(v2f i) : SV_Target { return Process(i.uv); }
			ENDCG
		}
		// Pass 1: alpha-blended copy (ePOSTFX_BLEND.Blend)
		Pass
		{
			Blend SrcAlpha OneMinusSrcAlpha
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag0
			#pragma target 2.0
			float _Exposure;
			fixed4 Process(float2 uv) { fixed4 c = tex2D(_MainTex, uv); c.rgb *= _Exposure; c.rgb = c.rgb / (1.0 + c.rgb); return c; }
			fixed4 frag0(v2f i) : SV_Target { return Process(i.uv); }
			ENDCG
		}
	}
}
