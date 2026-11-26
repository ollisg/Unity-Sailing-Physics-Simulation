#ifndef GERSTNER_WATER_COMMON_INCLUDED
#define GERSTNER_WATER_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

#define MAX_WAVES 128

CBUFFER_START(UnityPerMaterial)
    float4 _DeepColor;
    float4 _ShallowColor;
    float4 _FoamColor;
    float  _FoamThreshold;
    float  _FoamSoftness;
    float  _FoamNoiseScale;
    float  _FoamNoiseStrength;
    float  _FoamTexScale;
    float  _FoamTexMix;
    float4 _FoamMapParams; // xy = map origin (world XZ), z = 1 / map size, w = 1 when the map is valid
    float  _FoamTrailAmount;
    float  _Smoothness;
    float  _SpecularPower;
    float  _FresnelPower;
    float  _ReflectionStrength;
    float  _ReflectionAmount;
    float  _NormalStrength;
    float  _NormalTiling;
    float  _NormalSpeed;
    float4 _SkyTex_TexelSize;
    float4 _SkyCube_TexelSize;
    float4 _SkyTint;
    float  _SkyExposure;
    float  _SkyRotation;
    float  _UseSkyTex;
    float  _EnvReflectionResolution;
    float  _TessEdgeLength;
    float  _TessMax;
    float  _TessMinDist;
    float  _TessMaxDist;
    float  _WaveCount;
    float  _TotalAmplitude;
    float  _WaveTime;
    float  _CullMargin;
CBUFFER_END

// Skybox texture, synced from the skybox material by GerstnerWaveController.
// _UseSkyTex: 0 = none (reflection probe only), 1 = equirect 2D (Skybox/Panoramic), 2 = cubemap (Skybox/Cubemap)
// Inline sampler for the 2D one: repeat wrap, so there is no seam at the 0/360 deg line.
TEXTURE2D(_SkyTex);
SAMPLER(sampler_linear_repeat);
TEXTURE2D(_NormalMap);
SAMPLER(sampler_NormalMap);
TEXTURE2D(_FoamTex);
SAMPLER(sampler_FoamTex);
TEXTURE2D(_FoamMap);
SAMPLER(sampler_FoamMap);
TEXTURECUBE(_SkyCube);
SAMPLER(sampler_SkyCube);
float4 _SkyCube_HDR;

// Per wave data, filled by GerstnerWaveController (Material.SetVectorArray)
// _GerstnerWaveA = (dir.x, dir.z, amplitude, k)       k = 2*PI / wavelength
// _GerstnerWaveB = (steepness Q, omega, phase, unused)
// Arrays live outside the cbuffer, so this shader isn't SRP Batcher compatible.
// That's fine for a single water surface.
float4 _GerstnerWaveA[MAX_WAVES];
float4 _GerstnerWaveB[MAX_WAVES];

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS   : TEXCOORD1;
    float  height     : TEXCOORD2; // -1..1, relative to total amplitude
    float  crest      : TEXCOORD3; // local surface compression (0 = flat, ~1 = sharpest possible crest)
    float2 flatXZ     : TEXCOORD4; // undisplaced position, the foam map is keyed to it
};

