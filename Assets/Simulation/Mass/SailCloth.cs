using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Custom cloth simulation for the sail (position-based dynamics), replacing Unity's Cloth.
///
/// The physics runs on the sail mesh as modelled (its own vertices); the drawn mesh is subdivided
/// from it and smoothed (Phong), so the cloth is cheap to simulate but still looks curved.
/// At start the flat sail mesh is prepared:
///   1. welded into cloth points (vertices split at UV seams become one point)
///   2. flattened onto its best-fit plane
///   3. relaxed: inner vertices are evened out within the plane; their UVs are re-sampled from
///      the original mesh at the new position, so the texture stays where it was
///   4. its outline is split at the head (highest), clew (furthest along the boom) and tack
///      (lowest-forward) into luff, foot and leech
///   5. a camber (draft) is cut into the rest shape, like a real sail's broadseaming, so wind fills
///      it into a designed shape instead of stretching flat cloth
/// Every physics step:
///   - luff vertices turn with the boom around the mast centre at their own height
///   - foot vertices follow the boom
///   - wind pressure acts on every triangle along its normal (apparent wind, flat-plate normal
///     force), plus gravity
///   - distance constraints keep the cloth's lengths (stretch) and smoothness (bending)
///
/// BoomRig adds and initialises this automatically for a sail with a MeshFilter (and no Cloth).
/// The total aerodynamic force and its centre are exposed for the sail model / thesis.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(MeshFilter))]
[DefaultExecutionOrder(-150)] // after BoomRig (-200) has moved the boom this step
public class SailCloth : MonoBehaviour
{
    [Header("Mesh preparation (at start)")]
    [Tooltip("Drawn mesh only: each level splits every triangle into four. The physics always runs on the modelled mesh.")]
    [Range(0, 4)] public int subdivisions = 2;
    [Tooltip("How much the drawn mesh is rounded between cloth points (Phong smoothing). 0 = flat facets.")]
    [Range(0f, 1f)] public float smoothing = 0.75f;
    [Tooltip("Project the sail onto its best-fit plane first (removes modelling wobble).")]
    public bool flatten = true;
    [Tooltip("Smoothing passes that even out the spacing of inner vertices. 0 = off.")]
    [Range(0, 50)] public int relaxIterations = 10;

    [Header("Designed shape (camber)")]
    [Tooltip("Depth of the sail's belly as a fraction of the chord (luff to leech). Dinghy mainsails ~0.08-0.12.")]
    [Range(0f, 0.25f)] public float draftDepth = 0.10f;
    [Tooltip("Where the deepest point is, as a fraction of the chord from the luff.")]
    [Range(0.2f, 0.7f)] public float draftPosition = 0.45f;

    [Header("Cloth")]
    [Tooltip("Small steps per physics step. Many substeps with one iteration each gives the stiffest, cheapest cloth.")]
    [Range(1, 32)] public int substeps = 8;
    [Tooltip("Constraint passes per substep.")]
    [Range(1, 30)] public int iterations = 1;
    [Tooltip("How strongly the cloth keeps its lengths (1 = practically unstretchable sailcloth).")]
    [Range(0f, 1f)] public float stretchStiffness = 1f;
    [Tooltip("Resistance to sharp folds (sailcloth is fairly stiff).")]
    [Range(0f, 1f)] public float bendStiffness = 0.3f;
    [Tooltip("Velocity damping per second (air resistance of the cloth itself).")]
    [Min(0f)] public float damping = 1f;
    public bool useGravity = true;

    [Tooltip("Draw the cloth between the last two physics steps, so it moves smoothly at any frame rate.")]
    public bool interpolate = true;

    [Header("Wind")]
    [Tooltip("Normal force coefficient when the wind hits the cloth side-on (flat plate ~1.2).")]
    [Min(0f)] public float maxNormalCoefficient = 1.2f;
    [Min(0f)] public float airDensity = 1.225f;
    [Tooltip("Scales the wind pressure on the cloth (1 = physical).")]
    [Min(0f)] public float pressureScale = 1f;
    [Tooltip("Multiplies the wind speed the cloth feels (visual only: the boom uses its own wind " +
             "torque). Light winds pass almost unchanged; Max Cloth Wind tames the strong ones.")]
    [Min(0f)] public float clothWindGain = 1f;
    [Tooltip("Soft ceiling (m/s) for the wind the cloth feels, after Wind Scale. It eases in " +
             "smoothly, so stronger wind still fills the sail a bit more but never past this.")]
    [Min(0.1f)] public float maxClothWind = 8f;

