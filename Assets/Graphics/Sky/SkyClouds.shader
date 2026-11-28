// Skybox: the usual cubemap sky (same properties as Skybox/Cubemap, so the water's reflection
// sync keeps working) with the shared cloud layer drawn on top. Cloud settings live on the
// SkyClouds component in the scene, not on this material.
Shader "Sailing/Skybox Clouds"
{
    Properties
    {
        _Tint ("Tint Color", Color) = (0.5, 0.5, 0.5, 0.5)
        [Gamma] _Exposure ("Exposure", Range(0, 8)) = 1.0
        _Rotation ("Rotation", Range(0, 360)) = 0
        [NoScaleOffset] _Tex ("Cubemap (HDR)", Cube) = "grey" {}
        [Toggle] _UseGradient ("Use Gradient Instead Of Cubemap", Float) = 0
        _ZenithColor ("Gradient Zenith", Color) = (0.25, 0.45, 0.8, 1)
        _HorizonColor ("Gradient Horizon", Color) = (0.7, 0.82, 0.95, 1)
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" "RenderPipeline"="UniversalPipeline" }
        Cull Off ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/EntityLighting.hlsl" // DecodeHDREnvironment
            #include "Assets/Graphics/Sky/SailingClouds.hlsl"

            TEXTURECUBE(_Tex);
            SAMPLER(sampler_Tex);
            float4 _Tex_HDR;
            float4 _Tint;
            float _Exposure;
            float _Rotation;
            float _UseGradient;
            float4 _ZenithColor;
            float4 _HorizonColor;

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 texDir : TEXCOORD0;   // cubemap lookup (unrotated, like Skybox/Cubemap)
                float3 viewDir : TEXCOORD1;  // world direction of this pixel
            };

            float3 RotateAroundYInDegrees(float3 v, float degrees)
            {
                float a = degrees * PI / 180.0;
                float s, c;
                sincos(a, s, c);
                return float3(c * v.x - s * v.z, v.y, s * v.x + c * v.z);
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 rotated = RotateAroundYInDegrees(IN.positionOS.xyz, _Rotation);
                OUT.positionCS = TransformObjectToHClip(rotated);
                OUT.texDir = IN.positionOS.xyz;
                OUT.viewDir = rotated;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 dir = normalize(IN.viewDir);
                float3 sky;
                if (_UseGradient > 0.5)
                {
                    float up = saturate(dir.y);
                    sky = lerp(_HorizonColor.rgb, _ZenithColor.rgb, sqrt(up));
                    if (dir.y < 0.0) sky = _HorizonColor.rgb * 0.6;
                }
                else
                {
                    float4 tex = SAMPLE_TEXTURECUBE(_Tex, sampler_Tex, IN.texDir);
                    float3 c = DecodeHDREnvironment(tex, _Tex_HDR);
                #ifdef UNITY_COLORSPACE_GAMMA
                    float csDouble = 2.0;
                #else
                    float csDouble = 4.59479380;
                #endif
                    sky = c * _Tint.rgb * csDouble * _Exposure;
                }
                sky = StormSky(sky);
                float foot = max(length(ddx(dir)), length(ddy(dir))) + 1e-6;
                sky = CloudLayer(_WorldSpaceCameraPos, dir, foot, sky);
                return half4(sky, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
