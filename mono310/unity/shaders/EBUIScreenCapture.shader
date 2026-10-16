// Inferred from EBUIScreenCapture.cs L254-261: _Contrast,_Saturation,_Brightness floats, _Tint color, _Overlay tex, _OverlayOpacity, _StampThreshold, _StampAmount floats. Blit uses pass 0 only; pass 1 duplicate.
Shader "Hidden/EB/UIScreenCapture"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}
		_Contrast ("Contrast", Float) = 1
		_Saturation ("Saturation", Float) = 1
		_Brightness ("Brightness", Float) = 0
		_Tint ("Tint", Color) = (1,1,1,1)
		_Overlay ("Overlay", 2D) = "black" {}
		_OverlayOpacity ("Overlay Opacity", Float) = 0
		_StampThreshold ("Stamp Threshold", Float) = 1
		_StampAmount ("Stamp Amount", Float) = 0
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
			sampler2D _Overlay;
			float _Contrast, _Saturation, _Brightness, _OverlayOpacity, _StampThreshold, _StampAmount;
			fixed4 _Tint;
			fixed4 Process(float2 uv)
			{
				fixed4 c = tex2D(_MainTex, uv);
				float l = dot(c.rgb, float3(0.299, 0.587, 0.114));
				c.rgb = lerp(l.xxx, c.rgb, _Saturation);
				c.rgb = (c.rgb - 0.5) * _Contrast + 0.5 + _Brightness;
				c.rgb *= _Tint.rgb;
				fixed4 o = tex2D(_Overlay, uv);
				c.rgb = lerp(c.rgb, o.rgb, _OverlayOpacity);
				return c;
			}
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
			sampler2D _Overlay;
			float _Contrast, _Saturation, _Brightness, _OverlayOpacity, _StampThreshold, _StampAmount;
			fixed4 _Tint;
			fixed4 Process(float2 uv)
			{
				fixed4 c = tex2D(_MainTex, uv);
				float l = dot(c.rgb, float3(0.299, 0.587, 0.114));
				c.rgb = lerp(l.xxx, c.rgb, _Saturation);
				c.rgb = (c.rgb - 0.5) * _Contrast + 0.5 + _Brightness;
				c.rgb *= _Tint.rgb;
				fixed4 o = tex2D(_Overlay, uv);
				c.rgb = lerp(c.rgb, o.rgb, _OverlayOpacity);
				return c;
			}
			fixed4 frag0(v2f i) : SV_Target { return Process(i.uv); }
			ENDCG
		}
	}
}
