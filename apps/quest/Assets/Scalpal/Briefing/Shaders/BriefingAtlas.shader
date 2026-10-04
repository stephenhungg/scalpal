// One merged briefing mesh, one draw call. Each vertex reads its part's row data from _PartTex (column = uv2.x):
//   row 0 offset.xyz, alpha | row 1 scale, glow, spin angle (rad, about mesh-local Y), highlight | row 2 pivot | row 3 color
// p' = Ry(angle) * ((p - pivot) * scale) + pivot + offset   (BriefingAtlas.Apply mirrors this for the picker)
// Opaque single pass. Fading parts use a screen-space dither so depth stays correct; hidden parts collapse and clip.
Shader "Scalpal/BriefingAtlas"
{
    Properties
    {
        _PartTex ("Part data", 2D) = "black" {}
        _PartCount ("Part count", Float) = 1
        _GlowColor ("Glow", Color) = (0.82, 0.86, 0.9, 1)
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" "IgnoreProjector"="True" }
        Cull Off ZWrite On ZTest LEqual
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _PartTex;
            float _PartCount;
            fixed4 _GlowColor;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 part : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                float3 world : TEXCOORD1;
                half4 color : TEXCOORD2;
                half4 fx : TEXCOORD3; // alpha, glow, highlight
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 Row(float index, float row)
            {
                return tex2Dlod(_PartTex, float4((index + 0.5) / _PartCount, (row + 0.5) / 4.0, 0, 0));
            }
            float3 SpinY(float3 v, float s, float c) { return float3(c * v.x + s * v.z, v.y, -s * v.x + c * v.z); }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float index = floor(v.part.x + 0.5);
                float4 a = Row(index, 0);
                float4 b = Row(index, 1);
                float4 pivot = Row(index, 2);
                float s, c; sincos(b.z, s, c);
                float3 p = SpinY((v.vertex.xyz - pivot.xyz) * b.x, s, c) + pivot.xyz + a.xyz;
                float4 world = mul(unity_ObjectToWorld, float4(p, 1));
                o.world = world.xyz;
                o.pos = mul(UNITY_MATRIX_VP, world);
                o.normal = UnityObjectToWorldNormal(SpinY(v.normal, s, c));
                o.color = Row(index, 3);
                o.fx = half4(a.w, b.y, b.w, 0);
                // Hidden part: every vertex collapses to one point outside the clip volume, so its triangles are culled.
                if (a.w < 0.004) o.pos = float4(2, 2, 2, 1);
                return o;
            }

            fixed4 frag(v2f i, fixed facing : VFACE) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // Peeling layers fade by screen-door dither (interleaved gradient noise): no sorting, depth stays right.
                float noise = frac(52.9829189 * frac(dot(floor(i.pos.xy), float2(0.06711056, 0.00583715))));
                clip(i.fx.x - noise * 0.999);
                float3 n = normalize(i.normal) * (facing > 0 ? 1 : -1);
                float3 view = normalize(_WorldSpaceCameraPos - i.world);
                float3 light = normalize(view + float3(0, 0.7, 0));
                float diffuse = saturate(dot(n, light));
                float specular = pow(saturate(dot(reflect(-light, n), view)), 24) * 0.12;
                float3 color = i.color.rgb * (0.36 + 0.64 * diffuse) + specular;
                float rim = pow(1 - saturate(dot(n, view)), 2.5);
                color += _GlowColor.rgb * (rim * 0.9 + 0.10) * i.fx.y;
                color = lerp(color, color * 1.15 + 0.12, i.fx.z);
                return fixed4(color, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
