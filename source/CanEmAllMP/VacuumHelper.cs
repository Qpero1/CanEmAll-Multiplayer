using System;
using System.Collections.Generic;
using UnityEngine;

namespace CanEmAllMP
{
    /// <summary>
    /// The local vacuum and hands vs. shared objects.
    ///
    /// How the game's vacuum works (from its code): every frame Vacuum.Suck finds grabbables in range, skips any that
    /// are "free falling", moves the rest to a no-collision layer and pulls them; close enough, it restores their layer,
    /// calls OnGrabbed() and switches them off into its inventory. Throw switches one back on and calls OnThrown().
    ///
    /// So: while Suck runs we flag everything someone else is holding as "free falling" (the vacuum then ignores it),
    /// OnGrabbed during Suck is a vacuum grab (first come, first served) rather than a hand grab (which may snatch),
    /// and after Suck we claim what it pulled or stored, letting go of anything we turn out not to be allowed.
    /// </summary>
    public static class VacuumHelper
    {
        /// True while Vacuum.Suck runs (OnGrabbed in that window = the vacuum storing something).
        public static bool InSuck;
        private static float _inSuckSince;

        // ------------------------------------------------ what our player holds (hand, vacuum inventory, being pulled)

        private static readonly HashSet<IntPtr> _held = new HashSet<IntPtr>();
        private static int _heldFrame = -1;

        public static bool HeldLocally(IntPtr ptr)
        {
            if (_heldFrame != Time.frameCount) RefreshHeld();
            return _held.Contains(ptr);
        }

        private static void RefreshHeld()
        {
            _heldFrame = Time.frameCount;
            _held.Clear();
            try
            {
                var h = Player.Instance?.heldEnemy;
                if (h != null) _held.Add(h.Pointer);
            }
            catch { }
            try
            {
                var v = Vacuum.Instance;
                if (v == null) return;
                var list = v.grabbables;
                if (list != null) for (int i = 0; i < list.Count; i++) { var g = list[i]; if (g != null) _held.Add(g.Pointer); }
                var inRange = v.grabbablesInRange;
                if (inRange != null) foreach (var g in inRange) if (g != null) _held.Add(g.Pointer);
            }
            catch { }
        }

        public static void RefreshBar()
        {
            try { VacuumBar.Instance?.UpdateBar(); } catch { }
        }

        // ------------------------------------------------ Vacuum.Suck hooks

        public static void BeforeSuck()
        {
            InSuck = true; _inSuckSince = Time.unscaledTime;
            EnemySync.MarkHeldByOthers();
            CanSync.MarkHeldByOthers();
        }

        public static void AfterSuck(Vacuum v)
        {
            try
            {
                if (!MpManager.Net.Connected || !Plugin.SyncEnemies.Value) return;
                _scratch.Clear();
                var inRange = v.grabbablesInRange;
                if (inRange != null) foreach (var g in inRange) if (g != null) _scratch.Add(g);
                var list = v.grabbables;
                if (list != null) for (int i = 0; i < list.Count; i++) if (list[i] != null) _scratch.Add(list[i]);
                foreach (var g in _scratch) ClaimOrLetGo(g);
            }
            finally
            {
                _scratch.Clear();
                EnemySync.UnmarkHeldByOthers();
                CanSync.UnmarkHeldByOthers();
                InSuck = false;
                _heldFrame = -1;
            }
        }
        private static readonly List<IGrabbable> _scratch = new List<IGrabbable>();

        /// Safety net: if Suck ever threw before our postfix, don't leave anything flagged.
        public static void Tick()
        {
            if (InSuck && Time.unscaledTime - _inSuckSince > 0.5f)
            {
                InSuck = false;
                try { EnemySync.UnmarkHeldByOthers(); CanSync.UnmarkHeldByOthers(); } catch { }
            }
        }

        private static void ClaimOrLetGo(IGrabbable g)
        {
            var e = g.TryCast<Enemy>();
            if (e != null) { if (!EnemySync.LocalGrab(e, false)) Eject(e); return; }
            var c = g.TryCast<Can>();
            if (c != null && !CanSync.LocalGrab(c, false)) Eject(c);
        }

        // ------------------------------------------------ letting go

        /// Our hand / vacuum lets go of it (someone else has it now). Stored ones pop back out.
        public static void Eject(Component obj) => Remove(obj, true);

        /// Same, for an object about to be destroyed or pooled: just drop every reference to it.
        public static void Forget(Component obj) => Remove(obj, false);

        private static void Remove(Component obj, bool reactivate)
        {
            if (obj == null) return;
            var ptr = obj.Pointer;
            try
            {
                var pl = Player.Instance;
                var held = pl?.heldEnemy;
                if (held != null && held.Pointer == ptr) pl.DropHeldBody();
            }
            catch { }
            bool stored = false, pulled = false;
            try
            {
                var v = Vacuum.Instance;
                if (v != null)
                {
                    var list = v.grabbables;
                    if (list != null)
                        for (int i = list.Count - 1; i >= 0; i--)
                            if (list[i] != null && list[i].Pointer == ptr) { list.RemoveAt(i); stored = true; }
                    var g = new IGrabbable(ptr);
                    try { v.grabbablesInRange?.Remove(g); } catch { }
                    try { if (v.ignoredGrabbables != null && v.ignoredGrabbables.Remove(g)) pulled = true; } catch { }
                }
            }
            catch { }
            // the vacuum moved things it pulls to a no-collision layer; put that back
            if (pulled) RestoreLayer(obj);
            if (stored && reactivate)
            {
                try { var go = obj.gameObject; if (!go.activeSelf) go.SetActive(true); } catch { }
            }
            if (stored) RefreshBar();
            _heldFrame = -1;
        }

        private static void RestoreLayer(Component obj)
        {
            try
            {
                var e = obj.TryCast<Enemy>();
                if (e != null) { e.RestoreCollidersLayer(); return; }
                var c = obj.TryCast<Can>();
                if (c != null) c.RestoreCollidersLayer();
            }
            catch { }
        }
    }
}
