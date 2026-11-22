#nullable disable
// F9 weapon tuning menu for the Can 'Em All Multiplayer player models add-on.
// One tab per weapon: offset / rotation / scale / grip, applied live to every other player's miner on this screen
// (and to an optional test dummy), saved to the add-on's config under [WeaponTuning].

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using CanEmAllMP;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CanEmAllMPModels
{
    internal class Tune
    {
        public Vector3 Offset;           // metres: right, up, forward (player's facing)
        public Vector3 Rotation;         // degrees
        public float Scale = 1f;
        public float Forward = 0.3f;     // share of the weapon's length in front of the hand (0 = held at its middle)
    }

    /// Per-weapon tuning, one config line each: "offset x,y,z | rotation x,y,z | scale | forward".
    internal static class WeaponTuning
    {
        private static ConfigFile _cfg;
        private static readonly Dictionary<string, Tune> Tunes = new Dictionary<string, Tune>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ConfigEntry<string>> Entries = new Dictionary<string, ConfigEntry<string>>(StringComparer.OrdinalIgnoreCase);
        public static bool Dirty;

        // tuned in game by Arkin (v1.3.0 F9 menu). Guns point down at idle on purpose, ready for a hold-gun animation.
        // tuned in game by Arkin in the gun / knife stances (add-on 1.7.0)
        private static readonly Dictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Vacuum",         "-0.08, 0.08, 0.35 | 5, 0, 5 | 6.4 | 0.4" },
            { "BaseballBat",    "-0.03, -0.09, 0.08 | -40, 0, 0 | 1.6 | 0.35" },
            { "Knife",          "-0.01, -0.1, 0.02 | -25, 10, -80 | 2.5 | 0.3" },
            { "AK47",           "-0.13, -0.17, 0.44 | -20, 175, 5 | 1 | 0.3" },
            { "Pistol",         "-0.06, -0.03, 0.21 | 0, 180, -5 | 1.55 | 0.15" },
            { "SMG",            "-0.06, -0.04, 0.33 | -10, -180, -10 | 1 | 0.3" },
            { "Chainsaw",       "0.08, -0.23, 0.54 | -15, 90, -5 | 6.6 | 0" },
            { "Shotgun",        "-0.09, -0.08, 0.24 | -20, 180, 0 | 1.15 | 0.15" },
            { "RocketLauncher", "0, 0.11, 0.49 | -5, -180, -80 | 1.9 | 0.15" },
            { "FlameThrower",   "-0.05, 0.08, 0 | 10, 0, 105 | 1.55 | 0.3" },
        };
        private const string OldBlank = "0, 0, 0 | 0, 0, 0 | 1 | 0.3";

        // earlier shipped defaults: a weapon still on one of these was never tuned by the user, so it moves to the current one
        private static readonly (string type, string value)[] OldDefaults =
        {
            ("Vacuum", "0, 0, 0 | 0, 0, 0 | 1.5 | 0.3"),
            ("Vacuum", "-0.08, -0.29, 0.28 | 65, -10, 0 | 6.4 | 0.4"),
            ("Knife", "-0.01, -0.1, 0.01 | -25, -25, -80 | 2.5 | 0.3"),
            ("AK47", "-0.03, -0.42, 0.27 | -60, 180, -15 | 1 | 0.3"),
            ("Pistol", "-0.03, -0.24, 0.21 | -55, 180, 0 | 1.55 | 0.3"),
            ("SMG", "-0.03, -0.31, 0.33 | -45, -180, 5 | 1 | 0.3"),
            ("Chainsaw", "0.14, -0.24, 0.45 | -15, 90, -5 | 6.6 | 0"),
            ("Shotgun", "-0.08, -0.47, 0.24 | -75, 165, 0 | 1.15 | 0.3"),
            ("Shotgun", "-0.08, -0.47, 0.24 | -75, 165, -0 | 1.15 | 0.3"),
            ("RocketLauncher", "-0.1, -0.86, 0.27 | -80, -205, -90 | 1.9 | 0.3"),
            ("FlameThrower", "-0.05, 0, 0.15 | 75, 10, 100 | 1.55 | 0.3"),
            ("FlameThrower", "-0.05, -0, 0.15 | 75, 10, 100 | 1.55 | 0.3"),
        };

        public static void Init(ConfigFile cfg)
        {
            _cfg = cfg;
            foreach (var (type, value) in OldDefaults)
            {
                var e = Entry(type);
                if (e.Value.Replace(" ", "") == value.Replace(" ", "")) e.Value = DefaultFor(type);
            }
            // configs made by 1.3.0 hold the old blank default: move untouched weapons onto the tuned values
            foreach (var kv in Defaults)
            {
                var e = Entry(kv.Key);
                if (e.Value.Replace(" ", "") == OldBlank.Replace(" ", "") && kv.Value != OldBlank) e.Value = kv.Value;
            }
        }

        private static string DefaultFor(string type) => Defaults.TryGetValue(type, out var d) ? d : OldBlank;

        private static ConfigEntry<string> Entry(string type)
        {
            if (Entries.TryGetValue(type, out var e)) return e;
            e = _cfg.Bind("WeaponTuning", type, DefaultFor(type),
                $"{type}: offset right,up,forward (m) | rotation x,y,z (deg) | scale | grip (0 = held at its middle). Set it with the F9 menu");
            Entries[type] = e;
            return e;
        }

        public static Tune Get(string type)
        {
            if (string.IsNullOrEmpty(type) || type == "?") return new Tune();
            if (Tunes.TryGetValue(type, out var t)) return t;
            t = Parse(Entry(type).Value);
            Tunes[type] = t;
            return t;
        }

        public static void Reset(string type) { Tunes[type] = Parse(DefaultFor(type)); Dirty = true; }

        /// The config file was reloaded: re-read everything that isn't being edited right now.
        public static void ReloadFromConfig() { if (!Dirty) Tunes.Clear(); }

        public static void Save()
        {
            bool autosave = _cfg.SaveOnConfigSet;
            _cfg.SaveOnConfigSet = false;
            try { foreach (var kv in Tunes) Entry(kv.Key).Value = Format(kv.Value); }
            finally { _cfg.SaveOnConfigSet = autosave; }
            _cfg.Save();
            Dirty = false;
        }

        private static string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        private static string V(Vector3 v) => $"{F(v.x)}, {F(v.y)}, {F(v.z)}";
        public static string Format(Tune t) => $"{V(t.Offset)} | {V(t.Rotation)} | {F(t.Scale)} | {F(t.Forward)}";

        private static Tune Parse(string s)
        {
            var t = new Tune();
            try
            {
                var p = (s ?? "").Split('|');
                var c = System.Globalization.CultureInfo.InvariantCulture;
                if (p.Length > 0) t.Offset = WeaponVisuals.ParseVec(p[0], Vector3.zero);
                if (p.Length > 1) t.Rotation = WeaponVisuals.ParseVec(p[1], Vector3.zero);
                if (p.Length > 2) t.Scale = float.Parse(p[2].Trim(), c);
                if (p.Length > 3) t.Forward = float.Parse(p[3].Trim(), c);
            }
            catch { }
            return t;
        }
    }

    public class TuneMenu : MonoBehaviour
    {
        public TuneMenu(IntPtr ptr) : base(ptr) { }

        public static bool Open;
        /// While the menu is open with "show on players" ticked: the weapon every remote miner holds.
        public static int PreviewWeapon = -1;
        /// Show the weapon's in-use effect (the vacuum's tornado) on the preview/dummy.
        public static bool PreviewEffect;

        private static bool _preview = true;
        private static int _tab;
        private static string _status = "";
        private static float _statusAt;
        private static readonly List<(int index, string type)> Weapons = new List<(int, string)>();
        private static CursorLockMode _prevLock;
        private static bool _prevVisible;
        private static int _err;

        // test dummy
        private static GameObject _dummyRoot;
        private static ModelState _dummy;
        private static float _dummyYaw;
        private static int _dummyFit = -1;
        private static int _fakeAttacks;
        private static float _nextFakeAttack;

        // ---- hooks (patched in ModelsPlugin.Load)
        public static bool BlockWhileOpen() => !Open && !EmoteWheel.Open && !SelfView.Active; // also stops scroll switching weapons while emoting
        public static bool BlockCamera() => !Open && !EmoteWheel.Open && !SelfView.Active;

        public static bool BlockMovement(Player __instance)
        {
            if (!Open && !EmoteWheel.Open) return true;
            try { __instance.input = Vector3.zero; } catch { }
            return false;
        }

        public void Update()
        {
            bool pressed = false;
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null) pressed = kb[ModelsPlugin.TuneMenuKey.Value].wasPressedThisFrame;
            }
            catch { }
            if (pressed && !EmoteWheel.Open) Toggle();

            if (Open)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            PreviewWeapon = Open && _preview && _tab < Weapons.Count ? Weapons[_tab].index : -1;

            try { UpdateDummy(); } catch (Exception e) { if (_err++ < 3) ModelsPlugin.L.LogWarning("tuning dummy: " + e.Message); }
        }

        private static void Toggle()
        {
            Open = !Open;
            if (Open)
            {
                _prevLock = Cursor.lockState; _prevVisible = Cursor.visible;
                RefreshWeapons();
            }
            else
            {
                Cursor.lockState = _prevLock; Cursor.visible = _prevVisible;
                if (WeaponTuning.Dirty) Say("Closed with unsaved changes (they still apply until you restart)");
            }
        }

        private static void RefreshWeapons()
        {
            Weapons.Clear();
            try
            {
                var all = Inventory.Instance?.allWeapons;
                if (all == null) return;
                var seen = new HashSet<string>();
                for (int i = 0; i < all.Count; i++)
                {
                    var w = all[i];
                    if (w == null) continue;
                    string type;
                    try { type = w.GetIl2CppType().Name; } catch { type = "Weapon" + i; }
                    if (seen.Add(type)) Weapons.Add((i, type));
                }
            }
            catch { }
            if (_tab >= Weapons.Count) _tab = 0;
        }

        private static void Say(string s) { _status = s; _statusAt = Time.unscaledTime; }

        // ================================================================ test dummy

        private static void SpawnDummy()
        {
            if (!GameApi.TryGetLocalPose(out var pos, out var yaw, out _)) { Say("Load into the game first"); return; }
            _dummyRoot = new GameObject("MP_TuningDummy");
            _dummyRoot.transform.position = pos + Quaternion.Euler(0, yaw, 0) * Vector3.forward * 2.5f;
            _dummyYaw = yaw + 90f; // side on, so you see the weapon's profile
            _dummyRoot.transform.rotation = Quaternion.Euler(0, _dummyYaw, 0);

            var model = Bundle.Spawn("PlayerModel", _dummyRoot.transform);
            if (model == null) { Object.Destroy(_dummyRoot); _dummyRoot = null; Say("Couldn't load the miner model"); return; }
            var m = AvatarPatches.MeasureModel(_dummyRoot, model);
            _dummy = new ModelState { Model = model, RawHeight = m.height, RawFeet = m.feet };
            AvatarPatches.ApplyFit(_dummy);
            _dummyFit = ModelsPlugin.ConfigVersion;
            AvatarPatches.FixShaders(_dummyRoot, model);
            PlayerTint.Track(model, PlayerTint.LocalColor);
            var anim = model.GetComponent<Animator>();
            if (anim != null)
            {
                anim.applyRootMotion = false;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                try { anim.Rebind(); } catch { }
            }
            _dummy.Anim = anim;
            _dummy.Hand = WeaponVisuals.FindHand(model);
            // debug: hitting the dummy plays the hit sound + crosshair (the main mod's friendly-fire check)
            Extras.TestTarget = () => _dummyRoot != null && _dummy != null && _dummy.Corpse == null ? _dummyRoot.transform.position : (Vector3?)null;
            Say("Test dummy spawned 2.5 m in front of you - hit it to test the hit sound / crosshair");
        }

        private static void RemoveDummy()
        {
            if (_dummy?.Corpse != null) Ragdoll.Exit(_dummy, null);
            if (_dummyRoot != null) Object.Destroy(_dummyRoot);
            _dummyRoot = null;
            _dummy = null;
        }

        private static void UpdateDummy()
        {
            if (_dummy == null) return;
            if (_dummyRoot == null) { _dummy = null; return; } // scene changed
            if (_dummy.Corpse != null) return; // ragdoll test: leave it lying
            if (_dummyFit != ModelsPlugin.ConfigVersion) { AvatarPatches.ApplyFit(_dummy); _dummyFit = ModelsPlugin.ConfigVersion; }
            _dummyRoot.transform.rotation = Quaternion.Euler(0, _dummyYaw, 0);
            int want = _tab < Weapons.Count ? Weapons[_tab].index : -1;
            if (_tab < Weapons.Count) Moves.SetGunPose(_dummy, Weapons[_tab].type); // tune in the stance it's held in
            if (_dummy.Hand != null) WeaponVisuals.Show(_dummy, want, _dummyRoot.transform, PreviewEffect ? 1 : 0);
            if (_dummy.Anim != null && _tab < Weapons.Count)
            {
                // "in use" preview: repeat the weapon's attack move (shoot / stab / swing), vacuum shows its tornado
                var type = Weapons[_tab].type;
                if (PreviewEffect && Time.unscaledTime >= _nextFakeAttack)
                {
                    _nextFakeAttack = Time.unscaledTime + 1.2f;
                    _fakeAttacks++;
                }
                Moves.Actions(_dummy, want, type, Moves.IsGun(type), PreviewEffect ? 1 : 0, _fakeAttacks & 15);
            }
        }

        // ================================================================ drawing

        private static float _x, _cy;

        public void OnGUI()
        {
            if (!Open) return;
            try { Draw(); }
            catch (Exception e) { if (_err++ < 3) ModelsPlugin.L.LogWarning("tuning menu: " + e); }
        }

        private static void Draw()
        {
            float w = 450, h = 630; // (the dummy row is wider: it has a ragdoll button)
            _x = 20; float y = 70;
            GUI.Box(new Rect(_x, y, w, h), $"Weapon tuning   ({ModelsPlugin.TuneMenuKey.Value} to close)");
            _cy = y + 28;

            if (Weapons.Count == 0)
            {
                RefreshWeapons();
                GUI.Label(new Rect(_x + 12, _cy, w - 24, 22), "No weapons found yet: load into the game, then reopen.");
                return;
            }

            // one tab per weapon
            for (int i = 0; i < Weapons.Count; i++)
            {
                int col = i % 3, row = i / 3;
                var r = new Rect(_x + 10 + col * 144, _cy + row * 26, 140, 24);
                string label = i == _tab ? "[ " + Weapons[i].type + " ]" : Weapons[i].type;
                if (GUI.Button(r, label)) _tab = i;
            }
            _cy += ((Weapons.Count + 2) / 3) * 26 + 8;

            var type = Weapons[_tab].type;
            var t = WeaponTuning.Get(type);
            var before = WeaponTuning.Format(t);

            Header("Offset from the hand (metres)");
            t.Offset.x = Row("Right", t.Offset.x, -0.6f, 0.6f, 0.01f);
            t.Offset.y = Row("Up", t.Offset.y, -0.6f, 0.6f, 0.01f);
            t.Offset.z = Row("Forward", t.Offset.z, -0.6f, 0.6f, 0.01f);
            Header("Rotation (degrees)");
            t.Rotation.x = Row("Pitch (X)", t.Rotation.x, -180f, 180f, 5f);
            t.Rotation.y = Row("Yaw (Y)", t.Rotation.y, -180f, 180f, 5f);
            t.Rotation.z = Row("Roll (Z)", t.Rotation.z, -180f, 180f, 5f);
            Header("Size and grip");
            t.Scale = Row("Scale", t.Scale, 0.1f, 3f, 0.05f);
            t.Forward = Row("Grip", t.Forward, -0.6f, 0.6f, 0.05f);
            GUI.Label(new Rect(_x + 12, _cy, 420, 20), "Grip: 0 = held at its middle, + = more of it in front of the hand");
            _cy += 26;

            if (WeaponTuning.Format(t) != before) WeaponTuning.Dirty = true;

            _preview = GUI.Toggle(new Rect(_x + 12, _cy, 420, 22), _preview, " Show this weapon on every other player while this menu is open");
            _cy += 24;
            PreviewEffect = GUI.Toggle(new Rect(_x + 12, _cy, 420, 22), PreviewEffect, " Show it in use (attack move on the dummy, vacuum tornado)");
            _cy += 28;

            if (_dummy == null)
            {
                if (GUI.Button(new Rect(_x + 12, _cy, 200, 26), "Spawn test dummy")) SpawnDummy();
                if (GUI.Button(new Rect(_x + 220, _cy, 150, 26), "Test hit sound")) Say(HitSound.Test());
            }
            else
            {
                if (GUI.Button(new Rect(_x + 12, _cy, 130, 26), "Remove dummy")) RemoveDummy();
                if (GUI.Button(new Rect(_x + 150, _cy, 90, 26), "Turn left")) _dummyYaw -= 45f;
                if (GUI.Button(new Rect(_x + 248, _cy, 90, 26), "Turn right")) _dummyYaw += 45f;
                if (GUI.Button(new Rect(_x + 346, _cy, 90, 26), _dummy.Corpse == null ? "Ragdoll" : "Stand up"))
                {
                    if (_dummy.Corpse == null) { WeaponVisuals.Show(_dummy, -1, _dummyRoot.transform); Ragdoll.Enter(_dummy, Vector3.back * 2f + Vector3.up * 2f); }
                    else Ragdoll.Exit(_dummy, _dummyRoot.transform);
                }
            }
            _cy += 34;

            if (GUI.Button(new Rect(_x + 12, _cy, 200, 30), "Reset " + type)) WeaponTuning.Reset(type);
            if (GUI.Button(new Rect(_x + 222, _cy, 210, 30), WeaponTuning.Dirty ? "Save all  (unsaved changes)" : "Save all"))
            {
                try { WeaponTuning.Save(); Say("Saved to " + System.IO.Path.GetFileName(ModelsPlugin.Instance.Config.ConfigFilePath)); }
                catch (Exception e) { Say("Save failed: " + e.Message); }
            }
            _cy += 38;

            if (!string.IsNullOrEmpty(_status) && Time.unscaledTime - _statusAt < 6f)
                GUI.Label(new Rect(_x + 12, _cy, 420, 44), _status);   // two lines: the sound test's report is long
        }

        private static void Header(string text)
        {
            GUI.Label(new Rect(_x + 12, _cy, 420, 22), "<b>" + text + "</b>");
            _cy += 22;
        }

        private static float Row(string label, float v, float min, float max, float step)
        {
            GUI.Label(new Rect(_x + 12, _cy, 80, 22), label);
            if (GUI.Button(new Rect(_x + 92, _cy, 26, 22), "-")) v -= step;
            v = GUI.HorizontalSlider(new Rect(_x + 124, _cy + 6, 200, 18), v, min, max);
            if (GUI.Button(new Rect(_x + 330, _cy, 26, 22), "+")) v += step;
            GUI.Label(new Rect(_x + 362, _cy, 80, 22), v.ToString("0.###"));
            _cy += 24;
            return (float)Math.Round(v / step) * step;   // snap to the step so values stay tidy
        }
    }
}
