using UnityEngine;

namespace CanEmAllMP
{
    /// <summary>All reads/writes of the game's own objects go through here.</summary>
    public static class GameApi
    {
        /// In the factory playing (the main menu is drawn over the same scene, so exclude it).
        public static bool InGame =>
            GameManager.Instance != null && Player.Instance != null && MoneyManager.Instance != null && !HostSave.InMainMenu;

        public static bool TryGetLocalPose(out Vector3 pos, out float yaw, out float pitch)
        {
            pos = default; yaw = 0; pitch = 0;
            var p = Player.Instance;
            if (p == null || !InGame) return false;
            var gp = p.groundPoint;
            pos = gp != null ? gp.position : p.transform.position - Vector3.up * 1f;
            var cam = p.mainCamera;
            var camT = cam != null ? cam.transform : p.transform;
            if (FreezeFacing)
            {
                // the camera is off orbiting (third-person emote): keep facing where we were
                yaw = FrozenYaw; pitch = FrozenPitch;
                return true;
            }
            var e = camT.eulerAngles;
            yaw = e.y;
            pitch = e.x > 180f ? e.x - 360f : e.x;
            return true;
        }

        /// Night shift: the HUD clock shows the moon instead of the sun. Joiners follow the host.
        public static bool IsNight
        {
            get
            {
                try { var gm = GameManager.Instance; return gm != null && gm.nightTimer != null && gm.nightTimer.activeSelf; }
                catch { return false; }
            }
            set
            {
                try
                {
                    var gm = GameManager.Instance;
                    if (gm == null || gm.nightTimer == null || gm.sunTimer == null) return;
                    if (gm.nightTimer.activeSelf != value) gm.nightTimer.SetActive(value);
                    if (gm.sunTimer.activeSelf == value) gm.sunTimer.SetActive(!value);
                }
                catch { }
            }
        }

        /// False in the pause menu, skill tree / shop / gambling panels, day-end screens etc.
        public static bool GameAllowsInput
        {
            get
            {
                if (!InGame) return false;
                try
                {
                    var gm = GameManager.Instance;
                    if (gm == null || gm.IsPaused || !gm.CanPlayerInput) return false;
                }
                catch { }
                return true;
            }
        }

        public static bool IsPaused
        {
            get { try { var gm = GameManager.Instance; return gm == null || gm.IsPaused; } catch { return false; } }
        }

        /// Playing, no menu open, mouse captured: emotes / points may start.
        public static bool CanAct => GameAllowsInput && Cursor.lockState == CursorLockMode.Locked && !Spectate.Dead;

        /// Set by the models add-on while its third-person emote camera is in use.
        public static bool FreezeFacing;
        public static float FrozenYaw, FrozenPitch;

        public static int CurrentWeaponIndex
        {
            get
            {
                var inv = Inventory.Instance;
                return inv != null ? inv.currentWeaponIndex : -1;
            }
        }

        public static double Money
        {
            get => MoneyManager.Instance != null ? MoneyManager.Instance.Money : 0;
            set
            {
                var mm = MoneyManager.Instance;
                if (mm == null) return;
                long v = (long)System.Math.Round(value);
                if (v != mm.Money) mm.SetMoney(v);
            }
        }

        public static float DayTimer
        {
            get => GameManager.Instance != null ? GameManager.Instance.Timer : 0f;
            set { if (GameManager.Instance != null) GameManager.Instance.Timer = value; }
        }

        /// <summary>The weapon in hand as an index into Inventory.allWeapons (same list on every machine), or -1 (fists).
        /// The hotbar's currentWeaponIndex doesn't map onto either list reliably, so this asks which weapon object the
        /// game has switched on (it only enables the one in your hand).</summary>
        /// What the held weapon is doing, for other players' third-person copy. Vacuum: 0 idle, 1 sucking, 2 throwing.
        public static int CurrentWeaponAction
        {
            get
            {
                try
                {
                    var v = Vacuum.Instance;
                    if (v != null && v.gameObject.activeInHierarchy) return (int)v.state;
                }
                catch { }
                return 0;
            }
        }

        public static int CurrentWeaponGlobalIndex
        {
            get
            {
                var inv = Inventory.Instance;
                var all = inv?.allWeapons;
                if (all == null) return -1;
                for (int i = 0; i < all.Count; i++)
                {
                    var w = all[i];
                    try { if (w != null && w.gameObject.activeSelf) return i; } catch { }
                }
                return -1;
            }
        }

        /// <summary>Index of a weapon in Inventory.allWeapons (same list on every machine), or -1.</summary>
        public static int WeaponIndex(Weapon w)
        {
            var inv = Inventory.Instance;
            if (w == null || inv == null || inv.allWeapons == null) return -1;
            var list = inv.allWeapons;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].Pointer == w.Pointer) return i;
            return -1;
        }

        public static Weapon WeaponAt(int index)
        {
            var inv = Inventory.Instance;
            if (index < 0 || inv == null || inv.allWeapons == null || index >= inv.allWeapons.Count) return null;
            return inv.allWeapons[index];
        }
    }
}