    [Header("Read-only")]
    [SerializeField] int particleCount;
    [SerializeField] int luffCount, footCount, leechCount;
    [SerializeField] Vector3 aeroForce;
    [SerializeField] Vector3 centreOfEffort;

    /// <summary>Total wind force on the sail (world, N) and where it acts, from the last step.</summary>
    public Vector3 AeroForce => aeroForce;
    public Vector3 CentreOfEffort => centreOfEffort;
    public bool Ready => _ready;

    // ---- setup ----
    BoomRig _rig;
    Transform _root, _pivot;
    Mesh _mesh;
    bool _ready;

    // drawn (fine) mesh: every vertex is a point inside one cloth triangle
    int[] _fA, _fB, _fC;            // the cloth points of that triangle
    float[] _fU, _fV, _fW;          // barycentric weights
    Vector3[] _fPos, _fNrm;
    Vector3[] _pNrm;                // per cloth point normal (world)
    int[] _rvParticle;              // modelled render vertex -> cloth point (used while preparing)

    // particles (world space)
    Vector3[] _x, _xPrev, _force;
    Vector3[] _good; bool _hasGood; // last healthy shape, in pivot space, to recover from a blow-up
    Vector3[] _xStepPrev, _xDraw;   // positions at the previous physics step / blended for drawing
    float[] _invMass;
    int[] _tri;                     // particle triangles
    // constraints
    int[] _cA, _cB; float[] _cRest; float[] _cK;

    // pins
    int[] _footIdx; Vector3[] _footLocal;              // in pivot space
    int[] _luffIdx; Vector3[] _luffCentre, _luffOffset; // root space
    Vector3 _axisRoot;
    Vector3[] _pinPrev, _pinNow;    // per particle, pin targets (world) at the start / end of the step
    bool[] _pinned;

    // ===================================================================================
    // Initialisation (called by BoomRig with the boom at angle 0)
    // ===================================================================================