// Sums all Gerstner waves at world XZ position p.
// Returns displacement (xyz), outputs the analytic normal and the local crest factor.
//
// Crest factor: Gerstner waves push surface points horizontally towards each crest.
// The Jacobian J of the horizontal displacement tells how much the surface is squeezed
// at a point (J = 1 flat, J -> 0 at a perfectly sharp crest). 1 - J is high on every
// crest regardless of its absolute height, so foam follows each individual wave top
// instead of a global height level.
float3 GerstnerWaves(float2 p, float t, out float3 normal, out float crest)
{
    float3 offset = 0;
    float2 nxz = 0;
    float  ny  = 0;

    float jxx = 0, jzz = 0, jxz = 0; // derivatives of the horizontal displacement
    float maxCompression = 0;        // sum(Q*k*A) = compression if all crests line up

    int count = (int)_WaveCount;
    [loop]
    for (int i = 0; i < count; i++)
    {
        float4 a = _GerstnerWaveA[i];
        float4 b = _GerstnerWaveB[i];

        float2 d   = a.xy;
        float  amp = a.z;
        float  k   = a.w;
        float  Q   = b.x;
        float  w   = b.y;

        // Waves that are completely faded out, or so small their slope is invisible, are skipped.
        // The data is the same for every vertex, so this branch costs nothing.
        if (amp * k < 5e-4) continue;

        float phase = k * dot(d, p) - w * t + b.z;
        float s, c;
        sincos(phase, s, c);

        offset.xz += Q * amp * d * c;
        offset.y  += amp * s;

        nxz += d * (k * amp * c);
        ny  += Q * k * amp * s;

        float qka = Q * k * amp;
        float qks = qka * s;
        jxx += qks * d.x * d.x;
        jzz += qks * d.y * d.y;
        jxz += qks * d.x * d.y;
        maxCompression += qka;
    }

    normal = normalize(float3(-nxz.x, 1.0 - ny, -nxz.y));

    float jacobian = (1.0 - jxx) * (1.0 - jzz) - jxz * jxz;
    // Normalize so the foam threshold behaves the same whatever the choppiness / wave count.
    crest = (1.0 - jacobian) / max(maxCompression, 1e-4);
    return offset;
}

// Takes the flat (undisplaced) world position of a surface point and returns
// the displaced, ready-to-rasterize vertex.
Varyings BuildWaterVertex(float3 flatPosWS)
{
    float3 normal;
    float  crest;
    // Time is 0: the controller bakes the motion into each wave's phase (b.z), so waves can change
    // wavelength and direction over time without jumping. _WaveTime only drives the foam/detail scrolling.
    float3 offset = GerstnerWaves(flatPosWS.xz, 0.0, normal, crest);
    float3 posWS = flatPosWS + offset;

    Varyings OUT;
    OUT.positionWS = posWS;
    OUT.positionCS = TransformWorldToHClip(posWS);
    OUT.normalWS   = normal;
    OUT.height     = offset.y / max(_TotalAmplitude, 1e-4);
    OUT.crest      = crest;
    OUT.flatXZ     = flatPosWS.xz;
    return OUT;
}

// ------------------------------------------------------------------
// Sky reflection
// ------------------------------------------------------------------

// Unity's skybox shaders rotate their vertices by _Rotation degrees around Y; apply the same here.
float3 RotateSkyDir(float3 dir)
{
    float a = _SkyRotation * PI / 180.0;
    float sa, ca;
    sincos(a, sa, ca);
    dir.xz = float2(ca * dir.x - sa * dir.z, sa * dir.x + ca * dir.z);
    return dir;
}

// Same direction -> UV mapping as Unity's Skybox/Panoramic shader (360, latitude-longitude).
float2 SkyDirToEquirectUV(float3 dir)
{
    dir = normalize(dir);
    float latitude  = acos(clamp(dir.y, -1.0, 1.0));
    float longitude = atan2(dir.z, dir.x);
    float2 sphere = float2(longitude * (0.5 / PI), latitude * (1.0 / PI));
    return float2(0.5, 1.0) - sphere;
}

// Unity skybox shaders: c * _Tint * unity_ColorSpaceDouble * _Exposure
float3 ApplySkyTintExposure(float3 c)
{
#ifdef UNITY_COLORSPACE_GAMMA
    float colorSpaceDouble = 2.0;
#else
    float colorSpaceDouble = 4.59479380;
#endif
    return c * _SkyTint.rgb * colorSpaceDouble * _SkyExposure;
}

float3 SampleSkyTexture(float3 dir)
{
    dir = RotateSkyDir(dir);
    float3 c;
    if (_UseSkyTex > 1.5)
        c = DecodeHDREnvironment(SAMPLE_TEXTURECUBE_LOD(_SkyCube, sampler_SkyCube, dir, 0), _SkyCube_HDR);
    else
        c = SAMPLE_TEXTURE2D_LOD(_SkyTex, sampler_linear_repeat, SkyDirToEquirectUV(dir), 0).rgb;

    return ApplySkyTintExposure(c);
}

