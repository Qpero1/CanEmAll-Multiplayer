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

        /// Delete a save (and its multiplayer stats file).
        public static void Delete(int slot)
        {
            var p = PathFor(slot);
            if (File.Exists(p)) File.Delete(p);
            if (File.Exists(p + ".mpstats")) File.Delete(p + ".mpstats");
        }

        /// Copy a save (and its stats) over another.
        public static void Copy(int from, int to)
        {
            string a = PathFor(from), b = PathFor(to);
            File.Copy(a, b, true);
            if (File.Exists(a + ".mpstats")) File.Copy(a + ".mpstats", b + ".mpstats", true);
            else if (File.Exists(b + ".mpstats")) File.Delete(b + ".mpstats");
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

    /// Hosting: copies of the save before the game overwrites it (at the start of a session and once per day),
    /// so a griefer or a bug can never cost more than a day. Kept in mp_backups/<save name>/ next to the saves.
    public static class SaveBackups
    {
        public const int Keep = 20;
        private static float _last = -1e9f;
        private static int _lastDay = int.MinValue;

        public static string Folder(int slot) =>
            System.IO.Path.Combine(Application.persistentDataPath, "mp_backups", System.IO.Path.GetFileNameWithoutExtension(SaveSlots.PathFor(slot)));

        /// A new hosted session: the first save in it backs up whatever was there before.
        public static void NewSession() { _last = -1e9f; _lastDay = int.MinValue; }

        /// Called right before the game writes the save.
        public static void BeforeSave()
        {
            var net = MpManager.Net;
            if (!net.IsHost || HostSave.UsingHostSave) return;
            int day = int.MinValue;
            try { day = SaveManager.Instance.CurrentSaveData.day; } catch { }
            if (day == _lastDay && Time.unscaledTime - _last < 600f) return;   // already have this day (saves can come in bursts)
            if (!File.Exists(SaveSlots.PathFor(SaveSlots.Current))) return;
            _last = Time.unscaledTime; _lastDay = day;
            BackupNow(SaveSlots.Current, "while hosting");
        }

        /// Copy a slot's current save into its backup folder (keeps the newest Keep).
        public static void BackupNow(int slot, string why)
        {
            var src = SaveSlots.PathFor(slot);
            if (!File.Exists(src)) return;
            try
            {
                string dir = Folder(slot);
                Directory.CreateDirectory(dir);
                string dst = System.IO.Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".sav");
                File.Copy(src, dst, true);
                File.SetLastWriteTime(dst, DateTime.Now);
                var files = new DirectoryInfo(dir).GetFiles("*.sav");
                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = Keep; i < files.Length; i++) { try { files[i].Delete(); } catch { } }
                Plugin.Log.LogInfo($"[saves] backup ({why}): {dst}");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[saves] backup failed: " + e.Message); }
        }

        /// A slot's backups, newest first.
        public static System.Collections.Generic.List<FileInfo> List(int slot)
        {
            var l = new System.Collections.Generic.List<FileInfo>();
            try
            {
                var dir = Folder(slot);
                if (Directory.Exists(dir)) l.AddRange(new DirectoryInfo(dir).GetFiles("*.sav"));
            }
            catch { }
            l.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            return l;
        }

        /// "20 Nov 16:52 - day 4"
        public static string Describe(FileInfo f)
        {
            string when = f.LastWriteTime.ToString("d MMM HH:mm");
            try
            {
                var d = HostSave.FromJson(File.ReadAllText(f.FullName));
                if (d != null) return $"{when} - day {d.day}";
            }
            catch { }
            return when;
        }
    }
}
