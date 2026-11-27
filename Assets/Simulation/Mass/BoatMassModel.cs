using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Gives the boat's single Rigidbody its real mass, centre of mass and inertia tensor every
/// physics step:
///     fixed parts (hull, mast, foils; boxes from their mesh bounds)
///   + moving parts (every MassSource under the boat: SailorMassModel, BoomRig)
/// All of it is added up in the boat's own frame, so moving the sailor or swinging the boom
/// moves the centre of mass and changes the inertia, e.g. a hiked sailor makes the boat slower
/// to roll.
///
/// The boat root must have scale (1,1,1): the Rigidbody frame has no scale.
/// </summary>
[DefaultExecutionOrder(-100)]
[RequireComponent(typeof(Rigidbody))]
[DisallowMultipleComponent]
public class BoatMassModel : MonoBehaviour
{
    [Serializable]
    public class FixedPart
    {
        public Transform part;
        [Min(0f)] public float mass;
        [Tooltip("Hollow shell (like the hull): mass sits towards the surface, so more inertia than a solid box.")]
        public bool hollow;
        [Tooltip("Shift of this part's centre of mass from its mesh-bounds centre, in boat axes (m).")]
        public Vector3 comOffset;
    }

    [Tooltip("Hull, mast, centreboard, rudder... Masses are estimates: check them against class data.")]
    public List<FixedPart> fixedParts = new List<FixedPart>();

    [Tooltip("Moving masses. Leave empty to use every MassSource under the boat.")]
    public List<MassSource> movingParts = new List<MassSource>();

    [Tooltip("Spreads a hollow part's mass this much more than a solid box (rough shell factor).")]
    [Min(1f)] public float hollowFactor = 1.6f;

    [Header("Result (read-only)")]
    [SerializeField] float totalMass;
    [SerializeField] Vector3 centerOfMassLocal;
    [SerializeField] Vector3 principalInertia;

    public float TotalMass => totalMass;
    public Vector3 CenterOfMassLocal => centerOfMassLocal;

    Rigidbody _rb;
    MassAccumulator _fixed;

    void Reset()
    {
        fixedParts = DefaultFixedParts(transform);
    }

    static List<FixedPart> DefaultFixedParts(Transform root)
    {
        var list = new List<FixedPart>();
        void Add(string name, float mass, bool hollow)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) { list.Add(new FixedPart { part = t, mass = mass, hollow = hollow }); return; }
        }
        Add("Hull1", 59f, true);   // hull
        Add("Mast", 6f, false);    // mast (both sections)
        Add("Cube", 3f, false);    // centreboard
        Add("Cube.001", 2f, false); // rudder blade
        return list;
    }

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        if ((transform.lossyScale - Vector3.one).sqrMagnitude > 1e-4f)
            Debug.LogWarning("BoatMassModel: the boat root should have scale (1,1,1).", this);

        if (fixedParts.Count == 0) fixedParts = DefaultFixedParts(transform);
        if (movingParts.Count == 0) movingParts.AddRange(GetComponentsInChildren<MassSource>(true));

        _rb.automaticCenterOfMass = false;
        _rb.automaticInertiaTensor = false;
    }

    void Start()
    {
        // Fixed parts never move relative to the boat, so add them up once.
        // (Start, so BoomRig has already moved the boom and sail out of the way.)
        _fixed = new MassAccumulator();
        foreach (var fp in fixedParts)
        {
            if (fp.part == null || fp.mass <= 0f) continue;
            var mf = fp.part.GetComponent<MeshFilter>();
            Vector3 centreW, size;
            Quaternion rotW = fp.part.rotation;
            if (mf != null && mf.sharedMesh != null)
            {
                Bounds b = mf.sharedMesh.bounds;
                centreW = fp.part.TransformPoint(b.center);
                size = Vector3.Scale(b.size, fp.part.lossyScale);
                size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
            }
            else
            {
                centreW = fp.part.position;
                size = Vector3.one * 0.1f;
            }

            Quaternion inv = Quaternion.Inverse(transform.rotation);
            Vector3 centre = inv * (centreW - transform.position) + fp.comOffset;
            Sym3 spread = MassAccumulator.BoxSpread(size, inv * rotW, fp.hollow ? hollowFactor : 1f);
            _fixed.Add(fp.mass, centre, spread);
        }
        Apply();
    }

    void FixedUpdate()
    {
        Apply();
    }

    void Apply()
    {
        var acc = _fixed;
        Vector3 pos = transform.position;
        Quaternion rot = transform.rotation;
        foreach (var src in movingParts)
            if (src != null && src.isActiveAndEnabled) src.Accumulate(ref acc, pos, rot);

        if (acc.mass <= 0f) return;

        acc.InertiaAboutCenter().Eigen(out Vector3 values, out Quaternion axes);
        values = new Vector3(Mathf.Max(values.x, 1e-3f), Mathf.Max(values.y, 1e-3f), Mathf.Max(values.z, 1e-3f));

        _rb.mass = acc.mass;
        _rb.centerOfMass = acc.CenterOfMass;
        _rb.inertiaTensor = values;
        _rb.inertiaTensorRotation = axes;

        totalMass = acc.mass;
        centerOfMassLocal = acc.CenterOfMass;
        principalInertia = values;
    }

    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying || _rb == null) return;
        Vector3 c = _rb.worldCenterOfMass;
        Gizmos.color = Color.magenta;
        Gizmos.DrawSphere(c, 0.06f);
        Quaternion r = transform.rotation * _rb.inertiaTensorRotation;
        Gizmos.color = Color.red;   Gizmos.DrawLine(c, c + r * Vector3.right * 0.5f);
        Gizmos.color = Color.green; Gizmos.DrawLine(c, c + r * Vector3.up * 0.5f);
        Gizmos.color = Color.blue;  Gizmos.DrawLine(c, c + r * Vector3.forward * 0.5f);
    }
}
