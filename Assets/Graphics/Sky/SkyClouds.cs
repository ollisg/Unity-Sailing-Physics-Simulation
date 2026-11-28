using UnityEngine;

/// <summary>
/// Cheap clouds for the whole scene: a flat cloud layer drawn by the sky shader
/// (Sailing/Skybox Clouds), reflected by the water, and casting moving shadows on the sea.
///
/// Everything reads one tiling noise texture made here at start, through shader globals, so the
/// sky, the reflections and the shadows always agree. The clouds drift with the
/// WeatherController's wind, and their cover and darkness follow the wind speed: a calm day has
/// a few fair-weather clouds, a near gale is mostly overcast and grey. Like the sea, the cover
/// changes gradually.
///
/// The boat and sailor use Unity's own Lit shader, which can't read the cloud layer, so in Play
/// mode the sun is dimmed by the cloud shadow at the boat (a cloud shadow is hundreds of metres
/// wide, so the whole boat is in or out of it). The water undoes that dimming and draws its own
/// per-pixel shadows.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[AddComponentMenu("Sailing/Sky Clouds")]
public class SkyClouds : MonoBehaviour
{
    [Header("Cover")]
    [Tooltip("Take the cloud cover from the wind speed (Calm -> Gale values below).")]
    public bool followWind = true;
    [Tooltip("How much of the sky is covered (0 = clear, 1 = overcast). Used directly when Follow Wind is off.")]
    [Range(0f, 1f)] public float coverage = 0.35f;
    [Range(0f, 1f)] public float coverageCalm = 0.25f;
    [Range(0f, 1f)] public float coverageGale = 0.85f;
    [Tooltip("Wind speed (m/s) where the cover reaches Coverage Gale. ~17 m/s is a gale (Beaufort 8).")]
    [Min(1f)] public float galeWindSpeed = 17f;
    [Tooltip("Seconds for the cover to catch up with a wind change.")]
    [Min(0f)] public float coverageChangeTime = 40f;

    [Header("Shape")]
    [Tooltip("Height of the cloud layer in metres (fair-weather cumulus ~1000-2000 m).")]
    [Min(50f)] public float cloudHeight = 1500f;
    [Tooltip("World size of one repeat of the cloud pattern (m). Bigger = bigger clouds.")]
    [Min(100f)] public float tileSize = 8000f;
    [Tooltip("How crisp the cloud edges are.")]
    [Range(1f, 12f)] public float sharpness = 3.5f;
    [Tooltip("Small-scale detail that reshapes the clouds as they drift (calm weather).")]
    [Range(0f, 1f)] public float detail = 0.3f;
    [Tooltip("Detail in a gale (with Follow Wind): stormy clouds are more ragged.")]
    [Range(0f, 1f)] public float detailGale = 0.55f;
    [Tooltip("Bends the cloud layer into a dome, like the sky of a small planet, so the clouds curve " +
             "down to the horizon instead of looking like a flat ceiling. Smaller = rounder. " +
             "0 = flat. (The real Earth, 6 371 000 m, is almost flat at this scale.)")]
    [Min(0f)] public float domeRadius = 30000f;
    public int seed = 7;

    [Header("Motion")]
    [Tooltip("Cloud drift speed relative to the surface wind (the wind higher up is stronger).")]
    [Min(0f)] public float driftMultiplier = 1.5f;
    [Tooltip("How fast the clouds change shape, as a fraction of the wind speed. The detail noise " +
             "runs ahead of the clouds, downwind, at this extra speed, so the shapes grow, split and fade " +
             "faster in strong wind.")]
    [Min(0f)] public float shapeChange = 0.6f;
    [Tooltip("Shape change speed (m/s) even when there is no wind, so a calm sky isn't frozen.")]
    [Min(0f)] public float shapeChangeMin = 1.5f;

