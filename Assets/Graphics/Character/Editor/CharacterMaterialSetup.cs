using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Gives Character.fbx real URP Lit materials. The FBX has five material slots (Cap, Hair, Pants,
/// Shirt, Skin); they are matched by slot name on every import, the same way ILCA7MaterialSetup
/// does it for the boat.
///
/// Missing materials are created once in Assets/Graphics/Character/Materials/ and are never
/// overwritten, so you can tweak colours, smoothness or add textures on those assets freely.
/// Tools > Character > Reapply Character Materials re-runs everything manually.
///
/// Note: the FBX currently has no UV maps, so only colours are set here. Texture slots are left
/// empty; once the mesh is unwrapped just drop textures onto the material assets.
/// </summary>
public class CharacterMaterialSetup : AssetPostprocessor
{
    const string ModelPath = "Assets/Graphics/Models/Character.fbx";
    const string MaterialDir = "Assets/Graphics/Character/Materials/";

    struct Def
    {
        public string slot;          // material slot name inside the FBX
        public Color srgb;           // colour as you see it in the inspector
        public float smoothness;
        public Def(string slot, Color srgb, float smoothness)
        {
            this.slot = slot; this.srgb = srgb; this.smoothness = smoothness;
        }
    }

    static readonly Def[] Defs =
    {
        new Def("Skin",  new Color(0.96f, 0.76f, 0.62f), 0.25f),
        new Def("Hair",  new Color(0.17f, 0.11f, 0.08f), 0.40f),
        new Def("Shirt", new Color(0.92f, 0.93f, 0.95f), 0.10f),
        new Def("Pants", new Color(0.10f, 0.17f, 0.32f), 0.10f),
        new Def("Cap",   new Color(0.85f, 0.18f, 0.12f), 0.15f),
    };

    static string MatPath(string slot) { return MaterialDir + "Character_" + slot + ".mat"; }

    // Creates any missing material asset. Never touches existing ones.
    static void EnsureMaterials()
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) return;

        Directory.CreateDirectory(MaterialDir);
        bool created = false;
        foreach (var d in Defs)
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(MatPath(d.slot)) != null) continue;
            var m = new Material(shader) { name = "Character_" + d.slot };
            m.SetColor("_BaseColor", d.srgb.linear);   // material colours are stored linear
            m.SetFloat("_Smoothness", d.smoothness);
            m.SetFloat("_Metallic", 0f);
            AssetDatabase.CreateAsset(m, MatPath(d.slot));
            created = true;
        }
        if (created) AssetDatabase.SaveAssets();
    }

    void OnPostprocessModel(GameObject root)
    {
        if (assetPath != ModelPath) return;
        Apply(root);
    }

    // Replaces each slot's material by the Character_<slot> asset. Returns false if any is missing.
    static bool Apply(GameObject root)
    {
        bool allFound = true;
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                string slot = mats[i].name;
                if (slot.StartsWith("Character_")) slot = slot.Substring("Character_".Length);

                var target = AssetDatabase.LoadAssetAtPath<Material>(MatPath(slot));
                if (target == null)
                {
                    if (System.Array.Exists(Defs, d => d.slot == slot)) allFound = false;
                    continue;
                }
                mats[i] = target;
            }
            r.sharedMaterials = mats;
        }
        return allFound;
    }

    [MenuItem("Tools/Character/Reapply Character Materials")]
    public static void Reimport()
    {
        EnsureMaterials();
        AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceUpdate);
    }

    // First run: create the materials, then reimport the model once so they get assigned.
    [InitializeOnLoadMethod]
    static void CheckOnLoad()
    {
        EditorApplication.delayCall += () =>
        {
            if (SessionState.GetBool("CharacterMaterialSetupDone", false)) return;
            SessionState.SetBool("CharacterMaterialSetupDone", true);

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (model == null) return;

            EnsureMaterials();
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && !m.name.StartsWith("Character_"))
                    {
                        Reimport();
                        return;
                    }
        };
    }
}
