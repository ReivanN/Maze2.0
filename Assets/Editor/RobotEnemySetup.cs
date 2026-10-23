using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Builds the patrolling robot and the knife (menu: Maze > Setup > Create Robot Enemy + Knife):
///   - the looping Sci-Fi ToiletMech clips (idle, walk, run, knock-down loop) are set to loop;
///   - the robot materials are moved from the Built-in Standard shader to URP Lit (they render pink in URP);
///   - an Animator Controller with the states RobotEnemy plays by name;
///   - Resources/RobotEnemy and Resources/KnifePickup prefabs, registered in the network prefab lists.
///
/// Runs once by itself after the scripts compile, while the prefabs do not exist yet. Running the menu again
/// rebuilds everything (the prefabs keep their GUIDs, so nothing that references them breaks).
/// </summary>
[InitializeOnLoad]
public static class RobotEnemySetup
{
    private const string MechFolder = "Assets/Sci-Fi ToiletMech";
    private const string ModelPrefabPath = MechFolder + "/Prefab/Sci-Fi ToiletMech Skin 1.prefab";
    private const string EnemiesFolder = "Assets/Prefabs/Enemies";
    private const string ControllerPath = EnemiesFolder + "/RobotEnemy.controller";
    private const string RobotPrefabPath = "Assets/Resources/RobotEnemy.prefab";
    private const string KnifePrefabPath = "Assets/Resources/KnifePickup.prefab";

    // NGO_Minimal_Setup list is the one the NetworkManager in LevelScene uses.
    private static readonly string[] PrefabListPaths =
    {
        "Assets/NGO_Minimal_Setup/NetworkPrefabsList.asset",
        "Assets/DefaultNetworkPrefabs.asset"
    };

    // Animator state (played by RobotEnemy) -> clip file Anim_SciFi_ToiletMech@<clip>.fbx
    private static readonly (string state, string clip)[] StateClips =
    {
        ("Idle", "Idle2"),
        ("Walk", "Walk_F"),
        ("Run", "Run"),
        ("Attack", "Attack1"),
        ("KnockStart", "KnockStart"),
        ("KnockLoop", "KnockLoop"),
        ("KnockEnd", "KnockEnd")
    };

    private static readonly string[] LoopingClips = { "Idle2", "Walk_F", "Run", "KnockLoop" };

    static RobotEnemySetup()
    {
        EditorApplication.delayCall += AutoSetup;
    }

    private static void AutoSetup()
    {
        if (Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        if (Load(RobotPrefabPath) != null && Load(KnifePrefabPath) != null)
            return;

        if (Load(ModelPrefabPath) == null)
            return;

        CreateAll();
    }

    [MenuItem("Maze/Setup/Create Robot Enemy + Knife")]
    public static void CreateAll()
    {
        GameObject modelPrefab = Load(ModelPrefabPath);
        if (modelPrefab == null)
        {
            Debug.LogError($"[Maze Setup] Robot model not found at {ModelPrefabPath}.");
            return;
        }

        SetClipsLooping();
        ConvertMaterialsToUrp();
        AnimatorController controller = CreateController();

        CreateRobotPrefab(modelPrefab, controller);
        CreateKnifePrefab();
        RegisterNetworkPrefabs(RobotPrefabPath, KnifePrefabPath);

        AssetDatabase.SaveAssets();
        Debug.Log("[Maze Setup] Robot enemy and knife created: " + RobotPrefabPath + ", " + KnifePrefabPath);
    }

    // ================= CLIPS =================

    private static string AnimationPath(string clip) => $"{MechFolder}/Animations/Anim_SciFi_ToiletMech@{clip}.fbx";

    private static void SetClipsLooping()
    {
        foreach (string clip in LoopingClips)
        {
            if (!(AssetImporter.GetAtPath(AnimationPath(clip)) is ModelImporter importer))
                continue;

            ModelImporterClipAnimation[] clips = importer.clipAnimations;
            if (clips == null || clips.Length == 0)
                clips = importer.defaultClipAnimations;

            bool changed = false;
            foreach (ModelImporterClipAnimation animation in clips)
            {
                if (animation.loopTime)
                    continue;

                animation.loopTime = true;
                changed = true;
            }

            if (!changed)
                continue;

            importer.clipAnimations = clips;
            importer.SaveAndReimport();
        }
    }

    private static AnimationClip LoadClip(string clip)
    {
        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(AnimationPath(clip)))
        {
            if (asset is AnimationClip animationClip && !animationClip.name.StartsWith("__preview__"))
                return animationClip;
        }

        Debug.LogWarning($"[Maze Setup] No animation clip in {AnimationPath(clip)}.");
        return null;
    }

    // ================= MATERIALS =================

