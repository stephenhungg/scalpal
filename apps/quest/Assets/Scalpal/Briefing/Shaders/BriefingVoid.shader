// Ethereal briefing void: the inside of one large sphere, a soft vertical gradient (deep blue-grey overhead, a faint
// haze at the horizon, near-black below). Background queue with no depth write, so everything else draws over it.
// For the closing fade BriefingStage moves it to the overlay queue with ZTest Always and lowers _Alpha to 0.
Shader "Scalpal/BriefingVoid"
{
    Properties
    {
        _Top ("Top", Color) = (0.10, 0.12, 0.16, 1)
        _Horizon ("Horizon", Color) = (0.13, 0.14, 0.17, 1)
        _Bottom ("Bottom", Color) = (0.012, 0.013, 0.018, 1)
        _Alpha ("Alpha", Range(0, 1)) = 1
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "IgnoreProjector"="True" "PreviewType"="Skybox" }
        Cull Front ZWrite Off ZTest [_ZTest]
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            fixed4 _Top, _Horizon, _Bottom;
            half _Alpha;

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 direction : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.direction = v.vertex.xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float y = normalize(i.direction).y;
                // Haze peaks just below eye level and falls off softly both ways.
                float haze = exp(-pow((y + 0.05) * 3.2, 2));
                float3 color = lerp(_Bottom.rgb, _Top.rgb, smoothstep(-0.6, 0.8, y));
                color = lerp(color, _Horizon.rgb, haze * 0.65);
                return fixed4(color, _Alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
