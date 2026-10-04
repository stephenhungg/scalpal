Shader "Scalpal/Shell/Hub Sky"
{
    Properties { _Top("Lilac",Color)=(.36,.31,.50,1) _Horizon("Rose",Color)=(.81,.69,.72,1) _Bottom("Butter",Color)=(.83,.78,.65,1) }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Top,_Horizon,_Bottom;
            struct v2f { float4 pos:SV_POSITION; float3 direction:TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert(appdata_base v) { v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);o.pos=UnityObjectToClipPos(v.vertex);o.direction=v.vertex.xyz;return o; }
            fixed4 frag(v2f i):SV_Target { UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);float h=normalize(i.direction).y;return h>0?lerp(_Horizon,_Top,smoothstep(0,.8,h)):lerp(_Horizon,_Bottom,smoothstep(0,.6,-h)); }
            ENDCG
        }
    }
}
