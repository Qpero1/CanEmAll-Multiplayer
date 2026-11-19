using System;
using System.IO;
using UnityEngine;

namespace CanEmAllMP
{
    /// Multiplayer has its own save slots, separate from the single-player save.
    /// Every save read/write/delete in the game goes through SaveManager.SavePath (save.sav), so pointing that
    /// at mp_slotN.sav while a hosted session is running makes the game use the slot by itself.
    ///   0 = the normal local save (save.sav), 1..3 = mp_slot1..3.sav
    /// CONTINUE / NEW GAME always use the local save: the slot is put back to 0 whenever we're on the main menu
    /// outside a session (and before Menu.Awake decides whether to show CONTINUE).
    public static class SaveSlots
    {
        public const int Count = 3;
        public static int Current;

        public static string PathFor(int slot)
        {
            string dir = Application.persistentDataPath;
            return System.IO.Path.Combine(dir, slot <= 0 ? "save.sav" : "mp_slot" + slot + ".sav");
        }

        public static bool Exists(int slot)
        {
            try { return File.Exists(PathFor(slot)); } catch { return false; }
        }

        /// Text for the dropdown: "Slot 1 - Day 4" / "Slot 2 - New game" / "Local save - Day 9".
        public static string Describe(int slot)
        {
            string name = slot <= 0 ? "Local save" : "Slot " + slot;
            if (!Exists(slot)) return name + " - New game";
            try
            {
                var d = HostSave.FromJson(File.ReadAllText(PathFor(slot)));
                if (d != null) return $"{name} - Day {d.day}";
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[saves] reading {PathFor(slot)}: {e.Message}"); }
            return name;
        }

        /// Is the game really using our path? (guards against the hook not being installed: then a "slot" would
        /// silently be the single-player save)
        public static bool RedirectWorks(int slot)
        {
            if (slot <= 0) return true;
            int prev = Current;
            Current = slot;
            string got = null;
            try { got = SaveManager.Instance?.SavePath; } catch (Exception e) { Plugin.Log.LogError("[saves] SavePath: " + e.Message); }
            Current = prev;
            bool ok = got != null && string.Equals(System.IO.Path.GetFullPath(got), System.IO.Path.GetFullPath(PathFor(slot)), StringComparison.OrdinalIgnoreCase);
            if (!ok) Plugin.Log.LogError($"[saves] save path redirect isn't working (game uses '{got}'), multiplayer slots disabled");
            return ok;
        }

        /// Point the game at a slot and load it into memory. Returns true if the slot already has a save.
        public static bool Use(int slot)
        {
            Current = Math.Max(0, Math.Min(Count, slot));
            bool exists = Exists(Current);
            try { SaveManager.Instance?.Load(); } catch (Exception e) { Plugin.Log.LogError("[saves] load: " + e); }
            Plugin.Log.LogInfo($"[saves] using {PathFor(Current)} ({(exists ? "existing" : "new")})");
            return exists;
        }

        /// Back to the single-player save. Returns true if anything changed.
        public static bool ResetToLocal(bool reload)
        {
            if (Current == 0) return false;
            Current = 0;
            if (reload) { try { SaveManager.Instance?.Load(); } catch (Exception e) { Plugin.Log.LogError("[saves] load: " + e); } }
            Plugin.Log.LogInfo("[saves] back on the local save");
            return true;
        }
    }
}