    /// <param name="mastTurns">True when the mast turns with the boom: then the whole rig is one
    /// rigid unit and the luff is simply pinned to it. Otherwise each luff point turns around the
    /// (static) mast centre at its own height.</param>
    public bool Init(BoomRig rig, Transform root, Transform pivot, Transform mast, float mass, bool mastTurns = false)
    {
        _ready = false;
        _rig = rig; _root = root; _pivot = pivot;
        var mf = GetComponent<MeshFilter>();
        Mesh src = mf.sharedMesh;
        if (src == null || !(src.isReadable || Application.isEditor))
        {
            Debug.LogWarning("SailCloth: the sail mesh can't be read (enable Read/Write in the model import settings).", this);
            return false;
        }

        // ---------- modelled mesh in boat-root space ----------
        Matrix4x4 sailToRoot = root.worldToLocalMatrix * transform.localToWorldMatrix;
        var rv = new List<Vector3>(); var ruv = new List<Vector2>(); var rt = new List<int>(src.triangles);
        var sv = src.vertices; var suv = src.uv;
        for (int i = 0; i < sv.Length; i++)
        {
            rv.Add(sailToRoot.MultiplyPoint3x4(sv[i]));
            ruv.Add(suv.Length == sv.Length ? suv[i] : Vector2.zero);
        }

        // ---------- weld render vertices into particles ----------
        var map = new Dictionary<Vector3Int, int>();
        _rvParticle = new int[rv.Count];
        var P = new List<Vector3>();
        var pVerts = new List<List<int>>();
        for (int i = 0; i < rv.Count; i++)
        {
            var key = new Vector3Int(Mathf.RoundToInt(rv[i].x * 1000f), Mathf.RoundToInt(rv[i].y * 1000f), Mathf.RoundToInt(rv[i].z * 1000f));
            if (!map.TryGetValue(key, out int p)) { p = P.Count; map[key] = p; P.Add(rv[i]); pVerts.Add(new List<int>()); }
            _rvParticle[i] = p;
            pVerts[p].Add(i);
        }
        int n = P.Count;

        // particle triangles (drop degenerate ones)
        var tris = new List<int>();
        for (int t = 0; t < rt.Count; t += 3)
        {
            int a = _rvParticle[rt[t]], b = _rvParticle[rt[t + 1]], c = _rvParticle[rt[t + 2]];
            if (a == b || b == c || a == c) continue;
            tris.Add(a); tris.Add(b); tris.Add(c);
        }
        _tri = tris.ToArray();

        // ---------- plane fit ----------
        Vector3 centroid = Vector3.zero;
        foreach (var p in P) centroid += p;
        centroid /= n;
        Sym3 cov = Sym3.Zero;
        foreach (var p in P) cov += Sym3.Outer(p - centroid);
        cov.Eigen(out Vector3 ev, out Quaternion axes);
        Vector3[] evec = { axes * Vector3.right, axes * Vector3.up, axes * Vector3.forward };
        float[] evals = { ev.x, ev.y, ev.z };
        int iMin = 0; for (int k = 1; k < 3; k++) if (evals[k] < evals[iMin]) iMin = k;
        Vector3 normal = evec[iMin].normalized;
        Vector3 e1 = evec[(iMin + 1) % 3].normalized, e2 = Vector3.Cross(normal, e1).normalized;
        if (flatten)
            for (int i = 0; i < n; i++) P[i] -= normal * Vector3.Dot(P[i] - centroid, normal);

        // ---------- edges, boundary, bending pairs ----------
        var edgeTris = new Dictionary<long, List<int>>(); // edge -> opposite particles
        for (int t = 0; t < _tri.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = _tri[t + k], b = _tri[t + (k + 1) % 3], o = _tri[t + (k + 2) % 3];
                long key = EdgeKey(a, b);
                if (!edgeTris.TryGetValue(key, out var list)) edgeTris[key] = list = new List<int>();
                list.Add(o);
            }

        var nbr = new List<int>[n];
        for (int i = 0; i < n; i++) nbr[i] = new List<int>();
        var boundaryAdj = new Dictionary<int, List<int>>();
        foreach (var kv in edgeTris)
        {
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            nbr[a].Add(b); nbr[b].Add(a);
            if (kv.Value.Count == 1)
            {
                if (!boundaryAdj.TryGetValue(a, out var la)) boundaryAdj[a] = la = new List<int>();
                if (!boundaryAdj.TryGetValue(b, out var lb)) boundaryAdj[b] = lb = new List<int>();
                la.Add(b); lb.Add(a);
            }
        }
        var isBoundary = new bool[n];
        foreach (var k in boundaryAdj.Keys) isBoundary[k] = true;

        // ---------- relax inner vertices within the plane, re-sampling their UVs ----------
        if (relaxIterations > 0) Relax(P, rv, ruv, rt, pVerts, nbr, isBoundary, centroid, e1, e2);

        // ---------- outline: tack, head, clew; luff, foot, leech ----------
        var q = new Vector3[n]; // pivot space: y along the mast axis, z along the boom
        for (int i = 0; i < n; i++) q[i] = pivot.InverseTransformPoint(root.TransformPoint(P[i]));
        int head = -1, clew = -1, tack = -1;
        foreach (int b in boundaryAdj.Keys)
        {
            if (head < 0 || q[b].y > q[head].y) head = b;
            if (clew < 0 || q[b].z > q[clew].z) clew = b;
            if (tack < 0 || q[b].y + q[b].z < q[tack].y + q[tack].z) tack = b;
        }
        var loop = BoundaryLoop(boundaryAdj, tack);
        if (loop == null || head == tack || clew == tack || head == clew)
        {
            Debug.LogWarning("SailCloth: couldn't read the sail's outline (it must be a single sheet with one edge loop).", this);
            return false;
        }
        int ih = loop.IndexOf(head), ic = loop.IndexOf(clew);
        List<int> luff, foot, leech;
        if (ic < ih) { foot = loop.GetRange(0, ic + 1); leech = loop.GetRange(ic, ih - ic + 1); luff = loop.GetRange(ih, loop.Count - ih); luff.Add(tack); }
        else { luff = loop.GetRange(0, ih + 1); leech = loop.GetRange(ih, ic - ih + 1); foot = loop.GetRange(ic, loop.Count - ic); foot.Add(tack); }
        luffCount = luff.Count; footCount = foot.Count; leechCount = leech.Count;

        // ---------- rest shape with camber ----------
        var luffLine = Polyline(luff, q); var leechLine = Polyline(leech, q);
        Vector3 sailNormal = normal;
        var rest = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float zl = Interp(luffLine, q[i].y), ze = Interp(leechLine, q[i].y);
            float chord = ze - zl;
            float depth = 0f;
            if (chord > 0.05f)
            {
                float xf = Mathf.Clamp01((q[i].z - zl) / chord);
                depth = draftDepth * chord * Profile(xf, draftPosition);
            }
            rest[i] = P[i] + sailNormal * depth;
        }

