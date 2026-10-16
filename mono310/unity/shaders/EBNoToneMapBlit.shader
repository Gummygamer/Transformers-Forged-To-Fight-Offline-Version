// Inferred: composite material without tone mapping. Pass 0 copy, pass 1 blend. PostFX stages bind own props at runtime (unknown).
Shader "Hidden/EB/PostFX/NoToneMapBlit"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}

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
			fixed4 Process(float2 uv) { return tex2D(_MainTex, uv); }
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
			fixed4 Process(float2 uv) { return tex2D(_MainTex, uv); }
			fixed4 frag0(v2f i) : SV_Target { return Process(i.uv); }
			ENDCG
		}
	}
}
