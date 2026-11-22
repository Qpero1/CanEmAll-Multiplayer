using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CanEmAllMP
{
    /// <summary>
    /// Creature sync.
    ///  - The host's truck/pool owns every creature's existence, AI and health.
    ///  - Clients show "puppets" (same prefab). Every Enemy.Damage on the host is replayed on the puppets
    ///    with the host's final damage number, so blood, damage text, screams, ragdolls and deaths play for real.
    ///  - Dead bodies can be held by one player at a time (see HoldTable): the holder simulates and streams it.
    ///  - Joiners never keep creatures of their own: anything their truck/menu registers is removed.
    /// </summary>
    public static class EnemySync
    {
        private static NetSession Net => MpManager.Net;
        private const int SnapRecord = 2 + 1 + 12 + 16 + 1 + 1 + 4 + 1 + 1 + 2 + 2;
        private const float Keepalive = 0.5f;     // host: resend unchanged creatures this often
        private const float StaleAfter = 3f;      // client: no news this long -> the puppet goes

        public static readonly HoldTable<Enemy> Hold = new HoldTable<Enemy>("body", Msg.EnemyClaim, Msg.EnemyOwner, Msg.EnemyRelease, Msg.EnemyOwned);

        static EnemySync()
        {
            Hold.HostObj = id => _hostById.TryGetValue(id, out var e) && e != null ? e : null;
            Hold.ClientObj = id => _puppets.TryGetValue(id, out var p) && p.Enemy != null ? p.Enemy : null;
            Hold.RbOf = e => e.rb;
            Hold.InMachine = BeingProcessed;
            Hold.KeepDynamic = e => e.IsDead;
            Hold.LocalChanged = (id, e, local) =>
            {
                if (!_puppets.TryGetValue(id, out var p) || p.Rb == null) return;
                if (local) { p.Rb.isKinematic = false; p.Rb.useGravity = true; }
                else
                {
                    p.Rb.isKinematic = true;
                    var t = e.transform;
                    p.FromPos = p.ToPos = t.position; p.FromRot = p.ToRot = t.rotation;
                    p.LerpStart = p.LastUpdate = Time.time;
                }
            };
        }

        // =====================================================================  shared helpers

        public static bool Replaying;        // client: replaying a host hit on a puppet
        public static float ReplayHit;       // the host's final damage (OnHitEnemy result) for that hit

        /// Enemy whose physics/AI belong to someone else on this machine.
        public static bool IsRemote(Enemy e)
        {
            if (e == null || !Net.Connected) return false;
            var ptr = e.Pointer;
            if (Net.IsClient) return _puppetIdByPtr.TryGetValue(ptr, out var id) && !Hold.IsLocal(id);
            return _hostIds.TryGetValue(ptr, out var hid) && Hold.HostRemote(hid);
        }

        public static bool IsPuppet(Enemy e) => e != null && _puppetIdByPtr.ContainsKey(e.Pointer);

        /// A local can processor is crushing this body: leave it alone (its coroutine moves it, then removes it).
        public static bool BeingProcessed(Enemy e)
        {
            try
            {
                var arr = GameManager.Instance?.canProcessors;
                if (arr == null || e == null) return false;
                for (int i = 0; i < arr.Length; i++)
                {
                    var set = arr[i]?.processingEnemies;
                    if (set != null && set.Contains(e)) return true;
                }
            }
            catch { }
            return false;
        }

        /// Our player grabs a body (hand: force) or our vacuum pulls/stores it. False = someone else has it.
        public static bool LocalGrab(Enemy e, bool force)
        {
            if (e == null || !Net.Connected || !Plugin.SyncEnemies.Value || !e.IsDead) return true;
            if (Net.IsHost) return Hold.HostGrab(HostIdOf(e), force);
            return !_puppetIdByPtr.TryGetValue(e.Pointer, out var id) || Hold.ClientGrab(id, force);
        }

        // while our vacuum runs, bodies someone else holds count as "free falling" so it ignores them
        private static readonly List<ushort> _ids = new List<ushort>();
        private static readonly List<Enemy> _marked = new List<Enemy>();

        public static void MarkHeldByOthers()
        {
            _marked.Clear();
            if (!Net.Connected || !Plugin.SyncEnemies.Value) return;
            Hold.CollectHeldByOthers(_ids);
            foreach (var id in _ids)
            {
                var e = Net.IsHost ? Hold.HostObj(id) : Hold.ClientObj(id);
                if (e == null || e._IsFreeFall_k__BackingField || !e.gameObject.activeInHierarchy) continue;
                e._IsFreeFall_k__BackingField = true;
                _marked.Add(e);
            }
        }

        public static void UnmarkHeldByOthers()
        {
            foreach (var e in _marked) { try { if (e != null) e._IsFreeFall_k__BackingField = false; } catch { } }
            _marked.Clear();
        }

        public static void Reset()
        {
            try { Hold.HostResetAll(); } catch { }
            Hold.Reset();
            _hostIds.Clear(); _hostById.Clear(); _sent.Clear(); _lastPresent.Clear(); _nextId = 1;
            _deformSent.Clear(); _pendingDeform.Clear();
            foreach (var p in _puppets.Values)
                if (p.Enemy != null) { VacuumHelper.Forget(p.Enemy); UnityEngine.Object.Destroy(p.Enemy.gameObject); }
            _puppets.Clear(); _puppetIdByPtr.Clear();
        }

        // =====================================================================  HOST

        private static readonly Dictionary<IntPtr, ushort> _hostIds = new Dictionary<IntPtr, ushort>();
        private static readonly Dictionary<ushort, Enemy> _hostById = new Dictionary<ushort, Enemy>();
        private static HashSet<ushort> _present = new HashSet<ushort>(), _lastPresent = new HashSet<ushort>();
        private static readonly List<ushort> _gone = new List<ushort>();
        private struct Sent { public Vector3 Pos; public Quaternion Rot; public byte Flags, State, Holder, Epoch; public float Health, Time, Burn, Dmg; }
        private static readonly Dictionary<ushort, Sent> _sent = new Dictionary<ushort, Sent>();
        private static readonly List<(Enemy e, ushort id, Sent s)> _toSend = new List<(Enemy, ushort, Sent)>();
        private static ushort _nextId = 1;

        // damage capture (host): Enemy.Damage prefix sets the context, OnHitEnemy postfix records the final number
        public static Enemy DamageCtx;
        public static float DamageCtxHit = float.NaN;

        private static ushort HostIdOf(Enemy e)
        {
            if (!_hostIds.TryGetValue(e.Pointer, out var id))
            {
                do { id = _nextId++; if (_nextId == 0) _nextId = 1; } while (_hostById.ContainsKey(id));
                _hostIds[e.Pointer] = id;
                _hostById[id] = e;
            }
            return id;
        }

        private static int PrefabIndexOf(Enemy e, Truck truck)
        {
            try
            {
                var map = truck.enemyPrefabByInstance;
                if (map == null || !map.ContainsKey(e)) return -1;
                var prefab = map[e];
                var all = truck.allEnemyPrefabs;
                for (int i = 0; i < all.Count; i++)
                    if (all[i] != null && all[i].Pointer == prefab.Pointer) return i;
            }
            catch { }
            return -1;
        }

        /// Host: creature id is no longer valid (gone, or the pooled creature came back as a new one).
        private static void HostDropId(NetSession net, ushort id)
        {
            net.Begin(Msg.EnemyGone).Put(id);
            net.Send(DeliveryMethod.ReliableOrdered);
            _deformSent.Remove(id);
            _hostById.TryGetValue(id, out var obj);
            Hold.HostForget(id, obj != null ? obj : null);
            if (!ReferenceEquals(obj, null)) _hostIds.Remove(obj.Pointer);
            _hostById.Remove(id);
            _sent.Remove(id);
        }

        public static void HostBroadcast(NetSession net)
        {
            var gm = GameManager.Instance;
            var truck = Truck.Instance;
            if (gm == null || truck == null || net.Players.Count < 2) return;
            float now = Time.unscaledTime;

            _present.Clear(); _toSend.Clear();
            var set = gm.registeredEnemies;   // registered = exists (switched off, e.g. inside a vacuum, is sent as hidden)
            if (set != null)
                foreach (var e in set)
                {
                    if (e == null) continue;
                    var id = HostIdOf(e);
                    var t = e.transform;
                    var s = new Sent { Pos = t.position, Rot = t.rotation, State = (byte)e.state, Health = e.health, Time = now };
                    if (e.IsDead) s.Flags |= 1;
                    if (e.IsShiny) s.Flags |= 2;
                    if (!e.gameObject.activeInHierarchy) s.Flags |= 4;
                    if (e.panicTimer > 0f) s.Flags |= 8;
                    ReadLook(e, out s.Burn, out s.Dmg);
                    if (_sent.TryGetValue(id, out var last) && (last.Flags & 1) != 0 && (s.Flags & 1) == 0)
                    {
                        // a pooled creature came back to life: it's a new creature for everyone
                        HostDropId(net, id);
                        _lastPresent.Remove(id);
                        id = HostIdOf(e);
                        last = default;
                    }
                    _present.Add(id);
                    HostDeform(net, e, id);
                    s.Holder = Hold.HostHolder(id); s.Epoch = Hold.HostEpoch(id);
                    if (last.Time > 0 && now - last.Time < Keepalive && last.Flags == s.Flags && last.State == s.State
                        && last.Holder == s.Holder && last.Epoch == s.Epoch && Mathf.Abs(last.Health - s.Health) < 0.01f
                        && (last.Pos - s.Pos).sqrMagnitude < 0.000025f && Quaternion.Angle(last.Rot, s.Rot) < 0.5f
                        && Mathf.Abs(last.Burn - s.Burn) < 0.01f && Mathf.Abs(last.Dmg - s.Dmg) < 0.01f)
                        continue;
                    _sent[id] = s;
                    _toSend.Add((e, id, s));
                }

            int per = Math.Max(1, Math.Min(255, (net.MaxUnreliablePayload - 2) / SnapRecord));
            for (int start = 0; start < _toSend.Count; start += per)
            {
                int end = Math.Min(_toSend.Count, start + per);
                var w = net.Begin(Msg.EnemySnapshot);
                w.Put((byte)(end - start));
                for (int i = start; i < end; i++)
                {
                    var (e, id, s) = _toSend[i];
                    w.Put(id);
                    w.Put((sbyte)PrefabIndexOf(e, truck));
                    w.PutVec(s.Pos); w.PutQuat(s.Rot);
                    w.Put(s.Flags); w.Put(s.State); w.Put(s.Health);
                    w.Put(s.Holder); w.Put(s.Epoch);
                    w.Put(Mathf.FloatToHalf(s.Burn)); w.Put(Mathf.FloatToHalf(s.Dmg));
                }
                net.Send(DeliveryMethod.Unreliable);
            }

            // creatures that disappeared (canned, cleaned up at day end, returned to the pool)
            _gone.Clear();
            foreach (var id in _lastPresent) if (!_present.Contains(id)) _gone.Add(id);
            foreach (var id in _gone) HostDropId(net, id);
            var swap = _lastPresent; _lastPresent = _present; _present = swap;
        }

        /// Host: stream the delivery truck so joiners' trucks are an exact mirror (their own never drives).
        public static void HostBroadcastTruck(NetSession net)
        {
            var truck = Truck.Instance;
            if (truck == null || truck.truck == null || net.Players.Count < 2) return;
            var w = net.Begin(Msg.TruckState);
            var tt = truck.truck.transform;
            w.PutVec(tt.position); w.PutQuat(tt.rotation);
            var door = truck.door;
            w.PutVec(door != null ? door.localPosition : Vector3.zero);
            w.PutQuat(door != null ? door.localRotation : Quaternion.identity);
            int hash = 0; float nt = 0;
            try
            {
                var anim = truck.facilityDoor;
                if (anim != null) { var info = anim.GetCurrentAnimatorStateInfo(0); hash = info.fullPathHash; nt = info.normalizedTime; }
            }
            catch { }
            w.Put(hash); w.Put(nt);
            net.Send(DeliveryMethod.Sequenced);
        }

        private static int _lastDoorHash;

        public static void ClientReceiveTruck(NetPacketReader r)
        {
            var pos = r.GetVec(); var rot = r.GetQuat();
            var dpos = r.GetVec(); var drot = r.GetQuat();
            int hash = r.GetInt(); float nt = r.GetFloat();
            var truck = Truck.Instance;
            if (truck == null || truck.truck == null || !GameApi.InGame) return;
            var rb = truck.truck;
            rb.position = pos; rb.rotation = rot;
            rb.transform.SetPositionAndRotation(pos, rot);
            if (truck.door != null) { truck.door.localPosition = dpos; truck.door.localRotation = drot; }
            try
            {
                var anim = truck.facilityDoor;
                if (anim != null && hash != 0 && hash != _lastDoorHash)
                {
                    _lastDoorHash = hash;
                    anim.Play(hash, 0, Mathf.Repeat(nt, 1f));
                }
            }
            catch { }
        }

        /// Host, every frame: bodies joiners hold follow them; ones nobody holds any more come back.
        public static void HostTick() => Hold.HostTick();

        /// Host: Enemy.Damage finished on a host-simulated creature -> replay it on every client.
        public static void HostBroadcastDamage(Enemy e, float damage, Vector3 dir, Vector3 hitPos, float radiusMul,
            bool showBlood, bool showText, Weapon weapon, bool scream, bool flash, float probMul, float finalHit)
        {
            if (!Net.IsHost || Net.Players.Count < 2 || e == null || IsRemote(e)) return;
            var id = HostIdOf(e);
            var w = Net.Begin(Msg.EnemyDamaged);
            w.Put(id); w.Put(damage); w.PutVec(dir); w.PutVec(hitPos); w.Put(radiusMul);
            w.Put(showBlood); w.Put(showText); w.Put((sbyte)GameApi.WeaponIndex(weapon));
            w.Put(scream); w.Put(flash); w.Put(probMul); w.Put(finalHit);
            Net.Send(DeliveryMethod.ReliableOrdered);
        }

        public static void HostApplyHit(int from, NetPacketReader r)
        {
            var id = r.GetUShort();
            float damage = r.GetFloat();
            var dir = r.GetVec();
            var hitPos = r.GetVec();
            float radiusMul = r.GetFloat();
            bool showBlood = r.GetBool(), showText = r.GetBool();
            int weaponIdx = r.GetSByte();
            bool scream = r.GetBool(), flash = r.GetBool();
            float probMul = r.GetFloat();
            bool notPlayer = r.AvailableBytes >= 1 && r.GetBool();

            if (!_hostById.TryGetValue(id, out var e) || e == null || IsRemote(e)) return;
            PlayerStats.Attacker = from;
            if (notPlayer) PlayerStats.Ignore++;
            try { e.Damage(damage, dir, hitPos, radiusMul, showBlood, showText, GameApi.WeaponAt(weaponIdx), scream, flash, probMul); }
            catch (Exception ex) { Plugin.Log.LogError("Remote hit failed: " + ex); }
            finally { PlayerStats.Attacker = -1; if (notPlayer && PlayerStats.Ignore > 0) PlayerStats.Ignore--; }
        }

        // -------------------------------------------------- burns (explosive bullets / flamethrower)

        public static void ClientForwardBurn(Enemy e, float amount)
        {
            if (e == null || !_puppetIdByPtr.TryGetValue(e.Pointer, out var id)) return;
            Net.Begin(Msg.EnemyBurn).Put(id);
            Net.W.Put(amount);
            Net.Send(DeliveryMethod.ReliableOrdered);
        }

        public static void HostReceiveBurn(NetPacketReader r, int from = -1)
        {
            var id = r.GetUShort(); float amount = r.GetFloat();
            if (_hostById.TryGetValue(id, out var e) && e != null && !IsRemote(e))
            {
                if (from >= 0) PlayerStats.Burned(e, from);
                try { e.AddBurn(amount); } catch (Exception ex) { Plugin.Log.LogError(ex); }
            }
        }

        /// A client left: the host takes their bodies back.
        public static void HostPlayerLeft(int playerId) => Hold.HostPlayerLeft(playerId);

        /// End of day (host): every body back to the host before the game cleans up.
        public static void ReclaimAll() { if (Net.IsHost) Hold.HostReclaimAll(); }

        // =====================================================================  CLIENT

        private class Puppet
        {
            public Enemy Enemy;
            public Rigidbody Rb;
            public ushort Id;
            public Vector3 FromPos, ToPos;
            public Quaternion FromRot, ToRot;
            public float LastUpdate, LerpStart;
            public bool Dead;
            public float Burn, Dmg;       // the host's burnt / damaged shader values
            public Vector4[] Slots;       // the host's damage slots (dents / broken-off parts)
        }

        // -------------------------------------------------- dents / broken-off parts
        // Every hit stores a "damage slot" (a vector per slot in the creature's shader) that dents the mesh and
        // breaks parts off. Hits on corpses and shockwaves aren't replayed as hits, so the host sends the slots
        // themselves whenever a creature's hit counter changes, and joiners' copies show exactly the host's.

        private static int _deformLogged;
        private static readonly Dictionary<ushort, int> _deformSent = new Dictionary<ushort, int>();
        private static readonly Dictionary<ushort, Vector4[]> _pendingDeform = new Dictionary<ushort, Vector4[]>();

        /// Host: a player joined - send everyone's current dents again.
        public static void ResendDeform() => _deformSent.Clear();

        private static void HostDeform(NetSession net, Enemy e, ushort id)
        {
            try
            {
                int count = e.damageCount;
                if (_deformSent.TryGetValue(id, out var last) && last == count) return;
                if (count == 0 && !_deformSent.ContainsKey(id)) { _deformSent[id] = 0; return; } // untouched: nothing to send
                var ids = Enemy.DamageSlots;
                var mpb = e.MaterialPropertyBlock;
                if (ids == null || mpb == null) return;
                int n = Math.Min(ids.Length, 32);
                var w = net.Begin(Msg.EnemyDeform);
                w.Put(id); w.Put((byte)n);
                for (int i = 0; i < n; i++)
                {
                    var v = mpb.GetVector(ids[i]);
                    w.Put(v.x); w.Put(v.y); w.Put(v.z); w.Put(v.w);
                }
                net.Send(DeliveryMethod.ReliableOrdered);
                _deformSent[id] = count;
                if (_deformLogged++ < 2)
                    Plugin.Log.LogInfo($"[MP] dents for creature {id}: {n} slots, hit count {count}, first {mpb.GetVector(ids[0])}");
            }
            catch (Exception ex) { if (_lookErr++ < 3) Plugin.Log.LogWarning("[MP] deform send: " + ex.Message); }
        }

        public static void ClientReceiveDeform(NetPacketReader r)
        {
            var id = r.GetUShort();
            int n = r.GetByte();
            var slots = new Vector4[n];
            for (int i = 0; i < n; i++) slots[i] = new Vector4(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
            if (_puppets.TryGetValue(id, out var p) && p.Enemy != null) p.Slots = slots;
            else _pendingDeform[id] = slots;   // the creature's first snapshot hasn't arrived yet
        }

        // -------------------------------------------------- burnt / damaged look
        // Burning (flamethrower, explosive bullets) and hit damage only change a creature's shader values on
        // the machine where they happen. The host sends its values with each snapshot and puppets show
        // whichever is stronger: the host's, or what this player did to it themselves.

        private static void ReadLook(Enemy e, out float burn, out float dmg)
        {
            burn = 0f; dmg = 0f;
            try
            {
                var mpb = e.MaterialPropertyBlock;
                if (mpb == null) return;
                burn = mpb.GetFloat(Enemy.Burning);
                dmg = mpb.GetFloat(Enemy.GlobalDamage);
            }
            catch { }
        }

        /// After the creature's own LateUpdate (which writes its shader values): show the host's burn / damage.
        public static void ApplyLook(Enemy e)
        {
            if (!Net.IsClient || e == null || !_puppetIdByPtr.TryGetValue(e.Pointer, out var id)) return;
            if (!_puppets.TryGetValue(id, out var p) || (p.Burn <= 0.001f && p.Dmg <= 0.001f && p.Slots == null)) return;
            try
            {
                var mpb = e.MaterialPropertyBlock;
                var mr = e.meshRenderer;
                if (mpb == null || mr == null) return;
                bool changed = false;
                if (p.Burn > mpb.GetFloat(Enemy.Burning) + 0.001f) { mpb.SetFloat(Enemy.Burning, p.Burn); changed = true; }
                if (p.Dmg > mpb.GetFloat(Enemy.GlobalDamage) + 0.001f) { mpb.SetFloat(Enemy.GlobalDamage, p.Dmg); changed = true; }
                if (p.Slots != null)
                {
                    var ids = Enemy.DamageSlots;
                    for (int i = 0; ids != null && i < p.Slots.Length && i < ids.Length; i++)
                        if (mpb.GetVector(ids[i]) != p.Slots[i]) { mpb.SetVector(ids[i], p.Slots[i]); changed = true; }
                }
                if (changed) mr.SetPropertyBlock(mpb);
            }
            catch (Exception ex) { if (_lookErr++ < 3) Plugin.Log.LogWarning("[MP] burn look: " + ex.Message); }
        }
        private static int _lookErr;

        private static readonly Dictionary<ushort, Puppet> _puppets = new Dictionary<ushort, Puppet>();
        private static readonly Dictionary<IntPtr, ushort> _puppetIdByPtr = new Dictionary<IntPtr, ushort>();
        private static readonly List<ushort> _remove = new List<ushort>();
        private static readonly List<Enemy> _strays = new List<Enemy>();

        /// Client: remove every puppet (end-of-day cleanup); the host's next updates recreate what still exists.
        public static void ClearPuppets()
        {
            foreach (var p in _puppets.Values)
                if (p.Enemy != null) { try { VacuumHelper.Forget(p.Enemy); UnityEngine.Object.Destroy(p.Enemy.gameObject); } catch { } }
            _puppets.Clear(); _puppetIdByPtr.Clear();
            Hold.ClientReset();
        }

        public static void ClientReceive(NetPacketReader r)
        {
            if (!GameApi.InGame || !HostSave.GameStartedOrLocal) return;
            var truck = Truck.Instance;
            if (truck == null) return;

            int n = r.GetByte();
            float now = Time.time;
            for (int i = 0; i < n; i++)
            {
                var id = r.GetUShort();
                int prefab = r.GetSByte();
                var pos = r.GetVec();
                var rot = r.GetQuat();
                byte flags = r.GetByte();
                byte state = r.GetByte();
                float health = r.GetFloat();
                byte holder = r.GetByte(), epoch = r.GetByte();
                float burn = Mathf.HalfToFloat(r.GetUShort()), dmg = Mathf.HalfToFloat(r.GetUShort());

                bool hidden = (flags & 4) != 0;
                bool dead = (flags & 1) != 0;
                _puppets.TryGetValue(id, out var p);
                if (p != null && p.Enemy != null && p.Dead && !dead) { DestroyPuppet(id, p); p = null; } // came back to life = a new creature
                if (p == null || p.Enemy == null)
                {
                    if (p != null) DestroyPuppet(id, p);
                    if (hidden) continue;
                    p = SpawnPuppet(truck, id, prefab, pos, rot);
                    if (p == null) continue;
                    _puppets[id] = p;
                    if (_pendingDeform.TryGetValue(id, out var pend)) { p.Slots = pend; _pendingDeform.Remove(id); }
                    _puppetIdByPtr[p.Enemy.Pointer] = id;
                }
                p.LastUpdate = now;
                p.Burn = burn; p.Dmg = dmg;   // burnt / damaged look, drawn in ApplyLook
                Hold.ClientState(id, holder, epoch);
                if (Hold.IsLocal(id)) continue;          // we're the authority for this one
                if (BeingProcessed(p.Enemy)) continue;

                var go = p.Enemy.gameObject;
                if (go.activeSelf == hidden) go.SetActive(!hidden);

                p.FromPos = p.Enemy.transform.position;
                p.FromRot = p.Enemy.transform.rotation;
                p.ToPos = pos; p.ToRot = rot;
                p.LerpStart = now;

                if ((flags & 2) != 0 && !p.Enemy.IsShiny) { try { p.Enemy.SetShiny(true); } catch { } }
                // Enemy.Damage ignores anything not in the Roaming state, so mirror the host's state
                if ((byte)p.Enemy.state != state) p.Enemy.state = (Enemy.State)state;
                p.Enemy.panicTimer = (flags & 8) != 0 ? 0.5f : 0f;

                if (!dead && !p.Enemy.IsDead) p.Enemy.health = health;
                if (dead && !p.Enemy.IsDead) ReplayKill(p);
                p.Dead = dead;
            }
        }

        private static Puppet SpawnPuppet(Truck truck, ushort id, int prefabIndex, Vector3 pos, Quaternion rot)
        {
            var all = truck.allEnemyPrefabs;
            if (all == null || all.Count == 0) return null;
            if (prefabIndex < 0 || prefabIndex >= all.Count) prefabIndex = 0;
            var prefab = all[prefabIndex];
            if (prefab == null) return null;

            var go = UnityEngine.Object.Instantiate(prefab.gameObject, pos, rot);
            go.name = "MP_Puppet_" + prefab.name;
            go.SetActive(true);
            var e = go.GetComponent<Enemy>();
            if (e == null) { UnityEngine.Object.Destroy(go); return null; }
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = true; rb.interpolation = RigidbodyInterpolation.None; }
            e.state = Enemy.State.Roaming;
            return new Puppet { Id = id, Enemy = e, Rb = rb, FromPos = pos, ToPos = pos, FromRot = rot, ToRot = rot, LastUpdate = Time.time, LerpStart = Time.time };
        }

        /// The host says this creature died but our puppet is still alive: kill it locally for the full death visuals.
        private static void ReplayKill(Puppet p)
        {
            var e = p.Enemy;
            e.state = Enemy.State.Roaming;
            Replaying = true; ReplayHit = 999999f;
            try { e.Damage(999999f, Vector3.up, e.transform.position + Vector3.up, 1f, true, false, null, true, true, 1f); }
            catch (Exception ex) { Plugin.Log.LogWarning("Replay kill failed: " + ex.Message); try { e.IsDead = true; e.EnableRagdoll(); } catch { } }
            finally { Replaying = false; }
        }

        public static void ClientReceiveDamage(NetPacketReader r)
        {
            var id = r.GetUShort();
            float damage = r.GetFloat();
            var dir = r.GetVec();
            var hitPos = r.GetVec();
            float radiusMul = r.GetFloat();
            bool showBlood = r.GetBool(), showText = r.GetBool();
            int weaponIdx = r.GetSByte();
            bool scream = r.GetBool(), flash = r.GetBool();
            float probMul = r.GetFloat();
            float finalHit = r.GetFloat();

            if (!_puppets.TryGetValue(id, out var p) || p.Enemy == null || Hold.IsLocal(id)) return;
            if (!p.Enemy.gameObject.activeInHierarchy) return;
            Replaying = true; ReplayHit = finalHit;
            try { p.Enemy.Damage(damage, dir, hitPos, radiusMul, showBlood, showText, GameApi.WeaponAt(weaponIdx), scream, flash, probMul); }
            catch (Exception ex) { Plugin.Log.LogWarning("Hit replay failed: " + ex.Message); }
            finally { Replaying = false; }
        }

        public static void ClientReceiveGone(NetPacketReader r)
        {
            var id = r.GetUShort();
            if (!_puppets.TryGetValue(id, out var p)) return;
            if (p.Enemy != null && BeingProcessed(p.Enemy))
            {
                // our processor is canning our copy: it returns/destroys it itself
                _puppets.Remove(id); _puppetIdByPtr.Remove(p.Enemy.Pointer); Hold.ClientForget(id);
                VacuumHelper.Forget(p.Enemy);
                return;
            }
            DestroyPuppet(id, p);
        }

        private static void DestroyPuppet(ushort id, Puppet p)
        {
            _puppets.Remove(id);
            Hold.ClientForget(id);
            if (ReferenceEquals(p.Enemy, null)) return;
            _puppetIdByPtr.Remove(p.Enemy.Pointer);
            if (p.Enemy != null)
            {
                VacuumHelper.Forget(p.Enemy);
                UnityEngine.Object.Destroy(p.Enemy.gameObject);
            }
        }

        /// Client, every frame.
        public static void ClientTick()
        {
            if (!GameApi.InGame || !HostSave.GameStartedOrLocal) return;

            // 1) never keep creatures of our own (menu leftovers, our own truck): the host's are the only ones
            var gm = GameManager.Instance;
            var truck = Truck.Instance;
            if (gm?.registeredEnemies != null && truck != null && gm.registeredEnemies.Count > 0)
            {
                _strays.Clear();
                foreach (var e in gm.registeredEnemies) if (e != null && !IsPuppet(e)) _strays.Add(e);
                foreach (var e in _strays) { try { truck.ReturnEnemy(e); } catch { } }
            }

            // 2) interpolate puppets, drop stale ones
            float now = Time.time;
            _remove.Clear();
            foreach (var kv in _puppets)
            {
                var p = kv.Value;
                if (p.Enemy == null) { _remove.Add(kv.Key); continue; }
                if (Hold.IsLocal(kv.Key)) continue;
                if (BeingProcessed(p.Enemy)) { p.LastUpdate = now; continue; }
                if (now - p.LastUpdate > StaleAfter) { _remove.Add(kv.Key); continue; }
                float k = Mathf.Clamp01((now - p.LerpStart) / (1f / 15f));
                var t = p.Enemy.transform;
                t.SetPositionAndRotation(Vector3.Lerp(p.FromPos, p.ToPos, k), Quaternion.Slerp(p.FromRot, p.ToRot, k));
                if (!p.Enemy.IsDead) { try { p.Enemy.UpdateAnimationBlend(Time.deltaTime); } catch { } }
            }
            foreach (var id in _remove) if (_puppets.TryGetValue(id, out var p)) DestroyPuppet(id, p);

            // 3) bodies we hold: hand back the settled ones, stream the rest
            Hold.ClientTick();
        }

        /// Client hit a host-controlled puppet: send it to the host (which replays the result to everyone).
        public static bool ClientForwardHit(Enemy e, float damage, Vector3 dir, Vector3 hitPos, float radiusMul,
            bool showBlood, bool showText, Weapon weapon, bool scream, bool flash, float probMul)
        {
            if (e == null || !_puppetIdByPtr.TryGetValue(e.Pointer, out var id)) return false;
            var w = Net.Begin(Msg.EnemyHit);
            w.Put(id); w.Put(damage); w.PutVec(dir); w.PutVec(hitPos); w.Put(radiusMul);
            w.Put(showBlood); w.Put(showText); w.Put((sbyte)GameApi.WeaponIndex(weapon));
            w.Put(scream); w.Put(flash); w.Put(probMul);
            w.Put(PlayerStats.Ignore > 0); // not a player's hit (my turret)
            Net.Send(DeliveryMethod.ReliableOrdered);
            return true;
        }
    }
}
