// Gelcoat hull shader for the ILCA 7.
// The hull mesh's UVs can't be painted on (the deck is collapsed to a point), so this shader
// paints from the boat's own object space instead: tiling textures are projected in metres and
// the deck non-skid / waterline are found from object-space height and surface direction.
// Masks stay locked to the hull when the boat heels and pitches.
Shader "Custom/URP/BoatHull"
{
    Properties
    {
        [Header(Gelcoat)]
        _GelcoatColor       ("Gelcoat Color", Color) = (0.93, 0.94, 0.93, 1)
        _GelcoatSmoothness  ("Gelcoat Smoothness", Range(0, 1)) = 0.85
        [NoScaleOffset] _VariationTex ("Variation (R tone, G scratches, B grime)", 2D) = "grey" {}
        _VariationTileSize  ("Variation Tile Size (m)", Float) = 1.5
        _VariationStrength  ("Tone Variation", Range(0, 0.2)) = 0.035
        _ScratchStrength    ("Scratch Dullness", Range(0, 1)) = 0.35
        _GrimeColor         ("Grime Color", Color) = (0.55, 0.52, 0.45, 1)
        _GrimeStrength      ("Grime Specks", Range(0, 1)) = 0.35

        [Header(Deck Non Skid)]
        _NonSkidColor       ("Non-Skid Color", Color) = (0.86, 0.88, 0.89, 1)
        [Normal][NoScaleOffset] _NonSkidNormal ("Non-Skid Normal", 2D) = "bump" {}
        [NoScaleOffset] _NonSkidCavity ("Non-Skid Cavity", 2D) = "white" {}
        _NonSkidTileSize    ("Non-Skid Tile Size (m)", Float) = 0.1
        _NonSkidNormalStrength ("Non-Skid Bump", Range(0, 2)) = 0.8
        _NonSkidSmoothness  ("Non-Skid Smoothness", Range(0, 1)) = 0.4
        _DeckMinNormalY     ("Deck Starts At Slope (normal.y)", Range(0, 1)) = 0.8
        _DeckBlend          ("Deck Edge Softness", Range(0.01, 0.5)) = 0.08
        _DeckMinHeight      ("Deck Min Height (m)", Float) = 0.12

        [Header(Waterline)]
        _WaterlineHeight    ("Waterline Height (m)", Float) = 0.085
        _WaterlineWidth     ("Waterline Band Width (m)", Float) = 0.025
        _WaterlineColor     ("Waterline Stain (A = strength)", Color) = (0.72, 0.68, 0.55, 0.35)
        _BelowWaterTint     ("Below Waterline Tint", Color) = (0.92, 0.95, 0.94, 1)
        _BelowWaterSmoothness ("Below Waterline Smoothness", Range(0, 1)) = 0.6
    }

    // One shared block for every pass (Unity applies HLSLINCLUDE to all passes in the file).
    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

    CBUFFER_START(UnityPerMaterial)
        float4 _GelcoatColor;
        float  _GelcoatSmoothness;
        float  _VariationTileSize;
        float  _VariationStrength;
        float  _ScratchStrength;
        float4 _GrimeColor;
        float  _GrimeStrength;
        float4 _NonSkidColor;
        float  _NonSkidTileSize;
        float  _NonSkidNormalStrength;
        float  _NonSkidSmoothness;
        float  _DeckMinNormalY;
        float  _DeckBlend;
        float  _DeckMinHeight;
        float  _WaterlineHeight;
        float  _WaterlineWidth;
        float4 _WaterlineColor;
        float4 _BelowWaterTint;
        float  _BelowWaterSmoothness;
    CBUFFER_END

    TEXTURE2D(_VariationTex);   SAMPLER(sampler_VariationTex);
    TEXTURE2D(_NonSkidNormal);  SAMPLER(sampler_NonSkidNormal);
    TEXTURE2D(_NonSkidCavity);  SAMPLER(sampler_NonSkidCavity);

    // Object space in metres, whatever the import / transform scale is.
    float3 ObjectScale()
    {
        return float3(length(UNITY_MATRIX_M._m00_m10_m20),
                      length(UNITY_MATRIX_M._m01_m11_m21),
                      length(UNITY_MATRIX_M._m02_m12_m22));
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex HullVert
            #pragma fragment HullFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog

            struct HullAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct HullVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 posM       : TEXCOORD2; // object space, metres
                float3 normalOS   : TEXCOORD3;
                half   fogFactor  : TEXCOORD4;
            };

            HullVaryings HullVert(HullAttributes IN)
            {
                HullVaryings OUT;
                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = pos.positionCS;
                OUT.positionWS = pos.positionWS;
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                float3 s = ObjectScale();
                OUT.posM       = IN.positionOS.xyz * s;
                OUT.normalOS   = IN.normalOS / max(s, 1e-6); // correct for non-uniform scale
                OUT.fogFactor  = ComputeFogFactor(pos.positionCS.z);
                return OUT;
            }

            half4 HullFrag(HullVaryings IN) : SV_Target
            {
                float3 pM = IN.posM;
                float3 nO = normalize(IN.normalOS);

                // --- gelcoat: triplanar tone / scratch / grime variation
                float3 w = pow(abs(nO), 4.0);
                w /= max(w.x + w.y + w.z, 1e-5);
                float  inv = 1.0 / max(_VariationTileSize, 1e-3);
                float3 var = SAMPLE_TEXTURE2D(_VariationTex, sampler_VariationTex, pM.zy * inv).rgb * w.x
                           + SAMPLE_TEXTURE2D(_VariationTex, sampler_VariationTex, pM.xz * inv).rgb * w.y
                           + SAMPLE_TEXTURE2D(_VariationTex, sampler_VariationTex, pM.xy * inv).rgb * w.z;

                float  tone   = 1.0 + (var.r - 0.5) * 2.0 * _VariationStrength;
                float3 albedo = _GelcoatColor.rgb * tone;
                albedo = lerp(albedo, _GrimeColor.rgb, var.b * _GrimeStrength);
                float  smooth = _GelcoatSmoothness * lerp(1.0 - _ScratchStrength, 1.0, var.g);

                // --- deck non-skid: upward-facing surfaces above the deck height, projected from the top
                float deck = smoothstep(_DeckMinNormalY, _DeckMinNormalY + _DeckBlend, nO.y)
                           * step(_DeckMinHeight, pM.y);
                float2 nsUV  = pM.xz / max(_NonSkidTileSize, 1e-3);
                float3 nsN   = UnpackNormalScale(SAMPLE_TEXTURE2D(_NonSkidNormal, sampler_NonSkidNormal, nsUV), _NonSkidNormalStrength);
                float  cav   = SAMPLE_TEXTURE2D(_NonSkidCavity, sampler_NonSkidCavity, nsUV).r;
                albedo = lerp(albedo, _NonSkidColor.rgb * cav * tone, deck);
                smooth = lerp(smooth, _NonSkidSmoothness, deck);

                float3 N  = normalize(IN.normalWS);
                float3 Tw = normalize(TransformObjectToWorldDir(float3(1, 0, 0)));
                float3 Bw = normalize(TransformObjectToWorldDir(float3(0, 0, 1)));
                N = normalize(N + (Tw * nsN.x + Bw * nsN.y) * deck);

                // --- waterline stain + slightly duller hull below it
                float band  = 1.0 - smoothstep(0.0, max(_WaterlineWidth, 1e-4), abs(pM.y - _WaterlineHeight));
                albedo = lerp(albedo, _WaterlineColor.rgb, band * _WaterlineColor.a * (0.5 + var.r));
                float below = 1.0 - smoothstep(_WaterlineHeight - 0.004, _WaterlineHeight + 0.004, pM.y);
                albedo *= lerp(float3(1, 1, 1), _BelowWaterTint.rgb, below);
                smooth  = lerp(smooth, min(smooth, _BelowWaterSmoothness), below);

                // --- standard URP PBR lighting
                InputData inputData = (InputData)0;
                inputData.positionWS = IN.positionWS;
                inputData.positionCS = IN.positionCS;
                inputData.normalWS = N;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                inputData.fogCoord = InitializeInputDataFog(float4(IN.positionWS, 1.0), IN.fogFactor);
                inputData.bakedGI = SampleSH(N);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surf = (SurfaceData)0;
                surf.albedo = albedo;
                surf.metallic = 0;
                surf.smoothness = smooth;
                surf.normalTS = half3(0, 0, 1);
                surf.occlusion = 1;
                surf.alpha = 1;

                half4 color = UniversalFragmentPBR(inputData, surf);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = 1;
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            float4 ShadowVert(ShadowAttributes IN) : SV_POSITION
            {
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(IN.normalOS);
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return positionCS;
            }

            half4 ShadowFrag() : SV_Target { return 0; }
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
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            float4 DepthVert(float4 positionOS : POSITION) : SV_POSITION
            {
                return TransformObjectToHClip(positionOS.xyz);
            }

            half DepthFrag(float4 positionCS : SV_POSITION) : SV_Target { return positionCS.z; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex DNVert
            #pragma fragment DNFrag

            struct DNAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct DNVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
            };

            DNVaryings DNVert(DNAttributes IN)
            {
                DNVaryings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            half4 DNFrag(DNVaryings IN) : SV_Target
            {
                return half4(NormalizeNormalPerPixel(IN.normalWS), 0.0);
            }
            ENDHLSL
        }
    }
}
