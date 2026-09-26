Shader "StoryPort/ChicagoRoad"
{
    Properties
    {
        _MainTex ("Chicago Asphalt", 2D) = "gray" {}
        _WorldScale ("Meters Per Tile", Float) = 6
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        sampler2D _MainTex;
        float _WorldScale;
        struct Input { float3 worldPos; };
        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 color = tex2D(_MainTex, IN.worldPos.xz / max(_WorldScale, 0.01));
            o.Albedo = color.rgb;
            o.Alpha = 1;
            o.Metallic = 0;
            o.Smoothness = 0.08;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
