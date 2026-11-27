using UnityEngine;

/// <summary>
/// Swings the boom and sail around the mast with their own dynamics, and reports their mass
/// to BoatMassModel.
///
/// The boom angle θ is one degree of freedom, integrated here every physics step:
///     I θ̈ = τ_inertial + τ_wind + τ_aero − c θ̇ − k θ̇|θ̇|
/// - I            boom + sail inertia about the mast axis (from their mass and geometry)
/// - τ_inertial   what the boat's motion does to the boom in the boat's frame: gravity when
///                heeled, the boat's acceleration, turning (centrifugal) and Coriolis forces.
/// - τ_wind       optional simple wind on the sail (flat plate), until a real sail model exists
/// - τ_aero       set from outside by the future sail script (aeroTorque)
/// - damping      friction at the gooseneck (c) and air resistance of the swinging sail (k)
/// The mainsheet limits |θ| to sheetMaxAngle; hitting it bounces with stopRestitution.
///
/// θ = 0 is the boom as modelled (on the centreline); positive θ swings it to the boat's +X side.
/// At start the boom and sail are moved under a new "BoomPivot" object at the gooseneck,
/// which is the transform that rotates.
/// </summary>
[DefaultExecutionOrder(-200)]
[DisallowMultipleComponent]
public class BoomRig : MassSource
{
    [Header("Parts (found by name under the boat if empty)")]
    public Rigidbody boat;
    public Transform boom;
    public Transform sail;
    public Transform mast;
    [Tooltip("Optional. Leave empty to use the boom end closest to the mast.")]
    public Transform gooseneck;

    [Header("Mass (estimates: check against class data)")]
    [Min(0f)] public float boomMass = 3f;
    [Min(0f)] public float sailMass = 2.5f;

    [Header("Mainsheet")]
    [Tooltip("How far the boom may swing out from the centreline, either side (degrees).")]
    [Range(0f, 90f)] public float sheetMaxAngle = 30f;
    [Tooltip("Bounce when the boom hits the sheet limit (0 = stops dead, 1 = full bounce).")]
    [Range(0f, 1f)] public float stopRestitution = 0.15f;

    [Header("Damping")]
    [Tooltip("Gooseneck friction, N·m per rad/s.")]
    [Min(0f)] public float pivotFriction = 1f;
    [Tooltip("Air resistance of the sail swinging, N·m per (rad/s)².")]
    [Min(0f)] public float airDamping = 4f;

    [Header("Simple wind (placeholder until the sail force model)")]
    public bool simpleWindTorque = true;
    [Tooltip("ILCA 7 sail area is 7.06 m².")]
    [Min(0f)] public float sailArea = 7.06f;
    [Min(0f)] public float airDensity = 1.225f;
    [Tooltip("Normal force coefficient of the sail when side-on to the wind (flat plate ≈ 1.2).")]
    [Min(0f)] public float maxNormalCoefficient = 1.2f;

    [Header("Coupling")]
    [Tooltip("Push the boat back when the boom's swing speeds up or slows down (Newton's third law). " +
             "Only used when simple wind is off, i.e. once the sail model applies the sail force to the hull.")]
    public bool applyReactionTorque = true;

    /// <summary>Torque about the mast from the sail model, N·m, positive = increases θ.</summary>
    [System.NonSerialized] public float aeroTorque;

    [Header("State (read-only)")]
    [SerializeField] float angleDeg;
    [SerializeField] float angularSpeedDeg;
    [SerializeField] float inertiaAboutMast;
    [SerializeField] float windTorque;

    public float Angle => _theta;                 // rad
    /// <summary>Boom rotation about the mast axis in degrees, as applied to the pivot.</summary>
    public float SignedAngleDeg => _sign * _theta * Mathf.Rad2Deg;
    public float AngularVelocity => _omega;       // rad/s
    public float InertiaAboutMast => _iAxis;      // kg·m²
    public Transform Pivot => _pivot;
    public Vector3 SailCentreWorld => _pivot != null ? _pivot.TransformPoint(_sailCom) : Vector3.zero;

    Transform _pivot;
    Quaternion _baseLocalRot;
    float _sign = 1f;
    float _theta, _omega;
    float _iAxis;

    // geometry in pivot space (pivot up = mast axis, forward = boom direction at θ = 0)
    Vector3 _boomCom, _sailCom;
    float _boomLength, _sailHeight, _sailFoot;

    Vector3 _prevV, _prevW;
    bool _hasPrev, _ready;

