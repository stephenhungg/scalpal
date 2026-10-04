Shader "Scalpal/OpenTissueSurface"
{
    Properties
    {
        _Color ("Tissue color", Color) = (0.7,0.4,0.3,1)
        _Glossiness ("Wet surface appearance", Range(0,1)) = 0.55
        _Layer ("Teaching layer", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        LOD 200
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert
        #pragma target 3.0
        #pragma multi_compile_instancing
        struct Input { float2 incisionMeters; };
        fixed4 _Color;
        half _Glossiness;
        float _Layer;
        void vert(inout appdata_full v,out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input,o);
            o.incisionMeters=v.texcoord.xy;
        }
        void surf(Input IN,inout SurfaceOutputStandard o)
        {
            float2 p=IN.incisionMeters;
            float micro=sin(p.x*9500)*sin(p.y*8400);
            float appearance=micro*.022;
            float3 tint=_Color.rgb;
            float relief=micro*.025;
            if(_Layer>0.5 && _Layer<1.5)
            {
                // Original 3–5 mm lobular appearance, not a photographed specimen.
                float2 q=p*250;
                float2 cell=floor(q),f=frac(q)-.5;
                float hash=frac(sin(dot(cell,float2(127.1,311.7)))*43758.5453);
                float lobe=1-smoothstep(.22,.65,length(f));
                tint*=.80+lobe*.24+hash*.10;
                relief+=(lobe-.5)*.16;
            }
            if(_Layer>1.5 && _Layer<2.5)
            {
                // Pale fascia with long, predominantly incision-aligned fibers.
                float fibers=pow(.5+.5*sin(p.y*3400+sin(p.x*95)*.7),5);
                tint=lerp(tint*.87,float3(.94,.92,.85),fibers*.48);
                relief+=fibers*.08;
            }
            if(_Layer>2.5 && _Layer<3.5)
            {
                // Preserved red muscle fascicles: all fibers run along +X.
                float fibers=pow(.5+.5*sin(p.y*2000+sin(p.x*85)*.5),3);
                tint*=.68+fibers*.36;
                appearance+=sin(p.x*390)*.025;
                relief+=fibers*.13;
            }
            if(_Layer>3.5)
            {
                // A thin wet membrane. This is opaque authored teaching appearance;
                // no claim of measured scattering, histology or clinical material fit.
                tint*=.94+sin(p.x*650+p.y*450)*.045;
                relief*=.25;
            }
            o.Albedo=saturate(tint*(1+appearance));
            o.Normal=normalize(float3(relief,relief*.35,1));
            o.Smoothness=_Glossiness;
            o.Metallic=0;
            o.Alpha=1;
        }
        ENDCG
    }
    FallBack "Standard"
}
