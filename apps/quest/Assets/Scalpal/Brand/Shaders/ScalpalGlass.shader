Shader "Scalpal/Brand/Glass"
{
    // Dark translucent card with a crisp hairline (site: white ink on near-black). Focused and
    // primary surfaces swap the hairline for the spark gradient (orange to gold) and a faint warm glow.
    // No GrabPass, blur, render texture or extra camera: one cheap transparent pass for Quest.
    Properties
    {
        _Color ("Fill", Color) = (.018,.018,.022,.84)
        _RimColor ("Hairline (left)", Color) = (1,1,1,.16)
        _RimColorB ("Hairline (right)", Color) = (1,1,1,.16)
        _RimWidth ("Hairline Width (Metres)", Range(.0004,.004)) = .0011
        _RimStrength ("Hairline Strength", Range(0,1)) = 1
        _RadiusMax ("Corner Radius Cap (Metres)", Range(0,.1)) = .05
        _RadiusFraction ("Corner Radius / Height", Range(0,.5)) = .16
        _Glow ("Accent Glow", Range(0,1)) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        // Depth write keeps text that sits behind a card (another panel's label, a patient caption)
        // from bleeding through it; nearly-clear pixels are clipped so corners never occlude.
        Cull Off ZWrite On ZTest LEqual Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            fixed4 _Color, _RimColor, _RimColorB;
            half _RimWidth, _RimStrength, _RadiusMax, _RadiusFraction, _Glow;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; float2 local : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                // Metres on the panel plane, so scaled quads and meshes share one rim/radius contract.
                float2 scale = float2(length(unity_ObjectToWorld._m00_m10_m20), length(unity_ObjectToWorld._m01_m11_m21));
                o.local = v.vertex.xy * scale;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // Panel size from the linear UV mapping (uv 0..1 spans the surface).
                float2 size = max(fwidth(i.local) / max(fwidth(i.uv), .000001), .0001);
                float2 center = (i.uv - .5) * size;
                float radius = min(_RadiusMax, size.y * _RadiusFraction);
                float2 q = abs(center) - size * .5 + radius;
                float distance = length(max(q, 0)) + min(max(q.x, q.y), 0) - radius;
                float inward = max(0, -distance);
                float aa = max(fwidth(distance), .0002);
                half inside = 1 - smoothstep(-aa, aa, distance);
                half rim = (1 - smoothstep(_RimWidth, _RimWidth + aa * 1.5, inward)) * _RimStrength;
                half glow = (1 - smoothstep(0, .016, inward)) * _Glow;
                fixed4 rimColor = lerp(_RimColor, _RimColorB, saturate(i.uv.x));
                // A level, symmetric top-to-bottom shade: no diagonal sheen that could read as a tilt.
                half3 color = _Color.rgb * (1 + .18 * (i.uv.y - .5));
                color += rimColor.rgb * glow * .10;
                color = lerp(color, rimColor.rgb, rim * rimColor.a);
                half alpha = max(_Color.a, rim * rimColor.a) * inside;
                clip(alpha - .02);
                return fixed4(color, alpha);
            }
            ENDCG
        }
    }
}
