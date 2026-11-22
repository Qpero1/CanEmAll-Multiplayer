using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CanEmAllMP
{
    /// <summary>
    /// Global pause + "elevator waits for everyone".
    ///
    /// How the game works (from disassembly):
    ///  - Pause:  UpdatePauseInput flips GameManager.IsPaused then calls UpdatePause(), which sets
    ///            timeScale, pauses audio and shows the pause menu. Resume() clears IsPaused + UpdatePause().
    ///  - Elevator: GameLoop waits until GameManager.triggerCompleted is true. The outside/inside trigger
    ///            volumes (TriggerEvent -> OnTriggerOutsideElevator / OnTriggerInsideElevator) set it when
    ///            elevatorState is WaitingForPlayerOutside (1) / WaitingForPlayerInside (2).
    /// We hold that trigger back until every player has stepped into it, then the host releases it for all.
    /// </summary>
    public static class CoopSync
    {
        public const int Outside = 1, Inside = 2;
        private static NetSession Net => MpManager.Net;
        public static bool Active => Net.Connected && Net.Players.Count > 1;

        // ------------------------------------------------------------ pause

        public static bool ApplyingNetPause;
        private static int _lastSentPause = -1;

        /// The host's "Multiplayer pause" setting (clients get it from the host).
        ///   true  = pausing pauses everyone (the game's normal pause, shared)
        ///   false = Esc only opens your own menu; the game keeps running for everyone, nobody can stop the day
        public static bool SharedPause = true;

        /// Host: the config setting changed (or a player joined): tell the joiners.
        public static void SendPauseMode(int to = -1)
        {
            if (!Net.IsHost) return;
            Net.Begin(Msg.PauseMode).Put(SharedPause);
            if (to >= 0) Net.SendTo(to, DeliveryMethod.ReliableOrdered); else Net.Send(DeliveryMethod.ReliableOrdered);
        }

        /// Host, every frame: follow the config.
        public static void HostTickPauseMode()
        {
            if (!Net.IsHost || SharedPause == Plugin.SharedPause.Value) return;
            SetPauseMode(Plugin.SharedPause.Value);
            SendPauseMode();
        }

        public static void SetPauseMode(bool shared)
        {
            if (SharedPause == shared) return;
            SharedPause = shared;
            MpManager.Instance?.Feed("Multiplayer pause " + (shared ? "ON" : "OFF - the game keeps running while you're in the menu"));
            KeepRunningIfLocal();
        }

        /// Local pause mode: the pause menu may be open, but the game must not stop.
        public static void KeepRunningIfLocal()
        {
            if (SharedPause || !Active) return;
            var gm = GameManager.Instance;
            if (gm == null || !gm.IsPaused) return;
            if (Time.timeScale == 0f) Time.timeScale = 1f;
            AudioListener.pause = false;
        }

        /// Called after the game's own UpdatePause (postfix).
        public static void OnLocalPauseChanged(bool paused)
        {
            if (!SharedPause && Active) { KeepRunningIfLocal(); return; } // our menu only: nobody else is told
            if (ApplyingNetPause || !Active) return;
            int p = paused ? 1 : 0;
            if (p == _lastSentPause) return;
            _lastSentPause = p;
            Net.Begin(Msg.Pause).Put(paused);
            Net.W.Put(Net.LocalId);
            Net.Send(DeliveryMethod.ReliableOrdered);
        }

        public static void Receive(int from, Msg type, NetDataReader r)
        {
            if (type == Msg.PauseMode && Net.IsClient)
            {
                SetPauseMode(r.GetBool());
                return;
            }
            if (type == Msg.Pause)
            {
                if (!SharedPause) return;
                bool paused = r.GetBool();
                int who = r.GetInt();
                if (Net.IsHost)
                {
                    who = from;
                    Net.Begin(Msg.Pause).Put(paused); Net.W.Put(who);
                    Net.SendExcept(from, DeliveryMethod.ReliableOrdered);
                }
                ApplyPause(paused);
                MpManager.Instance?.Feed($"{Net.NameOf(who)} {(paused ? "paused" : "resumed")} the game");
            }
            else if (type == Msg.ElevatorReady && Net.IsHost)
            {
                int kind = r.GetByte();
                bool ready = r.GetBool();
                SetReady(from, kind, ready);
            }
            else if (type == Msg.ElevatorStatus && Net.IsClient)
            {
                _hostStatus = r.GetString(300);
                if (r.AvailableBytes >= 1) _hostState = r.GetByte();
                if (r.AvailableBytes >= 1) _hostDone = r.GetBool();
            }
            else if (type == Msg.ElevatorGo && Net.IsClient)
            {
                int kind = r.GetByte();
                _pendingGo = kind;
                _pendingGoTime = Time.unscaledTime;
            }
        }

        private static int _deferredPause = -1;

        private static bool MenuPanelVisible()
        {
            try { var m = Menu.Instance; return m != null && m.menuPanel != null && m.menuPanel.activeInHierarchy; }
            catch { return false; }
        }

        private static void ApplyPause(bool paused)
        {
            // Joiner still fading in from the menu: StartGameCoroutine's fade runs on game time,
            // so pausing now would freeze the menu on screen. Apply it once the run has started.
            if (Net.IsClient && Menu.Instance != null && Menu.Instance.requestedStartGame && (!HostSave.GameStarted || MenuPanelVisible()))
            {
                _deferredPause = paused ? 1 : 0;
                return;
            }
            var gm = GameManager.Instance;
            if (gm == null || !GameApi.InGame || gm.IsPaused == paused) { _lastSentPause = paused ? 1 : 0; return; }
            ApplyingNetPause = true;
            try
            {
                if (paused) { gm.IsPaused = true; gm.UpdatePause(); }
                else gm.Resume();
            }
            catch (System.Exception e) { Plugin.Log.LogError("Pause sync failed: " + e); }
            finally { ApplyingNetPause = false; _lastSentPause = paused ? 1 : 0; }
        }

        // ------------------------------------------------------------ elevator

        public static bool ReleasingElevator;
        private static readonly Dictionary<int, int> _readyKind = new Dictionary<int, int>(); // host: playerId -> kind they're standing in (0 = none)
        private static int _localReady;           // kind the local player is waiting in
        private static int _pendingGo;            // client: host said go, waiting for our state to match
        private static float _pendingGoTime;
        public static string WaitingText = "";
        private static string _lastSentStatus = "";
        private static string _hostStatus = "";
        private static int _lastSentState = -1;
        private static bool _lastSentDone;
        private static int _hostState = -1;
        private static bool _hostDone;       // host's trigger for the current phase already completed

        public static void ResendStatus() { _lastSentStatus = null; _lastSentState = -1; }

        public static void Reset()
        {
            _readyKind.Clear(); _localReady = 0; _pendingGo = 0; WaitingText = ""; _lastSentPause = -1; _deferredPause = -1; _lastSentStatus = ""; _hostStatus = ""; _lastSentState = -1; _hostState = -1; _hostDone = false;
        }

        /// Prefix for OnTriggerOutside/InsideElevator. Returns false to hold the trigger back.
        public static bool OnLocalTrigger(int kind)
        {
            if (!Active || ReleasingElevator) return true;
            var gm = GameManager.Instance;
            if (gm == null || (int)gm.elevatorState != kind) return true; // not the phase this trigger cares about
            if (gm.triggerCompleted) return true;                        // this phase already went through
            // late joiner: if the host's elevator isn't in this phase, there's nobody to wait for
            // (also: host state unknown yet, or the host already went through this phase before we joined)
            if (Net.IsClient && (_hostState != kind || _hostDone)) return true;
            if (_localReady == kind) return false;
            _localReady = kind;
            ReportLocal(kind, true);
            return false;
        }

        /// The local player left a trigger volume. Leaving it can mean either direction: the "outside" zone is
        /// a strip just past the door, so walking further into the factory also exits it. Only un-ready if
        /// they left towards the wrong side (back into the elevator for Outside, away from it for Inside).
        public static void OnLocalTriggerExit(int kind, Vector3 zoneCenter, Vector3 playerPos)
        {
            if (!Active || _localReady != kind) return;
            var gm = GameManager.Instance;
            var refT = gm != null ? (gm.elevatorStartPoint != null ? gm.elevatorStartPoint : gm.elevatorDoor) : null;
            if (refT != null)
            {
                var e = refT.position;
                float zoneDist = Flat(zoneCenter - e);
                float playerDist = Flat(playerPos - e);
                bool wentTowardsElevator = playerDist < zoneDist;
                if (kind == Outside && !wentTowardsElevator) return; // walked out into the factory: still out
                if (kind == Inside && wentTowardsElevator) return;   // walked deeper into the elevator: still in
            }
            _localReady = 0;
            ReportLocal(kind, false);
        }

        private static void ReportLocal(int kind, bool ready)
        {
            if (Net.IsHost) SetReady(0, kind, ready);
            else
            {
                Net.Begin(Msg.ElevatorReady).Put((byte)kind);
                Net.W.Put(ready);
                Net.Send(DeliveryMethod.ReliableOrdered);
            }
        }

        private static void SetReady(int player, int kind, bool ready)
        {
            if (ready) _readyKind[player] = kind;
            else if (_readyKind.TryGetValue(player, out var k) && k == kind) _readyKind.Remove(player);
        }

        /// Every frame.
        public static void Tick()
        {
            if (_deferredPause >= 0 && HostSave.GameStarted && !MenuPanelVisible())
            {
                bool p = _deferredPause == 1;
                _deferredPause = -1;
                ApplyPause(p);
            }
            var gm = GameManager.Instance;
            if (gm == null) return;
            int state = (int)gm.elevatorState;

            if (!Active)
            {
                // someone left mid-wait: don't leave the local player stuck
                if (_localReady != 0 && state == _localReady) Release(_localReady);
                _localReady = 0; WaitingText = "";
                return;
            }

            if (Net.IsHost)
            {
                for (int kind = Outside; kind <= Inside; kind++)
                {
                    if (state != kind) continue;
                    if (gm.triggerCompleted)
                    {
                        // the host already passed this phase (e.g. left the elevator before anyone joined):
                        // nobody needs to wait, release anyone still standing in a trigger
                        if (_readyKind.Count > 0)
                        {
                            Net.Begin(Msg.ElevatorGo).Put((byte)kind);
                            Net.Send(DeliveryMethod.ReliableOrdered);
                            _readyKind.Clear();
                        }
                        WaitingText = "";
                        continue;
                    }
                    var waitingFor = new List<string>();
                    foreach (var p in Net.Players.Values)
                    {
                        if (Spectate.IsPlayerDead(p.Id)) continue; // dead players can't walk in or out
                        if (!_readyKind.TryGetValue(p.Id, out var k) || k != kind) waitingFor.Add(p.Name);
                    }

                    if (waitingFor.Count == 0)
                    {
                        Net.Begin(Msg.ElevatorGo).Put((byte)kind);
                        Net.Send(DeliveryMethod.ReliableOrdered);
                        Release(kind);
                        foreach (var p in Net.Players.Values) _readyKind.Remove(p.Id);
                    }
                    else
                    {
                        WaitingText = (kind == Outside ? "Waiting for everyone to leave the elevator: " : "Waiting for everyone to get in the elevator: ")
                                      + string.Join(", ", waitingFor);
                    }
                }
                if (state != Outside && state != Inside) WaitingText = "";
                bool done = gm.triggerCompleted;
                if (WaitingText != _lastSentStatus || state != _lastSentState || done != _lastSentDone)
                {
                    _lastSentStatus = WaitingText; _lastSentState = state; _lastSentDone = done;
                    Net.Begin(Msg.ElevatorStatus).Put(WaitingText);
                    Net.W.Put((byte)state);
                    Net.W.Put(done);
                    Net.Send(DeliveryMethod.ReliableOrdered);
                }
            }
            else
            {
                if (_pendingGo != 0)
                {
                    if (state == _pendingGo) { Release(_pendingGo); _pendingGo = 0; }
                    else if (Time.unscaledTime - _pendingGoTime > 20f) _pendingGo = 0; // stale
                }
                if (_localReady != 0 && state == _localReady && (_hostState != _localReady || _hostDone)) Release(_localReady);
                WaitingText = _localReady != 0 || _hostState == Outside || _hostState == Inside ? _hostStatus : "";
            }
        }

        private static void Release(int kind)
        {
            var gm = GameManager.Instance;
            if (gm == null) return;
            ReleasingElevator = true;
            try
            {
                if (kind == Outside) gm.OnTriggerOutsideElevator();
                else gm.OnTriggerInsideElevator();
            }
            catch (System.Exception e) { Plugin.Log.LogError("Elevator release failed: " + e); }
            finally { ReleasingElevator = false; }
            if (_localReady == kind) _localReady = 0;
            WaitingText = "";
        }

        private static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        /// Which elevator trigger (if any) a TriggerEvent drives.
        public static int KindOf(TriggerEvent te)
        {
            try
            {
                var ev = te.triggerEvent;
                if (ev == null) return 0;
                int n = ev.GetPersistentEventCount();
                for (int i = 0; i < n; i++)
                {
                    var m = ev.GetPersistentMethodName(i);
                    if (m == "OnTriggerOutsideElevator") return Outside;
                    if (m == "OnTriggerInsideElevator") return Inside;
                }
            }
            catch { }
            return 0;
        }
    }
}
