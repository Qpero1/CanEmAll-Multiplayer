#nullable disable
// CanEmAllMPModels — add-on for Can 'Em All Multiplayer.
// Replaces the placeholder capsule of other players (CanEmAllMP.RemoteAvatar) with the animated miner
// from the canemall_mp AssetBundle, without touching CanEmAllMP.dll itself.
// Install: CanEmAllMPModels.dll + canemall_mp in BepInEx\plugins next to CanEmAllMP.dll.
// Missing bundle / any error -> the capsule stays, so the main mod always keeps working.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using CanEmAllMP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using IL2CPP = Il2CppInterop.Runtime.IL2CPP;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CanEmAllMPModels
{
    [BepInPlugin(Guid, "Can 'Em All Multiplayer - Player Models", Version)]
    [BepInDependency(CanEmAllMP.Plugin.Guid)]
    public class ModelsPlugin : BasePlugin
    {
        public const string Guid = "arkin.canemall.multiplayer.models";
        public const string Version = "1.15.0";
        private const float DefaultHeight = 2.4f;
        private const float DefaultVerticalOffset = -0.25f;
        private const int ConfigRevision = 3;

        internal static ManualLogSource L;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> YawOffset;
        internal static ConfigEntry<float> Height;
        internal static ConfigEntry<float> VerticalOffset;
        internal static ConfigEntry<bool> ShowWeapons;
        internal static ConfigEntry<UnityEngine.InputSystem.Key> TuneMenuKey;
        internal static ConfigEntry<UnityEngine.InputSystem.Key> EmoteKey;
        internal static ConfigEntry<bool> ThirdPersonEmotes;
        internal static ModelsPlugin Instance;
        internal static int ConfigVersion;
        private static FileSystemWatcher _watcher;
        private static volatile bool _reloadPending;
        private static float _reloadAt;

        private static void OnConfigFileChanged(object sender, FileSystemEventArgs e) => _reloadPending = true;

        /// Called from the main thread every avatar tick: reloads the .cfg shortly after it was saved.
        internal static void PollConfigReload()
        {
            if (!_reloadPending) return;
            if (_reloadAt == 0f) { _reloadAt = Time.unscaledTime + 0.3f; return; } // let the editor finish writing
            if (Time.unscaledTime < _reloadAt) return;
            _reloadPending = false;
            _reloadAt = 0f;
            try
            {
                // don't let the reload write the file back (that would re-trigger the watcher forever)
                bool save = Instance.Config.SaveOnConfigSet;
                Instance.Config.SaveOnConfigSet = false;
                try { Instance.Config.Reload(); }
                finally { Instance.Config.SaveOnConfigSet = save; }
                _reloadPending = false; // drop any event the reload itself caused
                ConfigVersion++;
                WeaponTuning.ReloadFromConfig();
                L.LogInfo($"Config reloaded: ModelHeight={Height.Value} ModelVerticalOffset={VerticalOffset.Value} ModelYawOffset={YawOffset.Value}");
            }
            catch (Exception e) { L.LogWarning($"Config reload failed: {e.Message}"); }
        }

        public override void Load()
        {
            L = Log;
            Enabled = Config.Bind("Visuals", "UseCharacterModel", true, "Show other players as the animated miner; false = the main mod's coloured capsule");
            YawOffset = Config.Bind("Visuals", "ModelYawOffset", 0f, "Degrees to turn the miner if it faces the wrong way (e.g. 180)");
            Height = Config.Bind("Visuals", "ModelHeight", DefaultHeight, "Height of the miner in metres");
            VerticalOffset = Config.Bind("Visuals", "ModelVerticalOffset", DefaultVerticalOffset, "Metres to move the miner up (+) or down (-) so its feet touch the floor");
            ShowWeapons = Config.Bind("Weapons", "ShowWeapons", true, "Show the weapon each player is holding in the miner's right hand");
            TuneMenuKey = Config.Bind("Weapons", "TuneMenuKey", UnityEngine.InputSystem.Key.F9, "Key that opens the weapon tuning menu");
            ThirdPersonEmotes = Config.Bind("Emotes", "ThirdPersonWhileEmoting", true, "Switch to an orbiting third-person view of your own miner while you dance or point");
            EmoteKey = Config.Bind("Emotes", "EmoteWheelKey", UnityEngine.InputSystem.Key.Q, "Hold to open the emote wheel, point at a dance, let go");
            WeaponTuning.Init(Config);

            // configs written by older versions keep their old values (BepInEx never overwrites a saved value):
            // move them onto the tuned defaults once
            var revision = Config.Bind("Internal", "ConfigRevision", 0, "Used to upgrade old settings. Don't edit.");
            if (revision.Value < ConfigRevision)
            {
                Height.Value = DefaultHeight;
                VerticalOffset.Value = DefaultVerticalOffset;
                revision.Value = ConfigRevision;
                Log.LogInfo($"Upgraded model settings to ModelHeight={DefaultHeight}, ModelVerticalOffset={DefaultVerticalOffset}");
            }
            Instance = this;

            // live tuning: edits to this .cfg apply in game within a second, no restart needed
            try
            {
                _watcher = new FileSystemWatcher(Path.GetDirectoryName(Config.ConfigFilePath), Path.GetFileName(Config.ConfigFilePath));
                _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
                _watcher.Changed += OnConfigFileChanged;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception e) { Log.LogWarning($"Live config reload unavailable: {e.Message}"); }

            var h = new Harmony(Guid);
            h.Patch(AccessTools.Constructor(typeof(RemoteAvatar), new[] { typeof(int) }),
                postfix: new HarmonyMethod(typeof(AvatarPatches), nameof(AvatarPatches.CtorPostfix)));
            h.Patch(AccessTools.Method(typeof(RemoteAvatar), nameof(RemoteAvatar.Tick)),
                postfix: new HarmonyMethod(typeof(AvatarPatches), nameof(AvatarPatches.TickPostfix)));
            // weapon tuning menu (F9) + keep the game's camera/movement/weapons still while it's open
            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<TuneMenu>();
            AddComponent<TuneMenu>();
            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<EmoteWheel>();
            AddComponent<EmoteWheel>();
            h.Patch(AccessTools.Method(typeof(Player), nameof(Player.RotateCamera)),
                prefix: new HarmonyMethod(typeof(TuneMenu), nameof(TuneMenu.BlockCamera)));
            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<SelfView>();
            AddComponent<SelfView>();
            h.Patch(AccessTools.Method(typeof(Player), nameof(Player.ReadInput)),
                prefix: new HarmonyMethod(typeof(TuneMenu), nameof(TuneMenu.BlockMovement)));
            h.Patch(AccessTools.Method(typeof(Inventory), nameof(Inventory.Update)),
                prefix: new HarmonyMethod(typeof(TuneMenu), nameof(TuneMenu.BlockWhileOpen)));
            L.LogInfo($"Player models {Version} loaded.");
        }
    }

    internal class ModelState
    {
        public GameObject Model;
        public Animator Anim;
        public Vector3 LastPos;
        public bool HasLast;
        public float Speed, JumpCooldown;
        public float RawHeight, RawFeet; // unscaled measurements
        public float GroundAdjust = 0f;  // extra correction (reserved; ModelVerticalOffset is the user knob)
        public int FitVersion = -1;      // config version the fit was applied for
        public float Age;
        public int Checks;   // diagnostics/grounding passes done
        public float Pose0;  // bone pose signature at the first check
        public Transform Hand;               // mixamorig:RightHand
        public int ShownWeapon = int.MinValue;
        public GameObject WeaponGO;
        public Quaternion WeaponAlign = Quaternion.identity; // turns the weapon's long axis to face forward
        public string WeaponType = "";
        public Transform WeaponMid;   // the node that sets how far the weapon sticks out past the hand
        public float WeaponLength;    // metres before the per-weapon scale
        public int WeaponFitVersion = -1;
        public bool WeaponFailed;
        public List<Renderer> Effects = new List<Renderer>(); // the vacuum's tornado: hidden until that player uses it
        public float EffectLevel;
        public int LastGestures = -1, LastVac; // to spot new attacks / points / vacuum throws
        public int HasGunParam = -1, HasKnifeParam = -1; // -1 unknown, 0/1 (older bundles lack them)
        public int Emote;                      // emote this miner is playing (0 none)
        public float PointUntil;               // weapon put away while pointing
        public AudioSource ShotSrc, LoopSrc;   // 3D weapon sounds at this miner
        public GameObject Corpse;              // ragdoll while this player is dead
        public List<Component> RagdollParts = new List<Component>();
        public float DeadSince;                // when this player's death arrived (ragdoll starts a moment later)
        public float NextAuto;
    }

    internal static class AvatarPatches
    {
        private const string PrefabName = "PlayerModel";
        private const float JumpUpSpeed = 2.5f; // upward m/s that counts as a jump
        private static readonly ConditionalWeakTable<RemoteAvatar, ModelState> States = new ConditionalWeakTable<RemoteAvatar, ModelState>();

        public static void CtorPostfix(RemoteAvatar __instance)
        {
            try
            {
                if (!ModelsPlugin.Enabled.Value || __instance.Root == null) return;
                var root = __instance.Root;

                var model = Bundle.Spawn(PrefabName, root.transform);
                if (model == null) return;

                // bounds are only valid while active: Root is still at the origin and not rendered yet this frame
                bool wasActive = root.activeSelf;
                root.SetActive(true);
                (float rawH, float rawFeet) m;
                try { m = MeasureModel(root, model); }
                finally { root.SetActive(wasActive); }
                var state = new ModelState { Model = model, RawHeight = m.rawH, RawFeet = m.rawFeet };
                ApplyFit(state);

                // The bundle's own URP shader doesn't run in the game's build (renders magenta), so borrow the
                // shader the main mod's capsule uses (a working game shader) before hiding the capsule.
                FixShaders(root, model);
                int avatarId = __instance.Id;
                PlayerTint.Track(model, () => Palette.IndexOfPlayer(avatarId)); // body only: no weapon yet

                // hide the capsule / head / visor the main mod built (keep them: it still rotates/tints them)
                for (int i = 0; i < root.transform.childCount; i++)
                {
                    var child = root.transform.GetChild(i).gameObject;
                    if (child.Pointer != model.Pointer) child.SetActive(false);
                }

                var anim = model.GetComponent<Animator>();
                if (anim == null) ModelsPlugin.L.LogWarning("PlayerModel has no Animator; it won't animate.");
                else
                {
                    anim.enabled = true;
                    anim.applyRootMotion = false;
                    // the default (cull when off-screen) relies on skinned bounds, which the rescale can throw off
                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    // re-bind the skeleton after reparenting/rescaling (frozen-animator fix)
                    try { anim.Rebind(); } catch (Exception e) { ModelsPlugin.L.LogWarning($"Animator.Rebind unavailable: {e.GetType().Name}"); }
                }
                state.Anim = anim;
                state.Hand = WeaponVisuals.FindHand(model);
                if (state.Hand == null) ModelsPlugin.L.LogWarning("No right hand bone found on the model; weapons won't be shown.");
                States.AddOrUpdate(__instance, state);
                ModelsPlugin.L.LogInfo($"Player {__instance.Id}: miner model in place (scale {model.transform.localScale.x:0.###}, offset {model.transform.localPosition.y:0.###}, animator {(anim != null ? "yes" : "no")})");
            }
            catch (Exception e)
            {
                ModelsPlugin.L.LogError($"Couldn't swap in the player model (keeping the capsule): {e}");
            }
        }

        internal static Shader _gameShader;
        private static readonly HashSet<IntPtr> FixedMaterials = new HashSet<IntPtr>();

        internal static void FixShaders(GameObject root, GameObject model)
        {
            if (_gameShader == null)
            {
                for (int i = 0; i < root.transform.childCount && _gameShader == null; i++)
                {
                    var child = root.transform.GetChild(i).gameObject;
                    if (child.Pointer == model.Pointer) continue;
                    var r = child.GetComponent<Renderer>();
                    var m = r != null ? r.sharedMaterial : null;
                    if (m != null && m.shader != null) _gameShader = m.shader;
                }
                if (_gameShader == null)
                {
                    // nothing to borrow from (e.g. the solo test dummy): a primitive gets the game's default shader
                    try
                    {
                        var prim = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        var pm = prim.GetComponent<Renderer>()?.sharedMaterial;
                        if (pm != null) _gameShader = pm.shader;
                        Object.Destroy(prim);
                    }
                    catch { }
                }
                if (_gameShader == null) { ModelsPlugin.L.LogWarning("No game shader found to borrow; model may render magenta."); return; }
                _gameShader.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                string n = "?";
                try { n = _gameShader.name; } catch { }
                ModelsPlugin.L.LogInfo($"Using game shader '{n}' for the player model");
            }

            var rends = model.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rends.Length; i++)
            {
                var mats = rends[i].sharedMaterials;
                for (int j = 0; j < mats.Length; j++)
                {
                    var mat = mats[j];
                    if (mat == null || !FixedMaterials.Add(mat.Pointer)) continue; // bundle materials are shared: fix once
                    int queue = mat.renderQueue;
                    mat.shader = _gameShader; // same URP property names (_BaseMap, _BaseColor, _BumpMap...) carry over
                    mat.renderQueue = queue;
                    mat.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                }
            }
        }

        /// Measure the unscaled model: its height and where its lowest point sits relative to Root.
        internal static (float height, float feet) MeasureModel(GameObject root, GameObject model)
        {
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one;

            var rends = model.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) return (0f, 0f);
            var b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            for (int i = 0; i < rends.Length; i++)
                rends[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            return (b.size.y, b.min.y - root.transform.position.y);
        }

        /// Scale/turn/place the model from the current config (re-run whenever the config file changes).
        internal static void ApplyFit(ModelState s)
        {
            if (s.Model == null) return;
            float k = s.RawHeight > 0.01f ? ModelsPlugin.Height.Value / s.RawHeight : 1f;
            s.Model.transform.localScale = Vector3.one * k;
            s.Model.transform.localRotation = Quaternion.Euler(0, ModelsPlugin.YawOffset.Value, 0);
            s.Model.transform.localPosition = new Vector3(0, -s.RawFeet * k + s.GroundAdjust + ModelsPlugin.VerticalOffset.Value, 0);
            s.FitVersion = ModelsPlugin.ConfigVersion;
        }

        // Most Animator methods are stripped from this game's build (it barely uses Animator), and anything returning a
        // Unity object goes through a stripped generic unmarshaller. Only plain value getters are used for diagnostics.
        private static string Probe(Func<string> f) { try { return f(); } catch (Exception e) { return "ERR(" + e.GetType().Name + ")"; } }

        /// Sum of all bone rotations under the model: changes over time only if the skeleton is actually animating.
        private static float PoseSignature(GameObject model)
        {
            var ts = model.GetComponentsInChildren<Transform>(true);
            float sum = 0f;
            for (int i = 0; i < ts.Length; i++)
            {
                var q = ts[i].localRotation;
                sum += q.x * 1.3f + q.y * 1.7f + q.z * 2.3f + q.w * 0.7f;
            }
            return sum;
        }

        /// Lowest point of the skinned mesh in world space (renderer bounds follow the animated pose).
        private static float LowestPoint(GameObject model)
        {
            var rends = model.GetComponentsInChildren<Renderer>(true);
            float lowest = float.MaxValue;
            for (int i = 0; i < rends.Length; i++) lowest = Mathf.Min(lowest, rends[i].bounds.min.y);
            return lowest;
        }

        /// Called at 0.5 s, 1.5 s and 3 s after the model appears: grounds it once and reports whether bones move.
        private static void GroundAndReport(int id, ModelState s, GameObject root, int stage)
        {
            var a = s.Anim;
            if (stage == 0)
            {
                float lowest = LowestPoint(s.Model);
                if (lowest < float.MaxValue)
                    ModelsPlugin.L.LogInfo($"Player {id}: lowest point of the mesh is {lowest - root.transform.position.y:0.###} m from the player's feet " +
                                           $"(ModelHeight={ModelsPlugin.Height.Value}, ModelVerticalOffset={ModelsPlugin.VerticalOffset.Value})");
                s.Pose0 = PoseSignature(s.Model);
            }

            float pose = PoseSignature(s.Model);
            string moving = stage == 0 ? "-" : (Mathf.Abs(pose - s.Pose0) > 0.001f ? "YES" : "no");
            ModelsPlugin.L.LogInfo($"Player {id} anim check {stage}: bones moving={moving} " +
                $"enabled={Probe(() => a.isActiveAndEnabled.ToString())} initialized={Probe(() => a.isInitialized.ToString())} " +
                $"human={Probe(() => a.isHuman.ToString())} playables={Probe(() => a.hasBoundPlayables.ToString())} " +
                $"speedParam={s.Speed:0.##} animSpeed={Probe(() => a.speed.ToString("0.##"))} " +
                $"state={Probe(() => a.GetCurrentAnimatorStateInfo(0).fullPathHash.ToString())} " +
                $"t={Probe(() => a.GetCurrentAnimatorStateInfo(0).normalizedTime.ToString("0.##"))} timeScale={Time.timeScale}");
        }

        private static void UpdateWeapon(RemoteAvatar av, ModelState s)
        {
            if (av.Root == null) return;
            int want = !ModelsPlugin.ShowWeapons.Value || s.Hand == null ? -1
                     : TuneMenu.PreviewWeapon >= 0 ? TuneMenu.PreviewWeapon
                     : av.Emote > 0 || Time.time < s.PointUntil ? -1 // dancing / pointing: weapon put away
                     : av.Weapon;
            int action = TuneMenu.PreviewWeapon >= 0 && TuneMenu.PreviewEffect ? 1 : av.Action;
            WeaponVisuals.Show(s, want, av.Root.transform, action);
        }

        public static void TickPostfix(RemoteAvatar __instance)
        {
            ModelsPlugin.PollConfigReload();
            if (!States.TryGetValue(__instance, out var s)) return;
            if (s.FitVersion != ModelsPlugin.ConfigVersion) ApplyFit(s); // config file edited while playing
            if (__instance.Dead)
            {
                // died: weapon and sounds off; follow their (knocked back) body for a moment, then go limp with
                // its velocity and stay where it fell
                var rootT = __instance.Root.transform;
                if (s.Corpse == null)
                {
                    if (s.DeadSince <= 0f) { s.DeadSince = Time.time; s.LastPos = rootT.position; WeaponVisuals.Show(s, -1, rootT); WeaponSounds.Tick(s, -1, 0); }
                    var deadDt = Mathf.Max(Time.deltaTime, 0.001f);
                    var vel = (rootT.position - s.LastPos) / deadDt;
                    s.LastPos = rootT.position;
                    if (Time.time - s.DeadSince >= 0.15f) Ragdoll.Enter(s, vel.sqrMagnitude < 900f ? vel : Vector3.zero, __instance.Id);
                }
                return;
            }
            s.DeadSince = 0f;
            if (s.Corpse != null) { Ragdoll.Exit(s, __instance.Root.transform); s.HasLast = false; } // back for the new day
            UpdateWeapon(__instance, s);
            if (s.Anim == null) return;
            var root = __instance.Root;
            if (root == null || !root.activeSelf) { s.HasLast = false; return; }

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            s.Age += dt;
            float[] checkAt = { 0.5f, 1.5f, 3f };
            if (s.Checks < checkAt.Length && s.Age > checkAt[s.Checks])
            {
                try { GroundAndReport(__instance.Id, s, root, s.Checks); }
                catch (Exception e) { ModelsPlugin.L.LogWarning($"Grounding/diagnostics failed: {e}"); }
                s.Checks++;
            }
            var p = root.transform.position;
            if (!s.HasLast) { s.LastPos = p; s.HasLast = true; return; }
            var d = p - s.LastPos;
            s.LastPos = p;

            float horiz = new Vector2(d.x, d.z).magnitude / dt;
            float vert = d.y / dt;
            if (horiz > 15f) horiz = 0f; // teleport (respawn, elevator snap)
            s.Speed = Mathf.Lerp(s.Speed, horiz, 1f - Mathf.Exp(-10f * dt));

            try
            {
                s.Anim.SetFloat(AnimHash.Speed, s.Speed);
                int weapon = TuneMenu.PreviewWeapon >= 0 ? TuneMenu.PreviewWeapon : __instance.Weapon;
                string type = Moves.TypeOf(weapon);
                bool gun = Moves.SetGunPose(s, type);
                s.JumpCooldown -= dt;
                if (vert > JumpUpSpeed && vert < 20f && s.JumpCooldown <= 0f)
                {
                    if (!(gun && Moves.Play(s, AnimHash.GunJump, 0)))
                        s.Anim.CrossFade(AnimHash.Jump, 0.1f);
                    s.JumpCooldown = 1f;
                }
                Moves.Emote(s, __instance.Emote, type);
                if (s.Emote == 0) Moves.Actions(s, weapon, type, gun, __instance.Action, __instance.Gestures);
                bool away = s.Emote > 0 || Time.time < s.PointUntil;
                WeaponSounds.Tick(s, away ? -1 : weapon, away ? 0 : __instance.Action);
            }
            catch (Exception e)
            {
                ModelsPlugin.L.LogError($"Animator error, animation off for this player: {e}");
                s.Anim = null;
            }
        }
    }

    /// Loads canemall_mp (next to this DLL) on first use.
    /// Unity 6 + Il2CppInterop: engine calls that take a string (LoadFromFile, LoadAsset(name), Object.name=...)
    /// throw MissingMethodException (ReadOnlySpan.GetPinnableReference), so only string-free calls are used here:
    /// LoadFromStream on an IL2CPP MemoryStream, and LoadAllAssets(type).
    /// <summary>
    /// The weapon in each remote player's hand: a copy of just the meshes of the game's own weapon (no scripts),
    /// with copies of its materials switched to the game's Standard shader (its first-person shaders draw on top of
    /// everything), sized per weapon type, pointing the way the player faces and held at the right hand.
    /// </summary>
    internal static class WeaponVisuals
    {
        // longest side in metres, by weapon class
        private static readonly Dictionary<string, float> Lengths = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            { "Pistol", 0.25f }, { "Knife", 0.3f }, { "SMG", 0.5f }, { "Shotgun", 0.95f }, { "AK47", 0.9f },
            { "RocketLauncher", 1.1f }, { "BaseballBat", 0.85f }, { "Chainsaw", 0.8f }, { "FlameThrower", 0.9f }, { "Vacuum", 0.8f },
        };
        private static readonly HashSet<int> Logged = new HashSet<int>();
        private static readonly Dictionary<IntPtr, Material> MatCopies = new Dictionary<IntPtr, Material>();

        public static Transform FindHand(GameObject model)
        {
            var ts = model.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < ts.Length; i++)
            {
                string n = null;
                try { n = ts[i].name; } catch { }
                if (n != null && n.EndsWith("RightHand", StringComparison.OrdinalIgnoreCase)) return ts[i];
            }
            return null;
        }

        private static string TypeName(Weapon w)
        {
            try { return w.GetIl2CppType().Name; } catch { return "?"; }
        }

        public static Vector3 ParseVec(string s, Vector3 fallback)
        {
            try
            {
                var p = s.Split(',');
                if (p.Length != 3) return fallback;
                var c = System.Globalization.CultureInfo.InvariantCulture;
                return new Vector3(float.Parse(p[0].Trim(), c), float.Parse(p[1].Trim(), c), float.Parse(p[2].Trim(), c));
            }
            catch { return fallback; }
        }

        /// A copy of the material on the game's Standard shader (textures/colours carry over), made once per material.
        private static Material ThirdPersonMaterial(Material m)
        {
            if (m == null) return null;
            if (MatCopies.TryGetValue(m.Pointer, out var copy) && copy != null) return copy;
            copy = new Material(m);
            var sh = AvatarPatches._gameShader;
            if (sh != null) { copy.shader = sh; }
            copy.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            MatCopies[m.Pointer] = copy;
            return copy;
        }

        private static bool IsTornado(Material m)
        {
            try { return (m?.shader?.name ?? "").IndexOf("Tornado", StringComparison.OrdinalIgnoreCase) >= 0; } catch { return false; }
        }

        /// Own copy per held weapon (so each player's tornado has its own intensity), on the game's own shader.
        private static Material TornadoMaterial(Material m)
        {
            if (m == null) return null;
            var copy = new Material(m);
            if (!_loggedTornado)
            {
                _loggedTornado = true;
                try
                {
                    var sh = m.shader; var names = new List<string>();
                    for (int i = 0; i < sh.GetPropertyCount(); i++) names.Add(sh.GetPropertyName(i));
                    ModelsPlugin.L.LogInfo($"Tornado shader '{sh.name}' properties: {string.Join(", ", names)}; intensity id {IntensityId}");
                }
                catch { }
            }
            return copy;
        }
        private static bool _loggedTornado;

        private static int _intensityId = int.MinValue;
        private static int IntensityId
        {
            get
            {
                if (_intensityId == int.MinValue)
                {
                    try { _intensityId = TornadoRotationMotion.IntensityID; } catch { _intensityId = -1; }
                }
                return _intensityId;
            }
        }

        /// How strong the game draws its own tornado (falls back to 1).
        private static float FullIntensity()
        {
            try
            {
                var t = Vacuum.Instance?.tornado;
                if (t != null && t.intensity > 0.01f) return t.intensity;
            }
            catch { }
            return 1f;
        }

        /// Standard-shader copy in fade mode, mostly see-through.
        private static Material GlassMaterial(Material m)
        {
            if (m == null) return null;
            if (MatCopies.TryGetValue(m.Pointer, out var copy) && copy != null) return copy;
            copy = new Material(m);
            var sh = AvatarPatches._gameShader;
            if (sh != null) copy.shader = sh;
            MakeFade(copy, 0.2f);
            copy.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            MatCopies[m.Pointer] = copy;
            return copy;
        }

        // Standard shader "Fade" mode, set without string APIs (they throw in this game): ids are fixed by the Standard shader.
        private static void MakeFade(Material mat, float alpha)
        {
            try
            {
                var sh = mat.shader;
                for (int i = 0; i < sh.GetPropertyCount(); i++)
                {
                    string n = sh.GetPropertyName(i);
                    int id = sh.GetPropertyNameId(i);
                    switch (n)
                    {
                        case "_Mode": mat.SetFloat(id, 2f); break;
                        case "_SrcBlend": mat.SetFloat(id, (float)UnityEngine.Rendering.BlendMode.SrcAlpha); break;
                        case "_DstBlend": mat.SetFloat(id, (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha); break;
                        case "_ZWrite": mat.SetFloat(id, 0f); break;
                        case "_Color": { var c = mat.GetColor(id); c.a = alpha; mat.SetColor(id, c); break; }
                    }
                }
            }
            catch { }
            try
            {
                // no string-taking keyword APIs work here, so find the keyword by walking the shader's list
                var kws = mat.shader.keywordSpace.keywords;
                for (int i = 0; i < kws.Length; i++)
                    if (kws[i].name == "_ALPHABLEND_ON") { var kw = kws[i]; mat.SetKeyword(ref kw, true); break; }
            }
            catch { }
            mat.renderQueue = 3000;
        }

        private static bool IsEffectMaterial(Material m)
        {
            try
            {
                var n = m?.shader?.name ?? "";
                return n.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Tornado", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// Builds the copy; returns its holder (+Z = the weapon's forward once WeaponAlign is applied).
        public static GameObject Build(int weaponIndex, out Quaternion align, out string type, out float length, List<Renderer> effects)
        {
            effects.Clear();
            align = Quaternion.identity;
            type = "?";
            length = 0.7f;
            var w = GameApi.WeaponAt(weaponIndex);
            if (w == null) return null;
            type = TypeName(w);
            var root = w.transform;

            var holder = new GameObject("MP_HeldWeapon");
            var content = new GameObject("MP_WeaponMesh");
            content.transform.SetParent(holder.transform, false);

            var rends = FindRenderers(w, type, weaponIndex);
            int parts = 0, skipped = 0;
            var shaders = new HashSet<string>();
            for (int i = 0; i < rends.Length; i++)
            {
                var r = rends[i];
                Mesh mesh = null;
                try
                {
                    var mr = r.TryCast<MeshRenderer>();
                    if (mr != null) mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
                    else { var smr = r.TryCast<SkinnedMeshRenderer>(); if (smr != null) mesh = smr.sharedMesh; }
                }
                catch { }
                if (mesh == null) continue; // particles, trails, lines...

                Il2CppReferenceArray<Material> mats = null;
                try { mats = r.sharedMaterials; } catch { }
                if (mats == null || mats.Length == 0) continue;
                bool tornado = false;
                foreach (var m in mats) { try { if (m?.shader != null) shaders.Add(m.shader.name); } catch { } if (IsTornado(m)) tornado = true; }

                var t = r.transform;
                var go = new GameObject("part");
                go.transform.SetParent(content.transform, false);
                go.transform.localPosition = root.InverseTransformPoint(t.position);
                go.transform.localRotation = Quaternion.Inverse(root.rotation) * t.rotation;
                Vector3 rs = root.lossyScale, ls = t.lossyScale;
                go.transform.localScale = new Vector3(ls.x / Nz(rs.x), ls.y / Nz(rs.y), ls.z / Nz(rs.z));
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var nr = go.AddComponent<MeshRenderer>();
                var copies = new Il2CppReferenceArray<Material>(mats.Length);
                for (int j = 0; j < mats.Length; j++)
                    copies[j] = tornado ? TornadoMaterial(mats[j])          // keeps the game's tornado shader, driven per player
                              : IsEffectMaterial(mats[j]) ? GlassMaterial(mats[j]) // see-through parts (the vacuum's glass)
                              : ThirdPersonMaterial(mats[j]);
                try { nr.sharedMaterials = copies; } catch { try { nr.sharedMaterial = copies[0]; } catch { } }
                nr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                if (tornado) { effects.Add(nr); skipped++; }
                parts++;
            }
            if (parts == 0)
            {
                if (Logged.Add(weaponIndex)) DumpHierarchy(w, type, weaponIndex);
                Object.Destroy(holder);
                if (Logged.Add(weaponIndex)) ModelsPlugin.L.LogInfo($"Weapon #{weaponIndex} ({type}): no meshes to show");
                return null;
            }

            // normalise size (first-person models aren't at world scale) and centre it on the holder
            var all = content.GetComponentsInChildren<Renderer>(true);
            var b = all[0].bounds;
            for (int i = 1; i < all.Length; i++) b.Encapsulate(all[i].bounds);
            float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            float want = Lengths.TryGetValue(type, out var len) ? len : 0.7f;
            float k = longest > 1e-4f ? want / longest : 1f;
            content.transform.localScale = Vector3.one * k;
            content.transform.localPosition = -b.center * k;

            // turn its longest side to +Z (forward); the hand then holds it behind its middle
            if (b.size.x >= b.size.y && b.size.x >= b.size.z) align = Quaternion.Euler(0, -90, 0);
            else if (b.size.y >= b.size.z) align = Quaternion.Euler(90, 0, 0);
            var mid = new GameObject("MP_WeaponAlign");
            mid.transform.SetParent(holder.transform, false);
            content.transform.SetParent(mid.transform, false);
            mid.transform.localRotation = align;
            length = want; // how far it sticks out past the hand is applied live in Place
            foreach (var e in effects) e.enabled = false; // measured with it (keeps the tuned sizes), shown only while in use

            if (Logged.Add(weaponIndex))
                ModelsPlugin.L.LogInfo($"Weapon #{weaponIndex} ({type}): {parts} part(s){(skipped > 0 ? $" ({skipped} of them shown only while in use)" : "")}, " +
                                       $"raw size {b.size.x:0.###}x{b.size.y:0.###}x{b.size.z:0.###}, scaled to {want:0.##} m, shaders: {string.Join(", ", shaders)}");
            return holder;
        }

        /// The weapon's own renderers; if it has none (its model lives elsewhere in the first-person rig),
        /// look through the rig for objects named after the weapon.
        private static Il2CppArrayBase<Renderer> FindRenderers(Weapon w, string type, int index)
        {
            var own = w.GetComponentsInChildren<Renderer>(true);
            if (CountMeshes(own) > 0) return own;
            string key = type.Length > 4 ? type.Substring(0, 4) : type; // "Vacu"
            Transform t = w.transform.parent;
            for (int up = 0; up < 4 && t != null; up++, t = t.parent)
            {
                var cands = t.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < cands.Length; i++)
                {
                    string n = null;
                    try { n = cands[i].name; } catch { }
                    if (n == null || n.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (cands[i].GetComponentInParent<Weapon>() is Weapon other && other.Pointer != w.Pointer) continue;
                    var r = cands[i].GetComponentsInChildren<Renderer>(true);
                    if (CountMeshes(r) > 0)
                    {
                        if (Logged.Add(-1000 - index)) ModelsPlugin.L.LogInfo($"Weapon #{index} ({type}): using renderers under '{n}' ({up + 1} level(s) up)");
                        return r;
                    }
                }
            }
            return own;
        }

        private static int CountMeshes(Il2CppArrayBase<Renderer> rs)
        {
            int n = 0;
            for (int i = 0; i < rs.Length; i++)
            {
                try
                {
                    var r = rs[i];
                    if (r.TryCast<SkinnedMeshRenderer>() is SkinnedMeshRenderer smr ? smr.sharedMesh != null : r.GetComponent<MeshFilter>()?.sharedMesh != null) n++;
                }
                catch { }
            }
            return n;
        }

        /// One-off log of what a weapon is made of, to find where a missing model lives.
        private static void DumpHierarchy(Weapon w, string type, int index)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"Weapon #{index} ({type}): no meshes to show. Hierarchy:");
                void Walk(Transform t, int depth)
                {
                    if (depth > 6) return;
                    sb.Append('\n').Append(' ', depth * 2).Append(t.name);
                    try
                    {
                        var comps = t.GetComponents<Component>();
                        for (int i = 0; i < comps.Length; i++)
                        {
                            var c = comps[i]; if (c == null) continue;
                            string cn = c.GetIl2CppType().Name;
                            if (cn == "Transform") continue;
                            sb.Append(" [").Append(cn);
                            var r = c.TryCast<Renderer>();
                            if (r != null) { try { var m = r.sharedMaterial; sb.Append(": ").Append(m != null ? m.shader.name : "no material"); } catch { } }
                            sb.Append(']');
                        }
                    }
                    catch { }
                    for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1);
                }
                Walk(w.transform, 0);
                var p = w.transform.parent;
                if (p != null)
                {
                    sb.Append("\nSiblings under '").Append(p.name).Append("':");
                    for (int i = 0; i < p.childCount; i++) sb.Append(' ').Append(p.GetChild(i).name).Append(';');
                }
                ModelsPlugin.L.LogInfo(sb.ToString());
            }
            catch (Exception e) { ModelsPlugin.L.LogInfo($"Weapon #{index} ({type}): no meshes, dump failed: {e.Message}"); }
        }

        /// Fade the tornado in while that player is sucking/throwing, out when they stop.
        private static void DriveEffects(ModelState s, int action)
        {
            if (s.Effects.Count == 0) return;
            float target = (action & 3) == 1 ? 1f : 0f; // sucking only: throwing (shooting) has no tornado
            s.EffectLevel = Mathf.MoveTowards(s.EffectLevel, target, Time.deltaTime * 4f);
            bool on = s.EffectLevel > 0.01f;
            float intensity = s.EffectLevel * FullIntensity();
            int id = IntensityId;
            foreach (var r in s.Effects)
            {
                if (r == null) continue;
                if (r.enabled != on) r.enabled = on;
                if (on && id != -1) { try { r.sharedMaterial.SetFloat(id, intensity); } catch { } }
            }
        }

        private static float Nz(float v) => Mathf.Abs(v) < 1e-6f ? 1f : v;

        /// Every frame: at the right hand, pointing the way the player faces, adjusted by that weapon's tuning (F9 menu).
        public static void Place(ModelState s, Transform body)
        {
            var t = WeaponTuning.Get(s.WeaponType);
            var facing = body.rotation;
            var holder = s.WeaponGO.transform;
            holder.position = s.Hand.position + facing * t.Offset;
            holder.rotation = facing * Quaternion.Euler(t.Rotation);
            holder.localScale = Vector3.one * Mathf.Max(0.01f, t.Scale);
            if (s.WeaponMid != null) s.WeaponMid.localPosition = new Vector3(0, 0, s.WeaponLength * t.Forward);
        }

        /// Show weapon #want (or nothing) on a model; shared by remote players and the tuning dummy.
        public static void Show(ModelState s, int want, Transform body, int action = 0)
        {
            if (s.WeaponFitVersion != ModelsPlugin.ConfigVersion) { s.WeaponFitVersion = ModelsPlugin.ConfigVersion; s.ShownWeapon = int.MinValue; } // config edited: rebuild
            if (want != s.ShownWeapon)
            {
                if (s.WeaponGO != null) { Object.Destroy(s.WeaponGO); s.WeaponGO = null; s.WeaponMid = null; }
                s.ShownWeapon = want;
                if (want >= 0)
                {
                    try
                    {
                        s.WeaponGO = Build(want, out s.WeaponAlign, out s.WeaponType, out s.WeaponLength, s.Effects);
                        s.EffectLevel = 0f;
                        if (s.WeaponGO != null)
                        {
                            s.WeaponGO.transform.SetParent(body, false);
                            s.WeaponMid = s.WeaponGO.transform.childCount > 0 ? s.WeaponGO.transform.GetChild(0) : null;
                        }
                    }
                    catch (Exception e)
                    {
                        if (!s.WeaponFailed) ModelsPlugin.L.LogError($"Showing weapon #{want} failed: {e}");
                        s.WeaponFailed = true;
                    }
                }
            }
            if (s.WeaponGO != null && s.Hand != null)
            {
                try { Place(s, body); } catch { }
                try { DriveEffects(s, action); } catch { }
            }
        }
    }

    internal static class Bundle
    {
        private const string FileName = "canemall_mp";
        // The game's IL2CPP build stripped the AssetBundle class itself, so the bundle is held as a plain
        // UnityEngine.Object and only its native pointer is used with the static *_Injected bindings.
        private static UnityEngine.Object _bundle;
        private static IntPtr _bundleNative;
        private static Il2CppSystem.IO.MemoryStream _stream; // the bundle reads from this for its whole lifetime
        private static bool _tried;
        private static readonly Dictionary<string, GameObject> Prefabs = new Dictionary<string, GameObject>();

        private static bool EnsureLoaded()
        {
            if (_bundle != null) return true;
            if (_tried) return false;
            _tried = true;

            string path = Path.Combine(Path.GetDirectoryName(typeof(Bundle).Assembly.Location) ?? "", FileName);
            if (!File.Exists(path)) { ModelsPlugin.L.LogWarning($"{path} not found; other players stay capsules."); return false; }

            try
            {
                var bytes = File.ReadAllBytes(path);
                _stream = new Il2CppSystem.IO.MemoryStream(new Il2CppStructArray<byte>(bytes));
                // AssetBundle.LoadFromStream -> Unmarshal.UnmarshalUnityObject<AssetBundle>, which IL2CPP stripped
                // (the game never loads bundles). Call the native binding and unwrap the GC handle ourselves.
                IntPtr handle = AssetBundle.LoadFromStreamInternal_Injected(_stream, 0u, 0u);
                _bundle = FromHandle(handle, p => new Il2CppSystem.Object(p).Cast<UnityEngine.Object>());
                if (_bundle != null) _bundleNative = _bundle.GetCachedPtr();
            }
            catch (Exception e) { ModelsPlugin.L.LogError($"Loading the bundle threw: {e}"); return false; }
            if (_bundle == null || _bundleNative == IntPtr.Zero)
            {
                ModelsPlugin.L.LogError("Bundle load returned nothing (built with another Unity version, or corrupt?)");
                _bundle = null;
                return false;
            }
            ModelsPlugin.L.LogInfo("canemall_mp loaded.");

            try { _bundle.hideFlags |= HideFlags.DontUnloadUnusedAsset; } catch { }
            try { foreach (var n in AssetBundle.GetAllAssetNames_Injected(_bundleNative)) ModelsPlugin.L.LogInfo($"bundle: {n}"); }
            catch (Exception e) { ModelsPlugin.L.LogWarning($"Couldn't list bundle contents: {e.Message}"); }
            return true;
        }

        /// Unity 6 bindings return engine objects as a GCHandle; resolve it to the IL2CPP object and wrap it.
        private static T FromHandle<T>(IntPtr handle, Func<IntPtr, T> wrap) where T : class
        {
            if (handle == IntPtr.Zero) return null;
            IntPtr obj = IL2CPP.il2cpp_gchandle_get_target(handle);
            if (obj == IntPtr.Zero) return null;
            // NOTE: don't free the handle - it's the object's own persistent handle (the game's Unmarshal only reads it).
            // Freeing it let the GC reuse the object's memory and corrupted engine objects.
            return wrap(obj);
        }

        private static GameObject FindPrefab(string name)
        {
            // LoadAllAssets(type) == LoadAssetWithSubAssets_Internal("", type); call the binding with an empty name span
            var emptyName = default(UnityEngine.Bindings.ManagedSpanWrapper);
            var all = AssetBundle.LoadAssetWithSubAssets_Internal_Injected(_bundleNative, ref emptyName, Il2CppType.Of<GameObject>());
            if (all == null) { ModelsPlugin.L.LogWarning("Bundle returned no GameObjects"); return null; }
            ModelsPlugin.L.LogInfo($"Bundle has {all.Length} GameObject(s)");
            GameObject withAnimator = null, first = null;
            for (int i = 0; i < all.Length; i++)
            {
                var go = all[i]?.TryCast<GameObject>();
                if (go == null) continue;
                first ??= go;
                string n = null;
                try { n = go.name; } catch { }
                if (n != null && n.Equals(name, StringComparison.OrdinalIgnoreCase)) return go;
                if (withAnimator == null && go.GetComponent<Animator>() != null) withAnimator = go;
            }
            return withAnimator ?? first;
        }

        public static GameObject Spawn(string name, Transform parent)
        {
            if (!EnsureLoaded()) return null;
            if (!Prefabs.TryGetValue(name, out var prefab) || prefab == null)
            {
                try { prefab = FindPrefab(name); }
                catch (Exception e) { ModelsPlugin.L.LogError($"Loading '{name}' from the bundle threw: {e}"); return null; }
                if (prefab == null) { ModelsPlugin.L.LogWarning($"no GameObject in bundle for '{name}'"); return null; }
                prefab.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                Prefabs[name] = prefab;
            }
            return Object.Instantiate((Object)prefab, parent).Cast<GameObject>();
        }
    }

    /// Animator parameter/state hashes without string engine calls where possible.
    /// Weapon stances and one-shot moves for other players' miners (animator built by SetupPlayerCharacter.cs:
    /// base layer Locomotion / GunLocomotion on bool "Gun", upper-body layer 1 "Actions" with Empty + one-shots).
    internal static class Moves
    {
        // held up in front with both hands (gun idle/run/jump, shoot)
        private static readonly HashSet<string> GunTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Pistol", "SMG", "AK47", "Shotgun", "RocketLauncher", "FlameThrower", "Vacuum", "Chainsaw" };
        private static readonly HashSet<string> MeleeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "BaseballBat", "Knife" };
        // keep re-playing the shoot move while the trigger is held
        private static readonly HashSet<string> AutoTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "SMG", "AK47", "FlameThrower" };
        private static readonly Dictionary<int, string> Types = new Dictionary<int, string>();

        public static string TypeOf(int index)
        {
            if (index < 0) return "";
            if (Types.TryGetValue(index, out var t)) return t;
            try { var w = GameApi.WeaponAt(index); t = w != null ? w.GetIl2CppType().Name : ""; } catch { t = ""; }
            if (t != "") Types[index] = t;
            return t;
        }

        public static bool IsGun(string type) => GunTypes.Contains(type ?? "");

        public static bool IsKnife(string type) => string.Equals(type, "Knife", StringComparison.OrdinalIgnoreCase);

        /// Sets the "Gun" / "Knife" stance; returns whether it's a gun.
        public static bool SetGunPose(ModelState s, string type)
        {
            bool gun = IsGun(type), knife = IsKnife(type);
            var a = s.Anim;
            if (a == null) return gun;
            if (s.HasGunParam < 0)
            {
                // Animator.parameters can't be read through Il2CppInterop (TypeLoadException), but the
                // setup script only adds the "Gun"/"Knife" bools together with their stance states
                s.HasGunParam = SafeHasState(a, 0, AnimHash.GunLocomotion) ? 1 : 0;
                s.HasKnifeParam = SafeHasState(a, 0, AnimHash.KnifeLocomotion) ? 1 : 0;
            }
            if (s.HasGunParam == 1) a.SetBool(AnimHash.Gun, gun);
            if (s.HasKnifeParam == 1) a.SetBool(AnimHash.Knife, knife);
            return gun;
        }

        private static bool SafeHasState(Animator a, int layer, int hash)
        {
            try { return layer < a.layerCount && a.HasState(layer, hash); } catch { return false; }
        }

        /// The base-layer state for the stance this weapon is held in.
        public static int StanceState(string type) =>
            IsGun(type) ? AnimHash.GunLocomotion : IsKnife(type) ? AnimHash.KnifeLocomotion : AnimHash.Locomotion;

        /// Start/stop another player's emote (DanceN, looping on the base layer).
        public static void Emote(ModelState s, int emote, string type)
        {
            if (emote == s.Emote || s.Anim == null) return;
            s.Emote = emote;
            var a = s.Anim;
            if (emote > 0)
            {
                int hash = AnimHash.Dance(emote);
                if (a.HasState(0, hash)) a.CrossFade(hash, 0.25f, 0, 0f);
            }
            else
            {
                int home = StanceState(type);
                if (!a.HasState(0, home)) home = AnimHash.Locomotion;
                a.CrossFade(home, 0.25f, 0);
            }
        }

        /// Plays a state if the bundle has it: the preferred layer first, then the other. Restarts it if already playing.
        public static bool Play(ModelState s, int hash, int preferLayer = 1)
        {
            var a = s.Anim;
            if (a == null) return false;
            for (int pass = 0; pass < 2; pass++)
            {
                int layer = pass == 0 ? preferLayer : (preferLayer == 1 ? 0 : 1);
                if (layer >= a.layerCount || !a.HasState(layer, hash)) continue;
                a.CrossFade(hash, layer == 0 ? 0.1f : 0.05f, layer, 0f);
                return true;
            }
            return false;
        }

        private static bool UpperBusy(ModelState s)
        {
            var a = s.Anim;
            if (a.layerCount < 2) return false;
            return a.IsInTransition(1) || a.GetCurrentAnimatorStateInfo(1).shortNameHash != AnimHash.Empty;
        }

        public static void Actions(ModelState s, int weapon, string type, bool gun, int action, int gestures)
        {
            int vac = action & 3;
            if (s.LastGestures < 0) { s.LastGestures = gestures; s.LastVac = vac; return; }
            int attacks = (gestures - s.LastGestures) & 15;
            int points = ((gestures >> 4) - (s.LastGestures >> 4)) & 15;
            s.LastGestures = gestures;
            bool vacThrow = vac == 2 && s.LastVac != 2;
            s.LastVac = vac;

            if (attacks > 0) s.PointUntil = 0f; // attacking ends a point (weapon back out)
            if (points > 0 && attacks == 0)
            {
                // points with whatever is equipped: the weapon is put away for the point
                if (Play(s, AnimHash.Point)) s.PointUntil = Time.time + CanEmAllMP.Gestures.PointSeconds;
                return;
            }
            if (attacks > 0 && weapon >= 0 && !type.Equals("Vacuum", StringComparison.OrdinalIgnoreCase)
                && !type.Equals("FlameThrower", StringComparison.OrdinalIgnoreCase) && !type.Equals("Chainsaw", StringComparison.OrdinalIgnoreCase))
                WeaponSounds.Attack(s, weapon);
            if (vacThrow) WeaponSounds.Attack(s, weapon, vacThrow: true);
            if (weapon < 0) // empty hands
            {
                if (attacks > 0) Play(s, AnimHash.Punch);
            }
            else if (type.Equals("Vacuum", StringComparison.OrdinalIgnoreCase))
            {
                if (vacThrow) Play(s, AnimHash.GunShoot); // left click sucks; shooting out is the throw
            }
            else if (IsKnife(type))
            {
                if (attacks > 0 && !Play(s, AnimHash.KnifeHit)) Play(s, AnimHash.BatHit);
            }
            else if (MeleeTypes.Contains(type))
            {
                if (attacks > 0) Play(s, AnimHash.BatHit);
            }
            else if (type.Equals("Chainsaw", StringComparison.OrdinalIgnoreCase))
            {
                // gun stance only: a recoil doesn't suit it
            }
            else if (gun)
            {
                if (attacks > 0) Play(s, AnimHash.GunShoot);
                else if ((action & 4) != 0 && AutoTypes.Contains(type) && !UpperBusy(s)) Play(s, AnimHash.GunShoot);
            }
        }
    }

    internal static class AnimHash
    {
        public static readonly int Speed = Hash("Speed");
        public static readonly int Jump = Hash("Jump");
        public static readonly int Gun = Hash("Gun");
        public static readonly int GunJump = Hash("GunJump");
        public static readonly int GunShoot = Hash("GunShoot");
        public static readonly int BatHit = Hash("BatHit");
        public static readonly int Punch = Hash("Punch");
        public static readonly int Point = Hash("Point");
        public static readonly int Empty = Hash("Empty");
        public static readonly int Knife = Hash("Knife");
        public static readonly int KnifeHit = Hash("KnifeHit");
        public static readonly int Locomotion = Hash("Locomotion");
        public static readonly int GunLocomotion = Hash("GunLocomotion");
        public static readonly int KnifeLocomotion = Hash("KnifeLocomotion");
        private static readonly int[] Dances = { Hash("Dance1"), Hash("Dance2"), Hash("Dance3"), Hash("Dance4"), Hash("Dance5"), Hash("Dance6"), Hash("Dance7"), Hash("Dance8") };
        public static int Dance(int n) => n >= 1 && n <= Dances.Length ? Dances[n - 1] : 0;

        private static int Hash(string s)
        {
            int crc = Crc32(s);
            try
            {
                int unity = Animator.StringToHash(s);
                if (unity != crc) ModelsPlugin.L.LogWarning($"StringToHash('{s}')={unity} but CRC32={crc}; using Unity's");
                return unity;
            }
            catch { return crc; } // Unity's StringToHash is CRC32 of the UTF-8 name
        }

        private static int Crc32(string s)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(s))
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return unchecked((int)~crc);
        }
    }
}