    void Awake()
    {
        FindParts();
        if (boat == null || boom == null)
        {
            Debug.LogError("BoomRig: needs the boat Rigidbody and the Boom transform.", this);
            enabled = false;
            return;
        }
        BuildPivot();
    }

    void FindParts()
    {
        if (boat == null) boat = GetComponentInParent<Rigidbody>();
        if (boat == null) return;
        Transform root = boat.transform;
        if (boom == null) boom = FindDeep(root, "Boom");
        if (sail == null) sail = FindDeep(root, "Plane");
        if (mast == null) mast = FindDeep(root, "Mast");
    }

    static Transform FindDeep(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    static bool MeshAxis(Transform t, out Vector3 centreWorld, out Vector3 axisWorld, out float lengthWorld)
    {
        centreWorld = axisWorld = Vector3.zero; lengthWorld = 0f;
        var mf = t != null ? t.GetComponent<MeshFilter>() : null;
        if (mf == null || mf.sharedMesh == null) return false;
        Bounds b = mf.sharedMesh.bounds;
        Vector3 s = b.size;
        Vector3 local = s.x >= s.y && s.x >= s.z ? Vector3.right : (s.y >= s.z ? Vector3.up : Vector3.forward);
        float half = Vector3.Dot(b.extents, local);
        Vector3 a = t.TransformPoint(b.center + local * half);
        Vector3 c = t.TransformPoint(b.center - local * half);
        centreWorld = (a + c) * 0.5f;
        axisWorld = (a - c).normalized;
        lengthWorld = (a - c).magnitude;
        return true;
    }

    void BuildPivot()
    {
        Transform root = boat.transform;

        // mast axis
        Vector3 mastPoint = root.position, axis = root.up;
        if (MeshAxis(mast, out Vector3 mc, out Vector3 ma, out _)) { mastPoint = mc; axis = ma; }
        if (Vector3.Dot(axis, root.up) < 0f) axis = -axis;

        // boom ends
        if (!MeshAxis(boom, out Vector3 bc, out Vector3 bAxis, out _boomLength))
        {
            bc = boom.position; bAxis = -root.forward; _boomLength = 2.7f;
        }
        Vector3 endA = bc + bAxis * (_boomLength * 0.5f), endB = bc - bAxis * (_boomLength * 0.5f);
        float dA = Vector3.Cross(endA - mastPoint, axis).sqrMagnitude;
        float dB = Vector3.Cross(endB - mastPoint, axis).sqrMagnitude;
        Vector3 near = dA < dB ? endA : endB, far = dA < dB ? endB : endA;

        Vector3 pivotPos = gooseneck != null ? gooseneck.position : near;

        // The sail turns around its luff: the line from the gooseneck to the head of the sail.
        // (The ILCA mast is raked and bent, so a straight line through the mast mesh would miss
        // the masthead and the head of the sail would swing away from it.)
        if (SailHead(root.up, out Vector3 head) && (head - pivotPos).sqrMagnitude > 0.25f)
            axis = (head - pivotPos).normalized;
        if (Vector3.Dot(axis, root.up) < 0f) axis = -axis;

        Vector3 boomDir = Vector3.ProjectOnPlane(far - pivotPos, axis).normalized;
        if (boomDir.sqrMagnitude < 1e-6f) boomDir = Vector3.ProjectOnPlane(-root.forward, axis).normalized;

        _pivot = new GameObject("BoomPivot").transform;
        _pivot.SetParent(root, false);
        _pivot.SetPositionAndRotation(pivotPos, Quaternion.LookRotation(boomDir, axis));
        _baseLocalRot = _pivot.localRotation;

        boom.SetParent(_pivot, true);
        // A Cloth sail (SkinnedMeshRenderer) is skinned to the boom like a cape to a character,
        // so its luff stays on the mast. A plain mesh sail just turns with the boom.
        if (sail != null)
        {
            bool skinned = false;
            if (sail.GetComponent<SkinnedMeshRenderer>() != null)
            {
                var skin = sail.GetComponent<SailClothSkinning>();
                if (skin == null) skin = sail.gameObject.AddComponent<SailClothSkinning>();
                skinned = skin.Setup(root, _pivot);
            }
            if (!skinned) sail.SetParent(_pivot, true);
        }

        // positive θ = boom tip towards the boat's +X
        Vector3 turned = Quaternion.AngleAxis(1f, axis) * boomDir;
        _sign = Vector3.Dot(turned - boomDir, root.right) >= 0f ? 1f : -1f;

        // geometry in pivot space
        _boomCom = _pivot.InverseTransformPoint(bc);
        SailGeometry();

        _iAxis = AxisInertia();
        inertiaAboutMast = _iAxis;
        _ready = true;
    }

    // The sail's mesh from a MeshFilter, or from a SkinnedMeshRenderer (needed by the Cloth component).
    static Mesh SharedMesh(Transform t)
    {
        if (t == null) return null;
        var mf = t.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null) return mf.sharedMesh;
        var smr = t.GetComponent<SkinnedMeshRenderer>();
        return smr != null ? smr.sharedMesh : null;
    }

