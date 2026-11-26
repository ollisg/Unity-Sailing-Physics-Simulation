using UnityEngine;

/// <summary>
/// The "real" weather of the scene. Put it on an empty GameObject and change it from the
/// Inspector or from code (SetWind). The ocean (GerstnerWaveController) never jumps to these
/// values: it chases them over time, so a wind change builds up and dies down like a real sea.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[AddComponentMenu("Sailing/Weather Controller")]
public class WeatherController : MonoBehaviour
{
    /// <summary>The active weather controller in the scene (null if there is none).</summary>
    public static WeatherController Instance { get; private set; }

    [Tooltip("Wind speed in metres per second.\n" +
             "~3 light breeze, ~6 moderate (good dinghy sailing), ~10 strong, ~15 near gale.")]
    [Min(0f)] public float windSpeed = 6f;

    [Tooltip("Direction the wind blows towards, in degrees (0 = +X axis, 90 = +Z axis). " +
             "Same convention as the waves.")]
    [Range(0f, 360f)] public float windDirection = 0f;

    public float WindSpeed => windSpeed;
    public float WindDirection => windDirection;

    /// <summary>Wind as a world-space vector (metres per second, Y = 0).</summary>
    public Vector3 WindVector
    {
        get
        {
            float r = windDirection * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(r), 0f, Mathf.Sin(r)) * windSpeed;
        }
    }

    /// <summary>Sets the real wind. The sea follows it gradually.</summary>
    public void SetWind(float speed, float directionDegrees)
    {
        windSpeed = Mathf.Max(0f, speed);
        windDirection = Mathf.Repeat(directionDegrees, 360f);
    }

    void OnEnable()
    {
        Instance = this;
    }

    void OnDisable()
    {
        if (Instance == this) Instance = null;
    }

    void OnValidate()
    {
        windSpeed = Mathf.Max(0f, windSpeed);
    }

    void OnDrawGizmos()
    {
        float r = windDirection * Mathf.Deg2Rad;
        Vector3 dir = new Vector3(Mathf.Cos(r), 0f, Mathf.Sin(r));
        Vector3 from = transform.position;
        Vector3 to = from + dir * (2f + windSpeed * 0.5f);

        Gizmos.color = new Color(0.4f, 0.8f, 1f);
        Gizmos.DrawLine(from, to);

        Vector3 side = Vector3.Cross(Vector3.up, dir);
        Gizmos.DrawLine(to, to - dir * 0.8f + side * 0.4f);
        Gizmos.DrawLine(to, to - dir * 0.8f - side * 0.4f);
    }
}
