Shader "Scalpal/Tissue"
{
    Properties
    {
        _BaseColor ("Teaching color", Color) = (0.65,0.25,0.24,1)
        _Smoothness ("Surface wetness appearance", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0
        _TextureScale ("Procedural detail per source meter", Float) = 350
        _FiberAmount ("Directional appearance (not mechanics)", Range(0,1)) = 0
        _EmissionColor ("Highlight", Color) = (0,0,0,0)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        #pragma multi_compile_instancing
        struct Input { float3 worldPos; };
        fixed4 _BaseColor, _EmissionColor;
        half _Smoothness, _Metallic, _FiberAmount;
        float _TextureScale;
        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // Source-space procedural mottling works without photographic data or authored UVs.
            float3 p = mul(unity_WorldToObject, float4(IN.worldPos,1)).xyz * _TextureScale;
            float coarse = sin(p.x * .31 + sin(p.z * .22)) * sin(p.y * .27);
            float fine = sin(p.x * 1.7) * sin(p.y * 1.3 + p.z);
            float fiber = sin(p.y * 3 + sin(p.x * .1));
            float detail = coarse * .07 + fine * .025 + fiber * .07 * _FiberAmount;
            o.Albedo = saturate(_BaseColor.rgb * (1 + detail));
            o.Smoothness = saturate(_Smoothness + coarse * .04);
            o.Metallic = _Metallic;
            o.Emission = _EmissionColor.rgb;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Standard"
}