    // Highest point of the sail mesh (the head), in world space.
    bool SailHead(Vector3 up, out Vector3 head)
    {
        head = Vector3.zero;
        var mesh = SharedMesh(sail);
        if (mesh == null || !(mesh.isReadable || Application.isEditor)) return false;
        float best = float.MinValue;
        foreach (var v in mesh.vertices)
        {
            Vector3 w = sail.TransformPoint(v);
            float h = Vector3.Dot(w, up);
            if (h > best) { best = h; head = w; }
        }
        return best > float.MinValue;
    }

    void SailGeometry()
    {
        _sailCom = _boomCom + Vector3.up * 2.5f;
        _sailHeight = 5f; _sailFoot = _boomLength;
        if (sail == null) return;

        var mesh = SharedMesh(sail);
        if (mesh == null) return;

        if (mesh.isReadable || Application.isEditor)
        {
            // area-weighted centroid of the sail triangles, and its extent along / away from the mast
            var v = mesh.vertices;
            var tri = mesh.triangles;
            Vector3 sum = Vector3.zero;
            float area = 0f, minUp = float.MaxValue, maxUp = float.MinValue, maxOut = 0f;
            for (int i = 0; i < tri.Length; i += 3)
            {
                Vector3 a = _pivot.InverseTransformPoint(sail.TransformPoint(v[tri[i]]));
                Vector3 b = _pivot.InverseTransformPoint(sail.TransformPoint(v[tri[i + 1]]));
                Vector3 c = _pivot.InverseTransformPoint(sail.TransformPoint(v[tri[i + 2]]));
                float ar = 0.5f * Vector3.Cross(b - a, c - a).magnitude;
                sum += (a + b + c) / 3f * ar;
                area += ar;
            }
            foreach (var p in v)
            {
                Vector3 q = _pivot.InverseTransformPoint(sail.TransformPoint(p));
                minUp = Mathf.Min(minUp, q.y); maxUp = Mathf.Max(maxUp, q.y);
                maxOut = Mathf.Max(maxOut, new Vector2(q.x, q.z).magnitude);
            }
            if (area > 1e-4f)
            {
                _sailCom = sum / area;
                _sailHeight = maxUp - minUp;
                _sailFoot = maxOut;
            }
        }
        else
        {
            var b = mesh.bounds;
            _sailCom = _pivot.InverseTransformPoint(sail.TransformPoint(b.center));
        }
    }

    float AxisInertia()
    {
        // boom: rod perpendicular to the axis; sail: triangle with its luff on the axis (E[r²] = b²/6)
        float dBoom = new Vector2(_boomCom.x, _boomCom.z).magnitude;
        float iBoom = boomMass * (_boomLength * _boomLength / 12f + dBoom * dBoom);
        float iSail = sailMass * _sailFoot * _sailFoot / 6f;
        return Mathf.Max(1e-3f, iBoom + iSail);
    }

    void OnValidate()
    {
        if (_ready) { _iAxis = AxisInertia(); inertiaAboutMast = _iAxis; }
    }

