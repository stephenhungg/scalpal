// Briefing motes: additive soft round glow per particle (no texture), tinted by the particle color. Motes fade with
// distance from the eye (a gentle fog into the void), fade out right at the eye, and fade with _Alpha (the closing fade).
Shader "Scalpal/BriefingMote"
{
    Properties
    {
        _Alpha ("Alpha", Range(0, 1)) = 1
        _FogStart ("Fog start", Float) = 1.5
        _FogEnd ("Fog end", Float) = 7
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Cull Off ZWrite Off ZTest LEqual
        Blend SrcAlpha One
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            half _Alpha, _FogStart, _FogEnd;

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos = UnityWorldToClipPos(world);
                float range = distance(world, _WorldSpaceCameraPos);
                float fog = saturate((range - 0.4) / 0.6) * (1 - saturate((range - _FogStart) / max(0.01, _FogEnd - _FogStart)));
                o.color = v.color;
                o.color.a *= fog * _Alpha;
                o.uv = v.uv * 2 - 1;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float glow = saturate(1 - length(i.uv));
                return fixed4(i.color.rgb, i.color.a * glow * glow);
            }
            ENDCG
        }
    }
    Fallback Off
}
