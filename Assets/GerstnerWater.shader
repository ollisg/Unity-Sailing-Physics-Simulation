Shader "Custom/URP/GerstnerWater"
{
    Properties
    {
        _DeepColor      ("Deep Color", Color) = (0.02, 0.18, 0.28, 1)
        _ShallowColor   ("Shallow / Crest Color", Color) = (0.10, 0.55, 0.60, 1)

        [Header(Foam)]
        _FoamColor      ("Foam Color", Color) = (1, 1, 1, 1)
        _FoamThreshold  ("Foam Threshold (crest sharpness)", Range(0, 1)) = 0.2
        _FoamSoftness   ("Foam Softness", Range(0.01, 1)) = 0.25
        _FoamNoiseScale ("Foam Noise Scale (1/m)", Range(0.01, 5)) = 0.6
        _FoamNoiseStrength ("Foam Noise Strength", Range(0, 1)) = 0.2

        [Header(Surface)]
        _Smoothness     ("Smoothness", Range(0, 1)) = 0.95
        _SpecularPower  ("Sun Specular Power", Range(8, 1024)) = 256
        _FresnelPower   ("Fresnel Power", Range(0.5, 8)) = 5
        _ReflectionStrength ("Reflection Strength", Range(0, 2)) = 1
        _ReflectionAmount ("Reflection Amount (0 physical, 1 stylized)", Range(0, 1)) = 0.85

        [Header(Detail Normals)]
        [Normal][NoScaleOffset] _NormalMap ("Normal Map", 2D) = "bump" {}
        _NormalStrength ("Normal Strength", Range(0, 2)) = 0.6
        _NormalTiling   ("Normal Tiling (1/m)", Range(0.005, 2)) = 0.12
        _NormalSpeed    ("Normal Scroll Speed", Range(0, 0.2)) = 0.02

        [Header(Sky Reflection)]
        [NoScaleOffset] _SkyTex ("Sky Texture (equirect 2D)", 2D) = "grey" {}
        [NoScaleOffset] _SkyCube ("Sky Cubemap", Cube) = "grey" {}
        _SkyTint        ("Sky Tint", Color) = (0.5, 0.5, 0.5, 0.5)
        _SkyExposure    ("Sky Exposure", Range(0, 8)) = 1
        _SkyRotation    ("Sky Rotation", Range(0, 360)) = 0
        [Enum(ProbeOnly,0,Equirect2D,1,Cubemap,2)] _UseSkyTex ("Sky Source", Float) = 0
        [HideInInspector] _EnvReflectionResolution ("Env Reflection Resolution", Float) = 128

        [Header(Tessellation)]
        _TessMax        ("Max Tessellation (near camera)", Range(1, 64)) = 16
        _TessMinDist    ("Full Detail Until Distance", Float) = 20
        _TessMaxDist    ("No Tessellation Beyond Distance", Float) = 150

        // Driven by GerstnerWaveController
        [HideInInspector] _WaveCount      ("Wave Count", Float) = 1
        [HideInInspector] _TotalAmplitude ("Total Amplitude", Float) = 1
        [HideInInspector] _WaveTime       ("Wave Time", Float) = 0
    }


    // Shared by every pass. NOTE: Unity applies HLSLINCLUDE blocks to all passes in the
    // file (not only the SubShader they sit in), so there must be just one.
    HLSLINCLUDE
    #include "GerstnerWaterCommon.hlsl"

    struct Attributes
    {
        float4 positionOS : POSITION;
    };

    // Plain vertex program (fallback SubShader, no tessellation)
    Varyings vert(Attributes IN)
    {
        return BuildWaterVertex(TransformObjectToWorld(IN.positionOS.xyz));
    }

    // Tessellation stages: only compiled for passes with #pragma target 4.6
    #if SHADER_TARGET >= 46


    struct ControlPoint
    {
        float3 positionWS : INTERNALTESSPOS;
    };

    struct TessFactors
    {
        float edge[3] : SV_TessFactor;
        float inside  : SV_InsideTessFactor;
    };

    // Vertex stage only passes the flat grid position to the hull stage.
    ControlPoint TessVert(Attributes IN)
    {
        ControlPoint o;
        o.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
        return o;
    }

    // Tessellation level for one edge, based on distance from the camera to the edge midpoint.
    // Using the midpoint means two triangles sharing an edge always agree -> no cracks.
    float EdgeTessFactor(float3 p0, float3 p1)
    {
        float3 mid  = 0.5 * (p0 + p1);
        float  dist = distance(mid, _WorldSpaceCameraPos);
        float  t    = saturate((dist - _TessMinDist) / max(_TessMaxDist - _TessMinDist, 1e-3));
        return max(1.0, lerp(_TessMax, 1.0, t));
    }

    TessFactors PatchConstants(InputPatch<ControlPoint, 3> patch)
    {
        TessFactors f;
        f.edge[0] = EdgeTessFactor(patch[1].positionWS, patch[2].positionWS);
        f.edge[1] = EdgeTessFactor(patch[2].positionWS, patch[0].positionWS);
        f.edge[2] = EdgeTessFactor(patch[0].positionWS, patch[1].positionWS);
        f.inside  = (f.edge[0] + f.edge[1] + f.edge[2]) / 3.0;
        return f;
    }

    [domain("tri")]
    [outputcontrolpoints(3)]
    [outputtopology("triangle_cw")]
    [partitioning("fractional_odd")] // smooth blending between levels, avoids popping
    [patchconstantfunc("PatchConstants")]
    [maxtessfactor(64.0)]
    ControlPoint Hull(InputPatch<ControlPoint, 3> patch, uint id : SV_OutputControlPointID)
    {
        return patch[id];
    }

    // Runs for every new (tessellated) vertex: interpolate the flat position,
    // then apply the Gerstner displacement.
    [domain("tri")]
    Varyings Domain(TessFactors factors, OutputPatch<ControlPoint, 3> patch,
                    float3 bary : SV_DomainLocation)
    {
        float3 flatPosWS = patch[0].positionWS * bary.x
                         + patch[1].positionWS * bary.y
                         + patch[2].positionWS * bary.z;
        return BuildWaterVertex(flatPosWS);
    }
    #endif
    ENDHLSL

    // ------------------------------------------------------------------
    // SubShader 1: GPU tessellation (needs shader model 4.6: DX11+, Metal, Vulkan, GLES 3.2)
    // ------------------------------------------------------------------
    SubShader
    {
        // Opaque + depth write: the closest wave always wins the depth test,
        // so waves further back can no longer draw over the ones in front.
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }


        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 4.6
            #pragma vertex TessVert
            #pragma hull Hull
            #pragma domain Domain
            #pragma fragment frag

            half4 frag(Varyings IN) : SV_Target
            {
                return ShadeWater(IN);
            }
            ENDHLSL
        }

        // Writes the displaced surface into the camera depth texture / depth prepass,
        // so depth priming, SSAO, fog etc. see the actual waves.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma target 4.6
            #pragma vertex TessVert
            #pragma hull Hull
            #pragma domain Domain
            #pragma fragment fragDepth

            half fragDepth(Varyings IN) : SV_Target
            {
                return IN.positionCS.z;
            }
            ENDHLSL
        }
    }

    // ------------------------------------------------------------------
    // SubShader 2: fallback without tessellation (used when the GPU/API doesn't support it)
    // ------------------------------------------------------------------
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }


        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            half4 frag(Varyings IN) : SV_Target
            {
                return ShadeWater(IN);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment fragDepth

            half fragDepth(Varyings IN) : SV_Target
            {
                return IN.positionCS.z;
            }
            ENDHLSL
        }
    }
}