    [Header("Look")]
    [ColorUsage(false, true)] public Color litColor = new Color(1.05f, 1.03f, 1.0f);
    [ColorUsage(false, true)] public Color shadeColor = new Color(0.55f, 0.6f, 0.68f);
    [Tooltip("How much darker the clouds get when the sky is overcast.")]
    [Range(0f, 1f)] public float overcastDarkening = 0.45f;
    [Tooltip("Glow of thin cloud edges near the sun.")]
    [Range(0f, 3f)] public float silverLining = 1.2f;
    [Tooltip("Distance (m) over which far clouds fade into the sky colour.")]
    [Min(100f)] public float horizonFade = 30000f;

    [Header("Storm Sky")]
    [Tooltip("Colour the clear sky turns towards in a storm (it is also greyed out). " +
             "Darker = gloomier storms.")]
    public Color stormSkyTint = new Color(0.62f, 0.66f, 0.72f);
    [Tooltip("How far the sky goes towards the storm colour at full storm.")]
    [Range(0f, 1f)] public float stormSkyAmount = 0.85f;
    [Tooltip("Play mode: tint the ambient light (the sky light on the boat and sailor) the same way, " +
             "so models get greyer and darker in a storm too.")]
    public bool stormAmbient = true;
    [Tooltip("How much dimmer the sky reflections on models get at full storm.")]
    [Range(0f, 1f)] public float stormReflectionDim = 0.5f;
    [Tooltip("Play mode: how much weaker the sunlight is overall at full storm (thick, hazy air), " +
             "on top of the cloud shadows.")]
    [Range(0f, 1f)] public float stormSunDim = 0.3f;

    [Header("Shadows")]
    [Tooltip("How much sunlight a thick cloud blocks.")]
    [Range(0f, 1f)] public float shadowStrength = 0.6f;
    [Tooltip("Play mode: dim the sun by the cloud shadow at the boat, so the boat and sailor are shaded too.")]
    public bool shadeObjects = true;
    [Tooltip("Where the boat is (found automatically: the boat's mass model, else the main camera).")]
    public Transform focus;
    [Tooltip("The sun (found automatically: Lighting > Sun Source, else the first directional light).")]
    public Light sun;

    [Header("Read-only")]
    [SerializeField, Range(0f, 1f)] float currentCoverage;
    [SerializeField, Range(0f, 1f)] float shadowAtFocus = 1f;
    [SerializeField, Range(0f, 1f)] float stormAmount;

    const int Size = 256;
    const float DetailScale = 4f; // mip 2 + log2(4) = mip 4 for the detail layer in shadows

    Texture2D _noise;
    float[] _mip0, _mip2, _mip4;
    int _builtSeed = int.MinValue;
    Vector2 _offA, _offB;
    float _baseIntensity = -1f;
    bool _dimming;
    UnityEngine.Rendering.SphericalHarmonicsL2 _baseAmbient;
    float _baseReflection;
    bool _ambientSaved;

    static readonly int NoiseId = Shader.PropertyToID("_CloudNoise");
    static readonly int ParamsId = Shader.PropertyToID("_CloudParams");
    static readonly int OffsetId = Shader.PropertyToID("_CloudOffset");
    static readonly int ShapeId = Shader.PropertyToID("_CloudShape");
    static readonly int LitId = Shader.PropertyToID("_CloudLit");
    static readonly int ShadeId = Shader.PropertyToID("_CloudShade");
    static readonly int SunId = Shader.PropertyToID("_CloudSun");
    static readonly int MiscId = Shader.PropertyToID("_CloudMisc");
    static readonly int SkyTintId = Shader.PropertyToID("_CloudSkyTint");

    public float CurrentCoverage => currentCoverage;
    public float ShadowAtFocus => shadowAtFocus;

    void OnEnable()
    {
        currentCoverage = TargetCoverage();
        FindRefs();
        BuildNoise();
        if (Application.isPlaying && sun != null) { _baseIntensity = sun.intensity; _dimming = true; }
        if (Application.isPlaying)
        {
            _baseAmbient = RenderSettings.ambientProbe;
            _baseReflection = RenderSettings.reflectionIntensity;
            _ambientSaved = true;
        }
        Push(0f);
    }

