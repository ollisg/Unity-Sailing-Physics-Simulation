using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The sailor's mass as body segments that follow the animated Rigify DEF bones, so the centre
/// of mass and inertia change with the pose (SitClose ... SitLean, Duck).
///
/// Segment mass fractions and centre-of-mass positions are the standard anthropometric values
/// from Winter (2009), "Biomechanics and Motor Control of Human Movement", table 4.1
/// (originally Dempster 1955). Each segment is modelled as a solid cylinder between its two
/// joints. Put this on the sailor root (Character); the sailor must be a child of the boat.
/// </summary>
[DisallowMultipleComponent]
public class SailorMassModel : MassSource
{
    [Serializable]
    public class Segment
    {
        public string name;
        [Tooltip("Bone at the proximal joint (segment start).")]
        public string proximalBone;
        [Tooltip("Bone at the distal joint (segment end). If missing, the segment continues the " +
                 "direction fallbackFrom -> proximal for fallbackLength.")]
        public string distalBone;
        public string fallbackFrom;
        public float fallbackLength = 0.2f;
        [Tooltip("Fraction of total body mass.")]
        public float massFraction;
        [Tooltip("Centre of mass as a fraction of the segment length from the proximal joint.")]
        public float comFromProximal = 0.5f;
        [Tooltip("Cylinder radius used for the segment's own inertia (m).")]
        public float radius = 0.05f;

        [NonSerialized] public Transform prox, dist, from;

        public Segment() { }

        public Segment(string name, string prox, string dist, float fraction, float com, float radius,
                       string fallbackFrom = null, float fallbackLength = 0.2f)
        {
            this.name = name; proximalBone = prox; distalBone = dist; massFraction = fraction;
            comFromProximal = com; this.radius = radius;
            this.fallbackFrom = fallbackFrom; this.fallbackLength = fallbackLength;
        }
    }

    [Tooltip("Sailor body mass in kg (ILCA 7 sailors are typically around 80 kg).")]
    [Min(1f)] public float bodyMass = 80f;

    [Tooltip("Set the Animator to Animate Physics and Always Animate, so the bones are up to " +
             "date in the physics step and keep moving when the sailor is off screen.")]
    public bool fixAnimatorSettings = true;

    public List<Segment> segments = new List<Segment>();

    [Header("Debug (read-only)")]
    [SerializeField] Vector3 worldCenterOfMass;
    [SerializeField] float massFractionTotal;

    public Vector3 WorldCenterOfMass => worldCenterOfMass;

    bool _resolved;

    void Reset() { segments = DefaultSegments(); }

    public static List<Segment> DefaultSegments()
    {
        var s = new List<Segment>
        {
            new Segment("Head+Neck", "DEF-spine.006", "DEF-spine.006_end", 0.081f, 0.50f, 0.09f, "DEF-spine.004", 0.24f),
            new Segment("Trunk",     "DEF-spine",     "DEF-spine.004",     0.497f, 0.50f, 0.14f),
        };
        foreach (var side in new[] { "L", "R" })
        {
            s.Add(new Segment("UpperArm." + side, "DEF-upper_arm." + side, "DEF-forearm." + side, 0.028f, 0.436f, 0.045f));
            s.Add(new Segment("Forearm." + side,  "DEF-forearm." + side,   "DEF-hand." + side,    0.016f, 0.430f, 0.040f));
            s.Add(new Segment("Hand." + side,     "DEF-hand." + side,      "DEF-hand." + side + "_end", 0.006f, 0.506f, 0.040f,
                              "DEF-forearm." + side, 0.18f));
            s.Add(new Segment("Thigh." + side,    "DEF-thigh." + side,     "DEF-shin." + side,    0.100f, 0.433f, 0.075f));
            s.Add(new Segment("Shank." + side,    "DEF-shin." + side,      "DEF-foot." + side,    0.0465f, 0.433f, 0.050f));
            s.Add(new Segment("Foot." + side,     "DEF-foot." + side,      "DEF-toe." + side,     0.0145f, 0.50f, 0.040f));
        }
        return s;
    }

