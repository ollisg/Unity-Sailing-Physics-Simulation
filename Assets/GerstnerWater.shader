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
        [PowerSlider(3)] _FoamNoiseScale ("Foam Noise Scale (1/m)", Range(0.01, 40)) = 0.6
        _FoamNoiseStrength ("Foam Noise Strength", Range(0, 1)) = 0.2
        // Fine breakup from a tileable texture. The wave controller assigns it (Foam Texture field).
        [NoScaleOffset] _FoamTex ("Foam Texture (fine breakup)", 2D) = "gray" {}
        [PowerSlider(3)] _FoamTexScale ("Foam Texture Scale (1/m)", Range(0.05, 40)) = 3
        _FoamTexMix     ("Foam Texture Mix (0 smooth noise, 1 texture)", Range(0, 1)) = 0.8
        // Foam that lingers behind crests (Play mode, from the controller's foam map). 0 = off.
        _FoamTrailAmount ("Persistent Foam Amount", Range(0, 1)) = 1

        [Header(Surface)]
        _Smoothness     ("Smoothness", Range(0, 1)) = 0.95
        _SpecularPower  ("Sun Specular Power", Range(8, 1024)) = 256
        _FresnelPower   ("Fresnel Power", Range(0.5, 8)) = 5
        _ReflectionStrength ("Reflection Strength", Range(0, 2)) = 1
        _ReflectionAmount ("Reflection Amount (0 physical, 1 stylized)", Range(0, 1)) = 0.85
        _MaxReflection ("Max Reflection (lower = more water colour shows)", Range(0, 1)) = 0.75
        _CloudReflection ("Cloud Reflection", Range(0, 1)) = 0.6
        _CloudShadowDarkening ("Cloud Shadow Darkening", Range(0, 1)) = 0.6

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
        _TessPixels     ("Target Triangle Edge (pixels)", Range(2, 64)) = 8
        _TessMax        ("Max Subdivisions Per Edge", Range(1, 64)) = 64

        // Driven by GerstnerWaveController
        [HideInInspector] _WaveCount      ("Wave Count", Float) = 1
        [HideInInspector] _TotalAmplitude ("Total Amplitude", Float) = 1
        [HideInInspector] _WaveTime       ("Wave Time", Float) = 0
        [HideInInspector] _CullMargin     ("Cull Margin", Float) = 5
        [HideInInspector] [NoScaleOffset] _FoamMap ("Foam Map", 2D) = "black" {}
        [HideInInspector] _FoamMapParams  ("Foam Map Params", Vector) = (0, 0, 0, 0)
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

    // Tessellation level for one edge: split it until each piece is about _TessPixels long
    // on screen. The edge's on-screen size comes from its world length, its distance to the
    // camera, the field of view and the screen height, so detail falls off smoothly with
    // distance and follows zoom and resolution automatically. Far away the factor reaches 1
    // (no extra triangles) on its own, so there is no distance cut-off and no visible ring.
    // The distance is measured to the edge's midpoint rather than along the view direction,
    // so turning the camera doesn't change the detail (no shimmering while looking around).
    // Only the shared edge's own endpoints are used, so neighbouring triangles always agree -> no cracks.
    float EdgeTessFactor(float3 p0, float3 p1)
    {
        float3 mid  = 0.5 * (p0 + p1);
        float  dist = max(distance(mid, _WorldSpaceCameraPos), _ProjectionParams.y);

        // pixels per metre: (screen height / 2) * cot(fov / 2) / distance;
        // an orthographic camera has the same scale at every distance
        float  halfScreenScale = 0.5 * _ScreenParams.y * abs(UNITY_MATRIX_P[1][1]);
        float  pxPerMetre = unity_OrthoParams.w > 0.5 ? halfScreenScale : halfScreenScale / dist;

        float  f = distance(p0, p1) * pxPerMetre / max(_TessPixels, 0.5);
        return clamp(f, 1.0, _TessMax);
    }

    // True when a sphere lies completely outside one of the camera's four side planes
    // (left, right, bottom, top), taken from the view-projection matrix.
    bool SphereOutsideFrustum(float3 center, float radius)
    {
        float4 m0 = UNITY_MATRIX_VP[0];
        float4 m1 = UNITY_MATRIX_VP[1];
        float4 m3 = UNITY_MATRIX_VP[3];
        float4 planes[4];
        planes[0] = m3 + m0;
        planes[1] = m3 - m0;
        planes[2] = m3 + m1;
        planes[3] = m3 - m1;

        [unroll]
        for (int i = 0; i < 4; i++)
        {
            float len = length(planes[i].xyz);
            if (dot(planes[i].xyz, center) + planes[i].w < -radius * len)
                return true;
        }
        return false;
    }

    TessFactors PatchConstants(InputPatch<ControlPoint, 3> patch)
    {
        TessFactors f;

        // Frustum culling: patches that can't be seen (behind or beside the camera) get a
        // tessellation factor of 0 and are dropped before any of their vertices are computed.
        // _CullMargin = largest distance the waves can move a point, so nothing visible is lost.
        float3 c = (patch[0].positionWS + patch[1].positionWS + patch[2].positionWS) / 3.0;
        float  r = max(distance(c, patch[0].positionWS),
                   max(distance(c, patch[1].positionWS), distance(c, patch[2].positionWS)));
        if (SphereOutsideFrustum(c, r + _CullMargin))
        {
            f.edge[0] = 0; f.edge[1] = 0; f.edge[2] = 0; f.inside = 0;
            return f;
        }

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
