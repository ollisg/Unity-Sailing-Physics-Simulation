using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Tools > Sky > Set Up Clouds: switches the scene's skybox material to Sailing/Skybox Clouds
/// (keeping its cubemap, tint, exposure and rotation) and adds a "Sky Clouds" object.
/// </summary>
public static class SkyCloudsSetup
{
    const string ShaderName = "Sailing/Skybox Clouds";
    const string NewMaterialPath = "Assets/Graphics/Sky/SkyClouds.mat";

    [MenuItem("Tools/Sky/Set Up Clouds")]
    public static void SetUp()
    {
        var shader = Shader.Find(ShaderName);
        if (shader == null) { Debug.LogError("SkyCloudsSetup: shader '" + ShaderName + "' not found."); return; }

        // skybox: reuse a Skybox/Cubemap material (same property names), else make a new one
        Material sky = RenderSettings.skybox;
        if (sky != null && (sky.shader == shader || sky.shader.name == "Skybox/Cubemap"))
        {
            if (sky.shader != shader)
            {
                Undo.RecordObject(sky, "Set Up Clouds");
                sky.shader = shader;
                EditorUtility.SetDirty(sky);
            }
        }
        else
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(NewMaterialPath);
            if (mat == null)
            {
                mat = new Material(shader) { name = "SkyClouds" };
                mat.SetFloat("_UseGradient", 1f);
                AssetDatabase.CreateAsset(mat, NewMaterialPath);
            }
            RenderSettings.skybox = mat;
            Debug.Log("SkyCloudsSetup: the old skybox wasn't Skybox/Cubemap, so a gradient sky material was " +
                      "created at " + NewMaterialPath + ". Give it a cubemap and untick Use Gradient if you like.");
        }

        // cloud controller
        var clouds = Object.FindAnyObjectByType<SkyClouds>(FindObjectsInactive.Include);
        if (clouds == null)
        {
            var go = new GameObject("Sky Clouds");
            Undo.RegisterCreatedObjectUndo(go, "Set Up Clouds");
            clouds = go.AddComponent<SkyClouds>();
            var weather = Object.FindAnyObjectByType<WeatherController>();
            if (weather != null) go.transform.SetParent(weather.transform.parent, false);
        }
        Selection.activeObject = clouds.gameObject;

        // the water re-reads the skybox
        foreach (var w in Object.FindObjectsByType<GerstnerWaveController>(FindObjectsInactive.Include))
            w.SyncSkybox();

        EditorSceneManager.MarkSceneDirty(clouds.gameObject.scene);
        AssetDatabase.SaveAssets();
        Debug.Log("SkyCloudsSetup: clouds are set up. Settings are on the 'Sky Clouds' object.");
    }
}
