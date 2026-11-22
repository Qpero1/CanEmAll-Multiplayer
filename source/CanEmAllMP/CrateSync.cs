using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CanEmAllMP
{
    /// <summary>
    /// The crates (the "basket" you start with) are scene objects everyone already has, listed in GameManager.crates
    /// in the same order on every PC, so a crate's index is its id.
    ///  - The host simulates them and streams where they are; joiners' crates are kinematic and follow.
    ///  - Picking one up (hand or vacuum) makes you its holder through the same HoldTable as cans and bodies:
    ///    you simulate it and stream it until it settles, then it goes back to the host.
    /// </summary>
    public static class CrateSync
    {
        private static NetSession Net => MpManager.Net;
        private const float Keepalive = 0.5f;

        public static readonly HoldTable<Crate> Hold = new HoldTable<Crate>("crate", Msg.CrateClaim, Msg.CrateOwner, Msg.CrateRelease, Msg.CrateOwned);

        static CrateSync()
        {
            Hold.HostObj = Get;
            Hold.ClientObj = Get;
            Hold.RbOf = c => c.rb;
            Hold.InMachine = c => false;
            Hold.KeepDynamic = c => true;
            Hold.LocalChanged = (id, c, local) =>
            {
                var rb = c.rb;
                if (rb == null) return;
                if (local) { Restore(id, rb); }
                else
                {
                    MakePuppet(id, rb);
                    var t = c.transform;
                    Follow(id).Buf.Reset(t.position, t.rotation);
                }
            };
        }

        // ------------------------------------------------------------------ crates by id

        private static readonly List<Crate> _crates = new List<Crate>();
        private static float _listedAt = -10f;
        private static IntPtr _listedGm;

        private static void Refresh()
        {
            var gm = GameManager.Instance;
            if (gm == null) { _crates.Clear(); return; }
            if (gm.Pointer == _listedGm && Time.unscaledTime - _listedAt < 2f) return;
            _listedGm = gm.Pointer; _listedAt = Time.unscaledTime;
            _crates.Clear();
            try { var arr = gm.crates; if (arr != null) foreach (var c in arr) _crates.Add(c); } catch { }
        }

        private static Crate Get(ushort id)
        {
            Refresh();
            return id < _crates.Count && _crates[id] != null ? _crates[id] : null;
        }

        private static bool IdOf(Crate c, out ushort id)
        {
            Refresh();
            for (int i = 0; i < _crates.Count; i++)
                if (_crates[i] != null && _crates[i].Pointer == c.Pointer) { id = (ushort)i; return true; }
            id = 0; return false;
        }

        private static bool On => Net.Connected && Plugin.SyncEnemies.Value;

        // ------------------------------------------------------------------ cans riding in a crate we carry

        private static readonly HashSet<IntPtr> _carried = new HashSet<IntPtr>();
        public static bool IsCarried(IntPtr canPtr) => _carried.Contains(canPtr);

        /// Every frame: cans inside a crate our player holds become ours, so the crate's physics moves them.
        private static void CarryCans()
        {
            _carried.Clear();
            if (!On) return;
            Refresh();
            for (int i = 0; i < _crates.Count; i++)
            {
                var c = _crates[i];
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                ushort id = (ushort)i;
                bool mine = Net.IsHost ? Hold.HostHolder(id) == 0 : Hold.IsLocal(id);
                if (!mine) continue;
                try
                {
                    var t = c.transform;
                    var half = Vector3.Scale(c.insideSize * 0.5f, t.lossyScale) + new Vector3(0.05f, 0.15f, 0.05f);
                    var hits = Physics.OverlapBox(t.TransformPoint(c.insideCenter), half, t.rotation, c.insideMask.value, QueryTriggerInteraction.Ignore);
                    if (hits == null) continue;
                    foreach (var col in hits)
                    {
                        var rb = col != null ? col.attachedRigidbody : null;
                        var can = rb != null ? rb.GetComponent<Can>() : null;
                        if (can == null || _carried.Contains(can.Pointer)) continue;
                        _carried.Add(can.Pointer);
                        CanSync.ClaimCarried(can);
                    }
                }
                catch (Exception e) { if (_carryErr++ < 3) Plugin.Log.LogWarning("[MP] crate contents: " + e.Message); }
            }
        }
        private static int _carryErr;

        /// Our player grabs a crate (hand: force) or our vacuum pulls it. False = someone else has it.
        public static bool LocalGrab(Crate c, bool force)
        {
            if (c == null || !On || !IdOf(c, out var id)) return true;
            return Net.IsHost ? Hold.HostGrab(id, force) : Hold.ClientGrab(id, force);
        }

        /// Someone else holds it: our vacuum must ignore it.
        public static bool HeldByOther(Crate c)
        {
            if (c == null || !On || !IdOf(c, out var id)) return false;
            return Net.IsHost ? Hold.HostRemote(id) : Hold.HeldByOther(id);
        }

        private static readonly List<ushort> _ids = new List<ushort>();
        private static readonly List<Crate> _marked = new List<Crate>();

        /// While our vacuum runs, crates someone else holds count as "free falling" so it ignores them.
        public static void MarkHeldByOthers()
        {
            _marked.Clear();
            if (!On) return;
            Hold.CollectHeldByOthers(_ids);
            foreach (var id in _ids)
            {
                var c = Get(id);
                if (c == null || c._IsFreeFall_k__BackingField || !c.gameObject.activeInHierarchy) continue;
                c._IsFreeFall_k__BackingField = true;
                _marked.Add(c);
            }
        }

        public static void UnmarkHeldByOthers()
        {
            foreach (var c in _marked) { try { if (c != null) c._IsFreeFall_k__BackingField = false; } catch { } }
            _marked.Clear();
        }

        public static void Reset()
        {
            try { Hold.HostResetAll(); } catch { }
            Hold.Reset();
            foreach (var kv in _follow) { var c = Get(kv.Key); if (c != null && c.rb != null) Restore(kv.Key, c.rb); }
            _follow.Clear(); _sent.Clear();
        }

        // ===================================================================== HOST

        private struct Sent { public Vector3 Pos; public Quaternion Rot; public bool Active; public byte Holder, Epoch; public float Time; }
        private static readonly Dictionary<ushort, Sent> _sent = new Dictionary<ushort, Sent>();
        private const int Record = 2 + 12 + 16 + 1 + 1 + 1;

        public static void HostBroadcast(NetSession net)
        {
            if (net.Players.Count < 2 || !GameApi.InGame) return;
            Refresh();
            float now = Time.unscaledTime;
            var w = net.Begin(Msg.CrateSnapshot);
            int countAt = w.Length; w.Put((byte)0);
            int n = 0, max = Math.Max(1, (net.MaxUnreliablePayload - 2) / Record);
            for (int i = 0; i < _crates.Count && n < max; i++)
            {
                var c = _crates[i];
                if (c == null) continue;
                ushort id = (ushort)i;
                var t = c.transform;
                if (!Hold.HostRemotePose(id, out var sp, out var sr)) { sp = t.position; sr = t.rotation; }
                var s = new Sent { Pos = sp, Rot = sr, Active = c.gameObject.activeInHierarchy, Holder = Hold.HostHolder(id), Epoch = Hold.HostEpoch(id), Time = now };
                if (_sent.TryGetValue(id, out var last) && now - last.Time < Keepalive && last.Active == s.Active && last.Holder == s.Holder
                    && last.Epoch == s.Epoch && (last.Pos - s.Pos).sqrMagnitude < 0.000025f && Quaternion.Angle(last.Rot, s.Rot) < 0.5f)
                    continue;
                _sent[id] = s;
                w.Put(id); w.PutVec(s.Pos); w.PutQuat(s.Rot); w.Put(s.Active); w.Put(s.Holder); w.Put(s.Epoch);
                n++;
            }
            if (n == 0) return;
            w.Data[countAt] = (byte)n;
            net.Send(DeliveryMethod.Unreliable);
        }

        public static void HostTick() { CarryCans(); Hold.HostTick(); }
        public static void HostPlayerLeft(int id) => Hold.HostPlayerLeft(id);
        public static void ReclaimAll() { if (Net.IsHost) Hold.HostReclaimAll(); }

        // ===================================================================== CLIENT

        private class Follower
        {
            public readonly SnapBuffer Buf = new SnapBuffer();
            public bool Saved, Kin, Grav, Puppet;
        }
        private static readonly Dictionary<ushort, Follower> _follow = new Dictionary<ushort, Follower>();

        private static Follower Follow(ushort id)
        {
            if (!_follow.TryGetValue(id, out var f)) _follow[id] = f = new Follower();
            return f;
        }

        private static void MakePuppet(ushort id, Rigidbody rb)
        {
            var f = Follow(id);
            if (f.Puppet) return;
            if (!f.Saved) { f.Saved = true; f.Kin = rb.isKinematic; f.Grav = rb.useGravity; }
            rb.isKinematic = true;
            f.Puppet = true;
        }

        private static void Restore(ushort id, Rigidbody rb)
        {
            var f = Follow(id);
            if (f.Saved) { rb.isKinematic = f.Kin; rb.useGravity = f.Grav; }
            else { rb.isKinematic = false; rb.useGravity = true; }
            f.Puppet = false;
        }

        public static void ClientReceive(NetPacketReader r)
        {
            if (!GameApi.InGame || !HostSave.GameStartedOrLocal) return;
            int n = r.GetByte();
            float now = Time.time;
            for (int i = 0; i < n; i++)
            {
                ushort id = r.GetUShort(); var pos = r.GetVec(); var rot = r.GetQuat(); bool active = r.GetBool();
                byte holder = r.GetByte(), epoch = r.GetByte();
                var c = Get(id);
                if (c == null) continue;
                Hold.ClientState(id, holder, epoch);
                if (Hold.IsLocal(id)) continue;
                var rb = c.rb;
                if (rb != null) MakePuppet(id, rb);
                var go = c.gameObject;
                if (go.activeSelf != active) go.SetActive(active);
                var f = Follow(id);
                if (f.Buf.Empty) { var t = c.transform; f.Buf.Reset(t.position, t.rotation); }
                f.Buf.Add(pos, rot);
            }
        }

        public static void ClientTick()
        {
            if (!GameApi.InGame || !HostSave.GameStartedOrLocal) return;
            float now = Time.time;
            foreach (var kv in _follow)
            {
                if (!kv.Value.Puppet || Hold.IsLocal(kv.Key)) continue;
                var c = Get(kv.Key);
                if (c == null) continue;
                if (kv.Value.Buf.Sample(out var bp, out var br)) c.transform.SetPositionAndRotation(bp, br);
            }
            CarryCans();
            Hold.ClientTick();
        }
    }
}
