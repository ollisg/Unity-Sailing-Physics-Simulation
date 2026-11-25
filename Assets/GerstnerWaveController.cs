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
    public const int MaxWaves = 16; // must match MAX_WAVES in the shader

    [Header("Target")]
    public Material waterMaterial;
    [Tooltip("Optional. Used by GetHeight() as the base water level.")]
    public Transform waterSurface;

    [Header("Wave Count & Seed")]
    [Range(1, MaxWaves)] public int waveCount = 8;
    public int seed = 1234;

    [Header("Height")]
    [Tooltip("Amplitude of the largest wave in meters (wave height = 2 x amplitude).")]
    [Min(0f)] public float amplitude = 0.5f;
    [Tooltip("Each following wave is multiplied by this. Lower = calmer, dominated by one big swell.")]
    [Range(0.3f, 1f)] public float amplitudeFalloff = 0.8f;

    [Header("Length")]
    [Tooltip("Wavelength of the largest wave in meters.")]
    [Min(0.5f)] public float wavelength = 30f;
    [Tooltip("Each following wave's length is multiplied by this.")]
    [Range(0.3f, 1f)] public float wavelengthFalloff = 0.8f;

    [Header("Direction Distribution")]
    [Tooltip("Direction of the dominant wave in degrees (0 = +X axis, 90 = +Z axis).")]
    [Range(0f, 360f)] public float windDirection = 0f;
    [Tooltip("Other waves deviate randomly up to +/- this many degrees from the wind direction. 180 = waves from everywhere.")]
    [Range(0f, 180f)] public float directionSpread = 40f;

    [Header("Shape & Motion")]
    [Tooltip("0 = pure sine waves, 1 = maximum sharp crests (automatically limited per wave so the surface never loops).")]
    [Range(0f, 1f)] public float choppiness = 0.7f;
    [Tooltip("Random variation in each wave's amplitude and length.")]
    [Range(0f, 1f)] public float randomness = 0.4f;
    [Range(0f, 3f)] public float speedMultiplier = 1f;
    [Min(0.1f)] public float gravity = 9.81f;
    [Range(0f, 5f)] public float timeScale = 1f;

    [Header("Sky Reflection")]
    [Tooltip("Copy the panoramic texture, tint, exposure and rotation of the scene skybox (Lighting > Skybox Material) into the water material so it reflects the sky at full resolution.")]
    public bool syncSkyboxReflection = true;
    [Tooltip("Optional: use this skybox material instead of the one set in the Lighting window.")]
    public Material skyboxOverride;

    static readonly int WaveAId = Shader.PropertyToID("_WaveA");
    static readonly int WaveBId = Shader.PropertyToID("_WaveB");
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
        _totalAmplitude = 0f;

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

            float amp = amplitude * Mathf.Pow(amplitudeFalloff, i)
                        * Mathf.Lerp(1f, 0.6f + 0.8f * rAmp, randomness);
            float len = Mathf.Max(0.1f, wavelength * Mathf.Pow(wavelengthFalloff, i)
                        * Mathf.Lerp(1f, 0.6f + 0.8f * rLen, randomness));

            // First wave is the dominant one and follows the wind exactly
            float angle = windDirection + (i == 0 ? 0f : (rDir * 2f - 1f) * directionSpread);
            float rad = angle * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

            float k = 2f * Mathf.PI / len;
            float omega = Mathf.Sqrt(gravity * k) * speedMultiplier;

            // Limit steepness so that sum(Q*k*A) <= 1 -> the surface never self-intersects
            float q = Mathf.Min(choppiness, 1f / (k * amp * _count + 1e-5f));

            _waveA[i] = new Vector4(dir.x, dir.y, amp, k);
            _waveB[i] = new Vector4(q, omega, rPhase * 2f * Mathf.PI, 0f);
            _totalAmplitude += amp;
        }

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
