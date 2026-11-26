Shader "Custom/URP/WindStreak"
{
    // Soft, procedural streak for wind particles. No texture needed: the particle quad is
    // stretched along the wind by the Particle System renderer and this draws a soft ellipse in it.
    Properties
    {
        _Color    ("Tint", Color) = (1, 1, 1, 1)
        _NearFade ("Fade Out Closer Than (m)", Float) = 3
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float  _NearFade;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
                float  camDist    : TEXCOORD1;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.color = IN.color;
                OUT.uv = IN.uv;
                OUT.camDist = distance(posWS, _WorldSpaceCameraPos);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Soft ellipse: bright in the middle, fading to nothing at the edge
                float2 p = IN.uv * 2.0 - 1.0;
                float shape = saturate(1.0 - dot(p, p));
                shape *= shape;

                // Streaks passing right in front of the camera fade out instead of popping into view
                float nearFade = saturate((IN.camDist - _NearFade * 0.3) / max(_NearFade, 0.01));

                float alpha = IN.color.a * _Color.a * shape * nearFade;
                return half4(IN.color.rgb * _Color.rgb, alpha);
            }
            ENDHLSL
        }
    }
}
