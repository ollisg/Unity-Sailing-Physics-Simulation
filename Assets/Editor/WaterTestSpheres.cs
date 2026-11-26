using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Water > Create Test Spheres: adds five spheres of different sizes in front of the
/// Scene view camera, each with a WaterFloater. The first three follow the sampled surface
/// exactly, the last two are physics objects with buoyancy.
/// </summary>
public static class WaterTestSpheres
{
    [MenuItem("Tools/Water/Create Test Spheres")]
    static void Create()
    {
        var waves = Object.FindAnyObjectByType<GerstnerWaveController>();
        if (waves == null)
        {
            EditorUtility.DisplayDialog("Create Test Spheres", "No GerstnerWaveController found in the scene.", "OK");
            return;
        }

        Vector3 center = SceneView.lastActiveSceneView != null
            ? SceneView.lastActiveSceneView.pivot
            : waves.transform.position;
        float waterY = waves.waterSurface != null ? waves.waterSurface.position.y : waves.transform.position.y;
        center.y = waterY;

        var root = new GameObject("Water Test Spheres");
        Undo.RegisterCreatedObjectUndo(root, "Create Water Test Spheres");
        root.transform.position = center;

        float[] sizes = { 0.4f, 0.7f, 1.0f, 1.5f, 2.2f };
        for (int i = 0; i < sizes.Length; i++)
        {
            bool buoyant = i >= 3;

            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            s.name = (buoyant ? "Buoyant Sphere " : "Follow Sphere ") + (i + 1);
            s.transform.SetParent(root.transform, false);
            s.transform.localScale = Vector3.one * sizes[i];
            s.transform.position = center + new Vector3((i - 2) * 4f, buoyant ? 2f : 0f, 0f);

            // Follow spheres are moved directly, so they don't need a collider
            if (!buoyant)
                Object.DestroyImmediate(s.GetComponent<SphereCollider>());

            var f = Undo.AddComponent<WaterFloater>(s);
            f.waves = waves;
            f.mode = buoyant ? WaterFloater.Mode.Buoyant : WaterFloater.Mode.FollowSurface;
        }

        Selection.activeGameObject = root;
    }
}