// Inverse of URP's PerceptualRoughnessToMipmapLevel, so we can pick an exact cubemap mip.
float MipToPerceptualRoughness(float mip)
{
    float m = saturate(mip / UNITY_SPECCUBE_LOD_STEPS) * 6.0;
    return saturate((1.7 - sqrt(max(2.89 - 2.8 * m / 6.0, 0.0))) / 1.4);
}

float3 SkyReflection(float3 R, float perceptualRoughness)
{
    // Keep the reflection above the horizon: on a wavy surface reflect() can point
    // slightly downward, which would pick up the (dark) lower half of the sky.
    R.y = max(R.y, 0.01);
    R = normalize(R);

    // How wide a cone of directions this pixel covers (radians). Used to avoid
    // sparkly aliasing in the distance where the sharp texture has no mipmaps.
    float footprint = max(length(ddx(R)), length(ddy(R))) + 1e-6;

    // Ambient cubemap (URP's environment reflection, baked from the same skybox, has mips).
    float cubeTexelAngle = (PI * 0.5) / max(_EnvReflectionResolution, 1.0);
    float cubeMip = max(log2(footprint / cubeTexelAngle), 0.0);
    float roughForCube = max(perceptualRoughness, MipToPerceptualRoughness(cubeMip));
    float3 cubeRefl = GlossyEnvironmentReflection(R, roughForCube, 1.0);

    if (_UseSkyTex < 0.5)
        return cubeRefl;

    // Skybox cubemap: hardware mip selection keeps it sharp up close and smooth in the
    // distance (needs Generate Mip Maps on the cubemap texture). Roughness adds blur.
    if (_UseSkyTex > 1.5)
    {
        float4 enc = SAMPLE_TEXTURECUBE_BIAS(_SkyCube, sampler_SkyCube, RotateSkyDir(R),
                                             perceptualRoughness * 6.0);
        return ApplySkyTintExposure(DecodeHDREnvironment(enc, _SkyCube_HDR));
    }

    // Full-resolution reflection straight from the skybox texture, faded to the
    // filtered cubemap when the pixel footprint gets bigger than ~2 sky texels
    // (distance / grazing angles) or the surface gets rough.
    float skyTexelAngle = _UseSkyTex > 1.5
        ? (PI * 0.5) * _SkyCube_TexelSize.x   // cube face covers 90 deg
        : (2.0 * PI) * _SkyTex_TexelSize.x;   // equirect covers 360 deg
    float toCube = saturate(log2(footprint / skyTexelAngle) - 1.0);
    toCube = max(toCube, saturate(perceptualRoughness * 6.0));

    float3 sharp = SampleSkyTexture(R);
    return lerp(sharp, cubeRefl, toCube);
}

// ------------------------------------------------------------------
// Foam breakup: smooth value noise plus a tileable foam texture
// ------------------------------------------------------------------

float Hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float ValueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = Hash21(i);
    float b = Hash21(i + float2(1, 0));
    float c = Hash21(i + float2(0, 1));
    float d = Hash21(i + float2(1, 1));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float FoamNoise(float2 xz)
{
    // Smooth blobs: big, soft variation
    float2 p = xz * _FoamNoiseScale;
    float soft = ValueNoise(p + _WaveTime * 0.05) * 0.65
               + ValueNoise(p * 2.7 - _WaveTime * 0.08) * 0.35;

    // Fine bubbly breakup from the foam texture. Two layers at different scales and drift,
    // so the tiling never shows. Values are evenly spread 0..1, like the noise above.
    float2 uv = xz * _FoamTexScale;
    float f1 = SAMPLE_TEXTURE2D(_FoamTex, sampler_FoamTex, uv + _WaveTime * float2(0.011, 0.007)).r;
    float f2 = SAMPLE_TEXTURE2D(_FoamTex, sampler_FoamTex, uv * 2.37 + float2(0.31, 0.77)
                                - _WaveTime * float2(0.013, -0.009)).r;
    float fine = f1 * 0.6 + f2 * 0.4;

    return lerp(soft, fine, _FoamTexMix); // 0..1
}

