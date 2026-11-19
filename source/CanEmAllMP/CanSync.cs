using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CanEmAllMP
{
    /// <summary>
    /// Cans are shared objects, like bodies:
    ///  - Only the host's processors make cans. Joiners' processors still play the canning, but never spawn cans.
    ///  - Joiners see the host's cans as puppets (pooled cans, kinematic, following the host).
    ///  - A can is held by one player at a time (see HoldTable): the holder simulates and streams it.
    ///  - A reward machine only pays for cans its machine simulates. When a joiner drops a can they hold into their
    ///    reward machine, it goes back to the host right there, and the host's reward machine pays for it.
    /// </summary>
    public static class CanSync
    {
        private static NetSession Net => MpManager.Net;
        private const int SnapRecord = 2 + 12 + 16 + 4 + 1 + 1 + 1 + 1 + 1;
        private const float Keepalive = 0.5f;
        private const float StaleAfter = 3f;

        public static readonly HoldTable<Can> Hold = new HoldTable<Can>("can", Msg.CanClaim, Msg.CanOwner, Msg.CanRelease, Msg.CanOwned);

        static CanSync()
        {
            Hold.HostObj = id => _hostById.TryGetValue(id, out var c) && c != null && !c.IsInPool ? c : null;
            Hold.ClientObj = id => _puppets.TryGetValue(id, out var p) && p.Can != null ? p.Can : null;
            Hold.RbOf = c => c.rb;
            Hold.InMachine = c => c.IsProcessed;
            Hold.KeepDynamic = c => !c.IsProcessed;
            Hold.LocalChanged = (id, c, local) =>
            {
                if (!_puppets.TryGetValue(id, out var p)) return;
                if (local)
                {
                    if (p.Rb != null) { p.Rb.isKinematic = false; p.Rb.useGravity = true; }
                    ApplyLook(p);
                }
                else
                {
                    if (p.Rb != null) p.Rb.isKinematic = true;
                    var t = c.transform;
                    p.FromPos = p.ToPos = t.position; p.FromRot = p.ToRot = t.rotation;
                    p.LerpStart = p.LastUpdate = Time.time;
                }
            };
        }

        // ===================================================================== shared

        public static bool IsRemote(Can c)
        {
            if (c == null || !Net.Connected || !Plugin.SyncEnemies.Value) return false;
            if (Net.IsClient) return _puppetIdByPtr.TryGetValue(c.Pointer, out var id) && !Hold.IsLocal(id);
            return _hostIds.TryGetValue(c.Pointer, out var hid) && Hold.HostRemote(hid);
        }

        public static bool IsPuppet(Can c) => c != null && _puppetIdByPtr.ContainsKey(c.Pointer);

        /// Our player grabs a can (hand: force) or our vacuum pulls/stores it. False = someone else has it.
        public static bool LocalGrab(Can c, bool force)
        {
            if (c == null || !Net.Connected || !Plugin.SyncEnemies.Value) return true;
            if (Net.IsHost) return c.IsInPool || Hold.HostGrab(HostIdOf(c), force);
            return !_puppetIdByPtr.TryGetValue(c.Pointer, out var id) || Hold.ClientGrab(id, force);
        }

        private static readonly List<ushort> _ids = new List<ushort>();
        private static readonly List<Can> _marked = new List<Can>();

        /// While our vacuum runs, cans someone else holds count as "free falling" so it ignores them.
        public static void MarkHeldByOthers()
        {
            _marked.Clear();
            if (!Net.Connected || !Plugin.SyncEnemies.Value) return;
            Hold.CollectHeldByOthers(_ids);
            foreach (var id in _ids)
            {
                var c = Net.IsHost ? Hold.HostObj(id) : Hold.ClientObj(id);
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

        /// The texture value the host's can is actually drawn with (textureIndex, +1 if smoked at spawn).
        private static int TextureIndex(Can c)
        {
            try { var pb = c.propertyBlock; return pb != null ? Mathf.RoundToInt(pb.GetFloat(Can.TextureIndexId)) : 0; } catch { return 0; }
        }

        private static void ReturnToPool(Can c)
        {
            if (c == null) return;
            try
            {
                VacuumHelper.Forget(c);
                if (c.IsInPool) return;
                var gm = GameManager.Instance;
                if (gm != null) gm.ReturnCanToPool(c); else UnityEngine.Object.Destroy(c.gameObject);
            }
            catch { }
        }

        public static void Reset()
        {
            try { Hold.HostResetAll(); } catch { }
            Hold.Reset();
            _hostIds.Clear(); _hostById.Clear(); _sent.Clear(); _lastPresent.Clear(); _nextId = 1;
            foreach (var p in _puppets.Values) ReturnToPool(p.Can);
            _puppets.Clear(); _puppetIdByPtr.Clear(); _ignoreUntil.Clear();
        }

        // ===================================================================== HOST

        private static readonly Dictionary<IntPtr, ushort> _hostIds = new Dictionary<IntPtr, ushort>();
        private static readonly Dictionary<ushort, Can> _hostById = new Dictionary<ushort, Can>();
        private static HashSet<ushort> _present = new HashSet<ushort>(), _lastPresent = new HashSet<ushort>();
        private static readonly List<ushort> _gone = new List<ushort>();
        private struct Sent { public Vector3 Pos; public Quaternion Rot; public int Price; public byte Qty, Flags, Tex, Holder, Epoch; public float Time; }
        private static readonly Dictionary<ushort, Sent> _sent = new Dictionary<ushort, Sent>();
        private static readonly List<(ushort id, Sent s)> _toSend = new List<(ushort, Sent)>();
        private static ushort _nextId = 1;

        private static ushort HostIdOf(Can c)
        {
            if (!_hostIds.TryGetValue(c.Pointer, out var id))
            {
                do { id = _nextId++; if (_nextId == 0) _nextId = 1; } while (_hostById.ContainsKey(id));
                _hostIds[c.Pointer] = id;
                _hostById[id] = c;
            }
            return id;
        }

        private static void HostDropId(NetSession net, ushort id)
        {
            net.Begin(Msg.CanGone).Put(id);
            net.Send(DeliveryMethod.ReliableOrdered);
            _hostById.TryGetValue(id, out var obj);
            Hold.HostForget(id, obj != null ? obj : null);
            if (!ReferenceEquals(obj, null)) _hostIds.Remove(obj.Pointer);
            _hostById.Remove(id);
            _sent.Remove(id);
        }

        public static void HostBroadcast(NetSession net)
        {
            var gm = GameManager.Instance;
            if (gm == null || net.Players.Count < 2) return;
            float now = Time.unscaledTime;

            _present.Clear(); _toSend.Clear();
            var set = gm.registeredCans;
            if (set != null)
                foreach (var c in set)
                {
                    if (c == null || c.IsInPool) continue;   // switched off (e.g. inside a vacuum) is sent as "hidden"
                    var id = HostIdOf(c);
                    _present.Add(id);
                    var t = c.transform;
                    var s = new Sent { Pos = t.position, Rot = t.rotation, Price = c.price, Qty = (byte)Math.Min(255, c.quantity), Tex = (byte)TextureIndex(c), Time = now };
                    if (c.IsShiny) s.Flags |= 1;
                    if (c.IsSmoked) s.Flags |= 2;
                    if (c.HoleInOne) s.Flags |= 4;
                    if (!c.gameObject.activeInHierarchy) s.Flags |= 8;
                    s.Holder = Hold.HostHolder(id); s.Epoch = Hold.HostEpoch(id);
                    if (_sent.TryGetValue(id, out var last) && now - last.Time < Keepalive && last.Flags == s.Flags && last.Tex == s.Tex
                        && last.Price == s.Price && last.Qty == s.Qty && last.Holder == s.Holder && last.Epoch == s.Epoch
                        && (last.Pos - s.Pos).sqrMagnitude < 0.000025f && Quaternion.Angle(last.Rot, s.Rot) < 0.5f)
                        continue;
                    _sent[id] = s;
                    _toSend.Add((id, s));
                }

            int per = Math.Max(1, Math.Min(255, (net.MaxUnreliablePayload - 2) / SnapRecord));
            for (int start = 0; start < _toSend.Count; start += per)
            {
                int end = Math.Min(_toSend.Count, start + per);
                var w = net.Begin(Msg.CanSnapshot);
                w.Put((byte)(end - start));
                for (int i = start; i < end; i++)
                {
                    var (id, s) = _toSend[i];
                    w.Put(id);
                    w.PutVec(s.Pos); w.PutQuat(s.Rot);
                    w.Put(s.Price); w.Put(s.Qty); w.Put(s.Flags); w.Put(s.Tex);
                    w.Put(s.Holder); w.Put(s.Epoch);
                }
                net.Send(DeliveryMethod.Unreliable);
            }

            _gone.Clear();
            foreach (var id in _lastPresent) if (!_present.Contains(id)) _gone.Add(id);
            foreach (var id in _gone) HostDropId(net, id);
            var swap = _lastPresent; _lastPresent = _present; _present = swap;
        }

        public static void HostTick() => Hold.HostTick();

        /// A joiner dropped their can into the reward machine: give it back to the host's physics right there,
        /// so the host's reward machine (the paying one) takes it.
        public static void HostReceiveRewarded(int from, NetPacketReader r)
        {
            var id = r.GetUShort();
            var pos = r.GetVec();
            Hold.HostReturnAt(from, id, pos);
        }

        public static void HostPlayerLeft(int playerId) => Hold.HostPlayerLeft(playerId);

        public static void ReclaimAll() { if (Net.IsHost) Hold.HostReclaimAll(); }

        // ===================================================================== CLIENT

        private class Puppet
        {
            public Can Can; public Rigidbody Rb; public ushort Id;
            public Vector3 FromPos, ToPos; public Quaternion FromRot, ToRot;
            public float LastUpdate, LerpStart;
            public int Tex; public byte Flags;
        }

        private static readonly Dictionary<ushort, Puppet> _puppets = new Dictionary<ushort, Puppet>();
        private static readonly Dictionary<IntPtr, ushort> _puppetIdByPtr = new Dictionary<IntPtr, ushort>();
        private static readonly List<ushort> _remove = new List<ushort>();
        private static readonly List<Can> _strays = new List<Can>();
        private static readonly Dictionary<ushort, float> _ignoreUntil = new Dictionary<ushort, float>(); // ids we just handed to the reward machine

        /// Client: remove every puppet can (end-of-day cleanup); the host's next updates recreate what still exists.
        public static void ClearPuppets()
        {
            foreach (var p in _puppets.Values) ReturnToPool(p.Can);
            _puppets.Clear(); _puppetIdByPtr.Clear();
            Hold.ClientReset();
        }

        private static void ApplyLook(Puppet p)
        {
            try
            {
                var c = p.Can;
                c.Initialize(p.Tex, false, (p.Flags & 1) != 0); // p.Tex is already the final shader value
                c.IsSmoked = (p.Flags & 2) != 0;
                c.HoleInOne = (p.Flags & 4) != 0;
            }
            catch { }
        }

        public static void ClientReceive(NetPacketReader r)
        {
            if (!GameApi.InGame || !HostSave.GameStartedOrLocal) return;
            var gm = GameManager.Instance;
            if (gm == null) return;
            int n = r.GetByte();
            float now = Time.time;
            for (int i = 0; i < n; i++)
            {
                var id = r.GetUShort();
                var pos = r.GetVec(); var rot = r.GetQuat();
                int price = r.GetInt();
                int qty = r.GetByte();
                byte flags = r.GetByte();
                int tex = r.GetByte();
                byte holder = r.GetByte(), epoch = r.GetByte();

                bool hidden = (flags & 8) != 0;
                if (_ignoreUntil.TryGetValue(id, out var until))
                {
                    if (Time.unscaledTime < until) continue;
                    _ignoreUntil.Remove(id);
                }
                _puppets.TryGetValue(id, out var p);
                if (p == null || p.Can == null || p.Can.IsInPool)
                {
                    if (p != null) DestroyPuppet(id, p);
                    if (hidden) continue;
                    p = SpawnPuppet(gm, id, pos, rot, price, qty, flags, tex);
                    if (p == null) continue;
                    _puppets[id] = p;
                    _puppetIdByPtr[p.Can.Pointer] = id;
                }
                p.LastUpdate = now;
                Hold.ClientState(id, holder, epoch);
                if (Hold.IsLocal(id)) continue;
                var go = p.Can.gameObject;
                if (go.activeSelf == hidden) go.SetActive(!hidden);
                p.Can.price = price; p.Can.quantity = qty;
                byte look = (byte)(flags & 7);
                if (p.Tex != tex || (p.Flags & 5) != (look & 5)) { p.Tex = tex; p.Flags = look; ApplyLook(p); }
                else if (p.Flags != look) { p.Flags = look; p.Can.IsSmoked = (look & 2) != 0; }
                p.FromPos = p.Can.transform.position; p.FromRot = p.Can.transform.rotation;
                p.ToPos = pos; p.ToRot = rot; p.LerpStart = now;
            }
        }

        private static Puppet SpawnPuppet(GameManager gm, ushort id, Vector3 pos, Quaternion rot, int price, int qty, byte flags, int tex)
        {
            Can c = null;
            try
            {
                var prefab = gm.pooledCanPrefab;
                c = gm.GetCanFromPool(prefab, pos, rot);
                if (c == null) return null;
                c.Initialize(tex, false, (flags & 1) != 0);
                c.IsSmoked = (flags & 2) != 0;
                c.HoleInOne = (flags & 4) != 0;
                c.price = price; c.quantity = qty;
                c.IsProcessed = false;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Can puppet spawn failed: " + e.Message); return null; }
            var rb = c.rb;
            if (rb != null) { rb.isKinematic = true; rb.interpolation = RigidbodyInterpolation.None; }
            return new Puppet { Tex = tex, Flags = (byte)(flags & 7), Can = c, Rb = rb, Id = id, FromPos = pos, ToPos = pos, FromRot = rot, ToRot = rot, LastUpdate = Time.time, LerpStart = Time.time };
        }

        private static void DestroyPuppet(ushort id, Puppet p)
        {
            _puppets.Remove(id);
            Hold.ClientForget(id);
            if (ReferenceEquals(p.Can, null)) return;
            _puppetIdByPtr.Remove(p.Can.Pointer);
            ReturnToPool(p.Can);
        }

        public static void ClientReceiveGone(NetPacketReader r)
        {
            var id = r.GetUShort();
            if (_puppets.TryGetValue(id, out var p)) DestroyPuppet(id, p);
        }

        public static void ReapplyLook(Can c)
        {
            if (c != null && _puppetIdByPtr.TryGetValue(c.Pointer, out var id) && _puppets.TryGetValue(id, out var p)) ApplyLook(p);
        }

        // reward machine: only cans this machine simulates may be paid for here
        private static readonly List<Can> _masked = new List<Can>();

        public static void BeforeRewardCheck()
        {
            _masked.Clear();
            if (!Net.Connected || !Plugin.SyncEnemies.Value) return;
            if (Net.IsClient)
            {
                foreach (var kv in _puppets)
                {
                    var c = kv.Value.Can;
                    if (c != null && !Hold.IsLocal(kv.Key) && !c.IsProcessed) { c.IsProcessed = true; _masked.Add(c); }
                }
            }
            else
            {
                foreach (var kv in _hostById)
                {
                    var c = kv.Value;
                    if (c != null && Hold.HostRemote(kv.Key) && !c.IsProcessed) { c.IsProcessed = true; _masked.Add(c); }
                }
            }
        }

        public static void AfterRewardCheck()
        {
            foreach (var c in _masked) if (c != null) c.IsProcessed = false;
            _masked.Clear();
            if (!Net.IsClient || !Net.Connected) return;
            // a can we hold just went into our reward machine: hand it to the host's (paying) machine
            _remove.Clear();
            foreach (var kv in _puppets)
                if (Hold.IsLocal(kv.Key) && kv.Value.Can != null && kv.Value.Can.IsProcessed) _remove.Add(kv.Key);
            foreach (var id in _remove)
            {
                var p = _puppets[id];
                Net.Begin(Msg.CanRewarded).Put(id);
                Net.W.PutVec(p.Can.transform.position);
                Net.Send(DeliveryMethod.ReliableOrdered);
                _puppets.Remove(id);
                _puppetIdByPtr.Remove(p.Can.Pointer); // our reward machine returns it to the pool itself
                Hold.ClientForget(id);
                VacuumHelper.Forget(p.Can);
                _ignoreUntil[id] = Time.unscaledTime + 5f;
            }
        }

        public static void ClientTick()
        {
            if (!GameApi.InGame || !HostSave.GameStartedOrLocal) return;
            var gm = GameManager.Instance;
            if (gm == null) return;

            // our processors never make cans (the host's do): empty their queues
            try
            {
                var procs = gm.canProcessors;
                if (procs != null) for (int i = 0; i < procs.Length; i++) { var q = procs[i]?.pendingCans; if (q != null && q.Count > 0) q.Clear(); }
            }
            catch { }

            // no cans of our own: only the host's exist
            var set = gm.registeredCans;
            if (set != null && set.Count > 0)
            {
                _strays.Clear();
                foreach (var c in set) if (c != null && !c.IsInPool && !IsPuppet(c)) _strays.Add(c);
                foreach (var c in _strays) ReturnToPool(c);
            }

            float now = Time.time;
            _remove.Clear();
            foreach (var kv in _puppets)
            {
                var p = kv.Value;
                if (p.Can == null || p.Can.IsInPool) { _remove.Add(kv.Key); continue; }
                if (Hold.IsLocal(kv.Key)) continue;
                if (now - p.LastUpdate > StaleAfter) { _remove.Add(kv.Key); continue; }
                float k = Mathf.Clamp01((now - p.LerpStart) / (1f / 15f));
                var t = p.Can.transform;
                t.SetPositionAndRotation(Vector3.Lerp(p.FromPos, p.ToPos, k), Quaternion.Slerp(p.FromRot, p.ToRot, k));
            }
            foreach (var id in _remove) if (_puppets.TryGetValue(id, out var p)) DestroyPuppet(id, p);

            // cans we hold: hand back the settled ones, stream the rest
            Hold.ClientTick();
        }
    }
}
