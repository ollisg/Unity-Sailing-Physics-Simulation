using UnityEngine;

/// <summary>
/// Builds a flat, subdivided grid mesh for the water surface.
/// Optionally follows a target (e.g. the camera) in whole-cell steps; because the
/// waves are computed in world space, the water looks static while the grid moves.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
[ExecuteAlways]
public class WaterGridMesh : MonoBehaviour
{
    [Min(1f)] public float size = 200f;
    [Range(8, 1000)] public int resolution = 256;
    [Tooltip("Vertical bounds of the mesh for culling. Keep it larger than the total wave amplitude.")]
    public float boundsHeight = 20f;
    [Tooltip("Optional: the grid follows this transform on X/Z (snapped to grid cells).")]
    public Transform follow;

    Mesh _mesh;

    void OnEnable() => Build();

    void OnDisable()
    {
        if (_mesh != null)
        {
            if (Application.isPlaying) Destroy(_mesh); else DestroyImmediate(_mesh);
            _mesh = null;
        }
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        // Mesh changes are not allowed directly inside OnValidate
        UnityEditor.EditorApplication.delayCall += () => { if (this != null) Build(); };
    }
#endif

    void LateUpdate()
    {
        if (follow == null) return;

        // Snap in whole (world-size) grid cells so vertices always land on the same world
        // positions and the waves don't appear to slide. Accounts for the object's scale.
        Vector3 scale = transform.lossyScale;
        float cellX = size / resolution * Mathf.Abs(scale.x);
        float cellZ = size / resolution * Mathf.Abs(scale.z);
        Vector3 p = transform.position;
        if (cellX > 0f) p.x = Mathf.Round(follow.position.x / cellX) * cellX;
        if (cellZ > 0f) p.z = Mathf.Round(follow.position.z / cellZ) * cellZ;
        transform.position = p;
    }

    [ContextMenu("Rebuild Mesh")]
    public void Build()
    {
        int verts = resolution + 1;
        var vertices = new Vector3[verts * verts];
        var uvs = new Vector2[verts * verts];
        var indices = new int[resolution * resolution * 6];

        float half = size * 0.5f;
        float step = size / resolution;

        for (int z = 0; z < verts; z++)
        {
            for (int x = 0; x < verts; x++)
            {
                int i = z * verts + x;
                vertices[i] = new Vector3(x * step - half, 0f, z * step - half);
                uvs[i] = new Vector2((float)x / resolution, (float)z / resolution);
            }
        }

        int t = 0;
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                int i = z * verts + x;
                indices[t++] = i;
                indices[t++] = i + verts;
                indices[t++] = i + 1;
                indices[t++] = i + 1;
                indices[t++] = i + verts;
                indices[t++] = i + verts + 1;
            }
        }

        if (_mesh == null)
        {
            _mesh = new Mesh { name = "Water Grid", hideFlags = HideFlags.DontSave };
        }
        else
        {
            _mesh.Clear();
        }

        _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        _mesh.vertices = vertices;
        _mesh.uv = uvs;
        _mesh.triangles = indices;
        _mesh.bounds = new Bounds(Vector3.zero, new Vector3(size, boundsHeight, size));

        GetComponent<MeshFilter>().sharedMesh = _mesh;
    }
}