    void OnDisable()
    {
        if (_dimming && sun != null && _baseIntensity >= 0f) sun.intensity = _baseIntensity;
        _dimming = false;
        if (_ambientSaved)
        {
            RenderSettings.ambientProbe = _baseAmbient;
            RenderSettings.reflectionIntensity = _baseReflection;
            _ambientSaved = false;
        }
        Shader.SetGlobalVector(ParamsId, Vector4.zero); // turns the clouds off everywhere
    }

    void OnDestroy()
    {
        if (_noise != null) { if (Application.isPlaying) Destroy(_noise); else DestroyImmediate(_noise); }
    }

    void OnValidate()
    {
        if (!isActiveAndEnabled) return;
        if (seed != _builtSeed) BuildNoise();
        if (!Application.isPlaying) currentCoverage = TargetCoverage();
        Push(0f);
    }

    void Update()
    {
        if (_noise == null || seed != _builtSeed) BuildNoise();
        float dt = Application.isPlaying ? Time.deltaTime : 0f;

        float target = TargetCoverage();
        if (!Application.isPlaying || coverageChangeTime <= 0f) currentCoverage = target;
        else currentCoverage = Mathf.MoveTowards(currentCoverage, target, dt / coverageChangeTime);

        Push(dt);
    }

    float TargetCoverage()
    {
        if (!followWind) return coverage;
        float w = WeatherController.Instance != null ? WeatherController.Instance.WindSpeed : 6f;
        return Mathf.Lerp(coverageCalm, coverageGale, Mathf.SmoothStep(0f, 1f, w / galeWindSpeed));
    }

    float CurrentDetail()
    {
        if (!followWind) return detail;
        float w = WeatherController.Instance != null ? WeatherController.Instance.WindSpeed : 6f;
        return Mathf.Lerp(detail, detailGale, Mathf.SmoothStep(0f, 1f, w / galeWindSpeed));
    }

