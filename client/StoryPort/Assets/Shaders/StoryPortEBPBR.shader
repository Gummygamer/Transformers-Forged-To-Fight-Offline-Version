Shader "StoryPort/EBPBR"
{
    Properties
    {
        _base_tex ("Base Color", 2D) = "white" {}
        _base_col ("Base Tint", Color) = (1,1,1,1)
        _normal_tex ("Normal", 2D) = "bump" {}
        _pbr_composite_tex ("Roughness and Occlusion", 2D) = "white" {}
        _ao_tex ("Occlusion", 2D) = "white" {}
        _metallic_tex ("Metallic", 2D) = "white" {}
        _roughness_tex ("Roughness", 2D) = "white" {}
        _emissive_tex ("Emission", 2D) = "black" {}
        _emissive_col ("Emission Tint", Color) = (1,1,1,1)
        _metallic_range ("Metallic", Range(0,1)) = 0
        _roughness_range ("Roughness", Range(0,1)) = 0.5
        _emissive_range ("Emission Strength", Range(0,8)) = 0
        _normal_scale ("Normal Strength", Range(0,2)) = 1
        _use_pbr_composite ("Use Packed Roughness/AO", Float) = 0
        _use_metallic_tex ("Use Metallic Map", Float) = 0
        _use_roughness_tex ("Use Roughness Map", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 250

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        sampler2D _base_tex;
        sampler2D _normal_tex;
        sampler2D _pbr_composite_tex;
        sampler2D _ao_tex;
        sampler2D _metallic_tex;
        sampler2D _roughness_tex;
        sampler2D _emissive_tex;
        fixed4 _base_col;
        fixed4 _emissive_col;
        half _metallic_range;
        half _roughness_range;
        half _emissive_range;
        half _normal_scale;
        half _use_pbr_composite;
        half _use_metallic_tex;
        half _use_roughness_tex;
        struct Input
        {
            // Unity's surface shader fills uv_<texture-property> from the
            // model's UV0 channel. A generic uv_mesh field is not populated
            // automatically and samples every material at one texel. The game
            // maps all these channels through UV0, so reuse one interpolator
            // to stay within the Android forward-pass interpolator limit.
            float2 uv_base_tex;
        };

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 base = tex2D(_base_tex, IN.uv_base_tex) * _base_col;
            fixed4 packed = tex2D(_pbr_composite_tex, IN.uv_base_tex);
            fixed ao = tex2D(_ao_tex, IN.uv_base_tex).r;
            fixed metallic = tex2D(_metallic_tex, IN.uv_base_tex).r;
            fixed roughness = tex2D(_roughness_tex, IN.uv_base_tex).r;
            fixed3 emission = tex2D(_emissive_tex, IN.uv_base_tex).rgb;

            o.Albedo = base.rgb;
            o.Alpha = base.a;
            o.Normal = UnpackScaleNormal(tex2D(_normal_tex, IN.uv_base_tex), _normal_scale);
            o.Metallic = saturate(_metallic_range * (_use_metallic_tex > 0.5h ? metallic : 1.0h));
            o.Smoothness = saturate(1.0h - ((_use_roughness_tex > 0.5h ? roughness : _use_pbr_composite > 0.5h ? packed.r : 1.0h) * _roughness_range));
            o.Occlusion = _use_pbr_composite > 0.5h ? packed.g : ao;
            o.Emission = emission * _emissive_col.rgb * _emissive_range;
        }
        ENDCG
    }
    FallBack "Standard"
}
