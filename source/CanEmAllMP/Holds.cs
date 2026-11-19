using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CanEmAllMP
{
    /// <summary>
    /// Who is holding each shared physics object of one kind (dead bodies or cans). The host decides.
    ///
    ///   Holder = Nobody   it's lying around; the host simulates it.
    ///   Holder = a player that player has it (hand, vacuum, or just threw it and it hasn't settled yet)
    ///                     and simulates it; everyone else only follows. Nobody else's vacuum can take it.
    ///
    /// Built for lag:
    ///  - Grabs are optimistic (your vacuum pulls at once) and every grab gets exactly one answer from the host,
    ///    so a late or out-of-order message can never make you drop something you just got.
    ///  - Every change bumps the object's epoch; older news (e.g. a late snapshot) is ignored.
    ///  - Once you let go and it settles (or after a few seconds), it goes back to the host with its velocity.
    ///  - Packets are sized to the connection (a too-big unreliable packet throws, which used to stop all syncing).
    /// </summary>
    public sealed class HoldTable<T> where T : Component
    {
        public const byte Nobody = 255;
        private const float RestSpeedSq = 0.05f;    // (m/s)^2: slower than this counts as still
        private const float RestTime = 0.75f;        // let go + still this long -> back to the host
        private const float FreeTime = 3f;           // let go this long -> back to the host whatever it's doing
        private const float OwnerTimeout = 5f;       // host: a holder silent this long loses it
        private const float StreamMoving = 1f / 15f, StreamIdle = 0.33f;
        private const int StreamRecord = 2 + 12 + 16 + 1;

        private static NetSession Net => MpManager.Net;
        private static byte Me => (byte)Net.LocalId;

        private readonly string _kind;
        private readonly Msg _mClaim, _mOwner, _mRelease, _mStream;

        // ---- set by the owning sync class
        public Func<ushort, T> HostObj;            // host: the object for an id (null = gone)
        public Func<ushort, T> ClientObj;          // client: our copy for an id (null = none)
        public Func<T, Rigidbody> RbOf;
        public Func<T, bool> InMachine;            // being canned / paid for: leave it alone
        public Func<T, bool> KeepDynamic;          // host: after a joiner hands it back, give it normal physics
        public Action<ushort, T, bool> LocalChanged; // client: we started (true) / stopped (false) simulating it

        public HoldTable(string kind, Msg claim, Msg owner, Msg release, Msg stream)
        {
            _kind = kind; _mClaim = claim; _mOwner = owner; _mRelease = release; _mStream = stream;
        }

        private static int _logs;
        private void Log(string s) { if (_logs++ < 200) Plugin.Log.LogInfo($"[MP] {_kind} {s}"); }

        private static bool Older(byte a, byte b) => (sbyte)(a - b) < 0;

        /// Shared rule for "the holder let go of it and it has settled".
        private static bool Settled(ref float freeSince, ref float restSince, Rigidbody rb, bool busy, float now)
        {
            if (busy) { freeSince = restSince = -1; return false; }
            if (freeSince < 0) freeSince = now;
            bool still = rb == null || rb.isKinematic || rb.linearVelocity.sqrMagnitude < RestSpeedSq;
            if (!still) restSince = -1;
            else if (restSince < 0) restSince = now;
            return (restSince >= 0 && now - restSince >= RestTime) || now - freeSince >= FreeTime;
        }

        // =====================================================================  HOST

        private class HostInfo
        {
            public byte Holder = Nobody, Epoch;
            public Vector3 Pos, Vel; public Quaternion Rot = Quaternion.identity; public bool Active = true;
            public float Seen;
            public bool Saved, Kin, Grav;           // our copy's physics before a joiner took it over
            public float FreeSince = -1, RestSince = -1;
        }
        private readonly Dictionary<ushort, HostInfo> _host = new Dictionary<ushort, HostInfo>();
        private readonly List<ushort> _tmpA = new List<ushort>(), _tmpB = new List<ushort>(), _tmpC = new List<ushort>();

        private HostInfo HostEntry(ushort id)
        {
            if (!_host.TryGetValue(id, out var h)) _host[id] = h = new HostInfo();
            return h;
        }

        public byte HostHolder(ushort id) => _host.TryGetValue(id, out var h) ? h.Holder : Nobody;
        public byte HostEpoch(ushort id) => _host.TryGetValue(id, out var h) ? h.Epoch : (byte)0;

        /// Host: a joiner simulates this one (our copy just follows).
        public bool HostRemote(ushort id) => _host.TryGetValue(id, out var h) && h.Holder != Nobody && h.Holder != 0;

        /// Host's own player grabs (hand: force) or vacuums it. False = a joiner has it: let go of it.
        public bool HostGrab(ushort id, bool force)
        {
            var h = HostEntry(id);
            if (h.Holder == 0) return true;
            if (h.Holder != Nobody && !force) return false;
            HostSetHolder(id, 0);
            return true;
        }

        public void HostReceiveClaim(int from, NetPacketReader r)
        {
            ushort id = r.GetUShort(); byte seq = r.GetByte(); bool force = r.GetBool();
            byte who = (byte)from;
            if (HostObj(id) == null) { HostAnswer(from, id, Nobody, HostEpoch(id), seq); return; }
            var h = HostEntry(id);
            if (h.Holder == who) { HostAnswer(from, id, who, h.Epoch, seq); return; }
            if (h.Holder != Nobody && !force)
            {
                Log($"#{id}: {Net.NameOf(from)}'s vacuum was too late ({Net.NameOf(h.Holder)} has it)");
                HostAnswer(from, id, h.Holder, h.Epoch, seq);
                return;
            }
            HostSetHolder(id, who, who, seq);
        }

        private void HostAnswer(int to, ushort id, byte holder, byte epoch, byte seq)
        {
            var w = Net.Begin(_mOwner);
            w.Put(id); w.Put(holder); w.Put(epoch); w.Put((byte)to); w.Put(seq);
            Net.SendTo(to, DeliveryMethod.ReliableOrdered);
        }

        /// Host: change who holds it (and tell everyone). answerTo/answerSeq mark it as the answer to a grab.
        public void HostSetHolder(ushort id, byte holder, byte answerTo = Nobody, byte answerSeq = 0)
        {
            var obj = HostObj(id);
            if (obj == null) { if (answerTo != Nobody) HostAnswer(answerTo, id, Nobody, HostEpoch(id), answerSeq); return; }
            var h = HostEntry(id);
            byte old = h.Holder;
            if (old == holder) { if (answerTo != Nobody) HostAnswer(answerTo, id, holder, h.Epoch, answerSeq); return; }
            bool wasRemote = old != Nobody && old != 0, isRemote = holder != Nobody && holder != 0;
            if (isRemote && !wasRemote) BecomeFollower(obj, h);
            else if (!isRemote && wasRemote) StopFollowing(obj, h);
            if (isRemote) h.Seen = Time.unscaledTime;
            h.Holder = holder;
            h.Epoch++;
            h.FreeSince = h.RestSince = -1;
            var w = Net.Begin(_mOwner);
            w.Put(id); w.Put(holder); w.Put(h.Epoch); w.Put(answerTo); w.Put(answerSeq);
            Net.Send(DeliveryMethod.ReliableOrdered);
        }

        /// A joiner takes it over: our copy lets go and only follows from now on.
        private void BecomeFollower(T obj, HostInfo h)
        {
            VacuumHelper.Eject(obj);
            var rb = RbOf(obj);
            if (rb != null)
            {
                if (!h.Saved) { h.Saved = true; h.Kin = rb.isKinematic; h.Grav = rb.useGravity; }
                rb.isKinematic = true;
            }
            var t = obj.transform;
            h.Pos = t.position; h.Rot = t.rotation; h.Vel = Vector3.zero; h.Active = obj.gameObject.activeSelf;
        }

        /// Back to the host: real physics again, where (and as fast as) the joiner last had it.
        private void StopFollowing(T obj, HostInfo h)
        {
            var go = obj.gameObject;
            bool machine = InMachine(obj);
            if (!machine) obj.transform.SetPositionAndRotation(h.Pos, h.Rot);
            if (!go.activeSelf) go.SetActive(true);
            var rb = RbOf(obj);
            if (rb != null)
            {
                if (h.Saved) { rb.isKinematic = h.Kin; rb.useGravity = h.Grav; }
                if (!machine && KeepDynamic(obj)) { rb.isKinematic = false; rb.useGravity = true; }
                if (!rb.isKinematic) { try { rb.linearVelocity = h.Vel; } catch { } }
            }
            h.Saved = false;
        }

        public void HostReceiveRelease(int from, NetPacketReader r)
        {
            ushort id = r.GetUShort(); var pos = r.GetVec(); var rot = r.GetQuat(); var vel = r.GetVec();
            if (!_host.TryGetValue(id, out var h) || h.Holder != (byte)from) return;
            h.Pos = pos; h.Rot = rot; h.Vel = vel; h.Active = true;
            HostSetHolder(id, Nobody);
        }

        /// A joiner put it somewhere only the host's game should handle (e.g. the paying reward machine).
        public void HostReturnAt(int from, ushort id, Vector3 pos)
        {
            if (!_host.TryGetValue(id, out var h) || h.Holder != (byte)from) return;
            h.Pos = pos; h.Vel = Vector3.zero; h.Active = true;
            HostSetHolder(id, Nobody);
        }

        public void HostReceiveStream(int from, NetPacketReader r)
        {
            int n = r.GetByte();
            float now = Time.unscaledTime;
            for (int i = 0; i < n; i++)
            {
                ushort id = r.GetUShort(); var pos = r.GetVec(); var rot = r.GetQuat(); bool active = r.GetBool();
                if (_host.TryGetValue(id, out var h) && h.Holder == (byte)from)
                {
                    h.Pos = pos; h.Rot = rot; h.Active = active; h.Seen = now;
                }
            }
        }

        /// Host, every frame: follow joiners' objects, take back silent ones, hand back what our player let go of.
        public void HostTick()
        {
            if (_host.Count == 0) return;
            float now = Time.unscaledTime;
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 18f);
            _tmpA.Clear(); _tmpB.Clear(); _tmpC.Clear();
            foreach (var kv in _host)
            {
                var h = kv.Value;
                if (h.Holder == Nobody) continue;
                var obj = HostObj(kv.Key);
                if (obj == null) { _tmpC.Add(kv.Key); continue; }
                if (h.Holder == 0)
                {
                    bool busy = VacuumHelper.HeldLocally(obj.Pointer) || InMachine(obj) || !obj.gameObject.activeInHierarchy;
                    if (Settled(ref h.FreeSince, ref h.RestSince, RbOf(obj), busy, now)) _tmpA.Add(kv.Key);
                    continue;
                }
                if (InMachine(obj)) { h.Seen = now; continue; }   // the machine has it now
                if (now - h.Seen > OwnerTimeout) { _tmpB.Add(kv.Key); continue; }
                var go = obj.gameObject;
                if (go.activeSelf != h.Active) go.SetActive(h.Active);
                var t = obj.transform;
                t.SetPositionAndRotation(Vector3.Lerp(t.position, h.Pos, k), Quaternion.Slerp(t.rotation, h.Rot, k));
            }
            foreach (var id in _tmpA) HostSetHolder(id, Nobody);
            foreach (var id in _tmpB)
            {
                var h = _host[id];
                Log($"#{id}: no word from {Net.NameOf(h.Holder)} for {OwnerTimeout:0}s, host takes it back");
                h.Vel = Vector3.zero;
                HostSetHolder(id, Nobody);
            }
            foreach (var id in _tmpC) _host.Remove(id);
        }

        /// The object no longer exists on the host. If our copy was following a joiner, give it its physics back
        /// (it may be sitting in a pool and get reused).
        public void HostForget(ushort id, T objIfAny)
        {
            if (!_host.TryGetValue(id, out var h)) return;
            _host.Remove(id);
            if (h.Saved && objIfAny != null)
            {
                try { var rb = RbOf(objIfAny); if (rb != null) { rb.isKinematic = h.Kin; rb.useGravity = h.Grav; } } catch { }
            }
        }

        public void HostPlayerLeft(int player)
        {
            _tmpA.Clear();
            foreach (var kv in _host) if (kv.Value.Holder == (byte)player) _tmpA.Add(kv.Key);
            foreach (var id in _tmpA) HostSetHolder(id, Nobody);
        }

        /// End of day: everything goes back to the host so the game's own cleanup sees normal objects.
        public void HostReclaimAll()
        {
            _tmpA.Clear();
            foreach (var kv in _host) if (kv.Value.Holder != Nobody) { kv.Value.Vel = Vector3.zero; _tmpA.Add(kv.Key); }
            foreach (var id in _tmpA) HostSetHolder(id, Nobody);
        }

        /// Session over: give our copies their physics back, forget everything (no messages).
        public void HostResetAll()
        {
            foreach (var kv in _host)
            {
                var h = kv.Value;
                if (h.Holder == Nobody || h.Holder == 0) continue;
                var obj = HostObj(kv.Key);
                if (obj != null) { try { StopFollowing(obj, h); } catch { } }
            }
            _host.Clear();
        }

        public void HostWriteState(NetDataWriter w, ushort id)
        {
            if (_host.TryGetValue(id, out var h)) { w.Put(h.Holder); w.Put(h.Epoch); }
            else { w.Put(Nobody); w.Put((byte)0); }
        }

        /// Ids a joiner is holding (host) / someone else is holding (client).
        public void CollectHeldByOthers(List<ushort> into)
        {
            into.Clear();
            if (Net.IsHost) { foreach (var kv in _host) if (kv.Value.Holder != Nobody && kv.Value.Holder != 0) into.Add(kv.Key); }
            else
            {
                byte me = Me;
                foreach (var kv in _client) if (!kv.Value.Local && kv.Value.Holder != Nobody && kv.Value.Holder != me) into.Add(kv.Key);
            }
        }

        // =====================================================================  CLIENT

        private class ClientInfo
        {
            public byte Holder = Nobody, Epoch; public bool Known;
            public bool Local; public byte Pending, NextSeq; public float PendingSince;
            public float FreeSince = -1, RestSince = -1, SentAt = -10;
            public Vector3 SentPos; public Quaternion SentRot = Quaternion.identity; public bool SentActive;
        }
        private readonly Dictionary<ushort, ClientInfo> _client = new Dictionary<ushort, ClientInfo>();
        private readonly List<(ushort id, Vector3 pos, Quaternion rot, bool active)> _stream = new List<(ushort, Vector3, Quaternion, bool)>();

        private ClientInfo ClientEntry(ushort id)
        {
            if (!_client.TryGetValue(id, out var c)) _client[id] = c = new ClientInfo();
            return c;
        }

        public bool IsLocal(ushort id) => _client.TryGetValue(id, out var c) && c.Local;

        /// Someone else has it (as far as we know): our vacuum leaves it alone.
        public bool HeldByOther(ushort id)
        {
            if (!_client.TryGetValue(id, out var c) || c.Local) return false;
            return c.Holder != Nobody && c.Holder != Me;
        }

        /// Holder info from a snapshot (unreliable: may be late, may be out of order).
        public void ClientState(ushort id, byte holder, byte epoch)
        {
            var c = ClientEntry(id);
            if (c.Known && Older(epoch, c.Epoch)) return;
            c.Known = true; c.Holder = holder; c.Epoch = epoch;
            if (c.Pending != 0) return;                       // our grab is in flight: its answer decides
            if (c.Local && holder != Me) Lose(id, c, "someone else has it");
        }

        public void ClientReceiveOwner(NetPacketReader r)
        {
            ushort id = r.GetUShort(); byte holder = r.GetByte(), epoch = r.GetByte(), answerTo = r.GetByte(), seq = r.GetByte();
            if (ClientObj(id) == null) { _client.Remove(id); return; }
            var c = ClientEntry(id);
            bool answer = answerTo == Me && c.Pending != 0 && seq == c.Pending;
            if (answer) c.Pending = 0;
            if (!c.Known || !Older(epoch, c.Epoch)) { c.Known = true; c.Holder = holder; c.Epoch = epoch; }
            if (c.Pending != 0) return;                       // a newer grab of ours is still unanswered
            if (c.Local && c.Holder != Me) Lose(id, c, answer ? "grab refused, " + Net.NameOf(c.Holder) + " was first" : "taken by " + Net.NameOf(c.Holder));
            else if (!c.Local && c.Holder == Me) SendRelease(id, c, ClientObj(id));   // host thinks we have it, we don't
        }

        /// We grab it (hand: force) or our vacuum pulls it. False = someone else has it: let go of it.
        public bool ClientGrab(ushort id, bool force)
        {
            var obj = ClientObj(id);
            if (obj == null) return true;
            var c = ClientEntry(id);
            if (c.Local) return true;
            if (!force && c.Holder != Nobody && c.Holder != Me) return false;
            c.Local = true; c.FreeSince = c.RestSince = -1; c.SentAt = -10;
            if (++c.NextSeq == 0) c.NextSeq = 1;
            c.Pending = c.NextSeq; c.PendingSince = Time.unscaledTime;
            LocalChanged?.Invoke(id, obj, true);
            var w = Net.Begin(_mClaim);
            w.Put(id); w.Put(c.Pending); w.Put(force);
            Net.Send(DeliveryMethod.ReliableOrdered);
            return true;
        }

        private void Lose(ushort id, ClientInfo c, string why)
        {
            c.Local = false; c.FreeSince = c.RestSince = -1;
            var obj = ClientObj(id);
            if (obj != null) { VacuumHelper.Eject(obj); LocalChanged?.Invoke(id, obj, false); }
            Log($"#{id}: let go ({why})");
        }

        private void SendRelease(ushort id, ClientInfo c, T obj)
        {
            if (obj == null) return;
            var rb = RbOf(obj);
            var t = obj.transform;
            var vel = Vector3.zero;
            try { if (rb != null && !rb.isKinematic) vel = rb.linearVelocity; } catch { }
            var w = Net.Begin(_mRelease);
            w.Put(id); w.PutVec(t.position); w.PutQuat(t.rotation); w.PutVec(vel);
            Net.Send(DeliveryMethod.ReliableOrdered);
        }

        private void Release(ushort id, ClientInfo c, T obj)
        {
            SendRelease(id, c, obj);
            c.Local = false; c.FreeSince = c.RestSince = -1;
            if (c.Holder == Me) c.Holder = Nobody;           // the host confirms with a newer epoch
            LocalChanged?.Invoke(id, obj, false);
        }

        /// Client, every frame: hand back what we let go of, stream what we hold.
        public void ClientTick()
        {
            if (_client.Count == 0) return;
            float now = Time.unscaledTime;
            _tmpA.Clear(); _tmpB.Clear(); _tmpC.Clear();
            foreach (var kv in _client)
            {
                var c = kv.Value;
                if (!c.Local) continue;
                var obj = ClientObj(kv.Key);
                if (obj == null) { _tmpC.Add(kv.Key); continue; }
                if (c.Pending != 0 && now - c.PendingSince > 8f) { c.Pending = 0; _tmpB.Add(kv.Key); continue; }   // never answered: give up
                bool busy = c.Pending != 0 || VacuumHelper.HeldLocally(obj.Pointer) || InMachine(obj) || !obj.gameObject.activeInHierarchy;
                if (Settled(ref c.FreeSince, ref c.RestSince, RbOf(obj), busy, now)) _tmpA.Add(kv.Key);
            }
            foreach (var id in _tmpA) { var c = _client[id]; Release(id, c, ClientObj(id)); }
            foreach (var id in _tmpB) Lose(id, _client[id], "the host never answered");
            foreach (var id in _tmpC) _client.Remove(id);
            Stream(now);
        }

        private void Stream(float now)
        {
            _stream.Clear();
            foreach (var kv in _client)
            {
                var c = kv.Value;
                if (!c.Local) continue;
                var obj = ClientObj(kv.Key);
                if (obj == null) continue;
                var t = obj.transform;
                var pos = t.position; var rot = t.rotation;
                bool active = obj.gameObject.activeInHierarchy;
                bool changed = active != c.SentActive || (pos - c.SentPos).sqrMagnitude > 0.0001f || Quaternion.Angle(rot, c.SentRot) > 1f;
                if (now - c.SentAt < (changed ? StreamMoving : StreamIdle)) continue;
                c.SentAt = now; c.SentPos = pos; c.SentRot = rot; c.SentActive = active;
                _stream.Add((kv.Key, pos, rot, active));
            }
            if (_stream.Count == 0) return;
            int per = Math.Max(1, Math.Min(255, (Net.MaxUnreliablePayload - 2) / StreamRecord));
            for (int s = 0; s < _stream.Count; s += per)
            {
                int e = Math.Min(_stream.Count, s + per);
                var w = Net.Begin(_mStream);
                w.Put((byte)(e - s));
                for (int i = s; i < e; i++)
                {
                    var x = _stream[i];
                    w.Put(x.id); w.PutVec(x.pos); w.PutQuat(x.rot); w.Put(x.active);
                }
                Net.Send(DeliveryMethod.Unreliable);
            }
        }

        /// Our copy is gone (destroyed / handed to a machine).
        public void ClientForget(ushort id) => _client.Remove(id);
        public void ClientReset() => _client.Clear();

        public void Reset() { _host.Clear(); _client.Clear(); }
    }
}
