using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CanEmAllMP
{
    /// <summary>
    /// "Press to continue" ready check (end of day upgrade screen, and after a suicide).
    ///
    /// The game's loop waits for  Player.playerInput.actions["NextDay"].IsPressed()  OR  GameManager.isNextDayClicked,
    /// then clears the flag and moves on. While in a session we intercept that IsPressed (it only reports false), treat a real
    /// press as "I'm ready", and when everyone is ready the host sets isNextDayClicked on every machine at once.
    /// The game only polls that action while it's waiting, which is how we know the screen is up.
    /// </summary>
    public static class ContinueSync
    {
        private static NetSession Net => MpManager.Net;
        public static bool Bypass;
        private static IntPtr _actionPtr;
        private static float _lastResolve;
        private static float _phaseUntil;          // the game polled the NextDay action recently = continue screen is up
        private static bool _localReady;
        private static readonly HashSet<int> _ready = new HashSet<int>(); // host
        private static string _lastSent = "";
        public static string Text = "";

        public static bool InPhase => Time.unscaledTime < _phaseUntil;

        private static IntPtr NextDayAction()
        {
            if (_actionPtr != IntPtr.Zero || Time.unscaledTime - _lastResolve < 1f) return _actionPtr;
            _lastResolve = Time.unscaledTime;
            try
            {
                var a = Player.Instance?.playerInput?.actions?["NextDay"];
                if (a != null) _actionPtr = a.Pointer;
            }
            catch { }
            return _actionPtr;
        }

        public static void ResetAction() { _actionPtr = IntPtr.Zero; }

        /// Escape (pause) is held right now.
        public static bool EscapeHeld()
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                return kb != null && kb.escapeKey.isPressed;
            }
            catch { return false; }
        }

        /// The "Waiting for X to continue" line: only while this player is actually on the continue screen.
        public static string VisibleText => InPhase ? Text : "";

        /// Prefix for InputAction.IsPressed. Returns false to replace the result.
        public static bool OnIsPressed(UnityEngine.InputSystem.InputAction action, ref bool result)
        {
            if (Bypass || !CoopSync.Active || action == null) return true;
            var ptr = NextDayAction();
            if (ptr == IntPtr.Zero || action.Pointer != ptr) return true;

            _phaseUntil = Time.unscaledTime + 0.75f;
            bool real;
            Bypass = true;
            try { real = action.IsPressed(); } finally { Bypass = false; }
            // the NextDay action also fires on Escape, which opens the pause menu: that must not count as "ready"
            if (real && EscapeHeld()) real = false;
            if (real) LocalPressed();
            result = false;   // the host's release (isNextDayClicked) is what moves everyone on
            return false;
        }

        /// The player pressed continue (key or the on-screen button).
        public static void LocalPressed()
        {
            if (_localReady || !InPhase) return;
            _localReady = true;
            if (Net.IsHost) _ready.Add(0);
            else { Net.Begin(Msg.ContinueReady); Net.Send(DeliveryMethod.ReliableOrdered); }
        }

        public static void Receive(int from, Msg type, NetDataReader r)
        {
            if (type == Msg.ContinueReady && Net.IsHost) _ready.Add(from);
            else if (type == Msg.ContinueGo && Net.IsClient) Release();
            else if (type == Msg.ContinueStatus && Net.IsClient) Text = r.GetString(300);
        }

        private static void Release()
        {
            _localReady = false;
            Text = "";
            var gm = GameManager.Instance;
            if (gm != null) gm.isNextDayClicked = true;
        }

        public static void Tick()
        {
            if (!CoopSync.Active) { _localReady = false; _ready.Clear(); Text = ""; return; }
            if (!InPhase) _localReady = false;
            if (!Net.IsHost) { if (!InPhase && !_localReady) Text = ""; return; }

            if (_ready.Count == 0) { SetStatus(""); return; }
            var waiting = new List<string>();
            foreach (var p in Net.Players.Values) if (!_ready.Contains(p.Id)) waiting.Add(p.Name);
            if (waiting.Count == 0)
            {
                _ready.Clear();
                Net.Begin(Msg.ContinueGo);
                Net.Send(DeliveryMethod.ReliableOrdered);
                SetStatus("");
                Release();
                return;
            }
            SetStatus("Waiting for " + string.Join(", ", waiting) + " to continue");
        }

        private static void SetStatus(string s)
        {
            Text = s;
            if (s == _lastSent) return;
            _lastSent = s;
            Net.Begin(Msg.ContinueStatus).Put(s);
            Net.Send(DeliveryMethod.ReliableOrdered);
        }

        public static void PlayerLeft(int id) { _ready.Remove(id); }
        public static void Reset() { _localReady = false; _ready.Clear(); Text = ""; _lastSent = ""; _actionPtr = IntPtr.Zero; }
    }
}
