using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CanEmAllMP
{
    /// "Join Game" from the Steam friends list / a friend's profile.
    /// While hosting with Connection: Steam, the host sets the Steam rich presence key "connect" to
    /// "+canemall_join <host SteamID>", which makes Steam show "Join Game" to their friends. Clicking it:
    ///  - game not running: Steam starts it with that text on the command line, and we join once the menu is up;
    ///  - game running: Steam tells the game (GameRichPresenceJoinRequested), which the game's Steamworks library
    ///    passes to its OnGameRichPresenceJoinRequested event, where we listen.
    public static class SteamJoin
    {
        private static NetSession Net => MpManager.Net;
        public const string Arg = "+canemall_join";
        static ulong _pending;
        static bool _hooked, _toldInGame;
        static float _nextTry;

        public static string ConnectString(ulong hostId) => Arg + " " + hostId;

        public static void Init()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (string.Equals(args[i], Arg, StringComparison.OrdinalIgnoreCase) && ulong.TryParse(args[i + 1], out var id))
                    {
                        _pending = id;
                        Plugin.Log.LogInfo("[Steam] started from a Join Game click: host " + id);
                    }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[Steam] command line: " + e.Message); }
        }

        /// Hook the game's Steamworks event (once Steam is up).
        static void Hook()
        {
            _hooked = true;
            try
            {
                var cb = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Steamworks.Friend, string>>(
                    new Action<Steamworks.Friend, string>((f, connect) =>
                    {
                        try { OnRequest(connect); } catch (Exception e) { Plugin.Log.LogWarning("[Steam] join request: " + e.Message); }
                    }));
                Steamworks.SteamFriends.add_OnGameRichPresenceJoinRequested(cb);
                Plugin.Log.LogInfo("[Steam] listening for Join Game clicks");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[Steam] can't listen for Join Game clicks: " + e.Message); }
        }

        static void OnRequest(string connect)
        {
            Plugin.Log.LogInfo("[Steam] Join Game clicked: " + connect);
            var parts = (connect ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
                if (string.Equals(parts[i], Arg, StringComparison.OrdinalIgnoreCase) && ulong.TryParse(parts[i + 1], out var id))
                {
                    _pending = id;
                    _toldInGame = false;
                    return;
                }
        }

        /// Steam is the default connection type, picked fresh at every launch (on the main menu):
        /// Steam if it works and this account owns the game, otherwise Online (lobby code).
        /// Direct IP is a deliberate choice (port forwarding), so it is left alone.
        static bool _defaultDone;
        static void DefaultOnce()
        {
            if (_defaultDone || Net.Active) return;
            Menu m = null;
            try { m = Menu.Instance; } catch { }
            if (m == null || m.menuPanel == null) return;
            _defaultDone = true;
            var cur = (Plugin.Connection.Value ?? "").Trim();
            if (string.Equals(cur, "Direct", StringComparison.OrdinalIgnoreCase)) return;
            bool steam = SteamApi.Available && SteamApi.OwnsGame;
            Plugin.Connection.Value = steam ? "Steam" : "Online";
            Plugin.Log.LogInfo("[Steam] connection this launch: " + Plugin.Connection.Value + (steam ? "" : " (" + (SteamApi.Error ?? "account doesn't own the game on Steam") + ")"));
        }

        /// Every frame: hook once Steam is ready, and join a pending host as soon as we're on the main menu.
        public static void Tick()
        {
            if (Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 0.5f;
            if (!_hooked && SteamApi.Available) Hook();
            DefaultOnce();
            if (_pending == 0) return;
            if (!SteamApi.Available) return;
            string who = SteamApi.NameOf(_pending) ?? "your friend";
            if (Net.Active)
            {
                if (!_toldInGame) { MpManager.Instance?.Toast(Loc.F("Leave this session first to join {0}", who)); _toldInGame = true; }
                return;
            }
            if (GameApi.InGame)
            {
                if (!_toldInGame) { MpManager.Instance?.Toast(Loc.F("Go back to the main menu to join {0}", who)); _toldInGame = true; }
                return;
            }
            Menu m = null;
            try { m = Menu.Instance; } catch { }
            if (m == null || m.menuPanel == null) return;   // main menu not up yet
            var id = _pending;
            _pending = 0;
            MenuUi.JoinSteamFriend(id);
        }
    }
}
