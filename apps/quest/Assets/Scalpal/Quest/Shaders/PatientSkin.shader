Shader "Scalpal/PatientSkin"
{
    // Opaque VR mannequin skin. While an open-body teaching wound is live, the skin inside the wound's
    // finite measured incision aperture is cut away so the local wound and the organs under
    // it stay visible; everywhere else the patient is solid. The wound owner sets the globals.
    // Outside the field, blade strokes on the skin (Scalpal.Surgery.PatientIncisions) are drawn here from a
    // fixed ring of segments in registered torso metres: a dark parted cut, a reddened margin, a bead of
    // blood along the cut and rivulets running downhill along gravity. With no segments the loop never runs.
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
        #pragma surface surf Standard fullforwardshadows addshadow vertex:vert
        #pragma target 3.0
        #pragma multi_compile_instancing
        #define SCALPAL_INCISIONS 64
        struct Input { float3 worldPos; float3 patientPos; };
        fixed4 _Color;
        half _Glossiness;
        float4x4 _ScalpalWoundWorldToLocal;
        float _ScalpalWoundWindow;
        float4 _ScalpalWoundOpening; // centreXY, half length, half width (wound metres)
        float4 _ScalpalWoundAxis; // normalized incision direction XY
        float4x4 _ScalpalIncisionWorldToLocal;
        float4 _ScalpalIncisionGravity;            // torso-local unit downhill direction
        float _ScalpalIncisionCount;
        float4 _ScalpalIncisionA[SCALPAL_INCISIONS]; // xyz start (torso m), w half opening of the cut (m)
        float4 _ScalpalIncisionB[SCALPAL_INCISIONS]; // xyz end, w trickle length (m): > 0 wet, < 0 dried
        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.patientPos = mul(_ScalpalIncisionWorldToLocal, mul(unity_ObjectToWorld, v.vertex)).xyz;
        }
        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // Wound-local metres: +X along the incision, +Z inward. The slab runs from just outside the
            // wall top to below its 28 mm floor, so distant skin under a lateral wall edge is kept.
            float3 p = mul(_ScalpalWoundWorldToLocal, float4(IN.worldPos, 1)).xyz;
            float2 axis=_ScalpalWoundAxis.xy;
            axis/=max(length(axis),1e-6);
            float2 delta=p.xy-_ScalpalWoundOpening.xy;
            float2 aperture=float2(dot(delta,axis),dot(delta,float2(-axis.y,axis.x)))/max(_ScalpalWoundOpening.zw,float2(.001,.001));
            float inside=step(dot(aperture,aperture),1)*step(-.012,p.z)*step(p.z,.045);
            clip(0.5 - _ScalpalWoundWindow * inside);
            float3 albedo = _Color.rgb;
            half smoothness = _Glossiness;
            float3 q = IN.patientPos, g = _ScalpalIncisionGravity.xyz;
            int count = (int)_ScalpalIncisionCount;
            for (int i = 0; i < SCALPAL_INCISIONS; i++)
            {
                if (i >= count) break;
                float4 A = _ScalpalIncisionA[i], B = _ScalpalIncisionB[i];
                float3 a = A.xyz, ab = B.xyz - A.xyz;
                float open = A.w, trickle = abs(B.w), wet = step(0, B.w);
                // Broad phase: skip segments whose cut, margin and rivulets cannot reach this fragment.
                float bound = length(ab) * 0.5 + trickle + open * 4 + 0.004;
                float3 fromMid = q - (a + ab * 0.5);
                if (dot(fromMid, fromMid) > bound * bound) continue;
                float t = saturate(dot(q - a, ab) / max(dot(ab, ab), 1e-10));
                float d = length(q - (a + ab * t));
                float3 blood = lerp(float3(0.27, 0.06, 0.035), float3(0.54, 0.012, 0.012), wet);
                half bloodGloss = lerp(0.3, 0.8, wet);
                // Reddened, slightly swollen margin either side of the parted edges.
                float margin = 1 - smoothstep(open, open * 3.5 + 0.0025, d);
                albedo = lerp(albedo, float3(0.66, 0.24, 0.2), margin * 0.7);
                // Rivulets: below the cut along gravity, within a narrow band of the cut seen from above.
                float3 abh = ab - g * dot(ab, g);
                float lh2 = dot(abh, abh);
                float th = lh2 > 1e-8 ? saturate(dot(q - a, abh) / lh2) : 0;
                float3 dc = q - (a + ab * th);
                float down = dot(dc, g);
                float lateral = length(dc - g * down);
                float along = th * sqrt(lh2);
                float slot = floor(along / 0.009) + i * 7.0;
                float reach = trickle * (0.45 + 0.55 * frac(sin(slot * 12.9898) * 43758.5453));
                float lane = (th <= 0 || th >= 1) ? 1 : saturate(1 - abs(frac(along / 0.009) - 0.5) * 3);
                float width = 0.0012 * lane * saturate(1.15 - down / max(reach, 1e-5));
                float rivulet = step(-open, down) * step(down, reach) * step(lateral, width);
                // A bead of blood along the cut, and the dark parted cut itself.
                float bead = step(d, open + 0.0008);
                float cut = step(d, open);
                float stain = max(rivulet, bead);
                albedo = lerp(albedo, blood, stain);
                smoothness = lerp(smoothness, bloodGloss, stain);
                albedo = lerp(albedo, float3(0.16, 0.0, 0.01), cut);
                smoothness = lerp(smoothness, 0.55, cut);
            }
            o.Albedo = albedo;
            o.Smoothness = smoothness;
            o.Metallic = 0;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Standard"
}