    void FindRefs()
    {
        if (sun == null) sun = RenderSettings.sun;
        if (sun == null)
            foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Exclude))
                if (l.type == LightType.Directional) { sun = l; break; }
        if (focus == null)
        {
            var boat = FindAnyObjectByType<BoatMassModel>();
            if (boat != null) focus = boat.transform;
            else if (Camera.main != null) focus = Camera.main.transform;
        }
    }

    // Sends everything to the shaders and moves the clouds with the wind.
    void Push(float dt)
    {
        if (_noise == null) return;
        float invTile = 1f / tileSize;

        Vector3 wind = WeatherController.Instance != null ? WeatherController.Instance.WindVector : Vector3.zero;
        Vector2 drift = new Vector2(wind.x, wind.z) * (driftMultiplier * dt * invTile);
        _offA = Frac(_offA - drift);
        // detail moves with the clouds and a bit faster, in the same direction, which reshapes them
        // (faster in strong wind)
        Vector2 w2 = new Vector2(wind.x, wind.z);
        float ws = w2.magnitude;
        Vector2 wd = ws > 1e-3f ? w2 / ws : Vector2.right;
        Vector2 morph = wd * ((shapeChangeMin + ws * shapeChange) * dt * invTile);
        _offB = Frac(_offB - (drift + morph) * DetailScale);

        float c = currentCoverage;
        float dark = overcastDarkening * c * c;
        Color shade = shadeColor * (1f - dark);
        Color lit = Color.Lerp(litColor, shade * 1.25f, c * c * c * 0.6f);
        Color sunTint = sun != null ? sun.color : Color.white;
        lit *= sunTint; shade *= Color.Lerp(Color.white, sunTint, 0.5f);

        Vector3 toSun = sun != null ? -sun.transform.forward : Vector3.up;

        Shader.SetGlobalTexture(NoiseId, _noise);
        Shader.SetGlobalVector(ParamsId, new Vector4(c, sharpness, invTile, cloudHeight));
        Shader.SetGlobalVector(OffsetId, new Vector4(_offA.x, _offA.y, _offB.x, _offB.y));
        Shader.SetGlobalVector(ShapeId, new Vector4(CurrentDetail(), DetailScale, tileSize * 0.012f, horizonFade));
        Shader.SetGlobalVector(LitId, (Vector4)lit.linear);
        Shader.SetGlobalVector(ShadeId, (Vector4)shade.linear);
        // storm amount follows the (gradually changing) cover, so the sky darkens with the clouds
        float lo = followWind ? coverageCalm : 0.3f, hi = followWind ? coverageGale : 0.95f;
        stormAmount = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(lo, hi, c));
        Color st = stormSkyTint.linear;
        Shader.SetGlobalVector(SkyTintId, new Vector4(st.r, st.g, st.b, stormAmount * stormSkyAmount));
        if (_ambientSaved) ApplyStormAmbient(st, stormAmount * stormSkyAmount);
        Shader.SetGlobalVector(SunId, new Vector4(toSun.x, toSun.y, toSun.z, shadowStrength));

        // cloud shadow at the boat; dim the sun by it (Play mode only, so the Inspector value stays put)
        Vector3 fp = focus != null ? focus.position : Vector3.zero;
        shadowAtFocus = ShadowAt(fp, toSun, c, invTile);
        float comp = 1f;
        if (_dimming && sun != null && _baseIntensity >= 0f)
        {
            if (shadeObjects)
            {
                sun.intensity = _baseIntensity * shadowAtFocus * (1f - stormSunDim * stormAmount);
                comp = 1f / Mathf.Max(shadowAtFocus, 0.05f);
            }
            else sun.intensity = _baseIntensity * (1f - stormSunDim * stormAmount);
        }
        Shader.SetGlobalVector(MiscId, new Vector4(Size, comp, silverLining, domeRadius));
    }

    // Same as StormSky() in SailingClouds.hlsl, applied to the ambient light: each SH coefficient is
    // greyed (by luminance) and tinted. SH is linear, so this is the same as tinting the sky it came from.
    void ApplyStormAmbient(Color tint, float amount)
    {
        if (!stormAmbient) amount = 0f;
        var sh = _baseAmbient;
        if (amount > 0f)
        {
            for (int i = 0; i < 9; i++)
            {
                float r = _baseAmbient[0, i], g = _baseAmbient[1, i], b = _baseAmbient[2, i];
                float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                sh[0, i] = Mathf.Lerp(r, lum * tint.r, amount);
                sh[1, i] = Mathf.Lerp(g, lum * tint.g, amount);
                sh[2, i] = Mathf.Lerp(b, lum * tint.b, amount);
            }
        }
        RenderSettings.ambientProbe = sh;
        RenderSettings.reflectionIntensity = _baseReflection * (1f - stormReflectionDim * (stormAmbient ? stormAmount : 0f));
    }

    // Same as CloudShadow() in SailingClouds.hlsl, on the CPU.
    float ShadowAt(Vector3 p, Vector3 toSun, float cover, float invTile)
    {
        float sy = Mathf.Max(toSun.y, 0.1f);
        float qx = p.x + toSun.x / sy * (cloudHeight - p.y);
        float qz = p.z + toSun.z / sy * (cloudHeight - p.y);
        float u = qx * invTile, v = qz * invTile;
        float n1 = Bilinear(_mip2, Size / 4, u + _offA.x, v + _offA.y);
        float n2 = Bilinear(_mip4, Size / 16, u * DetailScale + _offB.x, v * DetailScale + _offB.y);
        float n = n1 + (n2 - 0.5f) * CurrentDetail();
        float d = Mathf.Clamp01((n - (1f - cover)) * sharpness);
        return 1f - d * shadowStrength;
    }

    static float Bilinear(float[] m, int s, float u, float v)
    {
        float x = u * s - 0.5f, y = v * s - 0.5f;
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        float fx = x - x0, fy = y - y0;
        int xa = Mod(x0, s), xb = Mod(x0 + 1, s), ya = Mod(y0, s), yb = Mod(y0 + 1, s);
        float a = Mathf.Lerp(m[ya * s + xa], m[ya * s + xb], fx);
        float b = Mathf.Lerp(m[yb * s + xa], m[yb * s + xb], fx);
        return Mathf.Lerp(a, b, fy);
    }

    static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }
    static Vector2 Frac(Vector2 v) => new Vector2(v.x - Mathf.Floor(v.x), v.y - Mathf.Floor(v.y));

    // ---------- noise texture ----------

    // Tiling fractal Perlin noise, histogram-equalised so 'coverage' is roughly the covered fraction.
    void BuildNoise()
    {
        _builtSeed = seed;
        var raw = new float[Size * Size];
        var rnd = new System.Random(seed);
        var gx = new float[64 * 64]; var gy = new float[64 * 64];
        for (int i = 0; i < gx.Length; i++)
        {
            double a = rnd.NextDouble() * Mathf.PI * 2.0;
            gx[i] = (float)System.Math.Cos(a); gy[i] = (float)System.Math.Sin(a);
        }
        int[] periods = { 4, 8, 16, 32, 64 };
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float sum = 0f, amp = 1f, norm = 0f;
                for (int o = 0; o < periods.Length; o++)
                {
                    int p = periods[o];
                    float fx = x * p / (float)Size, fy = y * p / (float)Size;
                    sum += amp * Perlin(fx, fy, p, gx, gy, o * 17);
                    norm += amp; amp *= 0.5f;
                }
                raw[y * Size + x] = sum / norm;
            }

        // equalise: value -> its rank, so the result is evenly spread over 0..1
        var idx = new int[raw.Length];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        System.Array.Sort(raw.Clone() as float[], idx);
        _mip0 = new float[raw.Length];
        for (int r = 0; r < idx.Length; r++) _mip0[idx[r]] = r / (float)(idx.Length - 1);

        if (_noise == null)
        {
            _noise = new Texture2D(Size, Size, TextureFormat.R8, true, true)
            {
                name = "CloudNoise", wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear, hideFlags = HideFlags.DontSave
            };
        }
        var px = new byte[Size * Size];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)Mathf.RoundToInt(_mip0[i] * 255f);
        _noise.SetPixelData(px, 0);
        _noise.Apply(true, false);

        // CPU copies of the mips the shadow uses (box filtered, like the GPU's mips)
        for (int i = 0; i < _mip0.Length; i++) _mip0[i] = px[i] / 255f;
        _mip2 = Downsample(Downsample(_mip0, Size), Size / 2);
        _mip4 = Downsample(Downsample(_mip2, Size / 4), Size / 8);
    }

    static float[] Downsample(float[] src, int s)
    {
        int h = s / 2;
        var dst = new float[h * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < h; x++)
                dst[y * h + x] = 0.25f * (src[(2 * y) * s + 2 * x] + src[(2 * y) * s + 2 * x + 1] +
                                          src[(2 * y + 1) * s + 2 * x] + src[(2 * y + 1) * s + 2 * x + 1]);
        return dst;
    }

    // Perlin noise that repeats every 'period' cells.
    static float Perlin(float x, float y, int period, float[] gx, float[] gy, int salt)
    {
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        float fx = x - x0, fy = y - y0;
        float u = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f);
        float v = fy * fy * fy * (fy * (fy * 6f - 15f) + 10f);
        float n00 = Grad(x0, y0, fx, fy, period, gx, gy, salt);
        float n10 = Grad(x0 + 1, y0, fx - 1f, fy, period, gx, gy, salt);
        float n01 = Grad(x0, y0 + 1, fx, fy - 1f, period, gx, gy, salt);
        float n11 = Grad(x0 + 1, y0 + 1, fx - 1f, fy - 1f, period, gx, gy, salt);
        return Mathf.Lerp(Mathf.Lerp(n00, n10, u), Mathf.Lerp(n01, n11, u), v);
    }

    static float Grad(int ix, int iy, float dx, float dy, int period, float[] gx, float[] gy, int salt)
    {
        ix = Mod(ix, period); iy = Mod(iy, period);
        int h = ((ix * 73856093) ^ (iy * 19349663) ^ (salt * 83492791)) & 0x7fffffff;
        int g = h % gx.Length;
        return gx[g] * dx + gy[g] * dy;
    }
}
