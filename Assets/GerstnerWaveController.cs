using UnityEngine;

/// <summary>
/// Generates a set of Gerstner waves from a few artist-friendly controls and
/// feeds them to the GerstnerWater shader. Also exposes CPU-side sampling
/// (GetHeight) so floating objects match the GPU surface.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class GerstnerWaveController : MonoBehaviour
{
    public const int MaxWaves = 128; // must match MAX_WAVES in the shader

    [Header("Target")]
    public Material waterMaterial;
    [Tooltip("Optional. Used by GetHeight() as the base water level.")]
    public Transform waterSurface;

    [Header("Wave Count & Seed")]
    [Range(1, MaxWaves)] public int waveCount = 8;
    public int seed = 1234;

    [Header("Height")]
    [Tooltip("Significant wave height in metres: the average height (trough to crest) of the highest third of the waves, " +
             "the number a forecast reports. The overall sea size stays the same whatever the wave count.")]
    [Min(0f)] public float waveHeight = 0.7f;
    [Tooltip("Caps every wave's steepness (amplitude x wavenumber) so short waves can't turn into spikes. " +
             "~0.08 smooth sea, ~0.12 typical, 0.2+ very steep.")]
    [Range(0.02f, 0.3f)] public float maxSteepness = 0.12f;

    [Header("Wavelength Distribution")]
    [Tooltip("Shortest wave in metres.")]
    [Min(0.05f)] public float minWavelength = 0.5f;
    [Tooltip("Longest wave (the swell) in metres.")]
    [Min(0.1f)] public float maxWavelength = 15f;
    [Tooltip("Where the waves sit between min and max wavelength.\n" +
             "X: wave (0 = first ... 1 = last). Y: 0 = shortest ... 1 = longest (log scale).\n" +
             "A straight diagonal spreads them evenly; bend it to put more waves at the short or long end.")]
    public AnimationCurve wavelengthDistribution = AnimationCurve.Linear(0f, 0f, 1f, 1f);
    [Tooltip("Relative height of a wave by its wavelength.\n" +
             "X: 0 = shortest ... 1 = longest wavelength. Y: relative amplitude (only the shape matters, Wave Height sets the size).")]
    public AnimationCurve amplitudeSpectrum = new AnimationCurve(
        new Keyframe(0f, 0.05f), new Keyframe(0.75f, 0.6f), new Keyframe(1f, 1f));

    [Header("Direction Distribution")]
    [Tooltip("Direction the waves travel in degrees (0 = +X axis, 90 = +Z axis).")]
    [Range(0f, 360f)] public float windDirection = 0f;
    [Tooltip("Waves deviate randomly up to +/- this many degrees from the wind direction (long swell deviates less than short chop). 180 = waves from everywhere.")]
    [Range(0f, 180f)] public float directionSpread = 40f;

    [Header("Shape & Motion")]
    [Tooltip("0 = pure sine waves, 1 = sharpest crests the surface allows without looping over itself.")]
    [Range(0f, 1f)] public float choppiness = 0.7f;
    [Tooltip("Random variation in each wave's amplitude and wavelength (wavelengths jitter within their slot of the distribution).")]
    [Range(0f, 1f)] public float randomness = 0.4f;
    [Range(0f, 3f)] public float speedMultiplier = 1f;
    [Min(0.1f)] public float gravity = 9.81f;
    [Range(0f, 5f)] public float timeScale = 1f;

    [Header("Sky Reflection")]
    [Tooltip("Copy the panoramic texture, tint, exposure and rotation of the scene skybox (Lighting > Skybox Material) into the water material so it reflects the sky at full resolution.")]
    public bool syncSkyboxReflection = true;
    [Tooltip("Optional: use this skybox material instead of the one set in the Lighting window.")]
    public Material skyboxOverride;

    static readonly int WaveAId = Shader.PropertyToID("_GerstnerWaveA");
    static readonly int WaveBId = Shader.PropertyToID("_GerstnerWaveB");
    static readonly int WaveCountId = Shader.PropertyToID("_WaveCount");
    static readonly int TotalAmpId = Shader.PropertyToID("_TotalAmplitude");
    static readonly int WaveTimeId = Shader.PropertyToID("_WaveTime");

    static readonly int SkyTexId = Shader.PropertyToID("_SkyTex");
    static readonly int SkyCubeId = Shader.PropertyToID("_SkyCube");
    static readonly int SkyTintId = Shader.PropertyToID("_SkyTint");
    static readonly int SkyExposureId = Shader.PropertyToID("_SkyExposure");
    static readonly int SkyRotationId = Shader.PropertyToID("_SkyRotation");
    static readonly int UseSkyTexId = Shader.PropertyToID("_UseSkyTex");
    static readonly int EnvReflResId = Shader.PropertyToID("_EnvReflectionResolution");

    // Property names used by Unity's Skybox/Panoramic (_MainTex) and Skybox/Cubemap (_Tex) shaders
    static readonly int PanoTexId = Shader.PropertyToID("_MainTex");
    static readonly int CubeTexId = Shader.PropertyToID("_Tex");
    static readonly int PanoTintId = Shader.PropertyToID("_Tint");
    static readonly int PanoExposureId = Shader.PropertyToID("_Exposure");
    static readonly int PanoRotationId = Shader.PropertyToID("_Rotation");

    // (dir.x, dir.z, amplitude, k)
    readonly Vector4[] _waveA = new Vector4[MaxWaves];
    // (steepness Q, omega, phase, unused)
    readonly Vector4[] _waveB = new Vector4[MaxWaves];

    int _count;
    float _totalAmplitude = 1f;
    float _simTime;
    float _lastRealtime;

    void OnEnable()
    {
        _lastRealtime = Time.realtimeSinceStartup;
        Rebuild();
    }

    void OnValidate()
    {
        Rebuild();
    }

    void Update()
    {
        // Own clock so the CPU sampler and the shader are always in sync,
        // and the water animates in edit mode as well (enable "Always Refresh" in Scene view).
        float now = Time.realtimeSinceStartup;
        float dt = Application.isPlaying ? Time.deltaTime : now - _lastRealtime;
        _lastRealtime = now;
        _simTime += dt * timeScale;

        if (waterMaterial != null)
        {
            waterMaterial.SetFloat(WaveTimeId, _simTime);
            SyncSkybox();
        }
    }

    /// <summary>
    /// Copies the skybox texture and its tint/exposure/rotation to the water material so the
    /// water reflects the sky at full resolution. Works with Skybox/Cubemap and Skybox/Panoramic
    /// (latitude-longitude, 360). For other skybox shaders the water falls back to URP's
    /// environment reflection (Lighting > Environment Reflections).
    /// </summary>
    public void SyncSkybox()
    {
        if (waterMaterial == null) return;

        Material sky = skyboxOverride != null ? skyboxOverride : RenderSettings.skybox;
        float mode = 0f;
        if (syncSkyboxReflection && sky != null)
        {
            Texture tex = null;
            if (sky.HasProperty(CubeTexId)) tex = sky.GetTexture(CubeTexId);
            if (tex == null && sky.HasProperty(PanoTexId)) tex = sky.GetTexture(PanoTexId);

            if (tex is Cubemap cube)
            {
                waterMaterial.SetTexture(SkyCubeId, cube);
                mode = 2f;
            }
            else if (tex is Texture2D tex2D)
            {
                waterMaterial.SetTexture(SkyTexId, tex2D);
                mode = 1f;
            }

            if (mode > 0f)
            {
                waterMaterial.SetColor(SkyTintId, sky.HasProperty(PanoTintId) ? sky.GetColor(PanoTintId) : new Color(0.5f, 0.5f, 0.5f, 0.5f));
                waterMaterial.SetFloat(SkyExposureId, sky.HasProperty(PanoExposureId) ? sky.GetFloat(PanoExposureId) : 1f);
                waterMaterial.SetFloat(SkyRotationId, sky.HasProperty(PanoRotationId) ? sky.GetFloat(PanoRotationId) : 0f);
            }
        }

        waterMaterial.SetFloat(UseSkyTexId, mode);
        waterMaterial.SetFloat(EnvReflResId, RenderSettings.defaultReflectionResolution);
    }

    [ContextMenu("Randomize Seed")]
    public void RandomizeSeed()
    {
        seed = Random.Range(0, 100000);
        Rebuild();
    }

    /// <summary>Recomputes all waves from the inspector values and uploads them to the material.</summary>
    public void Rebuild()
    {
        _count = Mathf.Clamp(waveCount, 1, MaxWaves);
        var rnd = new System.Random(seed);

        float lenLo = Mathf.Max(0.05f, Mathf.Min(minWavelength, maxWavelength));
        float lenHi = Mathf.Max(lenLo, Mathf.Max(minWavelength, maxWavelength));
        float logLo = Mathf.Log(lenLo);
        float logHi = Mathf.Log(lenHi);
        float slot = _count > 1 ? 1f / (_count - 1) : 0f;

        // Pass 1: wavelength, direction, phase and relative amplitude of every wave
        float sumA2 = 0f;
        for (int i = 0; i < MaxWaves; i++)
        {
            if (i >= _count)
            {
                _waveA[i] = Vector4.zero;
                _waveB[i] = Vector4.zero;
                continue;
            }

            // Keep the random stream stable regardless of other settings
            float rAmp = (float)rnd.NextDouble();
            float rLen = (float)rnd.NextDouble();
            float rDir = (float)rnd.NextDouble();
            float rPhase = (float)rnd.NextDouble();

            // Position on the distribution curve, jittered within this wave's slot
            float t = _count > 1 ? i * slot : 1f;
            t = Mathf.Clamp01(t + (rLen - 0.5f) * slot * randomness);
            float d = Mathf.Clamp01(wavelengthDistribution.Evaluate(t)); // 0 = shortest, 1 = longest
            float len = Mathf.Exp(Mathf.Lerp(logLo, logHi, d));

            float amp = Mathf.Max(0f, amplitudeSpectrum.Evaluate(d))
                      * Mathf.Lerp(1f, 0.6f + 0.8f * rAmp, randomness);

            // Long swell follows the wind closely, short chop spreads out more
            float angle = windDirection + (rDir * 2f - 1f) * directionSpread * Mathf.Lerp(1f, 0.35f, d);
            float rad = angle * Mathf.Deg2Rad;

            float k = 2f * Mathf.PI / len;
            float omega = Mathf.Sqrt(gravity * k) * speedMultiplier;

            _waveA[i] = new Vector4(Mathf.Cos(rad), Mathf.Sin(rad), amp, k);
            _waveB[i] = new Vector4(0f, omega, rPhase * 2f * Mathf.PI, 0f);
            sumA2 += amp * amp;
        }

        // Pass 2: scale to the requested significant wave height.
        // For random phases the surface std-dev is sqrt(sum(a^2) / 2) and Hs = 4 * std-dev,
        // so the sea keeps the same overall size whatever the wave count is.
        float scale = sumA2 > 0f ? (waveHeight * 0.25f) / Mathf.Sqrt(sumA2 * 0.5f) : 0f;
        float sumKA = 0f;
        for (int i = 0; i < _count; i++)
        {
            float k = _waveA[i].w;
            float amp = Mathf.Min(_waveA[i].z * scale, maxSteepness / k); // steepness cap: no spikes
            _waveA[i].z = amp;
            sumKA += k * amp;
        }

        // Pass 3: crest sharpness. sum(Q * k * A) = choppiness <= 1, so the surface never loops over itself.
        float q = choppiness / Mathf.Max(sumKA, 1e-5f);
        for (int i = 0; i < _count; i++)
            _waveB[i].x = q;

        // Used by the shader to colour troughs/crests: roughly +-1 at typical wave extremes
        _totalAmplitude = Mathf.Max(waveHeight * 0.5f, 1e-4f);

        if (waterMaterial != null)
        {
            waterMaterial.SetVectorArray(WaveAId, _waveA);
            waterMaterial.SetVectorArray(WaveBId, _waveB);
            waterMaterial.SetFloat(WaveCountId, _count);
            waterMaterial.SetFloat(TotalAmpId, Mathf.Max(_totalAmplitude, 1e-4f));
            SyncSkybox();
        }
    }

    // ------------------------------------------------------------------
    // CPU sampling (same math as the shader) - use it for buoyancy etc.
    // ------------------------------------------------------------------

    /// <summary>Total displacement of the surface point whose undisplaced XZ position is p.</summary>
    public Vector3 GetDisplacement(Vector2 p)
    {
        Vector3 offset = Vector3.zero;
        for (int i = 0; i < _count; i++)
        {
            Vector4 a = _waveA[i];
            Vector4 b = _waveB[i];
            float phase = a.w * (a.x * p.x + a.y * p.y) - b.y * _simTime + b.z;
            float s = Mathf.Sin(phase);
            float c = Mathf.Cos(phase);

            offset.x += b.x * a.z * a.x * c;
            offset.z += b.x * a.z * a.y * c;
            offset.y += a.z * s;
        }
        return offset;
    }

    /// <summary>World-space water height at a world position.</summary>
    public float GetHeight(Vector3 worldPos)
    {
        Vector2 target = new Vector2(worldPos.x, worldPos.z);
        Vector2 guess = target;

        // Gerstner waves move points horizontally, so find the undisplaced
        // position that ends up at 'target' (a few fixed-point iterations are enough).
        for (int i = 0; i < 4; i++)
        {
            Vector3 d = GetDisplacement(guess);
            guess = target - new Vector2(d.x, d.z);
        }

        float baseY = waterSurface != null ? waterSurface.position.y : transform.position.y;
        return baseY + GetDisplacement(guess).y;
    }
}
