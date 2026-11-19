using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace CanEmAllMP
{
    /// Late game, cutelings bumping each other while leaving the truck can get pushed into walls / behind the
    /// truck / out of the map. They still count towards the truck's maximum (fewer new ones per delivery), and ones
    /// that never start roaming make the truck wait its full 10 s every delivery. Host (or solo) puts them back.
    ///
    /// Stuck = off the walkable area the cutelings themselves path on (navmesh), squeezed motionless next to the
    /// truck, still "leaving the truck" after 12 s, or fallen out of the map. They're put next to a healthy roaming
    /// cuteling away from the truck (or next to a player), then Enemy.SpawnRoaming (roaming + a fresh path).
    public static class StuckRescue
    {
        private const float OffMeshFor = 3f, SqueezedFor = 8f, ExitingFor = 12f, FallenFor = 1f, Cooldown = 15f;
        private static float _nextCheck;
        private static readonly Dictionary<System.IntPtr, float> BadSince = new Dictionary<System.IntPtr, float>();
        private static readonly Dictionary<System.IntPtr, (Vector3 pos, float time)> LastMove = new Dictionary<System.IntPtr, (Vector3, float)>();
        private static readonly Dictionary<System.IntPtr, float> RescuedAt = new Dictionary<System.IntPtr, float>();
        private static bool _navBroken;
        private static int _rescued;

        private static bool OnNavMesh(Vector3 p, float range, out Vector3 snapped)
        {
            snapped = p;
            if (_navBroken) return true; // can't tell: assume fine
            try
            {
                if (NavMesh.SamplePosition(p, out NavMeshHit hit, range, NavMesh.AllAreas)) { snapped = hit.position; return true; }
                return false;
            }
            catch (System.Exception e) { _navBroken = true; Plugin.Log.LogWarning("navmesh check unavailable: " + e.Message); return true; }
        }

        public static void Tick()
        {
            if (!Plugin.RescueStuckCutelings.Value) return;
            var net = MpManager.Net;
            if (net.Connected && !net.IsHost) return;           // the host decides where creatures are
            if (!GameApi.InGame || Time.time < _nextCheck) return;
            _nextCheck = Time.time + 0.5f;

            var truck = Truck.Instance;
            if (truck == null) return;
            var list = truck.spawnedEnemies;
            if (list == null) return;
            var truckPos = truck.transform.position;
            float floorY = truckPos.y;
            try { if (truck.facilityDoor != null) floorY = truck.facilityDoor.transform.position.y; } catch { }

            float now = Time.time;
            var healthy = new List<Vector3>();
            var stuck = new List<Enemy>();
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == null) continue;
                try
                {
                    var ptr = e.Pointer;
                    if (!e.gameObject.activeInHierarchy || e.IsDead || e.isHeld) { BadSince.Remove(ptr); LastMove.Remove(ptr); continue; }
                    int state = (int)e.CurrentState;
                    if (state == 0) continue; // waiting inside the truck: fine
                    var pos = e.rb != null ? e.rb.position : e.transform.position;

                    // has it moved lately?
                    if (!LastMove.TryGetValue(ptr, out var lm) || (pos - lm.pos).sqrMagnitude > 0.3f * 0.3f) { LastMove[ptr] = (pos, now); lm = (pos, now); }
                    bool nearTruck = (pos - truckPos).sqrMagnitude < 9f * 9f;

                    bool fallen = pos.y < floorY - 12f;
                    bool offMesh = state == 2 && !OnNavMesh(pos, 1.5f, out _);
                    bool squeezed = state == 2 && nearTruck && now - lm.time > SqueezedFor;
                    bool exiting = state == 1;
                    float need = fallen ? FallenFor : offMesh ? OffMeshFor : squeezed ? 0f : exiting ? ExitingFor : -1f;

                    if (need < 0f)
                    {
                        BadSince.Remove(ptr);
                        if (state == 2 && !nearTruck) healthy.Add(pos);
                        continue;
                    }
                    if (RescuedAt.TryGetValue(ptr, out var at) && now - at < Cooldown) continue; // just moved it
                    if (!BadSince.TryGetValue(ptr, out var since)) { BadSince[ptr] = now; continue; }
                    if (now - since >= need) stuck.Add(e);
                }
                catch { }
            }

            int done = 0;
            foreach (var e in stuck)
            {
                if (done++ >= 6) break; // a few per check
                BadSince.Remove(e.Pointer);
                RescuedAt[e.Pointer] = now;
                LastMove.Remove(e.Pointer);
                Rescue(e, healthy, truck);
            }
        }

        private static void Rescue(Enemy e, List<Vector3> healthy, Truck truck)
        {
            try
            {
                Vector3 target;
                if (healthy.Count > 0) target = healthy[Random.Range(0, healthy.Count)];
                else
                {
                    var p = Player.Instance;
                    target = p != null ? p.transform.position : truck.transform.position;
                }
                target += new Vector3(Random.Range(-1.5f, 1.5f), 0f, Random.Range(-1.5f, 1.5f));
                if (OnNavMesh(target, 3f, out var snapped)) target = snapped;
                target += Vector3.up * 0.3f;

                var rb = e.rb;
                if (rb != null) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.position = target; }
                e.transform.position = target;
                float radius = 10f;
                try { radius = truck.roamRadius > 0f ? truck.roamRadius : e.roamRadius; } catch { }
                e.SpawnRoaming(radius);
                _rescued++;
                if (_rescued <= 20 || _rescued % 25 == 0) Plugin.Log.LogInfo($"[MP] rescued a stuck cuteling ({_rescued} so far)");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("cuteling rescue failed: " + ex.Message); }
        }
    }
}