    void FixedUpdate()
    {
        if (!_ready) return;
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;

        Vector3 axis = _pivot.up;
        Vector3 p = _pivot.position;

        // boat motion (accelerations from the change in velocity since the last step)
        Vector3 v = boat.linearVelocity, w = boat.angularVelocity;
        Vector3 acc = Vector3.zero, alpha = Vector3.zero;
        if (_hasPrev && !boat.isKinematic) { acc = (v - _prevV) / dt; alpha = (w - _prevW) / dt; }
        _prevV = v; _prevW = w; _hasPrev = true;
        if (boat.isKinematic) w = Vector3.zero;

        Vector3 com = boat.worldCenterOfMass;
        Vector3 boomW = axis * (_sign * _omega);

        float tau = InertialTorque(_pivot.TransformPoint(_boomCom), boomMass, p, axis, com, acc, alpha, w, boomW)
                  + InertialTorque(_pivot.TransformPoint(_sailCom), sailMass, p, axis, com, acc, alpha, w, boomW);

        windTorque = simpleWindTorque ? SimpleWindTorque(p, axis) : 0f;
        tau += windTorque;

        float tauTheta = _sign * tau + aeroTorque
                       - pivotFriction * _omega - airDamping * _omega * Mathf.Abs(_omega);

        float omegaOld = _omega;
        _omega += tauTheta / _iAxis * dt;
        _theta += _omega * dt;

        float limit = sheetMaxAngle * Mathf.Deg2Rad;
        if (_theta > limit) { _theta = limit; if (_omega > 0f) _omega = -_omega * stopRestitution; }
        else if (_theta < -limit) { _theta = -limit; if (_omega < 0f) _omega = -_omega * stopRestitution; }

        _pivot.localRotation = _baseLocalRot * Quaternion.AngleAxis(_sign * _theta * Mathf.Rad2Deg, Vector3.up);

        // Skipped while the placeholder wind is on: its force is not applied to the hull, so
        // passing only the sheet's share of it to the hull would leave a lopsided torque.
        if (applyReactionTorque && !simpleWindTorque && !boat.isKinematic)
        {
            // The boom's angular momentum about the mast changed by I Δω. Whatever caused that
            // inside the boat (sheet, gooseneck, inertial effects) pushes the boat the other way.
            // External torques (wind on the sail) are not reactions, so they are taken out again.
            float internalTau = _iAxis * (_omega - omegaOld) / dt - _sign * windTorque - aeroTorque;
            boat.AddTorque(-axis * (_sign * internalTau), ForceMode.Force);
        }

        angleDeg = _theta * Mathf.Rad2Deg;
        angularSpeedDeg = _omega * Mathf.Rad2Deg;
    }

    // Torque about the mast axis on a point mass, seen from the moving boat:
    // gravity minus the frame's acceleration at that point, minus the Coriolis force.
    static float InertialTorque(Vector3 x, float m, Vector3 pivot, Vector3 axis, Vector3 com,
                                Vector3 acc, Vector3 alpha, Vector3 w, Vector3 boomW)
    {
        if (m <= 0f) return 0f;
        Vector3 r = x - com;
        Vector3 aFrame = acc + Vector3.Cross(alpha, r) + Vector3.Cross(w, Vector3.Cross(w, r));
        Vector3 vRel = Vector3.Cross(boomW, x - pivot);
        Vector3 f = m * (Physics.gravity - aFrame - 2f * Vector3.Cross(w, vRel));
        return Vector3.Dot(Vector3.Cross(x - pivot, f), axis);
    }

    // Flat-plate normal force on the sail from the apparent wind, as torque about the mast.
    float SimpleWindTorque(Vector3 pivot, Vector3 axis)
    {
        var weather = WeatherController.Instance;
        if (weather == null || sailArea <= 0f) return 0f;

        Vector3 ce = _pivot.TransformPoint(_sailCom);
        Vector3 app = weather.WindVector - boat.GetPointVelocity(ce);
        app = Vector3.ProjectOnPlane(app, axis);
        float speed = app.magnitude;
        if (speed < 0.05f) return 0f;

        Vector3 chord = Vector3.ProjectOnPlane(_pivot.forward, axis).normalized;
        Vector3 normal = Vector3.Cross(axis, chord).normalized;
        float sinAttack = Vector3.Dot(app / speed, normal);       // signed
        float cn = maxNormalCoefficient * sinAttack;
        Vector3 force = normal * (0.5f * airDensity * sailArea * speed * speed * cn);
        return Vector3.Dot(Vector3.Cross(ce - pivot, force), axis);
    }

    public override void Accumulate(ref MassAccumulator acc, Vector3 framePos, Quaternion frameRot)
    {
        if (!_ready) return;
        Vector3 axisF = DirToFrame(_pivot.up, frameRot);
        Vector3 chordF = DirToFrame(_pivot.forward, frameRot);

        acc.Add(boomMass, ToFrame(_pivot.TransformPoint(_boomCom), framePos, frameRot),
                MassAccumulator.RodSpread(chordF, _boomLength, 0.03f));
        acc.Add(sailMass, ToFrame(_pivot.TransformPoint(_sailCom), framePos, frameRot),
                MassAccumulator.TriangleSpread(axisF, _sailHeight, chordF, _sailFoot));
    }

    void OnDrawGizmosSelected()
    {
        if (!_ready) return;
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(_pivot.position, _pivot.position + _pivot.up * 2f);
        Gizmos.DrawWireSphere(_pivot.TransformPoint(_boomCom), 0.05f);
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(_pivot.TransformPoint(_sailCom), 0.08f);
    }
}