    void Awake()
    {
        if (segments == null || segments.Count == 0) segments = DefaultSegments();
        Resolve();

        if (fixAnimatorSettings)
        {
            var anim = GetComponentInChildren<Animator>();
            if (anim != null)
            {
                anim.updateMode = AnimatorUpdateMode.Fixed;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
        }
    }

    void Resolve()
    {
        var lookup = new Dictionary<string, Transform>();
        foreach (var t in GetComponentsInChildren<Transform>(true))
            if (!lookup.ContainsKey(t.name)) lookup[t.name] = t;

        var missing = new List<string>();
        massFractionTotal = 0f;
        foreach (var s in segments)
        {
            lookup.TryGetValue(s.proximalBone ?? "", out s.prox);
            lookup.TryGetValue(s.distalBone ?? "", out s.dist);
            lookup.TryGetValue(s.fallbackFrom ?? "", out s.from);
            if (s.prox == null || (s.dist == null && s.from == null)) missing.Add(s.name);
            massFractionTotal += s.massFraction;
        }
        if (missing.Count > 0)
            Debug.LogWarning($"SailorMassModel: bones not found for {string.Join(", ", missing)}. " +
                             "Their mass is put at the trunk instead.", this);
        if (Mathf.Abs(massFractionTotal - 1f) > 0.01f)
            Debug.LogWarning($"SailorMassModel: segment mass fractions add up to {massFractionTotal:0.000}, not 1.", this);
        _resolved = true;
    }

    bool SegmentGeometry(Segment s, out Vector3 com, out Vector3 dir, out float length)
    {
        com = dir = Vector3.zero; length = 0f;
        if (s.prox == null) return false;
        Vector3 p = s.prox.position, d;
        if (s.dist != null) d = s.dist.position;
        else if (s.from != null)
        {
            Vector3 along = p - s.from.position;
            if (along.sqrMagnitude < 1e-8f) return false;
            d = p + along.normalized * s.fallbackLength;
        }
        else return false;

        Vector3 seg = d - p;
        length = seg.magnitude;
        if (length < 1e-4f) return false;
        dir = seg / length;
        com = p + seg * s.comFromProximal;
        return true;
    }

    public override void Accumulate(ref MassAccumulator acc, Vector3 framePos, Quaternion frameRot)
    {
        if (!_resolved) Resolve();

        var local = new MassAccumulator();
        float orphanMass = 0f;
        Vector3 trunkCom = transform.position;
        bool trunkFound = false;
        Vector3 worldSum = Vector3.zero;
        float worldMass = 0f;

        foreach (var s in segments)
        {
            float m = bodyMass * s.massFraction;
            if (!SegmentGeometry(s, out Vector3 com, out Vector3 dir, out float len))
            {
                orphanMass += m;
                continue;
            }
            if (!trunkFound && s.name == "Trunk") { trunkCom = com; trunkFound = true; }

            local.Add(m, ToFrame(com, framePos, frameRot),
                      MassAccumulator.RodSpread(DirToFrame(dir, frameRot), len, s.radius));
            worldSum += com * m; worldMass += m;
        }
        if (orphanMass > 0f)
        {
            local.Add(orphanMass, ToFrame(trunkCom, framePos, frameRot), Sym3.Zero);
            worldSum += trunkCom * orphanMass; worldMass += orphanMass;
        }

        worldCenterOfMass = worldMass > 0f ? worldSum / worldMass : transform.position;
        acc.Add(local);
    }

    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying || !_resolved) return;
        Gizmos.color = new Color(1f, 0.6f, 0.1f);
        foreach (var s in segments)
            if (SegmentGeometry(s, out Vector3 com, out _, out _))
                Gizmos.DrawWireSphere(com, 0.02f + 0.1f * Mathf.Sqrt(s.massFraction));
        Gizmos.color = Color.red;
        Gizmos.DrawSphere(worldCenterOfMass, 0.05f);
    }
}
