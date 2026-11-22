using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace CanEmAllMP
{
    /// Direct calls into the game's own steam_api64.dll (Steamworks flat API). The game already starts Steam
    /// (it ships Facepunch.Steamworks), so this just borrows that: no extra login, and it uses Can 'Em All's own
    /// Steam app. Everything here fails soft: if the game was started without Steam, Available is false.
    public static unsafe class SteamApi
    {
        const string Dll = "steam_api64";

        static bool _tried;
        public static IntPtr Sockets, Utils, Friends, User, NetUtils;
        public static ulong MySteamId;
        /// The logged-in Steam account has Can 'Em All (bought, family sharing or free weekend).
        public static bool OwnsGame;
        public static uint AppId;
        public static string Error;

        public static bool Available
        {
            get
            {
                if (!_tried) Init();
                return Sockets != IntPtr.Zero && Friends != IntPtr.Zero && MySteamId != 0;
            }
        }

        static void Init()
        {
            _tried = true;
            try
            {
                NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), Resolve);
            }
            catch { /* already set */ }
            try
            {
                if (!SteamAPI_IsSteamRunning()) { Error = "Steam isn't running"; return; }
                User = SteamAPI_SteamUser_v023();
                Sockets = SteamAPI_SteamNetworkingSockets_SteamAPI_v012();
                Friends = SteamAPI_SteamFriends_v017();
                Utils = SteamAPI_SteamUtils_v010();
                NetUtils = SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
                if (User == IntPtr.Zero || Sockets == IntPtr.Zero || Friends == IntPtr.Zero)
                {
                    Error = "The game didn't start Steam (launch it from Steam)";
                    Sockets = IntPtr.Zero;
                    return;
                }
                MySteamId = SteamAPI_ISteamUser_GetSteamID(User);
                AppId = Utils != IntPtr.Zero ? SteamAPI_ISteamUtils_GetAppID(Utils) : 0;
                try { var apps = SteamAPI_SteamApps_v008(); OwnsGame = apps != IntPtr.Zero && SteamAPI_ISteamApps_BIsSubscribed(apps); }
                catch { OwnsGame = false; }
                if (NetUtils != IntPtr.Zero) SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(NetUtils);
                Plugin.Log.LogInfo($"[Steam] ready: app {AppId}, me {MySteamId}, owns the game: {OwnsGame}");
            }
            catch (Exception e)
            {
                Error = "Steam isn't available (" + e.GetType().Name + ")";
                Sockets = IntPtr.Zero;
                Plugin.Log.LogWarning("[Steam] " + e);
            }
        }

        /// The game loads steam_api64.dll from its Plugins folder; point our imports at that same copy.
        static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
        {
            if (name != Dll) return IntPtr.Zero;
            var h = GetModuleHandleW("steam_api64.dll");
            if (h != IntPtr.Zero) return h;
            try
            {
                var p = Path.Combine(Application.dataPath, "Plugins", "x86_64", "steam_api64.dll");
                if (File.Exists(p) && NativeLibrary.TryLoad(p, out h)) return h;
            }
            catch { }
            return IntPtr.Zero;
        }

        // ---------------------------------------------------------------- friends / presence

        public const string PresenceKey = "canemall_mp";

        public static void SetHosting(string version)
        {
            if (!Available) return;
            try
            {
                SteamAPI_ISteamFriends_SetRichPresence(Friends, PresenceKey, version);
                SteamAPI_ISteamFriends_SetRichPresence(Friends, "status", "Hosting multiplayer");
                // makes Steam show "Join Game" on our profile / in friends' lists
                SteamAPI_ISteamFriends_SetRichPresence(Friends, "connect", SteamJoin.ConnectString(MySteamId));
            }
            catch (Exception e) { Plugin.Log.LogWarning("[Steam] presence: " + e.Message); }
        }

        public static void ClearHosting()
        {
            if (Friends == IntPtr.Zero) return;
            try
            {
                SteamAPI_ISteamFriends_SetRichPresence(Friends, PresenceKey, "");
                SteamAPI_ISteamFriends_SetRichPresence(Friends, "status", "");
                SteamAPI_ISteamFriends_SetRichPresence(Friends, "connect", "");
            }
            catch { }
        }

        public struct Host { public ulong Id; public string Name; public string Version; }

        /// Steam friends who are in Can 'Em All right now and hosting a multiplayer game (have our presence key set).
        public static List<Host> FriendHosts(bool refresh)
        {
            var list = new List<Host>();
            if (!Available) return list;
            try
            {
                int n = SteamAPI_ISteamFriends_GetFriendCount(Friends, 4 /* k_EFriendFlagImmediate */);
                byte* info = stackalloc byte[32];
                for (int i = 0; i < n && i < 2000; i++)
                {
                    ulong id = SteamAPI_ISteamFriends_GetFriendByIndex(Friends, i, 4);
                    if (id == 0) continue;
                    for (int k = 0; k < 32; k++) info[k] = 0;
                    if (!SteamAPI_ISteamFriends_GetFriendGamePlayed(Friends, id, info)) continue;
                    ulong game = *(ulong*)info;
                    if (AppId != 0 && (uint)(game & 0xFFFFFF) != AppId) continue;
                    if (refresh) SteamAPI_ISteamFriends_RequestFriendRichPresence(Friends, id);
                    var v = Utf8(SteamAPI_ISteamFriends_GetFriendRichPresence(Friends, id, PresenceKey));
                    if (string.IsNullOrEmpty(v)) continue;
                    list.Add(new Host { Id = id, Name = Utf8(SteamAPI_ISteamFriends_GetFriendPersonaName(Friends, id)) ?? id.ToString(), Version = v });
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[Steam] friends: " + e.Message); }
            return list;
        }

        public static string NameOf(ulong id)
        {
            try { return Utf8(SteamAPI_ISteamFriends_GetFriendPersonaName(Friends, id)); } catch { return null; }
        }

        static string Utf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUTF8(p); } catch { return null; }
        }

        // ---------------------------------------------------------------- imports

        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SteamAPI_IsSteamRunning();
        [DllImport(Dll)] static extern IntPtr SteamAPI_SteamUser_v023();
        [DllImport(Dll)] static extern IntPtr SteamAPI_SteamNetworkingSockets_SteamAPI_v012();
        [DllImport(Dll)] static extern IntPtr SteamAPI_SteamFriends_v017();
        [DllImport(Dll)] static extern IntPtr SteamAPI_SteamUtils_v010();
        [DllImport(Dll)] static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();

        [DllImport(Dll)] static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
        [DllImport(Dll)] static extern IntPtr SteamAPI_SteamApps_v008();
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SteamAPI_ISteamApps_BIsSubscribed(IntPtr self);
        [DllImport(Dll)] static extern uint SteamAPI_ISteamUtils_GetAppID(IntPtr self);
        [DllImport(Dll)] static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);

        [DllImport(Dll)] static extern int SteamAPI_ISteamFriends_GetFriendCount(IntPtr self, int flags);
        [DllImport(Dll)] static extern ulong SteamAPI_ISteamFriends_GetFriendByIndex(IntPtr self, int i, int flags);
        [DllImport(Dll)] static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong id);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SteamAPI_ISteamFriends_GetFriendGamePlayed(IntPtr self, ulong id, byte* info);
        [DllImport(Dll)] static extern IntPtr SteamAPI_ISteamFriends_GetFriendRichPresence(IntPtr self, ulong id, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
        [DllImport(Dll)] static extern void SteamAPI_ISteamFriends_RequestFriendRichPresence(IntPtr self, ulong id);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.U1)]
        static extern bool SteamAPI_ISteamFriends_SetRichPresence(IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        // networking sockets
        [StructLayout(LayoutKind.Sequential)]
        public struct ConfigValue { public int Value; public int DataType; public long Val; }

        [DllImport(Dll)] public static extern uint SteamAPI_ISteamNetworkingSockets_CreateListenSocketP2P(IntPtr self, int virtualPort, int nOptions, ConfigValue* options);
        [DllImport(Dll)] public static extern uint SteamAPI_ISteamNetworkingSockets_ConnectP2P(IntPtr self, byte* identity, int virtualPort, int nOptions, ConfigValue* options);
        [DllImport(Dll)] public static extern int SteamAPI_ISteamNetworkingSockets_AcceptConnection(IntPtr self, uint conn);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool SteamAPI_ISteamNetworkingSockets_CloseConnection(IntPtr self, uint conn, int reason, [MarshalAs(UnmanagedType.LPUTF8Str)] string debug, [MarshalAs(UnmanagedType.U1)] bool linger);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool SteamAPI_ISteamNetworkingSockets_CloseListenSocket(IntPtr self, uint socket);
        [DllImport(Dll)] public static extern int SteamAPI_ISteamNetworkingSockets_SendMessageToConnection(IntPtr self, uint conn, byte* data, uint len, int flags, long* msgNum);
        [DllImport(Dll)] public static extern int SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnConnection(IntPtr self, uint conn, IntPtr* msgs, int max);
        [DllImport(Dll)] public static extern void SteamAPI_ISteamNetworkingSockets_RunCallbacks(IntPtr self);
        [DllImport(Dll)] public static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr msg);
        [DllImport(Dll)] public static extern void SteamAPI_SteamNetworkingIdentity_SetSteamID64(byte* identity, ulong id);
    }
}
