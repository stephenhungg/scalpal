Shader "Scalpal/PatientSkin"
{
    // Opaque VR mannequin skin. While an open-body teaching wound is live, the skin inside the wound's
    // 160 x 100 mm wall footprint and depth slab is cut away so the layered wall and the organs under
    // it stay visible; everywhere else the patient is solid. The wound owner sets the globals.
    Properties
    {
        _Color ("Skin color", Color) = (0.65,0.5,0.4,1)
        _Glossiness ("Smoothness", Range(0,1)) = 0.15
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows addshadow
        #pragma target 3.0
        #pragma multi_compile_instancing
        struct Input { float3 worldPos; };
        fixed4 _Color;
        half _Glossiness;
        float4x4 _ScalpalWoundWorldToLocal;
        float _ScalpalWoundWindow;
        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // Wound-local metres: +X along the incision, +Z inward. The slab runs from just outside the
            // wall top to below its 28 mm floor, so distant skin under a lateral wall edge is kept.
            float3 p = mul(_ScalpalWoundWorldToLocal, float4(IN.worldPos, 1)).xyz;
            float inside = step(abs(p.x), 0.08) * step(abs(p.y), 0.05) * step(-0.012, p.z) * step(p.z, 0.03);
            clip(0.5 - _ScalpalWoundWindow * inside);
            o.Albedo = _Color.rgb;
            o.Smoothness = _Glossiness;
            o.Metallic = 0;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Standard"
}
