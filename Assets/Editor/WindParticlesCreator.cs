using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Weather > Create Wind Particles: adds a wind particle system as a child of the main camera.
/// </summary>
public static class WindParticlesCreator
{
    const string MaterialFolder = "Assets/Graphics/Wind";
    const string MaterialPath = MaterialFolder + "/WindStreak.mat";

    [MenuItem("Tools/Weather/Create Wind Particles")]
    static void Create()
    {
        Camera cam = Camera.main != null ? Camera.main : Object.FindAnyObjectByType<Camera>();
        if (cam == null)
        {
            EditorUtility.DisplayDialog("Create Wind Particles", "No camera found in the scene.", "OK");
            return;
        }

        Shader shader = Shader.Find("Custom/URP/WindStreak");
        if (shader == null)
        {
            EditorUtility.DisplayDialog("Create Wind Particles",
                "The WindStreak shader isn't imported yet. Wait for Unity to finish compiling and try again.", "OK");
            return;
        }

        // Material asset (so the shader is included in builds)
        var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (mat == null)
        {
            if (!AssetDatabase.IsValidFolder(MaterialFolder))
                AssetDatabase.CreateFolder("Assets/Graphics", "Wind");
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, MaterialPath);
        }

        var go = new GameObject("Wind Particles");
        Undo.RegisterCreatedObjectUndo(go, "Create Wind Particles");
        go.transform.SetParent(cam.transform, false);

        var ps = go.AddComponent<ParticleSystem>();
        // Stop the default particle preview: WindParticles configures and starts it in Play mode
        var emission = ps.emission;
        emission.enabled = false;
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        go.GetComponent<ParticleSystemRenderer>().sharedMaterial = mat;

        var wp = go.AddComponent<WindParticles>();
        wp.follow = cam.transform;

        Selection.activeGameObject = go;

        if (Object.FindAnyObjectByType<WeatherController>() == null)
            Debug.LogWarning("Wind Particles created, but there is no WeatherController in the scene, so there is no wind yet. " +
                             "Add one (Add Component > Sailing > Weather Controller).");
    }
}
