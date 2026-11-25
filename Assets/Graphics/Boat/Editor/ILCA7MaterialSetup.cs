using UnityEditor;
using UnityEngine;

/// <summary>
/// Assigns the ILCA 7 materials to the parts of ILCA7.fbx every time the model is imported.
/// The FBX only has a material slot on the hull, so parts are matched by name instead.
/// Tools > ILCA 7 > Reapply Boat Materials re-runs it manually.
/// </summary>
public class ILCA7MaterialSetup : AssetPostprocessor
{
    const string ModelPath = "Assets/Graphics/Models/ILCA7.fbx";
    const string MaterialDir = "Assets/Graphics/Boat/Materials/";

    // part (object name in the FBX) -> material file name
    static readonly string[,] PartMaterials =
    {
        { "Hull1",    "ILCA7_Hull"  },
        { "Plane",    "ILCA7_Sail"  },
        { "Mast",     "ILCA7_Spars" },
        { "Boom",     "ILCA7_Spars" },
        { "Cube",     "ILCA7_Foils" }, // centreboard
        { "Cube.001", "ILCA7_Foils" }, // rudder
    };

    void OnPostprocessModel(GameObject root)
    {
        if (assetPath != ModelPath) return;
        Apply(root);
    }

    static bool Apply(GameObject root)
    {
        bool allFound = true;
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < PartMaterials.GetLength(0); i++)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialDir + PartMaterials[i, 1] + ".mat");
            if (mat == null) { allFound = false; continue; }

            foreach (var r in renderers)
            {
                if (r.name != PartMaterials[i, 0]) continue;
                var mats = r.sharedMaterials;
                for (int m = 0; m < mats.Length; m++) mats[m] = mat;
                r.sharedMaterials = mats;
            }
        }
        return allFound;
    }

    [MenuItem("Tools/ILCA 7/Reapply Boat Materials")]
    public static void Reimport()
    {
        AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceUpdate);
    }

    // On first import the model can be processed before the material files exist.
    // Check once after scripts load and reimport the model if the hull is still unassigned.
    [InitializeOnLoadMethod]
    static void CheckOnLoad()
    {
        EditorApplication.delayCall += () =>
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            var hullMat = AssetDatabase.LoadAssetAtPath<Material>(MaterialDir + "ILCA7_Hull.mat");
            if (model == null || hullMat == null) return;

            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (r.name == "Hull1" && r.sharedMaterial != hullMat)
                {
                    Reimport();
                    return;
                }
            }
        };
    }
}
