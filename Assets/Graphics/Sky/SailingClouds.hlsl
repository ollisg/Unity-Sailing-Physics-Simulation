// Cheap 2D cloud layer shared by the sky (SkyClouds.shader) and the water (reflections and
// cloud shadows), so all three always show the same clouds.
// The clouds are a flat layer at _CloudParams.w metres. A view ray is followed up to that height
// and a tiling noise texture is read there: a few texture reads per pixel, no raymarching.
// All values are globals set by SkyClouds.cs. With no SkyClouds in the scene they are 0 and
// everything here does nothing.
#ifndef SAILING_CLOUDS_INCLUDED
#define SAILING_CLOUDS_INCLUDED

TEXTURE2D(_CloudNoise);
SAMPLER(sampler_CloudNoise);
float4 _CloudParams;  // x coverage 0-1, y edge sharpness, z 1 / tile size (1/m), w layer height (m), 0 = off
float4 _CloudOffset;  // xy drift of the main layer, zw drift of the detail layer (texture UV)
float4 _CloudShape;   // x detail amount, y detail scale, z light step (m), w aerial fade distance (m)
float4 _CloudLit;     // rgb colour of the sunlit side (linear)
float4 _CloudShade;   // rgb colour of the shaded underside (linear)
float4 _CloudSun;     // xyz direction towards the sun, w shadow strength
float4 _CloudMisc;    // x noise texture size (texels), y sun compensation for the water, z silver lining,
                      // w dome radius (m): the layer is a sphere shell of this radius, 0 = flat
float4 _CloudSkyTint; // rgb storm sky colour, a how stormy it is (0-1)

// Greys and darkens the clear sky towards the storm colour as the weather gets worse.
float3 StormSky(float3 sky)
{
    if (_CloudParams.w <= 0.0) return sky;
    float lum = dot(sky, float3(0.2126, 0.7152, 0.0722));
    return lerp(sky, lum * _CloudSkyTint.rgb, saturate(_CloudSkyTint.a));
}

// 0 = clear sky, 1 = solid cloud, at world XZ point q. lod = texture mip of the main layer.
float CloudDensity(float2 q, float lod)
{
    float2 uv = q * _CloudParams.z;
    float n1 = SAMPLE_TEXTURE2D_LOD(_CloudNoise, sampler_CloudNoise, uv + _CloudOffset.xy, max(lod, 0.0)).r;
    float n2 = SAMPLE_TEXTURE2D_LOD(_CloudNoise, sampler_CloudNoise, uv * _CloudShape.y + _CloudOffset.zw,
                                    max(lod + log2(_CloudShape.y), 0.0)).r;
    float n = n1 + (n2 - 0.5) * _CloudShape.x;
    return saturate((n - (1.0 - _CloudParams.x)) * _CloudParams.y);
}

// Blends the clouds over 'sky' for a ray from 'origin' going 'dir' (normalised).
// footAngle: how wide a cone of directions the pixel covers (radians), picks a blurrier mip in
// the distance so the clouds don't sparkle at the horizon.
float3 CloudLayer(float3 origin, float3 dir, float footAngle, float3 sky)
{
    float h = _CloudParams.w - origin.y;
    if (_CloudParams.w <= 0.0 || h <= 0.0 || dir.y <= 0.0) return sky;

    float t, cosI;
    float R = _CloudMisc.w;
    if (R > 0.0)
    {
        // Curved layer: a sphere shell 'h' above us on a small planet of radius R, so the clouds
        // bend down to the horizon like a dome instead of stretching out flat forever.
        float r0 = R;          // our distance from the planet centre
        float rl = R + h;      // the cloud shell
        float b = r0 * dir.y;
        t = -b + sqrt(b * b + rl * rl - r0 * r0);
        float3 n = (float3(0.0, r0, 0.0) + dir * t) / rl; // shell normal at the hit point
        cosI = max(dot(dir, n), 0.015);
    }
    else
    {
        cosI = max(dir.y, 0.015);
        t = h / cosI;
    }
    float2 q = origin.xz + dir.xz * t;

    float texel = 1.0 / (_CloudParams.z * _CloudMisc.x);   // metres per texel
    float foot = t * footAngle / sqrt(cosI);                // pixel size on the layer (m)
    float lod = log2(max(foot, 1e-3) / texel);

    float d = CloudDensity(q, lod);
    if (d < 0.002) return sky;

    // fake lighting: more cloud towards the sun = this spot is in the cloud's own shade
    float2 sunH = _CloudSun.xz;
    float sl = length(sunH);
    sunH = sl > 1e-4 ? sunH / sl : float2(0.0, 0.0);
    float dl = CloudDensity(q + sunH * _CloudShape.z, lod);
    float thick = saturate(d * 0.45 + dl * 0.75);
    float3 col = lerp(_CloudLit.rgb, _CloudShade.rgb, thick);

    // far clouds fade into the sky colour, and the layer fades out at the horizon
    float aerial = exp(-t / max(_CloudShape.w, 1.0));
    col = lerp(sky, col, aerial);
    float fade = smoothstep(0.0, R > 0.0 ? 0.04 : 0.08, dir.y);
    float3 result = lerp(sky, col, d * fade);

    // silver lining: thin cloud edges light up when you look towards the sun. Added on top
    // (not blended by density), so the edges actually glow instead of staying see-through.
    float toward = saturate(dot(dir, _CloudSun.xyz));
    float edge = saturate(d * (1.0 - d) * 4.0);
    result += _CloudLit.rgb * (pow(toward, 6.0) * edge * _CloudMisc.z * fade * aerial);
    return result;
}

// Sunlight left after passing the cloud layer (1 = clear, 1 - strength under a thick cloud).
// Uses fixed blurry mips (2 for the main layer, 4 for detail), matched by SkyClouds.cs on the CPU.
float CloudShadow(float3 posWS)
{
    if (_CloudParams.w <= 0.0) return 1.0;
    float sy = max(_CloudSun.y, 0.1);
    float2 q = posWS.xz + _CloudSun.xz / sy * (_CloudParams.w - posWS.y);
    return 1.0 - CloudDensity(q, 2.0) * _CloudSun.w;
}

// Factor for the sun's light on the water. In Play mode SkyClouds dims the sun by the cloud
// shadow at the boat, so the boat and sailor get shaded too; the water undoes that dimming and
// uses its own per-pixel shadow instead.
float CloudSunFactor(float3 posWS)
{
    if (_CloudParams.w <= 0.0) return 1.0;
    return CloudShadow(posWS) * max(_CloudMisc.y, 1.0);
}

#endif
