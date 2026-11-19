using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CanEmAllMP
{
    /// <summary>
    /// Shared slot machine: one player's spin plays on everyone's slot machine with the same result,
    /// and nobody can spin again until every player has seen it finish.
    ///
    /// How the game's SlotMachine works (from the IL2CPP code):
    ///   Spin()          reads the bet from moneyInputField, checks money, totals the stat, takes the bet
    ///                   (MoneyManager.ChangeMoney), clears the input, StartCoroutine(SpinCoroutine()).
    ///   SpinCoroutine   sets IsSpinning, GenerateResult() -> int[3] prize per reel, spins/stops the reels,
    ///                   then ResolveResult() -> ShowResult(win, payout, newMoney).
    ///   ShowResult      animates the result; its tween callback &lt;ShowResult&gt;b__4 does
    ///                   MoneyManager.ChangeMoney(newMoney) and IsSpinning = false.
    /// Replays on other machines skip Spin() entirely (no bet taken), force GenerateResult to the spinner's
    /// result, and skip the ChangeMoney in b__4 (the spinner's money change already reaches everyone through
    /// the shared wallet) while still clearing IsSpinning.
    /// Nothing is forced open: if a player's slot machine isn't on screen the spin waits and plays
    /// when they open the gamble tab (if it would still be running), otherwise it counts as seen.
    /// </summary>
    public static class GambleSync
    {
        private static NetSession Net => MpManager.Net;

        private const float LockTimeout = 30f;   // a spin can hold the lock at most this long (lost packets, AFK tab...)

        // ---- the spin everyone is currently watching (one at a time)
        private static int _spinner = -1, _seq;
        private static float _startedAt;
        private static readonly HashSet<int> _done = new HashSet<int>();

        // ---- this machine
        private static int _localSeqCounter;
        private static bool _localSpinning;           // our own real spin is running
        private static bool _suppressBroadcast;       // GenerateResult call was a forced replay
        private static int[] _forceResult;            // consumed by the next GenerateResult
        private static int[] _pendingResult;          // replay waiting for the slot machine to be on screen
        private static long _pendingBet;
        private static float _pendingUntil;
        public static bool Replaying { get; private set; }
        private static float _replayStartedAt;
        private static float _lastToast;

        public static bool Busy => _spinner >= 0;

        // ---- shared bet amount (the slot machine's MoneyInputField)
        private static long _lastBet = -1;      // last value we saw / applied
        private static bool _betDirty;
        private static float _betSendAt;
        private static long _pendingRemoteBet = -1; // arrived while our slot machine wasn't loaded

        public static void Reset()
        {
            _spinner = -1; _seq = 0; _done.Clear();
            _localSpinning = false; _suppressBroadcast = false; _forceResult = null; _pendingResult = null;
            if (Replaying)
            {
                var sm = SlotMachine.Instance;
                try { if (sm != null) sm.IsSpinning = false; } catch { }
            }
            Replaying = false;
            _lastBet = -1; _betDirty = false; _pendingRemoteBet = -1;
        }

        // ================================================================ bet amount

        private static MoneyInputField BetField()
        {
            try { return SlotMachine.Instance?.moneyInputField; } catch { return null; }
        }

        /// Every frame: send our bet when we change it (max 10 per second, always the final value).
        private static void TickBet()
        {
            var f = BetField();
            if (f == null) return;

            if (_pendingRemoteBet >= 0) { ApplyBet(f, _pendingRemoteBet); _pendingRemoteBet = -1; return; }

            long v = f.Value;
            if (_lastBet < 0) { _lastBet = v; return; }
            if (v != _lastBet) { _lastBet = v; _betDirty = true; }
            if (_betDirty && Time.unscaledTime >= _betSendAt)
            {
                _betDirty = false;
                _betSendAt = Time.unscaledTime + 0.1f;
                var w = Net.Begin(Msg.GambleBet); w.Put(Net.LocalId); w.Put(v);
                Net.Send(DeliveryMethod.ReliableOrdered);
            }
        }

        private static void ApplyBet(MoneyInputField f, long v)
        {
            try { f.SetValue(v); } catch { }   // the game clamps it to the money available (shared wallet)
            _lastBet = f.Value;                 // don't echo it back
            _betDirty = false;
        }

        // ================================================================ hooks

        /// Prefix of SlotMachine.Spin. false = refuse the spin.
        public static bool AllowSpin(SlotMachine sm)
        {
            if (!CoopSync.Active) return true;
            if (Replaying) { Wait("someone's spin is still playing"); return false; }
            if (!Busy) return true;
            if (_spinner == Net.LocalId && _localSpinning) return true; // the game itself ignores this (IsSpinning)
            Wait(WaitingText());
            return false;
        }

        /// Prefix of SlotMachine.GenerateResult: a replay gets the spinner's result.
        public static bool ForceResult(ref Il2CppStructArray<int> result)
        {
            if (_forceResult == null) return true;
            var arr = new Il2CppStructArray<int>(_forceResult.Length);
            for (int i = 0; i < _forceResult.Length; i++) arr[i] = _forceResult[i];
            result = arr;
            _forceResult = null;
            _suppressBroadcast = true;
            return false;
        }

        /// Postfix of SlotMachine.GenerateResult: a real local spin -> send its result to everyone.
        public static void OnResultGenerated(SlotMachine sm, Il2CppStructArray<int> result)
        {
            if (_suppressBroadcast) { _suppressBroadcast = false; return; }
            if (!CoopSync.Active || sm == null || result == null) return;

            int seq = ++_localSeqCounter;
            Begin(Net.LocalId, seq);
            _localSpinning = true;

            var w = Net.Begin(Msg.GambleSpin);
            w.Put(Net.LocalId); w.Put(seq); w.Put(sm.currentBet);
            w.Put((byte)result.Length);
            for (int i = 0; i < result.Length; i++) w.Put(result[i]);
            Net.Send(DeliveryMethod.ReliableOrdered);
        }

        /// Prefix of SlotMachine.&lt;&gt;c__DisplayClass85_0.&lt;ShowResult&gt;b__4 (pays out, then clears IsSpinning).
        /// false = skip it: replays must not touch money.
        public static bool AllowPayout(SlotMachine.__c__DisplayClass85_0 closure)
        {
            if (!Replaying) return true;
            try { var sm = closure.__4__this; if (sm != null) sm.IsSpinning = false; } catch { }
            return false;
        }

        /// Prefix of GameManager.OnClickButtonPanel (tab buttons): the game refuses to switch tabs while the slot
        /// machine is spinning, which would lock everyone else's tabs during someone's replayed spin.
        /// Lift the flag for that one call when the spin is only a replay. Returns true if it was lifted.
        public static bool LiftSpinForTabSwitch()
        {
            if (!Replaying) return false;
            var sm = SlotMachine.Instance;
            if (sm == null || !sm.IsSpinning) return false;
            sm.IsSpinning = false;
            return true;
        }

        /// Postfix of the same: put the flag back if the replay is still running (if the switch hid the slot
        /// machine its coroutine is gone, and Tick will end the replay).
        public static void RestoreSpinAfterTabSwitch()
        {
            var sm = SlotMachine.Instance;
            if (!Replaying || sm == null) return;
            bool onScreen = false;
            try { onScreen = sm.isActiveAndEnabled; } catch { }
            if (onScreen) sm.IsSpinning = true;
        }

        // ================================================================ network

        public static void Receive(int from, Msg type, NetDataReader r)
        {
            switch (type)
            {
                case Msg.GambleSpin:
                {
                    int spinner = r.GetInt(), seq = r.GetInt();
                    long bet = r.GetLong();
                    int n = r.GetByte();
                    var res = new int[n];
                    for (int i = 0; i < n; i++) res[i] = r.GetInt();
                    if (Net.IsHost) Relay(from, Msg.GambleSpin, spinner, seq, bet, res);
                    if (spinner == Net.LocalId) return;
                    Begin(spinner, seq);
                    QueueReplay(bet, res);
                    break;
                }
                case Msg.GambleBet:
                {
                    int who = r.GetInt(); long v = r.GetLong();
                    if (Net.IsHost)
                    {
                        var w = Net.Begin(Msg.GambleBet); w.Put(who); w.Put(v);
                        Net.SendExcept(from, DeliveryMethod.ReliableOrdered);
                    }
                    if (who == Net.LocalId) return;
                    var f = BetField();
                    if (f != null) ApplyBet(f, v); else _pendingRemoteBet = v;
                    break;
                }
                case Msg.GambleDone:
                {
                    int spinner = r.GetInt(), seq = r.GetInt(), who = r.GetInt();
                    if (Net.IsHost)
                    {
                        var w = Net.Begin(Msg.GambleDone); w.Put(spinner); w.Put(seq); w.Put(who);
                        Net.SendExcept(from, DeliveryMethod.ReliableOrdered);
                    }
                    MarkDone(spinner, seq, who);
                    break;
                }
            }
        }

        private static void Relay(int from, Msg type, int spinner, int seq, long bet, int[] res)
        {
            var w = Net.Begin(type);
            w.Put(spinner); w.Put(seq); w.Put(bet); w.Put((byte)res.Length);
            foreach (var x in res) w.Put(x);
            Net.SendExcept(from, DeliveryMethod.ReliableOrdered);
        }

        public static void PlayerLeft(int id) => CheckAllDone();

        // ================================================================ state

        private static void Begin(int spinner, int seq)
        {
            _spinner = spinner; _seq = seq; _startedAt = Time.unscaledTime;
            _done.Clear();
        }

        private static void SendDone()
        {
            if (_spinner < 0) return;
            // read the spin's id first: marking ourselves done can complete the round and clear _spinner,
            // and the others must still be told which spin we finished
            int spinner = _spinner, seq = _seq, me = Net.LocalId;
            var w = Net.Begin(Msg.GambleDone); w.Put(spinner); w.Put(seq); w.Put(me);
            Net.Send(DeliveryMethod.ReliableOrdered);
            MarkDone(spinner, seq, me);
        }

        private static void MarkDone(int spinner, int seq, int who)
        {
            if (spinner != _spinner || seq != _seq) return;
            _done.Add(who);
            CheckAllDone();
        }

        private static void CheckAllDone()
        {
            if (_spinner < 0) return;
            foreach (var id in Net.Players.Keys)
                if (!_done.Contains(id)) return;
            _spinner = -1;
        }

        private static string WaitingText()
        {
            var waiting = new List<string>();
            foreach (var p in Net.Players.Values)
                if (!_done.Contains(p.Id)) waiting.Add(p.Id == Net.LocalId ? "you" : p.Name);
            string who = _spinner == Net.LocalId ? "your" : Net.NameOf(_spinner) + "'s";
            return waiting.Count > 0 ? $"waiting for {string.Join(", ", waiting)} to see {who} spin" : "a spin is finishing";
        }

        private static void Wait(string why)
        {
            if (Time.unscaledTime - _lastToast < 1.5f) return;
            _lastToast = Time.unscaledTime;
            MpManager.Instance?.Toast("Can't gamble yet: " + why);
        }

        // ================================================================ replay

        private static float ExpectedDuration(SlotMachine sm)
        {
            float d = 6f;
            try
            {
                if (sm != null)
                    d = sm.spinDuration + sm.reelStopInterval * 3f + sm.decelerationDuration
                        + sm.resultFadeInDuration + sm.resultVisibleDuration + 1f;
            }
            catch { }
            return Mathf.Clamp(d, 3f, 20f);
        }

        private static void QueueReplay(long bet, int[] result)
        {
            _pendingResult = result;
            _pendingBet = bet;
            _pendingUntil = Time.unscaledTime + ExpectedDuration(SlotMachine.Instance);
            TryStartReplay();
        }

        private static void TryStartReplay()
        {
            if (_pendingResult == null || Replaying) return;
            var sm = SlotMachine.Instance;

            bool onScreen = false;
            try { onScreen = sm != null && sm.isActiveAndEnabled; } catch { }
            if (!onScreen || sm.IsSpinning)
            {
                // not looking at the slot machine (or it's mid something): it counts as seen once the spin would be over
                if (Time.unscaledTime > _pendingUntil) { _pendingResult = null; SendDone(); }
                return;
            }

            var result = _pendingResult;
            _pendingResult = null;
            try
            {
                Replaying = true;
                _replayStartedAt = Time.unscaledTime;
                _forceResult = result;
                sm.currentBet = _pendingBet;
                try { sm.HideResult(); } catch { }
                sm.StartCoroutine(sm.SpinCoroutine());
                if (!sm.IsSpinning) throw new Exception("SpinCoroutine didn't start");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[gamble] couldn't replay spin: " + e.Message);
                _forceResult = null;
                Replaying = false;
                SendDone();
            }
        }

        /// Every frame from MpManager.
        public static void Tick()
        {
            if (!CoopSync.Active) { if (Busy || Replaying) Reset(); return; }

            var sm = SlotMachine.Instance;

            try { TickBet(); } catch (Exception e) { Plugin.Log.LogWarning("[gamble] bet sync: " + e.Message); }

            // our own spin finished (IsSpinning is cleared once the result has been shown and paid)
            if (_localSpinning)
            {
                bool stillSpinning = false;
                try { stillSpinning = sm != null && sm.IsSpinning && sm.isActiveAndEnabled; } catch { }
                if (!stillSpinning) { _localSpinning = false; SendDone(); }
            }

            if (Replaying)
            {
                bool onScreen = false, spinning = false;
                try { onScreen = sm != null && sm.isActiveAndEnabled; spinning = sm != null && sm.IsSpinning; } catch { }
                if (!onScreen || !spinning || Time.unscaledTime - _replayStartedAt > LockTimeout)
                {
                    // finished, or the tab was closed (which kills the coroutine): don't leave the machine stuck
                    try { if (sm != null && spinning) sm.IsSpinning = false; } catch { }
                    Replaying = false;
                    _forceResult = null;
                    SendDone();
                }
            }
            else TryStartReplay();

            if (Busy && Time.unscaledTime - _startedAt > LockTimeout)
            {
                Plugin.Log.LogWarning("[gamble] spin lock timed out: " + WaitingText());
                _spinner = -1;
            }
        }
    }
}
