using UnityEngine;
using UnityEngine.InputSystem;

namespace CanEmAllMP
{
    /// Local attack / point inputs, sent to the other players so their copy of you can animate.
    /// Counters (not one-frame flags) so a quick click still arrives when a snapshot is dropped.
    public static class Gestures
    {
        public static int AttackCount;   // every left click while playing
        public static int PointCount;    // every T press with empty hands
        public static bool Firing;       // left mouse held while playing (automatic guns, flamethrower)
        public const float PointSeconds = 2.5f; // how long a point lasts (weapon put away meanwhile)
        private static float _pointUntil;
        private static int _pointWeapon = -1, _pointAttacks;
        private static System.Collections.Generic.List<Renderer> _hidden = new System.Collections.Generic.List<Renderer>();
        public static bool Pointing => _pointUntil > 0f;
        public static int Emote;         // emote playing (0 = none); the models add-on's wheel sets and clears it

        public static void Poll()
        {
            Firing = false;
            if (!GameApi.GameAllowsInput)
            {
                // a menu opened (or we left): drop any emote / point so nothing is left half on
                Emote = 0;
                if (Pointing) EndPoint();
            }
            if (!GameApi.InGame || Cursor.lockState != CursorLockMode.Locked || Spectate.Dead) return; // in a menu / panel / dead
            try
            {
                var m = Mouse.current;
                if (m != null)
                {
                    Firing = m.leftButton.isPressed;
                    if (m.leftButton.wasPressedThisFrame) AttackCount++;
                    if (Firing && Emote == 0 && !Pointing)
                    {
                        // friendly fire: a click, or holding the trigger on automatic weapons
                        string type = WeaponType();
                        bool auto = type == "SMG" || type == "AK47" || type == "FlameThrower" || type == "Chainsaw";
                        if (m.leftButton.wasPressedThisFrame || auto) Extras.ShooterCheck(type);
                    }
                }
                var kb = Keyboard.current;
                if (kb != null && kb[Key.T].wasPressedThisFrame && GameApi.CanAct) StartPoint();
            }
            catch { }
            TickPoint();
        }

        /// Point with whatever's equipped: the first-person weapon is hidden for the point, then comes back.
        private static void StartPoint()
        {
            PointCount++;
            if (!Pointing)
            {
                _pointWeapon = GameApi.CurrentWeaponGlobalIndex;
                HideWeapon(_pointWeapon);
            }
            _pointAttacks = AttackCount;
            _pointUntil = Time.unscaledTime + PointSeconds;
        }

        private static void TickPoint()
        {
            if (!Pointing) return;
            bool done = Time.unscaledTime > _pointUntil || !GameApi.InGame
                     || AttackCount != _pointAttacks                       // attacking brings it straight back
                     || GameApi.CurrentWeaponGlobalIndex != _pointWeapon;  // switched weapon
            if (done) EndPoint();
        }

        private static void EndPoint()
        {
            _pointUntil = 0f;
            foreach (var r in _hidden) { try { if (r != null) r.enabled = true; } catch { } }
            _hidden.Clear();
        }

        private static void HideWeapon(int index)
        {
            _hidden.Clear();
            if (index < 0) return;
            try
            {
                var w = GameApi.WeaponAt(index);
                if (w == null) return;
                var rs = w.GetComponentsInChildren<Renderer>(false);
                for (int i = 0; i < rs.Length; i++)
                    if (rs[i] != null && rs[i].enabled) { rs[i].enabled = false; _hidden.Add(rs[i]); }
            }
            catch { }
        }

        private static int _typeIdx = -2; private static string _type = "";
        private static string WeaponType()
        {
            int i = GameApi.CurrentWeaponGlobalIndex;
            if (i == _typeIdx) return _type;
            _typeIdx = i;
            _type = "";
            try { var w = GameApi.WeaponAt(i); if (w != null) _type = w.GetIl2CppType().Name; } catch { }
            return _type;
        }

        /// low nibble: attacks, high nibble: points (both wrap at 16)
        public static int Packed => (AttackCount & 15) | ((PointCount & 15) << 4);
    }
}