        // ---------- constraints ----------
        var cA = new List<int>(); var cB = new List<int>(); var cR = new List<float>(); var cK = new List<float>();
        foreach (var kv in edgeTris)
        {
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            cA.Add(a); cB.Add(b); cR.Add(Vector3.Distance(rest[a], rest[b])); cK.Add(-1f); // -1 = stretch
            if (kv.Value.Count == 2)
            {
                int o1 = kv.Value[0], o2 = kv.Value[1];
                cA.Add(o1); cB.Add(o2); cR.Add(Vector3.Distance(rest[o1], rest[o2])); cK.Add(-2f); // -2 = bend
            }
        }
        _cA = cA.ToArray(); _cB = cB.ToArray(); _cRest = cR.ToArray(); _cK = cK.ToArray();

        // ---------- masses ----------
        var area = new float[n]; float totalArea = 0f;
        for (int t = 0; t < _tri.Length; t += 3)
        {
            float ar = 0.5f * Vector3.Cross(P[_tri[t + 1]] - P[_tri[t]], P[_tri[t + 2]] - P[_tri[t]]).magnitude;
            totalArea += ar;
            area[_tri[t]] += ar / 3f; area[_tri[t + 1]] += ar / 3f; area[_tri[t + 2]] += ar / 3f;
        }

        // ---------- pins ----------
        _pinned = new bool[n];
        foreach (int i in luff) _pinned[i] = true;
        foreach (int i in foot) _pinned[i] = true;

        var footSet = new HashSet<int>(foot);
        if (mastTurns) foreach (int i in luff) if (footSet.Add(i)) foot.Add(i); // luff rides the turning mast too
        _footIdx = foot.ToArray();
        _footLocal = new Vector3[_footIdx.Length];
        for (int k = 0; k < _footIdx.Length; k++) _footLocal[k] = q[_footIdx[k]];

        var luffOnly = new List<int>();
        foreach (int i in luff) if (!footSet.Contains(i)) luffOnly.Add(i);
        _luffIdx = luffOnly.ToArray();
        _luffCentre = new Vector3[_luffIdx.Length];
        _luffOffset = new Vector3[_luffIdx.Length];
        MastCentres(mast, root, P, _luffIdx, _luffCentre);
        for (int k = 0; k < _luffIdx.Length; k++) _luffOffset[k] = P[_luffIdx[k]] - _luffCentre[k];
        _axisRoot = root.InverseTransformDirection(pivot.up).normalized;

