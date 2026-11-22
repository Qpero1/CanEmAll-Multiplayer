using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using Il2CppInterop.Runtime;

namespace CanEmAllMP
{
    /// <summary>
    /// The host's save is THE save. A joiner connects from the main menu, receives the host's
    /// SaveData (JSON, gzip, chunked), swaps it into SaveManager.CurrentSaveData and presses Continue.
    /// Joiners never write anything to disk while using the host's save.
    ///
    /// Game facts this relies on (from disassembly):
    ///  - SaveManager.Awake reads save.sav (JsonUtility) into CurrentSaveData once.
    ///  - SaveManager.Save writes JsonUtility.ToJson(CurrentSaveData).
    ///  - Menu.ContinueGame -> GameManager.StartGame, which builds money/skills/upgrades from CurrentSaveData.
    /// </summary>
    public static class HostSave
    {
        private static NetSession Net => MpManager.Net;
        private const int Chunk = 30000;

        /// True once this client has swapped in the host's save. Stays true after a disconnect until the
        /// player is back on the main menu, so the host's data can never end up in the joiner's own save.sav.
        public static bool UsingHostSave;
        public static bool WaitingForSave;
        public static float MoneySettleUntil;
        /// True once StartGameCoroutine has finished its fade and built the run (MoneyManager.StartGame ran).
        public static bool GameStarted;
        /// Creature sync may run: either the host's save finished loading, or we joined mid-game on our own world.
        public static bool GameStartedOrLocal => GameStarted || !UsingHostSave;
        private static byte[] _rx;
        private static int _rxGot;

        public static bool InMainMenu
        {
            get
            {
                var m = Menu.Instance;
                return m != null && !m.requestedStartGame;
            }
        }

        // ------------------------------------------------------------- host

        public static void SendTo(int playerId)
        {
            var sm = SaveManager.Instance;
            if (sm == null || sm.CurrentSaveData == null) return;

            // copy the live save and patch in live values the game only writes at day end
            var copy = FromJson(ToJson(sm.CurrentSaveData));
            if (MoneyManager.Instance != null && GameApi.InGame) copy.money = MoneyManager.Instance.Money;
            string json = ToJson(copy);

            byte[] data;
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
                {
                    var raw = Encoding.UTF8.GetBytes(json);
                    gz.Write(raw, 0, raw.Length);
                }
                data = ms.ToArray();
            }

            int chunks = Math.Max(1, (data.Length + Chunk - 1) / Chunk);
            for (int i = 0; i < chunks; i++)
            {
                int off = i * Chunk, len = Math.Min(Chunk, data.Length - off);
                var w = Net.Begin(Msg.HostSave);
                w.Put(data.Length); w.Put(off);
                var part = new byte[len];
                Buffer.BlockCopy(data, off, part, 0, len);
                w.PutBytesWithLength(part);
                Net.SendTo(playerId, DeliveryMethod.ReliableOrdered);
            }
            Plugin.Log.LogInfo($"Sent save to {Net.NameOf(playerId)}: {json.Length} chars json, {data.Length} bytes gz, {chunks} chunk(s)");
        }

        // Unity's managed JsonUtility wrappers call Type helpers that are stripped from this build,
        // so go straight to the native internal calls.
        private static string ToJson(SaveData d) => JsonUtility.ToJsonInternal(d, false);
        internal static SaveData FromJson(string json)
        {
            var o = JsonUtility.FromJsonInternal(json, null, Il2CppType.Of<SaveData>());
            return o?.Cast<SaveData>();
        }

        // ------------------------------------------------------------- client

        public static void OnConnected()
        {
            _rx = null; _rxGot = 0;
            WaitingForSave = InMainMenu;
            if (!WaitingForSave)
                MpManager.Instance?.Feed("You joined mid-game, so you're playing on your own loaded world. Join from the main menu to use the host's save.");
            else
                MpManager.Instance?.Feed("Downloading the host's save...");
        }

        private const int MaxCompressed = 4 << 20, MaxJson = 32 << 20;

        public static void Receive(NetDataReader r)
        {
            int total = r.GetInt();
            int off = r.GetInt();
            var part = r.GetBytesWithLength();
            if (!WaitingForSave) return;
            // a real save is a few KB: refuse absurd sizes (a bad host could otherwise make us allocate gigabytes)
            if (total <= 0 || total > MaxCompressed || part == null || off < 0 || off + part.Length > total)
            {
                Plugin.Log.LogWarning($"[MP] host save chunk refused (size {total}, offset {off})");
                _rx = null;
                return;
            }
            if (_rx == null || _rx.Length != total) { _rx = new byte[total]; _rxGot = 0; }
            Buffer.BlockCopy(part, 0, _rx, off, part.Length);
            _rxGot += part.Length;
            if (_rxGot < total) return;

            string json;
            try
            {
                // unpack with a cap, so a "zip bomb" can't eat all our memory
                using (var ms = new MemoryStream(_rx))
                using (var gz = new GZipStream(ms, CompressionMode.Decompress))
                using (var outMs = new MemoryStream())
                {
                    var buf = new byte[65536];
                    int n;
                    while ((n = gz.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (outMs.Length + n > MaxJson) throw new InvalidDataException("host save unpacks to more than " + (MaxJson >> 20) + " MB");
                        outMs.Write(buf, 0, n);
                    }
                    json = Encoding.UTF8.GetString(outMs.GetBuffer(), 0, (int)outMs.Length);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[MP] host save refused: " + e.Message);
                MpManager.Instance?.Feed("The host's save couldn't be read (see BepInEx log).");
                _rx = null;
                return;
            }
            _rx = null;
            WaitingForSave = false;
            LoadAndStart(json);
        }

        private static void LoadAndStart(string json)
        {
            var sm = SaveManager.Instance;
            var menu = Menu.Instance;
            if (sm == null || menu == null || !InMainMenu)
            {
                MpManager.Instance?.Feed("Got the host's save but you're not on the main menu, so it wasn't loaded.");
                return;
            }
            try
            {
                var data = FromJson(json);
                if (data == null) throw new Exception("host save json didn't parse");
                UsingHostSave = true;          // set BEFORE anything could call Save()
                sm.CurrentSaveData = data;
                MpManager.CloseMenuForGameplay();
                MpManager.Instance?.Feed($"Loaded {Net.NameOf(0)}'s save (day {data.day}). Starting...");
                MoneySettleUntil = Time.unscaledTime + 8f; // StartGame sets money from the save over the next few seconds
                menu.ContinueGame();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Loading host save failed: " + e);
                MpManager.Instance?.Feed("Loading the host's save failed (see BepInEx log).");
            }
        }

        /// Back on the main menu and not connected: put the joiner's own save back in memory.
        public static void OnMainMenu()
        {
            GameStarted = false;
            if (!UsingHostSave || Net.Active) return;
            UsingHostSave = false;
            try { SaveManager.Instance?.Load(); Plugin.Log.LogInfo("Restored your own save after leaving the host's game."); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }
    }
}