// ------------------------------------------------------------------
// Detail normals: two scrolling layers of a normal map in world space.
// They add the small ripples that break up the sky reflection.
// ------------------------------------------------------------------

float3 ApplyDetailNormals(float3 N, float2 xz)
{
    float2 uv1 = xz * _NormalTiling        + _WaveTime * _NormalSpeed * float2( 1.0,  0.35);
    float2 uv2 = xz * _NormalTiling * 0.41 + _WaveTime * _NormalSpeed * float2(-0.45, 0.8);

    float3 n1 = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv1), _NormalStrength);
    float3 n2 = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv2), _NormalStrength);
    float3 nTS = normalize(float3(n1.xy + n2.xy, n1.z * n2.z)); // whiteout blend

    // Tangent frame on the wave surface: tangent along +X, bitangent along +Z
    float3 T = normalize(float3(1, 0, 0) - N * N.x);
    float3 B = cross(T, N);
    return normalize(T * nTS.x + B * nTS.y + N * nTS.z);
}

// ------------------------------------------------------------------
// Shading
// ------------------------------------------------------------------

half4 ShadeWater(Varyings IN)
{
    float3 N = ApplyDetailNormals(normalize(IN.normalWS), IN.positionWS.xz);
    float3 V = GetWorldSpaceNormalizeViewDir(IN.positionWS);

    Light mainLight = GetMainLight();
    float ndl = saturate(dot(N, mainLight.direction));

    // Base (body) color: deep in troughs, shallow on crests
    float  t01     = saturate(IN.height * 0.5 + 0.5);
    float3 baseCol = lerp(_DeepColor.rgb, _ShallowColor.rgb, t01);
    float3 diffuse = baseCol * (mainLight.color * ndl + SampleSH(N));

    // Fresnel (water F0 ~= 0.02) blended with the sky reflection.
    // Reflection Amount pushes it from physical (0) to an all-reflection, stylized look (1)
    // where the water takes its colour from the sky, like the Captain Clanker ocean.
    float  nv   = saturate(dot(N, V));
    float  fres = 0.02 + 0.98 * pow(1.0 - nv, _FresnelPower);
    fres = saturate(lerp(fres, 1.0, _ReflectionAmount) * _ReflectionStrength);
    float3 refl = SkyReflection(reflect(-V, N), 1.0 - _Smoothness);
    float3 col  = lerp(diffuse, refl, fres);

    // Sun specular
    float3 H = normalize(mainLight.direction + V);
    col += mainLight.color * pow(saturate(dot(N, H)), _SpecularPower) * _Smoothness;

    // Crest foam: local surface compression, broken up with noise
    float fn   = FoamNoise(IN.positionWS.xz);
    float foam = smoothstep(_FoamThreshold, _FoamThreshold + _FoamSoftness, IN.crest + (fn - 0.5) * _FoamNoiseStrength);

    // Persistent foam: what the foam map remembers from earlier crests. As it fades it breaks up
    // into patches (the noise threshold rises through it) instead of just getting more transparent.
    if (_FoamMapParams.w > 0.5)
    {
        float2 fuv = (IN.flatXZ - _FoamMapParams.xy) * _FoamMapParams.z;
        float trail = SAMPLE_TEXTURE2D(_FoamMap, sampler_FoamMap, fuv).r * _FoamTrailAmount;
        float edge = saturate(min(min(fuv.x, 1.0 - fuv.x), min(fuv.y, 1.0 - fuv.y)) * 8.0); // fade out at the map border
        foam = max(foam, smoothstep(fn, fn + 0.15, trail * 1.15) * edge);
    }
    float3 foamLit = _FoamColor.rgb * (mainLight.color * (ndl * 0.6 + 0.4) + SampleSH(float3(0, 1, 0)));
    col = lerp(col, foamLit, foam * _FoamColor.a);

    return half4(col, 1.0);
}

#endif
