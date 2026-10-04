Shader "Scalpal/Shell/Frosted Glass"
{
    Properties
    {
        _Color ("Lilac Slate Tint", Color) = (.065,.055,.095,.85)
        _CenterOpacity ("Reading Area Opacity Boost", Range(0,.15)) = .055
        _FrostStrength ("Soft Sky Reflection", Range(0,1)) = .75
        _RimColor ("Lilac Rim", Color) = (.78,.69,.96,1)
        _RimWidth ("Rim Width (Meters)", Range(.0005,.004)) = .0015
        _RimStrength ("Rim Light", Range(0,1)) = .45
        _Sheen ("Surface Sheen", Range(0,.1)) = .025
        _SkyTop ("Blurred Sky Lilac", Color) = (.36,.31,.50,1)
        _SkyHorizon ("Blurred Sky Rose", Color) = (.81,.69,.72,1)
        _SkyBottom ("Blurred Sky Butter", Color) = (.83,.78,.65,1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off ZWrite Off ZTest LEqual Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            fixed4 _Color, _RimColor, _SkyTop, _SkyHorizon, _SkyBottom;
            half _CenterOpacity, _FrostStrength, _RimWidth, _RimStrength, _Sheen;
            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 local : TEXCOORD1;
                float3 world : TEXCOORD2;
                half3 normal : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv; o.local = v.vertex.xy;
                o.world = mul(unity_ObjectToWorld,v.vertex).xyz;
                o.normal = UnityObjectToWorldNormal(float3(0,0,-1));
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // Recover local panel size from the linear UV mapping. This preserves a meter-wide
                // rounded rim on launch, chart and card geometry without per-card material instances.
                float2 size = max(fwidth(i.local) / max(fwidth(i.uv), .000001), .0001);
                float radius = min(.05, size.y * .16); // Same rounded geometry contract as ShellView.
                float2 q = abs(i.local) - size * .5 + radius;
                float distance = length(max(q,0)) + min(max(q.x,q.y),0) - radius;
                float inward = max(0,-distance);
                float aa = max(fwidth(distance), .0002);
                half rim = 1-smoothstep(_RimWidth, _RimWidth+aa*1.5, inward);
                half glow = 1-smoothstep(_RimWidth, .009, inward);
                half readingArea = smoothstep(.006,.032,inward);

                // A smooth analytic sky stands in for a heavily blurred environment reflection.
                // This does NOT sample, refract or blur live scene objects: no GrabPass, render
                // texture, extra camera, noise shimmer or full-screen post-processing is required.
                half3 view = normalize(UnityWorldSpaceViewDir(i.world));
                half3 reflected = reflect(-view,normalize(i.normal));
                half h = reflected.y;
                half3 sky = h > 0 ? lerp(_SkyHorizon.rgb,_SkyTop.rgb,smoothstep(0,.8,h))
                    : lerp(_SkyHorizon.rgb,_SkyBottom.rgb,smoothstep(0,.6,-h));
                // Broad stationary lobes create a satin/frost finish; they do not move with time.
                half lobe = saturate(1-abs(i.uv.x*.65+i.uv.y-.82));
                lobe = lobe*lobe;
                half fresnel = 1-abs(dot(view,normalize(i.normal)));
                fresnel *= fresnel; fresnel *= fresnel;
                half3 color = _Color.rgb + sky * (_FrostStrength * .035)
                    + _Sheen * lobe * half3(.76,.70,1)
                    + _RimColor.rgb * (_RimStrength*rim + .035*glow + .025*fresnel);
                half alpha = saturate(_Color.a + _CenterOpacity*readingArea + rim*.06);
                return fixed4(color,alpha);
            }
            ENDCG
        }
    }
}
