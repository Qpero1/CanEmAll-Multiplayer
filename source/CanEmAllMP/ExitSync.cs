using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CanEmAllMP
{
    /// The exit door and its elevator (ExitDoor): the door opens for everyone when anyone opens it, and the
    /// exit elevator waits until every living player has walked in, then starts for everyone at once
    /// (like the normal elevator wait). Without this whoever walked in first left the others behind.
    public static class ExitSync
    {
        private static NetSession Net => MpManager.Net;
        private static ExitDoor _door;
        public static bool Releasing, ApplyingOpen;
        private static bool _localReady;
        private static bool _done;   // the exit already started: ignore the trigger firing again
        private static readonly HashSet<int> Ready = new HashSet<int>(); // host
        public static string WaitingText = "";
        private static string _hostText = "", _lastSent = "";

        public static void OnDoorStart(ExitDoor d) { _door = d; _done = false; _localReady = false; Ready.Clear(); WaitingText = ""; _hostText = ""; }

        /// Postfix of ExitDoor.Open: open it for the others too.
        public static void OnLocalOpen()
        {
            if (ApplyingOpen || !CoopSync.Active) return;
            Plugin.Log.LogInfo("[MP] exit door opened: opening it for everyone");
            Net.Begin(Msg.ExitDoorOpen);
            Net.Send(DeliveryMethod.ReliableOrdered);
        }

        /// Prefix of ExitDoor.OnTriggerInsideElevator. false = hold it until everyone's in.
        public static bool OnLocalEnter()
        {
            if (!CoopSync.Active || Releasing) return true;
            if (_done) return false; // already on our way out: don't start it again or wait again
            if (!_localReady) { _localReady = true; Report(true); }
            return false;
        }

        private static void Report(bool ready)
        {
            if (Net.IsHost) { if (ready) Ready.Add(Net.LocalId); else Ready.Remove(Net.LocalId); }
            else { Net.Begin(Msg.ExitReady).Put(ready); Net.Send(DeliveryMethod.ReliableOrdered); }
        }

        public static void Receive(int from, Msg type, NetDataReader r)
        {
            switch (type)
            {
                case Msg.ExitDoorOpen:
                    if (Net.IsHost) { Net.Begin(Msg.ExitDoorOpen); Net.SendExcept(from, DeliveryMethod.ReliableOrdered); }
                    if (_door != null)
                    {
                        ApplyingOpen = true;
                        try { _door.Open(); } catch (System.Exception e) { Plugin.Log.LogWarning("exit door open failed: " + e.Message); }
                        finally { ApplyingOpen = false; }
                    }
                    break;
                case Msg.ExitReady when Net.IsHost:
                    { bool rd = r.GetBool(); if (_done) break; if (rd) Ready.Add(from); else Ready.Remove(from); }
                    break;
                case Msg.ExitGo when Net.IsClient:
                    Release();
                    break;
                case Msg.ExitStatus when Net.IsClient:
                    _hostText = r.GetString(300);
                    if (_done) _hostText = "";
                    break;
            }
        }

        public static void Tick()
        {
            if (_done || !GameApi.InGame) { WaitingText = ""; _hostText = ""; if (_done) Ready.Clear(); return; }
            if (!CoopSync.Active) { if (_localReady) { _localReady = false; Release(); } Ready.Clear(); WaitingText = ""; return; }

            // walked back out of the exit elevator: not ready any more
            if (_localReady && _door != null && _door.elevatorDoor != null)
            {
                var p = Player.Instance;
                if (p != null && Vector3.Distance(p.transform.position, _door.elevatorDoor.position) > 8f) { _localReady = false; Report(false); }
            }

            if (Net.IsHost)
            {
                string text = "";
                if (Ready.Count > 0)
                {
                    var waiting = new List<string>();
                    foreach (var pl in Net.Players.Values)
                        if (!Spectate.IsPlayerDead(pl.Id) && !Ready.Contains(pl.Id)) waiting.Add(pl.Name);
                    if (waiting.Count == 0)
                    {
                        Plugin.Log.LogInfo("[MP] everyone is in the exit elevator: going");
                        Net.Begin(Msg.ExitGo); Net.Send(DeliveryMethod.ReliableOrdered);
                        Ready.Clear();
                        Release();
                    }
                    else text = "Waiting for everyone to get in the exit elevator: " + string.Join(", ", waiting);
                }
                WaitingText = text;
                if (text != _lastSent) { _lastSent = text; Net.Begin(Msg.ExitStatus).Put(text); Net.Send(DeliveryMethod.ReliableOrdered); }
            }
            else WaitingText = _hostText;
        }

        private static void Release()
        {
            _done = true;
            _localReady = false;
            WaitingText = ""; _hostText = "";
            if (_door == null) return;
            if (Spectate.Dead) Spectate.Revive("leaving through the exit");
            Releasing = true;
            try { _door.OnTriggerInsideElevator(); }
            catch (System.Exception e) { Plugin.Log.LogError("exit elevator release failed: " + e); }
            finally { Releasing = false; }
        }
    }
}
