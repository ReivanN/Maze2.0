using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-click project setup (menu: Maze > Setup).
///
/// Everything here is done through Unity's own APIs instead of editing scene / settings files by hand,
/// so it is safe to run while the Editor is open.
/// </summary>
public static class MazeProjectSetup
{
    private const string LevelScenePath = "Assets/Scenes/LevelScene.unity";
    private const string IconPath = "Assets/Art/Icons/GameIcon.png";
    private const string ApplicationId = "com.reivanstudio.mazetogether";

    [MenuItem("Maze/Setup/Apply All (Mutators + Release Settings)")]
    public static void ApplyAll()
    {
        AddMutatorsToLevelScene();
        ApplyReleaseSettings();
    }

    /// <summary>
    /// Adds LevelMutatorManager to the same object as DungeonProgressManager in LevelScene
    /// (LevelMutatorManager is a NetworkBehaviour, so it has to live on a NetworkObject).
    /// </summary>
    [MenuItem("Maze/Setup/Add Level Mutators To LevelScene")]
    public static void AddMutatorsToLevelScene()
    {
        // Lets the user save the scene they are working on before we switch scenes.
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        var scene = EditorSceneManager.OpenScene(LevelScenePath, OpenSceneMode.Single);

        var progressManager = UnityEngine.Object.FindAnyObjectByType<DungeonProgressManager>();
        if (progressManager == null)
        {
            Debug.LogError($"[Maze Setup] DungeonProgressManager was not found in {LevelScenePath}.");
            return;
        }

        if (progressManager.GetComponent<LevelMutatorManager>() != null)
        {
            Debug.Log("[Maze Setup] LevelMutatorManager is already in LevelScene.");
            return;
        }

        Undo.AddComponent<LevelMutatorManager>(progressManager.gameObject);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log("[Maze Setup] Added LevelMutatorManager to '" + progressManager.gameObject.name +
                  "' in LevelScene and saved the scene.");
    }

    /// <summary>
    /// Replaces the template application identifiers (com.UnityTechnologies...urpblank) and sets the app icon.
    /// </summary>
    [MenuItem("Maze/Setup/Apply Release Settings (App ID + Icon)")]
    public static void ApplyReleaseSettings()
    {
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, ApplicationId);
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationId);

        ApplyIcon();

        AssetDatabase.SaveAssets();
        Debug.Log($"[Maze Setup] Application identifier set to '{ApplicationId}'.");
    }

    private static void ApplyIcon()
    {
        var importer = AssetImporter.GetAtPath(IconPath) as TextureImporter;
        if (importer == null)
        {
            Debug.LogWarning($"[Maze Setup] Icon not found at {IconPath}, skipping the icon.");
            return;
        }

        // Keep the icon sharp: no mipmaps, no compression.
        bool changed = false;

        if (importer.mipmapEnabled)
        {
            importer.mipmapEnabled = false;
            changed = true;
        }

        if (importer.textureCompression != TextureImporterCompression.Uncompressed)
        {
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            changed = true;
        }

        if (changed)
            importer.SaveAndReimport();

        var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);
        if (icon == null)
            return;

        try
        {
            // NamedBuildTarget.Unknown = the default icon used by every platform.
            PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { icon }, IconKind.Any);
            Debug.Log("[Maze Setup] Default app icon set.");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Maze Setup] Could not set the icon automatically ({e.Message}). " +
                             "Assign it manually in Project Settings > Player > Icon.");
        }
    }
}
