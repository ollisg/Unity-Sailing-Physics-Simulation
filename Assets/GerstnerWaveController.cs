using UnityEngine;

/// <summary>
/// Generates a wind-driven sea from the scene's WeatherController and feeds it to the
/// GerstnerWater shader. Also exposes CPU-side sampling (GetHeight) so floating objects
/// match the GPU surface.
///
/// The real wind (WeatherController) is never applied directly. A "sea state" chases it:
///  - wind speed sets the peak wavelength, longest wave and wave height (Pierson-Moskowitz sea),
///  - when the sea is still catching up to a stronger wind it is steeper, when it is settling
///    after the wind dropped it is flatter,
///  - every wave turns towards a new wind direction at its own pace: short chop follows quickly,
///    long swell keeps its old heading for a while (a natural cross-sea during a wind shift).
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

    [Header("Weather")]
    [Tooltip("Optional. Leave empty to use the WeatherController in the scene. " +
             "Without any WeatherController the fallback wind below is used.")]
    public WeatherController weather;
    [Tooltip("Used only when there is no WeatherController. Metres per second.")]
    [Min(0f)] public float fallbackWindSpeed = 6f;
    [Tooltip("Used only when there is no WeatherController. Direction the waves travel in degrees (0 = +X axis, 90 = +Z axis).")]
    [Range(0f, 360f)] public float windDirection = 0f;

    [Header("Sea State (how fast the sea follows the wind)")]
    [Tooltip("Seconds the sea needs to catch up with a stronger wind (about 63% of the way). " +
             "Also the base time for replacing waves after the wind direction changes (see below).")]
    [Min(0.1f)] public float seaResponseTime = 30f;
    [Tooltip("The sea calms down slower than it builds up. 2 = twice as slow.")]
    [Min(1f)] public float decayTimeFactor = 2f;

    [Header("Wind Direction Changes (waves never rotate: they fade out and regrow on the new heading)")]
    [Tooltip("How long the shortest chop waits after the wind turned before it starts fading out, as a fraction of Sea Response Time.")]
    [Range(0.02f, 1f)] public float shortWaveResponse = 0.1f;
    [Tooltip("How long the longest swell waits before fading out, as a multiple of Sea Response Time. " +
             "Old swell lingering while new chop grows is what makes the cross-sea.")]
    [Range(0.5f, 5f)] public float swellResponse = 1.5f;
    [Tooltip("A wave is replaced once its heading is more than this many degrees away from where it would be for the current wind.")]
    [Range(2f, 60f)] public float headingTolerance = 15f;
    [Tooltip("Fade out / fade in length in wave periods. Short waves have short periods, so chop turns over in seconds and big swell takes about a minute.")]
    [Range(2f, 20f)] public float fadePeriods = 6f;

    [Header("Wind -> Waves")]
    [Tooltip("Shortest wave in metres (the fine chop).")]
    [Min(0.05f)] public float minWavelength = 0.25f;
    [Tooltip("Hard limit for the longest wave in metres, so a gale can't create absurdly long waves.")]
    [Min(5f)] public float maxWavelengthLimit = 200f;
    [Tooltip("Multiplies the wavelength the wind builds (0.83 x windspeed^2 metres at 1). Lower = shorter, choppier sea, e.g. a small lake.")]
    [Min(0.05f)] public float wavelengthScale = 1f;
    [Tooltip("Multiplies the wave height the wind builds (0.022 x windspeed^2 metres at 1).")]
    [Min(0f)] public float heightScale = 1f;

    [Header("Steepness (growing / steady / decaying sea)")]
    [Tooltip("Steepness (amplitude x wavenumber) when the sea has caught up with the wind.")]
    [Range(0.01f, 0.15f)] public float steepnessSteady = 0.04f;
    [Tooltip("Steepness while the sea is still growing towards a stronger wind.")]
    [Range(0.01f, 0.15f)] public float steepnessGrowing = 0.05f;
    [Tooltip("Steepness while the sea settles after the wind dropped.")]
    [Range(0.01f, 0.15f)] public float steepnessDecaying = 0.03f;
    [Tooltip("Wind speed difference (m/s) between real and sea wind at which steepness reaches the growing/decaying value.")]
    [Min(0.1f)] public float steepnessGapRange = 3f;

    [Header("Wave Count & Seed")]
    [Range(1, MaxWaves)] public int waveCount = 64;
    public int seed = 1234;

    [Header("Wavelength Distribution")]
    [Tooltip("Where the waves sit between min and max wavelength.\n" +
             "X: wave (0 = first ... 1 = last). Y: 0 = shortest ... 1 = longest (log scale).\n" +
             "A straight diagonal spreads them evenly; bend it to put more waves at the short or long end. " +
             "The sea keeps the same energy whatever you do here.")]
    public AnimationCurve wavelengthDistribution = AnimationCurve.Linear(0f, 0f, 1f, 1f);
    [Tooltip("Optional extra shaping on top of the wind spectrum.\n" +
             "X: 0 = shortest ... 1 = longest wavelength. Y: height multiplier (flat 1 = pure wind spectrum).")]
    public AnimationCurve spectrumShape = AnimationCurve.Constant(0f, 1f, 1f);

    [Header("Direction Spread (tied to the sea's wind speed)")]
    [Tooltip("Waves deviate randomly up to +/- this many degrees from the wind direction in a calm sea. 180 = waves from everywhere.")]
    [Range(0f, 180f)] public float spreadCalm = 75f;
    [Tooltip("Same in a very strong wind (long swell always deviates less than short chop).")]
    [Range(0f, 180f)] public float spreadStrong = 12f;
    [Tooltip("Sea wind speed (m/s) at and below which the spread is the calm value.")]
    [Min(0f)] public float spreadCalmWind = 2f;
    [Tooltip("Sea wind speed (m/s) at and above which the spread is the strong value.")]
    [Min(0.1f)] public float spreadStrongWind = 18f;

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

    [Header("Live Sea State (read-only, overwritten every frame)")]
    [SerializeField] float liveSeaWindSpeed;
    [SerializeField] float livePeakWavelength;
    [SerializeField] float liveWaveHeight;
    [SerializeField] float liveSteepness;
    [SerializeField] float liveSpread;
    [SerializeField] int liveTurningWaves;

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
    // (steepness Q, omega, phase, unused). The phase is accumulated on the CPU every frame
    // (the shader gets no time), so wavelength and direction can change without the waves jumping.
    readonly Vector4[] _waveB = new Vector4[MaxWaves];

    // Per wave random numbers, fixed by the seed
    readonly float[] _rAmp = new float[MaxWaves];
    readonly float[] _rLen = new float[MaxWaves];
    readonly float[] _rDir = new float[MaxWaves];
    readonly float[] _rPhase = new float[MaxWaves];
    // Per wave state that evolves over time
    readonly float[] _heading = new float[MaxWaves]; // fixed for a wave's life, changes only when it is reborn
    readonly float[] _phase = new float[MaxWaves];
    readonly float[] _env = new float[MaxWaves];     // 0..1 fade multiplier
    readonly float[] _timer = new float[MaxWaves];
    readonly byte[] _state = new byte[MaxWaves];     // see WaveState
    readonly System.Random _turnRnd = new System.Random();

    const byte Alive = 0, Waiting = 1, FadingOut = 2, FadingIn = 3, Inactive = 255;

    int _count;
    float _totalAmplitude = 1f;
    float _lastRealtime;
    float _simTime; // clock for the water's foam / detail-normal scrolling

    int _cachedSeed;
    bool _randomsReady;
    bool _anglesReady;
    bool _seaReady;
    bool _steepReady;
    float _seaSpeed;
    float _steepSmooth;

    float TargetWindSpeed
    {
        get
        {
            WeatherController w = weather != null ? weather : WeatherController.Instance;
            return Mathf.Max(0f, w != null ? w.windSpeed : fallbackWindSpeed);
        }
    }

    float TargetWindDirection
    {
        get
        {
            WeatherController w = weather != null ? weather : WeatherController.Instance;
            return w != null ? w.windDirection : windDirection;
        }
    }

    void OnEnable()
    {
        _lastRealtime = Time.realtimeSinceStartup;
        _seaReady = false;
        _steepReady = false;
        _anglesReady = false;
        Step(0f, true);
    }

    void OnValidate()
    {
        // In edit mode there is nothing to animate: show the inspector change at once.
        Step(0f, !Application.isPlaying);
    }

    void Update()
    {
        float now = Time.realtimeSinceStartup;
        float dt = Application.isPlaying ? Time.deltaTime : now - _lastRealtime;
        _lastRealtime = now;
        dt = Mathf.Clamp(dt, 0f, 0.25f);

        // Edit mode has no transitions: the sea simply matches the weather.
        Step(dt, !Application.isPlaying);

        if (waterMaterial != null)
            SyncSkybox();
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
        Step(0f, true);
    }

    /// <summary>Recomputes all waves at once from the current weather and uploads them (no transition).</summary>
    public void Rebuild()
    {
        Step(0f, true);
    }

    void EnsureRandoms()
    {
        if (_randomsReady && _cachedSeed == seed) return;

        // Same random stream per wave as before, independent of other settings
        var rnd = new System.Random(seed);
        for (int i = 0; i < MaxWaves; i++)
        {
            _rAmp[i] = (float)rnd.NextDouble();
            _rLen[i] = (float)rnd.NextDouble();
            _rDir[i] = (float)rnd.NextDouble();
            _rPhase[i] = (float)rnd.NextDouble();
            _phase[i] = _rPhase[i] * 2f * Mathf.PI;
            _env[i] = 1f;
            _state[i] = Alive;
        }
        _cachedSeed = seed;
        _randomsReady = true;
        _anglesReady = false;
    }

    /// <summary>
    /// Advances the sea state by dt seconds and uploads the waves.
    /// snap = jump straight to the weather (start-up, edit mode) instead of chasing it.
    /// </summary>
    void Step(float dt, bool snap)
    {
        _count = Mathf.Clamp(waveCount, 1, MaxWaves);
        EnsureRandoms();

        // ---- Sea wind speed chases the real wind ----
        float targetSpeed = TargetWindSpeed;
        float targetDir = TargetWindDirection;

        if (snap || !_seaReady)
        {
            _seaSpeed = targetSpeed;
            _seaReady = true;
        }
        else if (dt > 0f)
        {
            float tau = Mathf.Max(0.01f, seaResponseTime * (targetSpeed > _seaSpeed ? 1f : decayTimeFactor));
            _seaSpeed = Mathf.Lerp(_seaSpeed, targetSpeed, 1f - Mathf.Exp(-dt / tau));
        }

        // ---- Steepness: steeper while growing, flatter while decaying ----
        float gap = targetSpeed - _seaSpeed; // > 0 growing, < 0 decaying, 0 steady
        float gapN = Mathf.Clamp(gap / Mathf.Max(0.01f, steepnessGapRange), -1f, 1f);
        float steepTarget = gapN >= 0f
            ? Mathf.Lerp(steepnessSteady, steepnessGrowing, gapN)
            : Mathf.Lerp(steepnessSteady, steepnessDecaying, -gapN);

        if (snap || !_steepReady)
        {
            _steepSmooth = steepTarget;
            _steepReady = true;
        }
        else if (dt > 0f)
        {
            // Smooth, so an instant wind change eases the steepness in instead of snapping it
            float tauS = Mathf.Max(0.5f, seaResponseTime * 0.1f);
            _steepSmooth = Mathf.Lerp(_steepSmooth, steepTarget, 1f - Mathf.Exp(-dt / tauS));
        }
        float steep = Mathf.Max(0.005f, _steepSmooth);

        // ---- Wind speed -> sea size (Pierson-Moskowitz fully developed sea) ----
        float lo = Mathf.Max(0.05f, minWavelength);
        float limit = Mathf.Max(maxWavelengthLimit, lo * 4f);
        float peak = Mathf.Clamp(wavelengthScale * 0.83f * _seaSpeed * _seaSpeed, lo * 3f, limit * 0.5f);
        float hi = Mathf.Clamp(peak * 3f, lo * 4f, limit);
        float hs = heightScale * 0.022f * _seaSpeed * _seaSpeed * (steep / Mathf.Max(steepnessSteady, 0.005f));

        float logLo = Mathf.Log(lo);
        float logHi = Mathf.Log(hi);
        float slot = _count > 1 ? 1f / (_count - 1) : 0f;
        float h = Mathf.Max(slot * 0.5f, 0.01f);
        float simDt = dt * timeScale;
        _simTime += simDt;

        // Directional spread narrows as the sea's wind builds
        float st = Mathf.Clamp01(Mathf.InverseLerp(spreadCalmWind, Mathf.Max(spreadStrongWind, spreadCalmWind + 0.1f), _seaSpeed));
        float spread = Mathf.Lerp(spreadCalm, spreadStrong, st * st * (3f - 2f * st));
        int turning = 0;

        // Pass 1: wavelength, direction, phase and relative amplitude of every wave
        float sumA2 = 0f;
        for (int i = 0; i < MaxWaves; i++)
        {
            if (i >= _count)
            {
                _waveA[i] = Vector4.zero;
                _waveB[i] = Vector4.zero;
                _state[i] = Inactive; // when Wave Count rises again this wave grows in on the current heading
                continue;
            }

            // Position on the distribution curve, jittered within this wave's slot
            float t = _count > 1 ? i * slot : 1f;
            t = Mathf.Clamp01(t + (_rLen[i] - 0.5f) * slot * randomness);
            float d = Mathf.Clamp01(wavelengthDistribution.Evaluate(t)); // 0 = shortest, 1 = longest
            float len = Mathf.Exp(Mathf.Lerp(logLo, logHi, d));

            // How many waves share this part of the range. The spectrum is energy per wavelength,
            // so a crowded part of the curve gives each wave less, keeping the total sea the same.
            float t0 = Mathf.Clamp01(t - h);
            float t1 = Mathf.Clamp01(t + h);
            float dens = Mathf.Max(0.02f,
                (wavelengthDistribution.Evaluate(t1) - wavelengthDistribution.Evaluate(t0)) / Mathf.Max(t1 - t0, 1e-4f));

            // Pierson-Moskowitz amplitude per wave: r * exp(-0.625 r^2), r = wavelength / peak wavelength
            float r = len / peak;
            float amp = r * Mathf.Exp(-0.625f * r * r) * Mathf.Sqrt(dens)
                      * Mathf.Max(0f, spectrumShape.Evaluate(d))
                      * Mathf.Lerp(1f, 0.6f + 0.8f * _rAmp[i], randomness);

            float k = 2f * Mathf.PI / len;

            // Where this wave would head for the current wind. Long swell follows the wind
            // closely, short chop spreads out more.
            float ideal = targetDir + (_rDir[i] * 2f - 1f) * spread * Mathf.Lerp(1f, 0.35f, d);

            // A wave never rotates. If it points the wrong way it waits (long swell waits longest),
            // fades out, takes the new heading while invisible, and grows back.
            if (snap || !_anglesReady)
            {
                _heading[i] = ideal;
                _env[i] = 1f;
                _state[i] = Alive;
            }
            else if (_state[i] == Inactive)
            {
                _heading[i] = ideal;
                _env[i] = 0f;
                _timer[i] = 0f;
                _state[i] = FadingIn;
            }
            else if (dt > 0f)
            {
                float err = Mathf.Abs(Mathf.DeltaAngle(_heading[i], ideal));
                float period = Mathf.Sqrt(2f * Mathf.PI * len / Mathf.Max(gravity, 0.1f));
                float fadeTime = Mathf.Clamp(fadePeriods * period, 3f, 90f);

                switch (_state[i])
                {
                    case Alive:
                        if (err > headingTolerance)
                        {
                            float wait = seaResponseTime * Mathf.Lerp(shortWaveResponse, swellResponse, d)
                                       * Mathf.Lerp(0.3f, 1.2f, (float)_turnRnd.NextDouble());
                            _timer[i] = wait;
                            _state[i] = Waiting;
                        }
                        break;

                    case Waiting:
                        if (err < headingTolerance * 0.5f) // wind came back: no need to turn
                        {
                            _state[i] = Alive;
                            break;
                        }
                        _timer[i] -= dt;
                        if (_timer[i] <= 0f)
                        {
                            _timer[i] = 0f;
                            _state[i] = FadingOut;
                        }
                        break;

                    case FadingOut:
                    {
                        _timer[i] += dt;
                        float x = Mathf.Clamp01(_timer[i] / fadeTime);
                        _env[i] = 1f - x * x * (3f - 2f * x);
                        if (x >= 1f)
                        {
                            _env[i] = 0f;
                            _heading[i] = ideal; // invisible right now, so no pop
                            _phase[i] = (float)_turnRnd.NextDouble() * 2f * Mathf.PI;
                            _timer[i] = 0f;
                            _state[i] = FadingIn;
                        }
                        break;
                    }

                    case FadingIn:
                    {
                        _timer[i] += dt;
                        float x = Mathf.Clamp01(_timer[i] / fadeTime);
                        _env[i] = x * x * (3f - 2f * x);
                        if (x >= 1f)
                        {
                            _env[i] = 1f;
                            _state[i] = Alive;
                        }
                        break;
                    }
                }
            }
            if (_state[i] != Alive) turning++;
            float rad = _heading[i] * Mathf.Deg2Rad;

            float omega = Mathf.Sqrt(gravity * k) * speedMultiplier;

            // Accumulate the phase: stays continuous when k or the direction change over time
            _phase[i] = Mathf.Repeat(_phase[i] - omega * simDt, 2f * Mathf.PI);

            _waveA[i] = new Vector4(Mathf.Cos(rad), Mathf.Sin(rad), amp, k);
            _waveB[i] = new Vector4(0f, omega, _phase[i], 0f);
            sumA2 += amp * amp;
        }
        _anglesReady = true;

        // Pass 2: scale to the significant wave height.
        // For random phases the surface std-dev is sqrt(sum(a^2) / 2) and Hs = 4 * std-dev,
        // so the sea keeps the same overall size whatever the wave count is.
        float scale = sumA2 > 0f ? (hs * 0.25f) / Mathf.Sqrt(sumA2 * 0.5f) : 0f;
        float sumKA = 0f;
        for (int i = 0; i < _count; i++)
        {
            float k = _waveA[i].w;
            float amp = Mathf.Min(_waveA[i].z * scale, steep / k); // steepness cap: no spikes
            _waveA[i].z = amp;
            sumKA += k * amp;
        }

        // Pass 3: crest sharpness. sum(Q * k * A) = choppiness <= 1, so the surface never loops over itself.
        float q = choppiness / Mathf.Max(sumKA, 1e-5f);
        for (int i = 0; i < _count; i++)
        {
            _waveB[i].x = q;
            _waveA[i].z *= _env[i]; // fading waves, applied last so the rest of the sea doesn't change
        }

        // Used by the shader to colour troughs/crests: roughly +-1 at typical wave extremes
        _totalAmplitude = Mathf.Max(hs * 0.5f, 0.05f);

        liveSeaWindSpeed = _seaSpeed;
        livePeakWavelength = peak;
        liveWaveHeight = hs;
        liveSteepness = steep;
        liveSpread = spread;
        liveTurningWaves = turning;

        if (waterMaterial != null)
        {
            waterMaterial.SetVectorArray(WaveAId, _waveA);
            waterMaterial.SetVectorArray(WaveBId, _waveB);
            waterMaterial.SetFloat(WaveCountId, _count);
            waterMaterial.SetFloat(TotalAmpId, _totalAmplitude);
            waterMaterial.SetFloat(WaveTimeId, _simTime); // wave motion itself is baked into the phases
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
            float phase = a.w * (a.x * p.x + a.y * p.y) + b.z;
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
