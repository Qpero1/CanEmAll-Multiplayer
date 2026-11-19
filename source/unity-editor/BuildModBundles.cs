// Assets/Editor/BuildModBundles.cs
// Unity 6000.2.15f1, URP — must match Can 'Em All.
//
// Tools -> Build Mod Bundles packs everything automatically, no Inspector tagging needed:
//   - every .prefab under Assets/Prefabs
//   - every .fbx under Assets/TripoModels that no prefab already uses (and isn't in ExcludedModels)
// Each asset is loadable in the mod by its file name, e.g. ModAssets.Spawn("miner_character_3d_model", ...).
// A prefab with the same name as an FBX wins, so make a prefab when you want to fix scale/rotation.
//
// Output: AssetBundles/canemall_mp -> copy next to your plugin DLL in BepInEx/plugins.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class BuildModBundles
{
    private const string BundleName = "canemall_mp";
    private const string OutputDir = "AssetBundles";
    private static readonly string[] PrefabFolders = { "Assets/Prefabs" };
    private static readonly string[] ModelFolders = { "Assets/TripoModels" };

    // Raw models in Assets/TripoModels to leave out of the bundle (old versions, tests).
    private static readonly HashSet<string> ExcludedModels = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
    {
        "miner_character_3d_model", // old unrigged miner, replaced by miner_boy_3d_model / PlayerModel prefab
    };

    [MenuItem("Tools/Build Mod Bundles")]
    public static void Build()
    {
        // make sure freshly (re)created assets like PlayerAnimator.controller are saved and imported first
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        // name -> asset path; prefabs override raw models with the same name
        var assets = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

        var prefabs = FindAssets("t:Prefab", PrefabFolders).ToList();

        // Raw models already used by a prefab are pulled in through the prefab — don't pack them twice.
        var usedByPrefabs = new HashSet<string>(
            prefabs.SelectMany(p => AssetDatabase.GetDependencies(p, true)),
            System.StringComparer.OrdinalIgnoreCase);

        foreach (var path in FindAssets("t:Model", ModelFolders))
        {
            if (usedByPrefabs.Contains(path)) continue;
            if (ExcludedModels.Contains(Path.GetFileNameWithoutExtension(path))) continue;
            assets[Path.GetFileNameWithoutExtension(path)] = path;
        }

        foreach (var path in prefabs)
            assets[Path.GetFileNameWithoutExtension(path)] = path;

        if (assets.Count == 0)
        {
            EditorUtility.DisplayDialog("Build Mod Bundles",
                "Nothing to build. Put prefabs in Assets/Prefabs or import models into Assets/TripoModels.", "OK");
            return;
        }

        var build = new AssetBundleBuild
        {
            assetBundleName = BundleName,
            assetNames = assets.Values.ToArray(),
            addressableNames = assets.Keys.ToArray(),
        };

        Directory.CreateDirectory(OutputDir);
        var manifest = BuildPipeline.BuildAssetBundles(
            OutputDir,
            new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression,
            BuildTarget.StandaloneWindows64);

        if (manifest == null)
        {
            Debug.LogError("[BuildModBundles] Build failed — check the Console.");
            return;
        }

        foreach (var kv in assets)
            Debug.Log($"[BuildModBundles] {kv.Key}  <-  {kv.Value}");

        // sanity check: a prefab with an Animator must bring its controller (class 91) and clips (class 74) along
        var manifestText = File.ReadAllText(Path.Combine(OutputDir, BundleName + ".manifest"));
        bool hasAnimator = manifestText.Contains("Class: 95");
        if (hasAnimator && (!manifestText.Contains("Class: 91") || !manifestText.Contains("Class: 74")))
        {
            EditorUtility.DisplayDialog("Build Mod Bundles",
                "The bundle has an Animator but NO Animator Controller / animation clips, so characters won't animate.\n\n" +
                "Run Tools -> Setup Player Character, wait for Unity to finish importing, then build again.", "OK");
            Debug.LogError("[BuildModBundles] Animator controller / clips missing from the bundle.");
        }
        Debug.Log($"[BuildModBundles] Built {Path.GetFullPath(Path.Combine(OutputDir, BundleName))} ({assets.Count} assets)");

        EditorUtility.RevealInFinder(Path.Combine(OutputDir, BundleName));
    }

    private static IEnumerable<string> FindAssets(string filter, string[] folders)
    {
        var existing = folders.Where(AssetDatabase.IsValidFolder).ToArray();
        if (existing.Length == 0) return Enumerable.Empty<string>();
        return AssetDatabase.FindAssets(filter, existing).Select(AssetDatabase.GUIDToAssetPath).Distinct();
    }
}
