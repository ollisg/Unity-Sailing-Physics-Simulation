using UnityEngine;

/// <summary>
/// Streaks of air that show the wind. Put it on a child of the camera together with a Particle
/// System (Tools > Weather > Create Wind Particles does this). The particles live in world space,
/// move with the WeatherController's wind, and are emitted in a box around the camera.
/// More wind = more, brighter, longer streaks. The Particle System is configured from this
/// component, so tweak the values here instead of in the Particle System.
/// </summary>
[RequireComponent(typeof(ParticleSystem))]
[DisallowMultipleComponent]
public class WindParticles : MonoBehaviour
{
    [Tooltip("Leave empty to use the WeatherController in the scene.")]
    public WeatherController weather;
    [Tooltip("The box of air is centred on this transform. Empty = this object's parent, then the main camera.")]
    public Transform follow;

    [Header("Look")]
    [Tooltip("Streak count multiplier.")]
    [Range(0.1f, 5f)] public float density = 1f;
    [Range(0.005f, 0.2f)] public float streakWidth = 0.03f;
    [Tooltip("How long streaks get per m/s of wind. 0.12 gives a 1.2 m streak at 10 m/s.")]
    [Range(0f, 0.5f)] public float streakLength = 0.12f;
    [Range(0f, 1f)] public float maxOpacity = 0.5f;
    public Color color = new Color(0.9f, 0.95f, 1f, 1f);
    [Tooltip("Wind speed (m/s) at which the streaks reach full opacity.")]
    [Min(0.1f)] public float fullOpacityWind = 8f;

    [Header("Air Volume (metres, stays aligned with the world)")]
    public Vector3 volume = new Vector3(60f, 10f, 60f);
    [Tooltip("Height of the middle of the box above the camera.")]
    public float centreHeight = 2f;

    [Header("Behaviour")]
    [Tooltip("Seconds the streaks take to follow a change of the real wind.")]
    [Min(0f)] public float windSmoothing = 1.5f;
    [Tooltip("Random swirl in the streaks. 0 = perfectly straight.")]
    [Min(0f)] public float turbulence = 1f;

    ParticleSystem _ps;
    ParticleSystemRenderer _renderer;
    Vector3 _wind;
    bool _windReady;

    void Awake()
    {
        _ps = GetComponent<ParticleSystem>();
        _renderer = GetComponent<ParticleSystemRenderer>();
        Configure();
    }

    void Configure()
    {
        _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = _ps.main;
        main.loop = true;
        main.duration = 5f;
        main.prewarm = true; // the air is already full of streaks when the game starts
        main.playOnAwake = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startSpeed = 0f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(3f, 5f);
        main.gravityModifier = 0f;
        main.maxParticles = 5000;

        var emission = _ps.emission;
        emission.enabled = true;
        emission.rateOverTime = 0f;

        var shape = _ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.rotation = Vector3.zero;

        // Wind moves every particle, set each frame in Update
        var vel = _ps.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.World;
        vel.x = 0f;
        vel.y = 0f;
        vel.z = 0f;

        var noise = _ps.noise;
        noise.enabled = true;
        noise.frequency = 0.25f;
        noise.scrollSpeed = 0.4f;
        noise.damping = true;
        noise.quality = ParticleSystemNoiseQuality.Low;

        // Fade in and out so streaks never pop
        var col = _ps.colorOverLifetime;
        col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f),
                    new GradientAlphaKey(1f, 0.85f), new GradientAlphaKey(0f, 1f) });
        col.color = grad;

        _renderer.renderMode = ParticleSystemRenderMode.Stretch;
        _renderer.lengthScale = 1f;
        _renderer.cameraVelocityScale = 0f;
        _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _renderer.receiveShadows = false;
        _renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        _renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

        if (_renderer.sharedMaterial == null)
        {
            Shader sh = Shader.Find("Custom/URP/WindStreak");
            if (sh != null) _renderer.sharedMaterial = new Material(sh);
        }

        _ps.Play();
    }

    void Update()
    {
        if (_ps == null) return;
        if (weather == null) weather = WeatherController.Instance;

        // The streaks follow the real wind (air reacts much faster than the sea), lightly smoothed
        Vector3 target = weather != null ? weather.WindVector : Vector3.zero;
        if (!_windReady) { _wind = target; _windReady = true; }
        else _wind = Vector3.Lerp(_wind, target, 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(windSmoothing, 0.01f)));
        float speed = _wind.magnitude;

        var vel = _ps.velocityOverLifetime;
        vel.x = _wind.x;
        vel.y = _wind.y;
        vel.z = _wind.z;

        var emission = _ps.emission;
        emission.rateOverTime = speed * 40f * density;

        var main = _ps.main;
        main.startSize = new ParticleSystem.MinMaxCurve(streakWidth * 0.6f, streakWidth * 1.4f);
        main.startColor = new Color(color.r, color.g, color.b, maxOpacity * Mathf.Clamp01(speed / fullOpacityWind));

        var shape = _ps.shape;
        shape.scale = volume;
        shape.position = new Vector3(0f, centreHeight, 0f);

        var noise = _ps.noise;
        noise.strength = (0.2f + speed * 0.05f) * turbulence;

        _renderer.velocityScale = streakLength;
    }

    void LateUpdate()
    {
        Transform t = follow;
        if (t == null) t = transform.parent;
        if (t == null && Camera.main != null) t = Camera.main.transform;

        // Follow the camera's position but stay aligned with the world, so the box doesn't tilt with the view
        if (t != null) transform.position = t.position;
        transform.rotation = Quaternion.identity;
    }
}
