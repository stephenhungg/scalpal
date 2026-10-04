Shader "Scalpal/Encounter Office/Glass"
{
    Properties { _Color ("Glass Tint", Color) = (0.06,0.09,0.10,0.86) }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off ZWrite Off ZTest LEqual Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            fixed4 _Color;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex); o.uv=v.uv; return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // Tinted translucent glass, with a gentle vertical sheen. No screen grab or full-screen blur.
                return fixed4(_Color.rgb + (.025 * i.uv.y), _Color.a);
            }
            ENDCG
        }
    }
}
