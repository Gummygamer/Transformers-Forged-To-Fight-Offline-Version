// Metadata interface only: temporary material copies use this to read saved donor values
// when an unsupported Android shader makes Material.HasProperty return false on Windows.
Shader "TFTF/DonorProperties"
{
    Properties
    {
        _MainTex ("Main texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _base_tex ("Base", 2D) = "white" {}
        _normal_tex ("Normal", 2D) = "bump" {}
        _base2_tex ("Terrain color layer", 2D) = "white" {}
        _mask_tex ("Terrain mask", 2D) = "white" {}
        _roughness_tex ("Roughness", 2D) = "white" {}
        _metallic_tex ("Metallic", 2D) = "black" {}
        _ao_tex ("Occlusion", 2D) = "white" {}
        _emissive_tex ("Emission", 2D) = "black" {}
        _pbr_composite_tex ("Packed material", 2D) = "black" {}
        _particle_lifetime_color_tex ("Particle color", 2D) = "white" {}
        _base_col ("Base color", Color) = (0.5,0.5,0.5,1)
        _emissive_intensity_col ("Emission color", Color) = (1,1,1,1)
        _emissive_overbright_range ("Emission intensity", Float) = 8
        _emissive_ramp_range ("Emission exponent", Float) = 1
        _emissive_pulse_intensity_range ("Emission pulse", Float) = 0
        _emissive_pulse_time_range ("Emission speed", Float) = 1
        _metallic_range ("Metallic", Float) = 0
        _roughness_range ("Roughness", Float) = 0.6
        _blendoffset_range ("Mask offset", Float) = 0
        _blendremap_range ("Mask scale", Float) = 1
    }
    SubShader { Pass {} }
    Fallback Off
}
