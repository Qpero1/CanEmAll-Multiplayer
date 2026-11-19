// Assets/Editor/SetupPlayerCharacter.cs
//
// Tools -> Setup Player Character does, in one click:
//   1. Sets the rigged character FBX to a Humanoid rig.
//   2. Sets every FBX in Assets/Animations (Mixamo downloads) to Humanoid, names the clip after the file,
//      loops the locomotion clips (anything with Idle/Walk/Run in its name), and keeps clips in place.
//   3. Builds Assets/Animations/PlayerAnimator.controller:
//      Base layer
//        - "Locomotion" blend tree on float "Speed": Idle (0) -> Walk (1.5) -> Run (4)
//        - "GunLocomotion" blend tree on "Speed": GunIdle (0) -> GunWalk (1.5) -> GunRun (4)   [whichever exist]
//          bool "Gun" switches between the two (the mod sets it for guns, the vacuum, the RPG, flamethrower, chainsaw)
//        - "KnifeLocomotion" on "Speed": KnifeIdle (0) -> KnifeWalk/Walk (1.5) -> KnifeRun/Run (4), bool "Knife"
//        - "Dance..." clips loop as emotes: the mod starts them and sends the player back to their stance
//        - every other clip (Jump, GunJump...) as a one-shot that returns to its stance when done
//      "Actions" layer (upper body only, legs keep walking/running underneath)
//        - "Empty" (does nothing) + the clips in UpperBodyClips as one-shots that return to Empty
//        - "<A>From<B>" clips (e.g. IdleFromPunch) are chained: Punch -> IdleFromPunch -> back.
//   4. Saves Assets/Prefabs/PlayerModel.prefab with an Animator using that controller (root motion off).
// Re-run it any time you add animations. Then Tools -> Build Mod Bundles.
//
// Mixamo download settings: Format "FBX for Unity", Skin "Without Skin", 30 fps, tick "In Place" for walk/run.
// Names the mod plays: Idle, Walk, Run, Jump, GunIdle, GunWalk, GunRun, GunJump, GunShoot, KnifeIdle, KnifeHit,
// BatHit, Punch, Point, Dance1..Dance8.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class SetupPlayerCharacter
{
    private const string CharacterFbx = "Assets/TripoModels/miner_boy_3d_model/miner_boy_3d_model.fbx";
    private const string AnimFolder = "Assets/Animations";
    private const string ControllerPath = AnimFolder + "/PlayerAnimator.controller";
    private const string MaskPath = AnimFolder + "/UpperBody.mask";
    private const string PrefabFolder = "Assets/Prefabs";
    private const string PrefabPath = PrefabFolder + "/PlayerModel.prefab";

    // Locomotion blend thresholds, in the same units the mod passes to Speed (metres/second).
    private static readonly (string keyword, float threshold)[] Locomotion =
    {
        ("idle", 0f),
        ("walk", 1.5f),
        ("run", 4f),
    };

    // Played on the upper body only (arms/head), so they work while walking or running.
    private static readonly HashSet<string> UpperBodyClips = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "GunShoot", "BatHit", "KnifeHit", "Punch", "Point" };

    private const string GunPrefix = "Gun";
    private const string KnifePrefix = "Knife";
    private const string EmotePrefix = "Dance";

    [MenuItem("Tools/Setup Player Character")]
    public static void Setup()
    {
        // ---------- 1. Character rig -> Humanoid ----------
        var charImporter = AssetImporter.GetAtPath(CharacterFbx) as ModelImporter;
        if (charImporter == null)
        {
            Fail($"Character FBX not found at {CharacterFbx}. Edit CharacterFbx at the top of SetupPlayerCharacter.cs.");
            return;
        }

        if (charImporter.animationType != ModelImporterAnimationType.Human ||
            charImporter.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
        {
            charImporter.animationType = ModelImporterAnimationType.Human;
            charImporter.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            charImporter.SaveAndReimport();
        }

        var avatar = AssetDatabase.LoadAllAssetsAtPath(CharacterFbx).OfType<Avatar>().FirstOrDefault();
        if (avatar == null || !avatar.isValid || !avatar.isHuman)
        {
            Fail("Unity couldn't map the character's bones to Humanoid. Select the FBX -> Rig tab -> Configure... and fix the red bones, then run this again.");
            return;
        }

        // ---------- 2. Animation FBXs -> Humanoid clips ----------
        EnsureFolder(AnimFolder);
        var clips = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in AssetDatabase.FindAssets("t:Model", new[] { AnimFolder }).Select(AssetDatabase.GUIDToAssetPath).Distinct())
        {
            var imp = AssetImporter.GetAtPath(path) as ModelImporter;
            if (imp == null) continue;

            string clipName = Path.GetFileNameWithoutExtension(path);

            // Each Mixamo file has its own skeleton; Humanoid retargets it onto the character's avatar.
            imp.animationType = ModelImporterAnimationType.Human;
            imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            imp.importAnimation = true;
            imp.SaveAndReimport(); // refresh defaultClipAnimations for the new rig type

            var defaults = imp.defaultClipAnimations;
            if (defaults == null || defaults.Length == 0)
            {
                Debug.LogWarning($"[SetupPlayerCharacter] No animation in {path}, skipped.");
                continue;
            }

            var c = defaults[0];
            c.name = clipName;
            c.loopTime = IsLocomotion(clipName) || clipName.StartsWith(EmotePrefix, StringComparison.OrdinalIgnoreCase);
            // Clip plays in place, mod code moves the character:
            //  - XZ NOT baked: forward motion goes to root motion, which the Animator discards (applyRootMotion off).
            //  - rotation and height baked: no spinning, jump height stays in the pose.
            c.lockRootRotation = true;      c.keepOriginalOrientation = true;
            c.lockRootHeightY = true;       c.keepOriginalPositionY = true;
            c.lockRootPositionXZ = false;   c.keepOriginalPositionXZ = true;
            imp.clipAnimations = new[] { c };
            imp.SaveAndReimport();

            var clip = AssetDatabase.LoadAllAssetsAtPath(path)
                .OfType<AnimationClip>()
                .FirstOrDefault(a => !a.name.StartsWith("__preview__"));
            if (clip != null) clips[clipName] = clip;
        }

        if (clips.Count == 0)
        {
            Fail($"No animations found in {AnimFolder}. Drop your Mixamo FBX files (Idle.fbx, Walk.fbx, Run.fbx...) in there and run this again.");
            return;
        }

        // ---------- 3. Animator Controller ----------
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) != null)
            AssetDatabase.DeleteAsset(ControllerPath);

        var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
        controller.AddParameter("Gun", AnimatorControllerParameterType.Bool);
        controller.AddParameter("Knife", AnimatorControllerParameterType.Bool);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Has(string k, string prefix) => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        var normalNames = clips.Keys.Where(k => !Has(k, GunPrefix) && !Has(k, KnifePrefix) && !Has(k, EmotePrefix)).ToList();
        var gunNames = clips.Keys.Where(k => Has(k, GunPrefix)).ToList();
        var knifeNames = clips.Keys.Where(k => Has(k, KnifePrefix)).ToList();

        var baseMachine = controller.layers[0].stateMachine;
        var normalPicked = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);
        var locomotionState = MakeLocomotion(controller, baseMachine, "Locomotion", normalNames, clips, used, "", null, normalPicked);
        var gunState = MakeLocomotion(controller, baseMachine, "GunLocomotion", gunNames, clips, used, GunPrefix, null, null);
        // knife: only an idle exists, so it walks/runs with the normal clips
        var knifeState = MakeLocomotion(controller, baseMachine, "KnifeLocomotion", knifeNames, clips, used, KnifePrefix, normalPicked, null);
        if (locomotionState != null) baseMachine.defaultState = locomotionState;

        void Stance(AnimatorState stance, string param)
        {
            if (locomotionState == null || stance == null) return;
            var to = locomotionState.AddTransition(stance);
            to.hasExitTime = false; to.hasFixedDuration = true; to.duration = 0.2f;
            to.AddCondition(AnimatorConditionMode.If, 0, param);
            var back = stance.AddTransition(locomotionState);
            back.hasExitTime = false; back.hasFixedDuration = true; back.duration = 0.2f;
            back.AddCondition(AnimatorConditionMode.IfNot, 0, param);
        }
        Stance(gunState, "Gun");
        Stance(knifeState, "Knife");

        // upper-body layer
        bool IsUpper(string name) => UpperBodyClips.Contains(name) ||
            UpperBodyClips.Any(u => name.EndsWith("From" + u, StringComparison.OrdinalIgnoreCase));

        var upperNames = clips.Keys.Where(k => !used.Contains(k) && IsUpper(k)).ToList();
        AnimatorStateMachine upperMachine = null;
        AnimatorState empty = null;
        if (upperNames.Count > 0)
        {
            var mask = MakeUpperBodyMask();
            controller.AddLayer("Actions");
            var layers = controller.layers;
            layers[1].avatarMask = mask;
            layers[1].defaultWeight = 1f;
            layers[1].blendingMode = AnimatorLayerBlendingMode.Override;
            controller.layers = layers;
            upperMachine = controller.layers[1].stateMachine;
            empty = upperMachine.AddState("Empty");
            upperMachine.defaultState = empty;
        }

        var baseShots = new Dictionary<string, AnimatorState>(StringComparer.OrdinalIgnoreCase);
        var upperShots = new Dictionary<string, AnimatorState>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in clips.Where(kv => !used.Contains(kv.Key)))
        {
            bool upper = upperMachine != null && IsUpper(kv.Key);
            var state = (upper ? upperMachine : baseMachine).AddState(kv.Key);
            state.motion = kv.Value;
            (upper ? upperShots : baseShots)[kv.Key] = state;
            if (!upper && locomotionState == null && baseMachine.defaultState == null)
                baseMachine.defaultState = state;
        }

        // Base one-shots return to their stance (GunJump -> GunLocomotion); upper ones to Empty.
        // "<X>From<Name>" follow-ups are chained in between.
        // emotes loop until the mod moves the player out of them: no automatic exit
        var emotes = baseShots.Keys.Where(k => Has(k, EmotePrefix)).ToList();
        foreach (var e in emotes) baseShots.Remove(e);
        LinkOneShots(baseShots, name =>
            Has(name, GunPrefix) && gunState != null ? gunState :
            Has(name, KnifePrefix) && knifeState != null ? knifeState : locomotionState);
        LinkOneShots(upperShots, _ => empty);

        // ---------- 4. Prefab ----------
        EnsureFolder(PrefabFolder);
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterFbx);
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
        try
        {
            var animator = instance.GetComponent<Animator>() ?? instance.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.avatar = avatar;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

            PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(instance);
        }

        AssetDatabase.SaveAssets();

        string Join(IEnumerable<string> xs) => string.Join(", ", xs.DefaultIfEmpty("none"));
        var summary = $"Locomotion: {Join(Children(locomotionState))}\n" +
                      $"Gun stance: {Join(Children(gunState))}\n" +
                      $"Knife stance: {Join(Children(knifeState))}\n" +
                      $"Emotes (loop): {Join(emotes)}\n" +
                      $"Base one-shots: {Join(baseShots.Keys)}\n" +
                      $"Upper-body moves: {Join(upperShots.Keys)}\n" +
                      $"Prefab: {PrefabPath}";
        Debug.Log("[SetupPlayerCharacter] Done.\n" + summary);
        EditorUtility.DisplayDialog("Setup Player Character", "Done.\n\n" + summary + "\n\nNow run Tools -> Build Mod Bundles.", "OK");
        Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
    }

    private static AnimatorState MakeLocomotion(AnimatorController controller, AnimatorStateMachine machine, string stateName,
        List<string> names, Dictionary<string, AnimationClip> clips, HashSet<string> used, string prefix,
        Dictionary<string, AnimationClip> fallback, Dictionary<string, AnimationClip> record)
    {
        var picked = new List<(AnimationClip clip, float threshold)>();
        foreach (var (keyword, threshold) in Locomotion)
        {
            var name = FindLocomotionClip(names, prefix + keyword, used) ?? FindLocomotionClip(names, keyword, used);
            if (name != null)
            {
                used.Add(name);
                picked.Add((clips[name], threshold));
                if (record != null) record[keyword] = clips[name];
            }
            else if (picked.Count > 0 && fallback != null && fallback.TryGetValue(keyword, out var shared))
                picked.Add((shared, threshold)); // borrow the normal walk/run
        }
        if (picked.Count == 0) return null;

        var tree = new BlendTree
        {
            name = stateName,
            blendType = BlendTreeType.Simple1D,
            blendParameter = "Speed",
            useAutomaticThresholds = false,
            hideFlags = HideFlags.HideInHierarchy,
        };
        AssetDatabase.AddObjectToAsset(tree, controller);
        foreach (var (clip, threshold) in picked) tree.AddChild(clip, threshold);
        var state = machine.AddState(stateName);
        state.motion = tree;
        return state;
    }

    private static void LinkOneShots(Dictionary<string, AnimatorState> shots, Func<string, AnimatorState> home)
    {
        foreach (var kv in shots)
        {
            var followUp = shots.FirstOrDefault(o =>
                o.Key.EndsWith("From" + kv.Key, StringComparison.OrdinalIgnoreCase)).Value;
            var target = followUp ?? home(kv.Key);
            if (target == null || target == kv.Value) continue;

            var t = kv.Value.AddTransition(target);
            t.hasExitTime = true;
            t.exitTime = followUp != null ? 0.95f : 0.85f;
            t.hasFixedDuration = true;
            t.duration = followUp != null ? 0.05f : 0.2f;
        }
    }

    /// Arms, hands, fingers and head only: legs and hips stay on the base layer.
    private static AvatarMask MakeUpperBodyMask()
    {
        if (AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath) != null) AssetDatabase.DeleteAsset(MaskPath);
        var mask = new AvatarMask();
        foreach (AvatarMaskBodyPart part in Enum.GetValues(typeof(AvatarMaskBodyPart)))
        {
            if (part == AvatarMaskBodyPart.LastBodyPart) continue;
            bool on = part == AvatarMaskBodyPart.Head ||
                      part == AvatarMaskBodyPart.LeftArm || part == AvatarMaskBodyPart.RightArm ||
                      part == AvatarMaskBodyPart.LeftFingers || part == AvatarMaskBodyPart.RightFingers ||
                      part == AvatarMaskBodyPart.LeftHandIK || part == AvatarMaskBodyPart.RightHandIK;
            mask.SetHumanoidBodyPartActive(part, on);
        }
        AssetDatabase.CreateAsset(mask, MaskPath);
        return mask;
    }

    private static IEnumerable<string> Children(AnimatorState s) =>
        s?.motion is BlendTree t ? t.children.Select(c => $"{c.motion.name}@{c.threshold}") : Enumerable.Empty<string>();

    // Exact name wins ("Idle" over "IdleFromPunch"); otherwise first name containing the keyword
    // that isn't a "...From..." transition clip.
    private static string FindLocomotionClip(IEnumerable<string> names, string keyword, HashSet<string> used)
    {
        var candidates = names.Where(n => !used.Contains(n)).ToList();
        return candidates.FirstOrDefault(n => n.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(n =>
                n.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 &&
                n.IndexOf("From", StringComparison.OrdinalIgnoreCase) < 0);
    }

    private static bool IsLocomotion(string name) =>
        FindLocomotionClip(new[] { name }, "idle", new HashSet<string>()) != null ||
        FindLocomotionClip(new[] { name }, "walk", new HashSet<string>()) != null ||
        FindLocomotionClip(new[] { name }, "run", new HashSet<string>()) != null;

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }

    private static void Fail(string message)
    {
        Debug.LogError("[SetupPlayerCharacter] " + message);
        EditorUtility.DisplayDialog("Setup Player Character", message, "OK");
    }
}
