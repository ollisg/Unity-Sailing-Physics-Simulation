// Updates the persistent foam map (see GerstnerWaveController, "Persistent Foam").
// One pass over the map every frame:
//   new = old (shifted by wind drift / camera movement, whole texels only) * decay
//         + crest foam injected where the waves are currently breaking
// Runs only in Play mode. Lives in a Resources folder so it is included in builds.
Shader "Hidden/GerstnerFoamUpdate"
{
    Properties
    {
        _MainTex ("Previous foam map", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #define MAX_WAVES 128

            TEXTURE2D(_MainTex);

            float4 _GerstnerWaveA[MAX_WAVES];
            float4 _GerstnerWaveB[MAX_WAVES];
            float  _WaveCount;

            float2 _MapOrigin;     // world XZ of the map's corner (texel 0,0)
            float  _MapSize;       // metres covered by the map
            float  _MapRes;        // texels per side
            float4 _Shift;         // xy: where each texel's old content sits, in texels (integer valued)
            float  _Decay;         // multiplier per update (exp(-dt / lifetime))
            float  _Inject;        // foam added per update where a crest is breaking
            float  _Threshold;
            float  _Softness;
            float  _KCut;          // waves with a larger k than this are too fine for the map

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes IN)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return o;
            }

            // Same crest measure as the water shader (1 - Jacobian, normalised), but waves finer than
            // the map can show are left out of the Jacobian (they would only alias). They still count
            // in the normalisation so the threshold keeps its meaning.
            float Crest(float2 p)
            {
                float jxx = 0, jzz = 0, jxz = 0;
                float maxCompression = 0;

                int count = (int)_WaveCount;
                [loop]
                for (int i = 0; i < count; i++)
                {
                    float4 a = _GerstnerWaveA[i];
                    float4 b = _GerstnerWaveB[i];
                    float2 d = a.xy;
                    float amp = a.z;
                    float k = a.w;
                    if (amp * k < 5e-4) continue;

                    float qka = b.x * k * amp;
                    maxCompression += qka;
                    if (k > _KCut) continue;

                    float s = sin(k * dot(d, p) + b.z);
                    float qks = qka * s;
                    jxx += qks * d.x * d.x;
                    jzz += qks * d.y * d.y;
                    jxz += qks * d.x * d.y;
                }

                float jacobian = (1.0 - jxx) * (1.0 - jzz) - jxz * jxz;
                return (1.0 - jacobian) / max(maxCompression, 1e-4);
            }

            float frag(Varyings IN) : SV_Target
            {
                // Pixel coordinates, used for both reading and writing, so any render-target
                // flip on Metal / Direct3D / OpenGL cancels out.
                int2 p = int2(IN.positionCS.xy);
                float2 wxz = _MapOrigin + IN.positionCS.xy * (_MapSize / _MapRes);

                int2 src = p + int2(_Shift.xy);
                float prev = 0;
                if (all(src >= 0) && all(src < (int)_MapRes))
                    prev = LOAD_TEXTURE2D(_MainTex, src).r;

                float crest = Crest(wxz);
                float inject = smoothstep(_Threshold, _Threshold + _Softness, crest) * _Inject;

                return saturate(prev * _Decay + inject);
            }
            ENDHLSL
        }
    }
}
