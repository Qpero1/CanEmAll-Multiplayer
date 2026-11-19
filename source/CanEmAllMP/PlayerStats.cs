using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CanEmAllMP
{
    /// Per-player session stats (kills, deaths), counted on the host and sent to everyone every couple of seconds.
    ///  - kills: Enemy.Damage on the host that turns a living creature dead is credited to whoever caused that hit:
    ///    a joiner's forwarded hit (Attacker), the host's own weapon hit, or - for damage with no weapon (burning) -
    ///    the last player who hit or set fire to that creature.
    ///  - deaths: a player going down (spectating) counts once per death.
    public static class PlayerStats
    {
        public class S { public int Kills, Deaths, Ping = -1; }

        public static readonly Dictionary<int, S> All = new Dictionary<int, S>();
        /// Host: the joiner whose hit is being applied right now (-1 = none).
        public static int Attacker = -1;
        /// >0 while the damage comes from something that isn't a player (turret, can processor, KillAll).
        public static int Ignore;
        private static readonly Dictionary<IntPtr, int> _lastHitter = new Dictionary<IntPtr, int>();
        private static readonly Dictionary<int, bool> _wasDead = new Dictionary<int, bool>();
        private static float _nextSend;

        private static NetSession Net => MpManager.Net;

        public static S Of(int id)
        {
            if (!All.TryGetValue(id, out var s))
            {
                s = new S();
                // host: carry on from the numbers saved with this save file (matched by name)
                if (Net.IsHost && Net.Players.TryGetValue(id, out var p) && _saved.TryGetValue(p.Name ?? "", out var old))
                { s.Kills = old.Kills; s.Deaths = old.Deaths; }
                All[id] = s;
            }
            return s;
        }

        // ------------------------------------------------ saved with the save file (host)
        // <save path>.mpstats, one line per player name: name \t kills \t deaths. Written when the game saves (end of day),
        // so like the game's own progress, a day you quit in the middle of isn't kept.
        private static readonly Dictionary<string, S> _saved = new Dictionary<string, S>();
        private static bool _loaded;

        private static string StatsPath()
        {
            try { var sp = SaveManager.Instance?.SavePath; return string.IsNullOrEmpty(sp) ? null : sp + ".mpstats"; } catch { return null; }
        }

        private static void LoadSaved()
        {
            _loaded = true;
            _saved.Clear();
            var path = StatsPath();
            if (path == null || !System.IO.File.Exists(path)) return;
            try
            {
                foreach (var line in System.IO.File.ReadAllLines(path))
                {
                    var f = line.Split('\t');
                    if (f.Length < 3 || !int.TryParse(f[1], out var k) || !int.TryParse(f[2], out var d)) continue;
                    _saved[f[0]] = new S { Kills = k, Deaths = d };
                }
                Plugin.Log.LogInfo($"[stats] loaded {_saved.Count} player(s) from {path}");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[stats] load: " + e.Message); }
        }

        /// Copy a player's live numbers into the saved table (on save and when they leave).
        private static void Remember(int id, string name)
        {
            if (string.IsNullOrEmpty(name) || !All.TryGetValue(id, out var s)) return;
            _saved[name] = new S { Kills = s.Kills, Deaths = s.Deaths };
        }

        public static void PlayerLeft(int id, string name)
        {
            if (!Net.IsHost) return;
            Remember(id, name);
            All.Remove(id); _wasDead.Remove(id);
        }

        /// The game just saved (host): write the stats next to the save.
        public static void OnGameSaved()
        {
            if (!Net.IsHost) return;
            foreach (var p in Net.Players.Values) Remember(p.Id, p.Name);
            var path = StatsPath();
            if (path == null) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var kv in _saved) sb.Append(kv.Key.Replace('\t', ' ')).Append('\t').Append(kv.Value.Kills).Append('\t').Append(kv.Value.Deaths).Append('\n');
                System.IO.File.WriteAllText(path, sb.ToString());
                Plugin.Log.LogInfo($"[stats] saved {_saved.Count} player(s) to {path}");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[stats] save: " + e.Message); }
        }

        public static void Reset()
        {
            All.Clear(); _lastHitter.Clear(); _wasDead.Clear(); _lastReal.Clear(); Attacker = -1; Ignore = 0;
            _saved.Clear(); _loaded = false;
        }

        public static void AfterDamage(Enemy e, Weapon weapon, bool wasDead)
        {
            if (!Net.IsHost || e == null || wasDead) return;
            if (Ignore > 0)
            {
                // turrets / machines: nobody's kill (and nobody's shockwave either)
                bool d = false; try { d = e.IsDead; } catch { }
                if (d) { _lastKiller = -1; _lastKillAt = Time.unscaledTime; }
                return;
            }
            int who;
            if (Attacker >= 0) { who = Attacker; _lastHitter[e.Pointer] = who; }
            else if (weapon != null) { who = Net.LocalId; _lastHitter[e.Pointer] = who; }
            else who = _lastHitter.TryGetValue(e.Pointer, out var h) ? h : Net.LocalId;
            bool dead = false;
            try { dead = e.IsDead; } catch { }
            if (!dead) return;
            Of(who).Kills++;
            _lastHitter.Remove(e.Pointer);
            _lastKiller = who; _lastKillAt = Time.unscaledTime;
        }

        // the shockwave skill: a creature that dies sends out a shockwave; its kills belong to whoever caused that death
        private static int _lastKiller = -1;
        private static float _lastKillAt = -99f;

        /// Kills that don't go through Enemy.Damage: shockwaves and bleeding (host).
        public static void AfterOtherDamage(Enemy e, bool wasDead, bool shockwave)
        {
            if (!Net.IsHost || e == null || wasDead) return;
            bool dead = false;
            try { dead = e.IsDead; } catch { }
            if (!dead) return;
            int who;
            if (shockwave) who = Time.unscaledTime - _lastKillAt < 2f ? _lastKiller : -1;
            else who = _lastHitter.TryGetValue(e.Pointer, out var h) ? h : -1; // bleeding: whoever made it bleed
            _lastHitter.Remove(e.Pointer);
            if (who < 0) return;
            Of(who).Kills++;
            _lastKiller = who; _lastKillAt = Time.unscaledTime; // chains of shockwaves stay with the same player
        }

        /// A day-ending (real) death. Counted once: not again within a few seconds (the game can call it twice),
        /// and not if this player is already counted as down (spectating).
        public static void RealDeath(int who, string why)
        {
            if (!Net.IsHost || who < 0) return;
            float now = Time.unscaledTime;
            if (_lastReal.TryGetValue(who, out var t) && now - t < 15f) { Plugin.Log.LogInfo($"[stats] {Net.NameOf(who)} death ({why}) already counted"); return; }
            bool down = false;
            try { down = Spectate.IsPlayerDead(who); } catch { }
            _wasDead.TryGetValue(who, out var was);
            if (down && was) { Plugin.Log.LogInfo($"[stats] {Net.NameOf(who)} death ({why}): already counted as down"); return; }
            _lastReal[who] = now;
            Of(who).Deaths++;
            Plugin.Log.LogInfo($"[stats] {Net.NameOf(who)} died ({why}): {Of(who).Deaths} death(s)");
        }
        private static readonly Dictionary<int, float> _lastReal = new Dictionary<int, float>();

        public static void Burned(Enemy e, int who)
        {
            if (Net.IsHost && e != null) _lastHitter[e.Pointer] = who;
        }

        public static void Tick()
        {
            if (!Net.Active) { if (All.Count > 0 || _lastHitter.Count > 0) Reset(); return; }
            if (!Net.IsHost) return;
            if (!_loaded && GameApi.InGame) { LoadSaved(); All.Clear(); } // re-seed everyone from the saved numbers

            // deaths: count each change to dead
            foreach (var id in Net.Players.Keys)
            {
                bool dead = false;
                try { dead = Spectate.IsPlayerDead(id); } catch { }
                _wasDead.TryGetValue(id, out var was);
                if (dead && !was)
                {
                    Of(id).Deaths++;
                    Plugin.Log.LogInfo($"[stats] {Net.NameOf(id)} went down: {Of(id).Deaths} death(s)");
                }
                _wasDead[id] = dead;
            }
            if (_lastHitter.Count > 2000) _lastHitter.Clear();

            if (Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + 2f;
            var w = Net.Begin(Msg.PlayerStats);
            int n = 0;
            foreach (var id in Net.Players.Keys) n++;
            w.Put((byte)Math.Min(n, 255));
            foreach (var id in Net.Players.Keys)
            {
                var s = Of(id);
                s.Ping = id == Net.LocalId ? -1 : Net.PingOf(id);
                w.Put(id); w.Put(s.Kills); w.Put(s.Deaths); w.Put(s.Ping);
            }
            Net.Send(DeliveryMethod.ReliableOrdered);
        }

        public static void Receive(NetDataReader r)
        {
            int n = r.GetByte();
            All.Clear();
            for (int i = 0; i < n; i++)
            {
                int id = r.GetInt();
                var s = Of(id);
                s.Kills = r.GetInt(); s.Deaths = r.GetInt();
                s.Ping = r.AvailableBytes >= 4 ? r.GetInt() : -1;
            }
        }
    }
}
