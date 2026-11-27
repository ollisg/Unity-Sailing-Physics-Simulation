using UnityEngine;

/// <summary>
/// Makes a Cloth sail follow the boom the same way a cape follows a character: by skinning.
/// Unity Cloth cannot attach to other objects; its pinned vertices (Max Distance 0) simply go
/// where the skinned mesh puts them. So the sail is skinned to two bones:
///   - a static anchor on the boat: the luff stays on the mast exactly as modelled
///   - the boom pivot: the rest of the sail turns with the boom
/// blended smoothly over luffBlendDistance from the luff. Pin the luff and the foot in the Cloth
/// constraints and let the rest move; the cloth then fills in the shape between.
///
/// BoomRig sets this up automatically at start for a sail with a SkinnedMeshRenderer.
/// </summary>
[DisallowMultipleComponent]
public class SailClothSkinning : MonoBehaviour
{
    [Tooltip("Distance from the luff (m) over which the sail blends from staying on the mast " +
             "to turning fully with the boom.")]
    [Min(0.01f)] public float luffBlendDistance = 0.3f;

    [Tooltip("How many height bands are used to find the luff in the sail mesh.")]
    [Range(4, 64)] public int luffSamples = 24;

    /// <summary>Skin the sail to (static anchor, boom pivot). The pivot must be at angle 0.</summary>
    public bool Setup(Transform root, Transform pivot)
    {
        var smr = GetComponent<SkinnedMeshRenderer>();
        if (smr == null || smr.sharedMesh == null) return false;
        Mesh src = smr.sharedMesh;
        if (!(src.isReadable || Application.isEditor)) return false;

        Vector3[] v = src.vertices;
        if (v.Length == 0) return false;

        // vertices in pivot space: y = along the mast axis, z = along the boom
        var p = new Vector3[v.Length];
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < v.Length; i++)
        {
            p[i] = pivot.InverseTransformPoint(transform.TransformPoint(v[i]));
            minY = Mathf.Min(minY, p[i].y); maxY = Mathf.Max(maxY, p[i].y);
        }
        if (maxY - minY < 0.5f) return false;

        // luff: the most forward vertex (smallest z) in each height band
        int bands = Mathf.Max(4, luffSamples);
        float bh = (maxY - minY) / bands;
        var luff = new Vector3[bands];
        var found = new bool[bands];
        for (int i = 0; i < p.Length; i++)
        {
            int b = Mathf.Clamp((int)((p[i].y - minY) / bh), 0, bands - 1);
            if (!found[b] || p[i].z < luff[b].z) { luff[b] = p[i]; found[b] = true; }
        }
        // fill empty bands from their neighbours
        for (int b = 0; b < bands; b++)
        {
            if (found[b]) continue;
            int lo = b - 1; while (lo >= 0 && !found[lo]) lo--;
            int hi = b + 1; while (hi < bands && !found[hi]) hi++;
            luff[b] = lo >= 0 && hi < bands ? Vector3.Lerp(luff[lo], luff[hi], 0.5f)
                    : lo >= 0 ? luff[lo] : luff[hi];
        }

        // weight of the boom bone: 0 on the luff, 1 from luffBlendDistance outwards
        var weights = new BoneWeight[v.Length];
        for (int i = 0; i < p.Length; i++)
        {
            float f = Mathf.Clamp((p[i].y - minY) / bh - 0.5f, 0f, bands - 1f);
            int b = Mathf.Min((int)f, bands - 2);
            Vector3 l = Vector3.Lerp(luff[b], luff[b + 1], f - b);
            float d = new Vector2(p[i].x - l.x, p[i].z - l.z).magnitude;
            float w = Mathf.SmoothStep(0f, 1f, d / luffBlendDistance);

            weights[i].boneIndex0 = 0; weights[i].weight0 = 1f - w;
            weights[i].boneIndex1 = 1; weights[i].weight1 = w;
        }

        // bones: static anchor (where the sail is now) and the boom pivot
        var anchor = new GameObject("SailAnchor").transform;
        anchor.SetParent(root, false);
        anchor.SetPositionAndRotation(transform.position, transform.rotation);
        var bones = new[] { anchor, pivot };
        var bindposes = new[]
        {
            anchor.worldToLocalMatrix * transform.localToWorldMatrix,
            pivot.worldToLocalMatrix * transform.localToWorldMatrix,
        };

        var mesh = Instantiate(src);
        mesh.name = src.name + " (boom skinned)";
        mesh.boneWeights = weights;
        mesh.bindposes = bindposes;

        // Cloth reads the mesh when it is enabled, so restart it around the swap.
        var cloth = GetComponent<Cloth>();
        ClothSkinningCoefficient[] coefficients = null;
        bool clothOn = cloth != null && cloth.enabled;
        if (cloth != null) { coefficients = cloth.coefficients; cloth.enabled = false; }

        smr.sharedMesh = mesh;
        smr.bones = bones;
        smr.rootBone = anchor;
        smr.updateWhenOffscreen = true;

        if (cloth != null)
        {
            if (coefficients != null && coefficients.Length == cloth.coefficients.Length)
                cloth.coefficients = coefficients;
            cloth.enabled = clothOn;
        }
        return true;
    }
}