    private static void ConvertMaterialsToUrp()
    {
        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null)
        {
            Debug.LogWarning("[Maze Setup] URP Lit shader not found, the robot materials were not converted.");
            return;
        }

        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { MechFolder + "/Materials" }))
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (material == null || material.shader == lit || !material.shader.name.StartsWith("Standard"))
                continue;

            // Read the Standard properties before the shader changes.
            Texture baseMap = GetTexture(material, "_MainTex");
            Color baseColor = material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
            Texture normalMap = GetTexture(material, "_BumpMap");
            Texture metallicMap = GetTexture(material, "_MetallicGlossMap");
            Texture occlusionMap = GetTexture(material, "_OcclusionMap");
            Texture emissionMap = GetTexture(material, "_EmissionMap");
            Color emissionColor = material.HasProperty("_EmissionColor") ? material.GetColor("_EmissionColor") : Color.black;
            bool emission = material.IsKeywordEnabled("_EMISSION");
            float metallic = material.HasProperty("_Metallic") ? material.GetFloat("_Metallic") : 0f;
            float smoothness = metallicMap != null
                ? material.HasProperty("_GlossMapScale") ? material.GetFloat("_GlossMapScale") : 1f
                : material.HasProperty("_Glossiness") ? material.GetFloat("_Glossiness") : 0.5f;

            material.shader = lit;
            material.SetTexture("_BaseMap", baseMap);
            material.SetColor("_BaseColor", baseColor);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);

            if (normalMap != null)
            {
                material.SetTexture("_BumpMap", normalMap);
                material.EnableKeyword("_NORMALMAP");
            }

            if (metallicMap != null)
            {
                material.SetTexture("_MetallicGlossMap", metallicMap);
                material.EnableKeyword("_METALLICSPECGLOSSMAP");
            }

            if (occlusionMap != null)
            {
                material.SetTexture("_OcclusionMap", occlusionMap);
                material.EnableKeyword("_OCCLUSIONMAP");
            }

            if (emission)
            {
                material.SetTexture("_EmissionMap", emissionMap);
                material.SetColor("_EmissionColor", emissionColor);
                material.EnableKeyword("_EMISSION");
            }

            EditorUtility.SetDirty(material);
        }
    }

    private static Texture GetTexture(Material material, string property)
    {
        return material.HasProperty(property) ? material.GetTexture(property) : null;
    }

    // ================= ANIMATOR =================

    private static AnimatorController CreateController()
    {
        EnsureFolder(EnemiesFolder);
        AssetDatabase.DeleteAsset(ControllerPath);

        AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        Dictionary<string, AnimatorState> states = new Dictionary<string, AnimatorState>();

        foreach ((string state, string clip) in StateClips)
        {
            AnimatorState animatorState = stateMachine.AddState(state);
            animatorState.motion = LoadClip(clip);
            states[state] = animatorState;
        }

        stateMachine.defaultState = states["Idle"];

        // RobotEnemy only starts the knock-down; the clip goes on into its loop by itself.
        AnimatorStateTransition transition = states["KnockStart"].AddTransition(states["KnockLoop"]);
        transition.hasExitTime = true;
        transition.exitTime = 0.95f;
        transition.hasFixedDuration = true;
        transition.duration = 0.1f;

        EditorUtility.SetDirty(controller);
        return controller;
    }

    // ================= PREFABS =================

    private static void CreateRobotPrefab(GameObject modelPrefab, AnimatorController controller)
    {
        GameObject root = new GameObject("RobotEnemy");

        try
        {
            root.AddComponent<NetworkObject>();

            // The server moves the robot; it only turns around Y and never changes its scale.
            NetworkTransform networkTransform = root.AddComponent<NetworkTransform>();
            networkTransform.SyncRotAngleX = false;
            networkTransform.SyncRotAngleZ = false;
            networkTransform.SyncScaleX = false;
            networkTransform.SyncScaleY = false;
            networkTransform.SyncScaleZ = false;

            root.AddComponent<CapsuleCollider>();

            GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(modelPrefab, root.transform);
            model.name = "Model";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;

            Animator animator = model.GetComponentInChildren<Animator>();
            if (animator == null)
                animator = model.AddComponent<Animator>();

            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;

            root.AddComponent<RobotEnemy>();

            SavePrefab(root, RobotPrefabPath);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    private static void CreateKnifePrefab()
    {
        GameObject root = new GameObject("KnifePickup");

        try
        {
            root.AddComponent<NetworkObject>();
            root.AddComponent<KnifePickup>();
            SavePrefab(root, KnifePrefabPath);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    private static void SavePrefab(GameObject root, string path)
    {
        EnsureFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
        PrefabUtility.SaveAsPrefabAsset(root, path);

        // NetworkObject computes its prefab hash in OnValidate; make sure the saved asset has it.
        GameObject asset = Load(path);
        NetworkObject networkObject = asset != null ? asset.GetComponent<NetworkObject>() : null;
        if (networkObject == null)
            return;

        MethodInfo onValidate = typeof(NetworkObject).GetMethod("OnValidate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        onValidate?.Invoke(networkObject, null);
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();
    }

    private static void RegisterNetworkPrefabs(params string[] prefabPaths)
    {
        foreach (string listPath in PrefabListPaths)
        {
            NetworkPrefabsList list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(listPath);
            if (list == null)
                continue;

            foreach (string prefabPath in prefabPaths)
            {
                GameObject prefab = Load(prefabPath);
                if (prefab != null && !list.Contains(prefab))
                    list.Add(new NetworkPrefab { Prefab = prefab });
            }

            EditorUtility.SetDirty(list);
        }
    }

    // ================= UTILS =================

    private static GameObject Load(string path) => AssetDatabase.LoadAssetAtPath<GameObject>(path);

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder))
            return;

        string parent = System.IO.Path.GetDirectoryName(folder).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));
    }
}
