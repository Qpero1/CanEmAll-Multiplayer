using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace CanEmAllMP
{
    [BepInPlugin(Guid, "Can 'Em All Multiplayer", NetSession.ModVersion)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "arkin.canemall.multiplayer";
        public new static ManualLogSource Log;
        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<string> LastAddress;
        public static ConfigEntry<int> Port;
        public static ConfigEntry<UnityEngine.InputSystem.Key> MenuKey;
        public static ConfigEntry<bool> VacuumPullsPlayers;
        public static ConfigEntry<bool> RescueStuckCutelings;
        public static ConfigEntry<bool> SharedMoney;
        public static ConfigEntry<bool> SyncEnemies;
        public static ConfigEntry<bool> FriendlyFire;
        public static ConfigEntry<int> PlayerColor;
        public static ConfigEntry<int> HostSlot;
        public static ConfigEntry<int> MaxPlayers;
        public static ConfigEntry<string> DisabledPatches;
        public static ConfigEntry<bool> CanDiagnostics;

        public override void Load()
        {
            Log = base.Log;
            PlayerName = Config.Bind("General", "PlayerName", System.Environment.UserName, "Name shown above your head for other players");
            PlayerColor = Config.Bind("General", "PlayerColor", -1, "Your colour (0-7: Blue, Purple, Yellow, Green, Red, Orange, Pink, Cyan; -1 = any free one)");
            LastAddress = Config.Bind("General", "LastAddress", "127.0.0.1", "Last address you joined (IP or hostname)");
            Port = Config.Bind("General", "Port", 7777, "UDP port. The host must forward this UDP port on their router.");
            RescueStuckCutelings = Config.Bind("Fixes", "RescueStuckCutelings", true, "Host/solo: cutelings stuck behind the truck, out of the map, or never leaving the truck are put back inside the facility door");
            VacuumPullsPlayers = Config.Bind("Fun", "VacuumPullsPlayers", true, "Other players' vacuums suck you in (and their throw launches you). This is about you being pulled, so each player decides for themselves");
            MenuKey = Config.Bind("General", "MenuKey", UnityEngine.InputSystem.Key.F8, "Key that opens the multiplayer panel");
            SharedMoney = Config.Bind("Sync", "SharedMoney", true, "Everyone shares one wallet (host is authoritative)");
            SyncEnemies = Config.Bind("Sync", "SyncEnemies", true, "Host owns the creatures; clients see and hit the host's creatures");

            MaxPlayers = Config.Bind("General", "MaxPlayers", 8, "Most players in a session you host, including you (2-8; 0 = no limit)");
            HostSlot = Config.Bind("General", "HostSaveSlot", 1, "Save used when you host from MULTIPLAYER: 0 = your normal (single-player) save, 1-3 = multiplayer slots");
            FriendlyFire = Config.Bind("Sync", "FriendlyFire", true, "Host only: players' bullets can hit each other");
            DisabledPatches = Config.Bind("Debug", "DisabledPatches", "", "Comma list of hooks NOT to install: * = none at all, Enemy = every Enemy hook, Enemy.Damage = just that one");
            CanDiagnostics = Config.Bind("Debug", "CanDiagnostics", false, "Log what gets canned and what each new can looks like");

            ClassInjector.RegisterTypeInIl2Cpp<MpManager>();
            AddComponent<MpManager>();

            PatchRunner.Run(new Harmony(Guid), DisabledPatches.Value);
            Log.LogInfo($"Can 'Em All Multiplayer {NetSession.ModVersion} loaded. Press {MenuKey.Value} in game.");
        }
    }
}
