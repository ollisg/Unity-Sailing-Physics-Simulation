using UnityEngine;

/// <summary>
/// Makes an object float on the Gerstner ocean using the CPU surface sampling of
/// GerstnerWaveController (GetHeightAndNormal / GetVelocity).
///
/// FollowSurface: the object is placed exactly on the surface (good for checking that the
///                sampled surface matches what you see).
/// Buoyant:       a Rigidbody gets an upthrust from the submerged part of a sphere plus drag
///                relative to the moving water (a first test of the physics side).
/// </summary>
[DisallowMultipleComponent]
public class WaterFloater : MonoBehaviour
{
    public enum Mode { FollowSurface, Buoyant }

    [Tooltip("Leave empty to use the GerstnerWaveController in the scene.")]
    public GerstnerWaveController waves;
    public Mode mode = Mode.FollowSurface;

    [Header("Follow Surface")]
    [Tooltip("Height of the object's pivot above the surface (0 = a sphere sits half submerged).")]
    public float heightOffset = 0f;
    [Tooltip("Tilt the object to match the surface slope.")]
    public bool alignToSurface = true;
    [Min(0.1f)] public float alignSpeed = 6f;

    [Header("Buoyant (sphere)")]
    [Tooltip("Density of the object in kg/m3. Water is 1000, so 500 floats half submerged.")]
    [Min(1f)] public float objectDensity = 500f;
    [Tooltip("Pulls the object along with the moving water (per second, scaled by how submerged it is).")]
    [Min(0f)] public float waterDrag = 2f;
    [Tooltip("Slows down spinning (per second, scaled by how submerged it is).")]
    [Min(0f)] public float rotationDamping = 1.5f;

    [Header("Debug")]
    [Tooltip("Draws the sampled surface normal (cyan) and water velocity (yellow) in the Scene view.")]
    public bool drawDebug = true;

    const float WaterDensity = 1000f;

    Rigidbody _rb;
    float _radius;

    float Radius => 0.5f * Mathf.Max(transform.lossyScale.x, Mathf.Max(transform.lossyScale.y, transform.lossyScale.z));

    void Awake()
    {
        FindWaves();
    }

    void Start()
    {
        if (mode == Mode.Buoyant) SetupRigidbody();
    }

    void FindWaves()
    {
        if (waves == null) waves = FindAnyObjectByType<GerstnerWaveController>();
    }

    void SetupRigidbody()
    {
        _radius = Radius;
        _rb = GetComponent<Rigidbody>();
        if (_rb == null) _rb = gameObject.AddComponent<Rigidbody>();
        _rb.isKinematic = false;
        _rb.useGravity = true;

        float volume = 4f / 3f * Mathf.PI * _radius * _radius * _radius;
        _rb.mass = Mathf.Max(0.01f, objectDensity * volume);
    }

    // Follow mode runs after the controller has advanced the waves this frame
    void LateUpdate()
    {
        if (mode != Mode.FollowSurface) return;
        if (waves == null) { FindWaves(); if (waves == null) return; }

        Vector3 pos = transform.position;
        float h = waves.GetHeightAndNormal(pos, out Vector3 normal);
        pos.y = h + heightOffset;
        transform.position = pos;

        if (alignToSurface)
        {
            Quaternion target = Quaternion.FromToRotation(transform.up, normal) * transform.rotation;
            transform.rotation = Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-alignSpeed * Time.deltaTime));
        }

        if (drawDebug)
        {
            Debug.DrawRay(pos, normal * 1.5f, Color.cyan);
            Debug.DrawRay(pos, waves.GetVelocity(pos), Color.yellow);
        }
    }

    void FixedUpdate()
    {
        if (mode != Mode.Buoyant) return;
        if (waves == null) { FindWaves(); if (waves == null) return; }
        if (_rb == null) SetupRigidbody();

        Vector3 pos = _rb.worldCenterOfMass;
        float h = waves.GetHeightAndNormal(pos, out Vector3 normal);

        float depth = Mathf.Clamp(h - (pos.y - _radius), 0f, 2f * _radius);
        if (drawDebug)
        {
            Debug.DrawRay(pos, normal * 1.5f, Color.cyan);
            Debug.DrawRay(pos, waves.GetVelocity(pos), Color.yellow);
        }
        if (depth <= 0f) return;

        // Volume of the part of the sphere under water (spherical cap)
        float cap = Mathf.PI * depth * depth * (3f * _radius - depth) / 3f;
        float full = 4f / 3f * Mathf.PI * _radius * _radius * _radius;
        float submerged = cap / full; // 0..1

        // Archimedes: the weight of the displaced water pushes up
        _rb.AddForce(Vector3.up * (WaterDensity * Physics.gravity.magnitude * cap));

        // Drag relative to the moving water
        Vector3 rel = waves.GetVelocity(pos) - _rb.GetPointVelocity(pos);
        _rb.AddForce(rel * (waterDrag * submerged), ForceMode.Acceleration);
        _rb.AddTorque(-_rb.angularVelocity * (rotationDamping * submerged), ForceMode.Acceleration);
    }
}