        // ---------- state ----------
        _x = new Vector3[n]; _xPrev = new Vector3[n]; _force = new Vector3[n]; _good = new Vector3[n]; _hasGood = false;
        _invMass = new float[n]; _pinPrev = new Vector3[n]; _pinNow = new Vector3[n];
        _xStepPrev = new Vector3[n]; _xDraw = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            _x[i] = _xPrev[i] = _xStepPrev[i] = root.TransformPoint(P[i]);
            float m = Mathf.Max(1e-4f, mass * area[i] / Mathf.Max(totalArea, 1e-6f));
            _invMass[i] = _pinned[i] ? 0f : 1f / m;
            _pinPrev[i] = _x[i];
        }
        particleCount = n;

        // ---------- drawn mesh: each modelled triangle split into an N x N grid ----------
        int N = 1 << Mathf.Clamp(subdivisions, 0, 4);
        var fA = new List<int>(); var fB = new List<int>(); var fC = new List<int>();
        var fU = new List<float>(); var fV = new List<float>(); var fW = new List<float>();
        var fuv = new List<Vector2>(); var ftri = new List<int>();
        for (int t = 0; t < rt.Count; t += 3)
        {
            int ra = rt[t], rb = rt[t + 1], rc = rt[t + 2];
            int pa = _rvParticle[ra], pb = _rvParticle[rb], pc = _rvParticle[rc];
            if (pa == pb || pb == pc || pa == pc) continue;
            int baseIdx = fA.Count;
            // vertex (i, j): weight i/N on b, j/N on c, the rest on a
            for (int j = 0; j <= N; j++)
                for (int i = 0; i <= N - j; i++)
                {
                    float v = (float)i / N, w = (float)j / N, u = 1f - v - w;
                    fA.Add(pa); fB.Add(pb); fC.Add(pc);
                    fU.Add(u); fV.Add(v); fW.Add(w);
                    fuv.Add(ruv[ra] * u + ruv[rb] * v + ruv[rc] * w);
                }
            int Idx(int i, int j) => baseIdx + j * (N + 1) - j * (j - 1) / 2 + i;
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N - j; i++)
                {
                    ftri.Add(Idx(i, j)); ftri.Add(Idx(i + 1, j)); ftri.Add(Idx(i, j + 1));
                    if (i + j < N - 1) { ftri.Add(Idx(i + 1, j)); ftri.Add(Idx(i + 1, j + 1)); ftri.Add(Idx(i, j + 1)); }
                }
        }
        _fA = fA.ToArray(); _fB = fB.ToArray(); _fC = fC.ToArray();
        _fU = fU.ToArray(); _fV = fV.ToArray(); _fW = fW.ToArray();
        _fPos = new Vector3[_fA.Length]; _fNrm = new Vector3[_fA.Length];
        _pNrm = new Vector3[n];

        _mesh = new Mesh { name = src.name + " (sail cloth)" };
        _mesh.indexFormat = _fA.Length > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        UpdateDrawnMesh(_x); // fills _fPos / _fNrm
        _mesh.vertices = _fPos;
        _mesh.normals = _fNrm;
        _mesh.uv = fuv.ToArray();
        _mesh.triangles = ftri.ToArray();
        _mesh.RecalculateBounds();
        _mesh.MarkDynamic();
        mf.sharedMesh = _mesh;

        _ready = true;
        return true;
    }

    static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    // Laplacian smoothing of inner particles in the sail plane. Particles on the outline or on a
    // UV seam stay put. Moved particles get their UV sampled from the original mesh there.
    void Relax(List<Vector3> P, List<Vector3> rv, List<Vector2> ruv, List<int> rt, List<List<int>> pVerts,
               List<int>[] nbr, bool[] isBoundary, Vector3 c, Vector3 e1, Vector3 e2)
    {
        int n = P.Count;
        var p2 = new Vector2[n];
        for (int i = 0; i < n; i++) p2[i] = new Vector2(Vector3.Dot(P[i] - c, e1), Vector3.Dot(P[i] - c, e2));
        var orig = (Vector2[])p2.Clone();

        var movable = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (isBoundary[i] || nbr[i].Count == 0) continue;
            bool seam = false;
            Vector2 uv0 = ruv[pVerts[i][0]];
            foreach (int r in pVerts[i]) if ((ruv[r] - uv0).sqrMagnitude > 1e-8f) { seam = true; break; }
            movable[i] = !seam;
        }

        var tmp = new Vector2[n];
        for (int it = 0; it < relaxIterations; it++)
        {
            for (int i = 0; i < n; i++)
            {
                tmp[i] = p2[i];
                if (!movable[i]) continue;
                Vector2 avg = Vector2.zero;
                foreach (int j in nbr[i]) avg += p2[j];
                avg /= nbr[i].Count;
                tmp[i] = Vector2.Lerp(p2[i], avg, 0.5f);
            }
            var sw = p2; p2 = tmp; tmp = sw;
        }

        // re-sample UVs from the original (pre-relax) layout
        for (int i = 0; i < n; i++)
        {
            if (!movable[i] || (p2[i] - orig[i]).sqrMagnitude < 1e-10f) continue;
            bool found = false;
            for (int t = 0; t < rt.Count && !found; t += 3)
            {
                int ra = rt[t], rb = rt[t + 1], rc = rt[t + 2];
                Vector2 a = orig[_rvParticle[ra]], b = orig[_rvParticle[rb]], cc = orig[_rvParticle[rc]];
                if (!Barycentric(p2[i], a, b, cc, out float u, out float v, out float w)) continue;
                Vector2 uvNew = ruv[ra] * u + ruv[rb] * v + ruv[rc] * w;
                foreach (int r in pVerts[i]) ruv[r] = uvNew;
                found = true;
            }
            if (!found) p2[i] = orig[i]; // outside every triangle (shouldn't happen): don't move it
        }

        for (int i = 0; i < n; i++)
            if (movable[i]) P[i] = c + e1 * p2[i].x + e2 * p2[i].y;
    }

    static bool Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out float u, out float v, out float w)
    {
        Vector2 v0 = b - a, v1 = c - a, v2 = p - a;
        float d = v0.x * v1.y - v1.x * v0.y;
        u = v = w = 0f;
        if (Mathf.Abs(d) < 1e-12f) return false;
        v = (v2.x * v1.y - v1.x * v2.y) / d;
        w = (v0.x * v2.y - v2.x * v0.y) / d;
        u = 1f - v - w;
        const float eps = -1e-4f;
        return u >= eps && v >= eps && w >= eps;
    }

    static List<int> BoundaryLoop(Dictionary<int, List<int>> adj, int start)
    {
        foreach (var kv in adj) if (kv.Value.Count != 2) return null;
        var loop = new List<int> { start };
        int prev = -1, cur = start;
        for (int guard = 0; guard < adj.Count + 1; guard++)
        {
            var nb = adj[cur];
            int next = nb[0] != prev ? nb[0] : nb[1];
            if (next == start) return loop.Count == adj.Count ? loop : null;
            loop.Add(next);
            prev = cur; cur = next;
        }
        return null;
    }

    static Vector2[] Polyline(List<int> idx, Vector3[] q)
    {
        var pts = new Vector2[idx.Count];
        for (int k = 0; k < idx.Count; k++) pts[k] = new Vector2(q[idx[k]].y, q[idx[k]].z);
        System.Array.Sort(pts, (a, b) => a.x.CompareTo(b.x));
        return pts;
    }

    static float Interp(Vector2[] line, float y)
    {
        if (line.Length == 0) return 0f;
        if (y <= line[0].x) return line[0].y;
        for (int k = 1; k < line.Length; k++)
            if (y <= line[k].x)
            {
                float t = Mathf.InverseLerp(line[k - 1].x, line[k].x, y);
                return Mathf.Lerp(line[k - 1].y, line[k].y, t);
            }
        return line[line.Length - 1].y;
    }

    // 0 at the luff, 1 at the draft position, 0 at the leech (two parabolas)
    static float Profile(float x, float p)
    {
        if (x <= p) { float t = x / p; return t * (2f - t); }
        float u = (x - p) / (1f - p);
        return 1f - u * u;
    }

    // Mast centre (root space) at the height of each luff particle: average of mast vertices nearby.
    static void MastCentres(Transform mast, Transform root, List<Vector3> P, int[] idx, Vector3[] outC)
    {
        var mf = mast != null ? mast.GetComponent<MeshFilter>() : null;
        var mesh = mf != null ? mf.sharedMesh : null;
        Vector3[] mv = null;
        if (mesh != null && (mesh.isReadable || Application.isEditor))
        {
            Matrix4x4 m = root.worldToLocalMatrix * mast.localToWorldMatrix;
            var raw = mesh.vertices;
            mv = new Vector3[raw.Length];
            for (int i = 0; i < raw.Length; i++) mv[i] = m.MultiplyPoint3x4(raw[i]);
        }
        for (int k = 0; k < idx.Length; k++)
        {
            Vector3 p = P[idx[k]];
            outC[k] = p; // fallback: turn in place
            if (mv == null) continue;
            Vector3 sum = Vector3.zero; int cnt = 0;
            float window = 0.1f;
            for (int tries = 0; tries < 4 && cnt == 0; tries++, window *= 2f)
                foreach (var v in mv)
                    if (Mathf.Abs(v.y - p.y) < window) { sum += v; cnt++; }
            if (cnt > 0) { outC[k] = sum / cnt; outC[k].y = p.y; }
        }
    }

    // ===================================================================================
    // Simulation
    // ===================================================================================

    void FixedUpdate()
    {
        if (!_ready) return;
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;
        int n = _x.Length;
        System.Array.Copy(_x, _xStepPrev, n);
        int subs = Mathf.Max(1, substeps);
        float h = dt / subs;

        // pin targets for the end of this step
        Quaternion rot = Quaternion.AngleAxis(_rig != null ? _rig.SignedAngleDeg : 0f, _axisRoot);
        var pinNow = _pinNow;
        for (int k = 0; k < _footIdx.Length; k++) pinNow[_footIdx[k]] = _pivot.TransformPoint(_footLocal[k]);
        for (int k = 0; k < _luffIdx.Length; k++)
            pinNow[_luffIdx[k]] = _root.TransformPoint(_luffCentre[k] + rot * _luffOffset[k]);

        // wind pressure per triangle (once per step)
        for (int i = 0; i < n; i++) _force[i] = Vector3.zero;
        Vector3 wind = WeatherController.Instance != null ? WeatherController.Instance.WindVector : Vector3.zero;
        wind = SoftLimit(wind * clothWindGain, maxClothWind);
        Vector3 total = Vector3.zero, momentSum = Vector3.zero; float magSum = 0f;
        float halfRhoCn = 0.5f * airDensity * maxNormalCoefficient * pressureScale;
        for (int t = 0; t < _tri.Length; t += 3)
        {
            int a = _tri[t], b = _tri[t + 1], c = _tri[t + 2];
            Vector3 cr = Vector3.Cross(_x[b] - _x[a], _x[c] - _x[a]);
            float twoA = cr.magnitude;
            if (twoA < 1e-8f) continue;
            Vector3 nrm = cr / twoA;
            Vector3 centre = (_x[a] + _x[b] + _x[c]) / 3f;
            Vector3 vCloth = ((_x[a] - _xPrev[a]) + (_x[b] - _xPrev[b]) + (_x[c] - _xPrev[c])) / (3f * h);
            Vector3 app = SoftLimit(wind - vCloth, 1.5f * maxClothWind); // vCloth is world velocity, so the boat's own motion is included
            float speed = app.magnitude;
            if (speed < 1e-3f) continue;
            float vn = Vector3.Dot(app, nrm);
            // flat-plate normal force: q * A * Cn(α), Cn = Cmax * sin α, sin α = |vn| / speed
            Vector3 f = nrm * (halfRhoCn * 0.5f * twoA * speed * vn);
            Vector3 f3 = f / 3f;
            _force[a] += f3; _force[b] += f3; _force[c] += f3;
            total += f;
            float fm = f.magnitude;
            momentSum += centre * fm; magSum += fm;
        }
        aeroForce = total;
        if (magSum > 1e-6f) centreOfEffort = momentSum / magSum;

        Vector3 g = useGravity ? Physics.gravity : Vector3.zero;
        float keep = Mathf.Clamp01(1f - damping * h);

        for (int s = 0; s < subs; s++)
        {
            float alpha = (s + 1f) / subs;
            for (int i = 0; i < n; i++)
            {
                if (_pinned[i])
                {
                    _xPrev[i] = _x[i];
                    _x[i] = Vector3.Lerp(_pinPrev[i], pinNow[i], alpha);
                    continue;
                }
                Vector3 vel = (_x[i] - _xPrev[i]) * keep;
                _xPrev[i] = _x[i];
                _x[i] += vel + (g + _force[i] * _invMass[i]) * (h * h);
            }

            for (int it = 0; it < iterations; it++)
            {
                for (int k = 0; k < _cA.Length; k++)
                {
                    int a = _cA[k], b = _cB[k];
                    float wa = _invMass[a], wb = _invMass[b], w = wa + wb;
                    if (w <= 0f) continue;
                    Vector3 d = _x[b] - _x[a];
                    float len = d.magnitude;
                    if (len < 1e-9f) continue;
                    float stiff = _cK[k] == -1f ? stretchStiffness : bendStiffness;
                    if (stiff <= 0f) continue;
                    // bending pairs only resist folding (getting closer), not straightening
                    if (_cK[k] == -2f && len >= _cRest[k]) continue;
                    Vector3 corr = d * ((len - _cRest[k]) / (len * w) * stiff);
                    _x[a] += corr * wa;
                    _x[b] -= corr * wb;
                }
            }
        }
        for (int i = 0; i < n; i++) if (_pinned[i]) _pinPrev[i] = pinNow[i];

        // safety net: if the cloth ever blows up (NaN or flung far away), put it back to the last
        // healthy shape instead of letting it fly off and spam errors
        Vector3 o = _pivot.position;
        bool bad = false;
        for (int i = 0; i < n && !bad; i++)
        {
            Vector3 p = _x[i];
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) || (p - o).sqrMagnitude > 400f) bad = true;
        }
        if (bad)
        {
            for (int i = 0; i < n; i++)
            {
                Vector3 p = _hasGood ? _pivot.TransformPoint(_good[i]) : (_pinned[i] ? pinNow[i] : o);
                _x[i] = _xPrev[i] = _xStepPrev[i] = p;
            }
        }
        else
        {
            for (int i = 0; i < n; i++) _good[i] = _pivot.InverseTransformPoint(_x[i]);
            _hasGood = true;
        }
    }

    // Keeps the direction, eases the length towards 'max' (max * tanh(|v| / max)).
    static Vector3 SoftLimit(Vector3 v, float max)
    {
        float m = v.magnitude;
        if (m < 1e-6f || max <= 0f) return v;
        float e = Mathf.Exp(-2f * m / max);
        return v * (max * (1f - e) / (1f + e) / m);
    }

    void LateUpdate()
    {
        if (!_ready) return;
        Vector3[] pts = _x;
        if (interpolate)
        {
            float dt = Time.fixedDeltaTime;
            float a = dt > 0f ? Mathf.Clamp01((Time.time - Time.fixedTime) / dt) : 1f;
            for (int i = 0; i < _x.Length; i++) _xDraw[i] = Vector3.LerpUnclamped(_xStepPrev[i], _x[i], a);
            pts = _xDraw;
        }
        UpdateDrawnMesh(pts);
        _mesh.vertices = _fPos;
        _mesh.normals = _fNrm;
        _mesh.RecalculateBounds();
    }

    // Drawn mesh from the cloth points: smooth point normals, then for every fine vertex a Phong
    // tessellation point (the flat position pulled towards the tangent planes of the three cloth
    // points) and an interpolated normal. Shared edges only depend on their two end points, so
    // neighbouring triangles always meet without cracks.
    void UpdateDrawnMesh(Vector3[] x)
    {
        int n = x.Length;
        for (int i = 0; i < n; i++) _pNrm[i] = Vector3.zero;
        for (int t = 0; t < _tri.Length; t += 3)
        {
            int a = _tri[t], b = _tri[t + 1], c = _tri[t + 2];
            Vector3 cr = Vector3.Cross(x[b] - x[a], x[c] - x[a]); // area-weighted
            _pNrm[a] += cr; _pNrm[b] += cr; _pNrm[c] += cr;
        }
        for (int i = 0; i < n; i++) _pNrm[i] = _pNrm[i].normalized;

        Matrix4x4 toLocal = transform.worldToLocalMatrix;
        float alpha = smoothing;
        for (int k = 0; k < _fA.Length; k++)
        {
            int a = _fA[k], b = _fB[k], c = _fC[k];
            float u = _fU[k], v = _fV[k], w = _fW[k];
            Vector3 xa = x[a], xb = x[b], xc = x[c];
            Vector3 na = _pNrm[a], nb = _pNrm[b], nc = _pNrm[c];
            Vector3 p = xa * u + xb * v + xc * w;
            if (alpha > 0f)
            {
                Vector3 pa = p - na * Vector3.Dot(p - xa, na);
                Vector3 pb = p - nb * Vector3.Dot(p - xb, nb);
                Vector3 pc = p - nc * Vector3.Dot(p - xc, nc);
                p = Vector3.LerpUnclamped(p, pa * u + pb * v + pc * w, alpha);
            }
            _fPos[k] = toLocal.MultiplyPoint3x4(p);
            _fNrm[k] = toLocal.MultiplyVector(na * u + nb * v + nc * w).normalized;
        }
    }

    void OnDrawGizmosSelected()
    {
        if (!_ready) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(centreOfEffort, centreOfEffort + aeroForce * 0.01f);
        Gizmos.DrawWireSphere(centreOfEffort, 0.08f);
    }
}
