using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Tools > Boat > Set Up Boat Physics: in the open scene, puts the sailor under the boat and adds
/// Rigidbody + BoatMassModel + BoomRig to the ILCA 7 and SailorMassModel to the sailor.
/// The Rigidbody starts kinematic, because nothing holds the boat up until buoyancy exists;
/// untick Is Kinematic once the hull floats. The scene is only marked dirty: save it yourself.
/// </summary>
public static class BoatPhysicsSetup
{
    const string BoatModel = "Assets/Graphics/Models/ILCA7.fbx";
    const string SailorModel = "Assets/Graphics/Models/Character.fbx";

    [MenuItem("Tools/Boat/Set Up Boat Physics")]
    public static void SetUp()
    {
        GameObject boat = FindInstance(BoatModel);
        GameObject sailor = FindInstance(SailorModel);
        if (boat == null)
        {
            Debug.LogError("BoatPhysicsSetup: no ILCA7 instance in the open scene.");
            return;
        }

        if (sailor != null && !sailor.transform.IsChildOf(boat.transform))
            Undo.SetTransformParent(sailor.transform, boat.transform, "Put sailor in boat");

        var rb = boat.GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = Undo.AddComponent<Rigidbody>(boat);
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
        }
        if (boat.GetComponent<BoatMassModel>() == null) Undo.AddComponent<BoatMassModel>(boat);
        if (boat.GetComponent<BoomRig>() == null) Undo.AddComponent<BoomRig>(boat);
        if (sailor != null && sailor.GetComponent<SailorMassModel>() == null)
            Undo.AddComponent<SailorMassModel>(sailor);

        EditorSceneManager.MarkSceneDirty(boat.scene);
        Debug.Log("BoatPhysicsSetup: boat physics set up on " + boat.name +
                  (sailor != null ? " with sailor " + sailor.name : " (no sailor found)") +
                  ". The Rigidbody is kinematic until buoyancy exists.", boat);
    }

    static GameObject FindInstance(string modelPath)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        if (model == null) return null;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
        {
            var go = t.gameObject;
            if (PrefabUtility.IsOutermostPrefabInstanceRoot(go) &&
                PrefabUtility.GetCorrespondingObjectFromOriginalSource(go) == model)
                return go;
        }
        return null;
    }
}
