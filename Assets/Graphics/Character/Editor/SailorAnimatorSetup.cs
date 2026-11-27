using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds Assets/Graphics/Character/Sailor.controller from the pose takes in Character.fbx:
///
///   Parameters: Hike (float 0..1), Duck (bool)
///   Hiking (default state): 1D blend tree on Hike
///       SitClose @ 0, SitFar @ 0.5, SitLean @ 1
///   Duck: Duck pose. Hiking -> Duck when Duck is true, back when false (0.25 s crossfades).
///
/// It also sets up the FBX clip list (one clip per Blender action, "rig|" prefix removed),
/// and gives every Character.fbx instance in the open scenes the controller plus a SailorPose
/// component. Scenes are only marked dirty; save them yourself.
///
/// Runs once automatically if the controller doesn't exist yet. Tools > Character >
/// Build Sailor Animator rebuilds it (the controller is regenerated, so put manual edits
/// in this script or rename the controller first).
/// </summary>
public static class SailorAnimatorSetup
{
    const string ModelPath = "Assets/Graphics/Models/Character.fbx";
    const string ControllerPath = "Assets/Graphics/Character/Sailor.controller";

    static readonly string[] HikePoses = { "SitClose", "SitFar", "SitLean" };
    static readonly float[] HikeThresholds = { 0f, 0.5f, 1f };
    const string DuckPose = "Duck";
    const float DuckFade = 0.25f;

    [MenuItem("Tools/Character/Build Sailor Animator")]
    public static void Build()
    {
        var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
        if (importer == null)
        {
            Debug.LogError("SailorAnimatorSetup: model not found at " + ModelPath);
            return;
        }
        if (FixImport(importer))
            importer.SaveAndReimport();

        var clips = AssetDatabase.LoadAllAssetsAtPath(ModelPath)
            .OfType<AnimationClip>()
            .Where(c => !c.name.StartsWith("__preview__"))
            .GroupBy(c => c.name)
            .ToDictionary(g => g.Key, g => g.First());

        var needed = HikePoses.Concat(new[] { DuckPose }).ToArray();
        var missing = needed.Where(n => !clips.ContainsKey(n)).ToArray();
        if (missing.Length > 0)
        {
            Debug.LogError("SailorAnimatorSetup: pose clips missing from Character.fbx: "
                           + string.Join(", ", missing) + ". Found: " + string.Join(", ", clips.Keys));
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ControllerPath));
        AssetDatabase.DeleteAsset(ControllerPath);
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        ctrl.AddParameter("Hike", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("Duck", AnimatorControllerParameterType.Bool);

        // Hiking blend tree (first state added becomes the default state)
        var hiking = ctrl.CreateBlendTreeInController("Hiking", out BlendTree tree, 0);
        tree.blendType = BlendTreeType.Simple1D;
        tree.blendParameter = "Hike";
        tree.useAutomaticThresholds = false;
        for (int i = 0; i < HikePoses.Length; i++)
            tree.AddChild(clips[HikePoses[i]], HikeThresholds[i]);

        var sm = ctrl.layers[0].stateMachine;
        sm.defaultState = hiking;

        var duck = sm.AddState("Duck");
        duck.motion = clips[DuckPose];

        var toDuck = hiking.AddTransition(duck);
        toDuck.hasExitTime = false;
        toDuck.hasFixedDuration = true;
        toDuck.duration = DuckFade;
        toDuck.AddCondition(AnimatorConditionMode.If, 0f, "Duck");

        var toHiking = duck.AddTransition(hiking);
        toHiking.hasExitTime = false;
        toHiking.hasFixedDuration = true;
        toHiking.duration = DuckFade;
        toHiking.AddCondition(AnimatorConditionMode.IfNot, 0f, "Duck");

        EditorUtility.SetDirty(ctrl);
        AssetDatabase.SaveAssets();

        int assigned = AssignInOpenScenes(ctrl);
        Debug.Log("SailorAnimatorSetup: built " + ControllerPath + ", assigned to " + assigned +
                  " Character instance(s) in the open scene(s).");
    }

    // One looping clip per Blender action, named without the "rig|" prefix, and a Generic avatar.
    // Returns true if the importer settings changed.
    static bool FixImport(ModelImporter imp)
    {
        bool changed = false;

        if (imp.animationType == ModelImporterAnimationType.Generic &&
            imp.avatarSetup == ModelImporterAvatarSetup.NoAvatar)
        {
            imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            changed = true;
        }

        var want = imp.defaultClipAnimations;
        foreach (var c in want)
        {
            c.name = Strip(c.takeName);
            c.loopTime = true;
            // Single-keyframe Blender poses come in as zero-length takes, and Unity drops
            // every curve of a zero-length clip. Give each one a one-frame range.
            if (c.lastFrame <= c.firstFrame) c.lastFrame = c.firstFrame + 1f;
        }

        var cur = imp.clipAnimations;
        bool same = cur.Length == want.Length &&
                    want.All(w => cur.Any(c => c.name == w.name && c.takeName == w.takeName && c.loopTime
                                               && c.lastFrame > c.firstFrame));
        if (!same)
        {
            imp.clipAnimations = want;
            changed = true;
        }
        return changed;
    }

    static string Strip(string take)
    {
        int i = take.LastIndexOf('|');
        return i >= 0 ? take.Substring(i + 1) : take;
    }

    static int AssignInOpenScenes(RuntimeAnimatorController ctrl)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        int n = 0;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
        {
            var go = t.gameObject;
            if (!PrefabUtility.IsOutermostPrefabInstanceRoot(go)) continue;
            if (PrefabUtility.GetCorrespondingObjectFromOriginalSource(go) != model) continue;

            var anim = go.GetComponent<Animator>();
            if (anim == null) anim = Undo.AddComponent<Animator>(go);
            Undo.RecordObject(anim, "Assign Sailor Animator");
            anim.runtimeAnimatorController = ctrl;

            if (go.GetComponent<SailorPose>() == null) Undo.AddComponent<SailorPose>(go);

            EditorSceneManager.MarkSceneDirty(go.scene);
            n++;
        }
        return n;
    }

    [InitializeOnLoadMethod]
    static void BuildOnceIfMissing()
    {
        EditorApplication.delayCall += () =>
        {
            if (SessionState.GetBool("SailorAnimatorSetupChecked", false)) return;
            SessionState.SetBool("SailorAnimatorSetupChecked", true);

            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) != null) return;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath) == null) return;
            Build();
        };
    }
}
