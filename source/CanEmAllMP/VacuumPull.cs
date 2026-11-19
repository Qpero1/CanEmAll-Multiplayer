using System.Collections.Generic;
using UnityEngine;

namespace CanEmAllMP
{
    /// For fun: another player's vacuum (sucking) pulls you towards its nozzle, and their throw launches you
    /// if you're right in front of it. Each player moves themselves, so this runs on the one being pulled.
    public static class VacuumPull
    {
        private const float ConeDegrees = 35f;      // half-angle of the suck cone
        private const float MaxPull = 9f;           // m/s at point blank
        private const float MinPull = 2.5f;         // m/s at the edge of range
        private const float StopDistance = 1.3f;    // don't pull into their face
        private const float ThrowRange = 4.5f, ThrowSpeed = 16f;

        private static Vector3 _pull;       // added every physics step while being sucked
        private static Vector3 _launch;     // added once
        private static readonly Dictionary<int, int> LastState = new Dictionary<int, int>();

        /// Every frame from MpManager, after the avatars have moved.
        public static void Update(IEnumerable<RemoteAvatar> avatars)
        {
            _pull = Vector3.zero;
            if (!Plugin.VacuumPullsPlayers.Value || !GameApi.GameAllowsInput || Spectate.Dead) { LastState.Clear(); return; }
            var p = Player.Instance;
            if (p == null || !GameApi.TryGetLocalPose(out var feet, out _, out _)) return;
            float eye = 1.6f;
            try { if (p.mainCamera != null) eye = Mathf.Clamp(p.mainCamera.transform.position.y - feet.y, 1f, 2.5f); } catch { }
            var me = feet + Vector3.up * (eye * 0.6f); // aim at the chest
            float range = 8f;
            try { var v = Vacuum.Instance; if (v != null && v.suckRange > 1f) range = v.suckRange; } catch { }
            range = Mathf.Max(range * 1.5f, 11f); // players are pulled from a bit further than cans

            foreach (var av in avatars)
            {
                if (av?.Root == null || !av.Root.activeSelf || av.Dead) continue;
                int state = av.Action & 3;
                LastState.TryGetValue(av.Id, out int was);
                LastState[av.Id] = state;
                if (state == 0) continue;

                var nozzle = av.Root.transform.position + Vector3.up * eye;
                var aim = Quaternion.Euler(av.Pitch, av.Root.transform.eulerAngles.y, 0f) * Vector3.forward;
                var to = me - nozzle;
                float dist = to.magnitude;
                if (dist < 0.01f || Vector3.Angle(aim, to) > ConeDegrees) continue;
                if (Physics.Linecast(nozzle, me, p.groundMask, QueryTriggerInteraction.Ignore)) continue; // wall in the way

                if (state == 1 && dist < range && dist > StopDistance)
                {
                    float k = 1f - dist / range;
                    _pull += -to.normalized * Mathf.Lerp(MinPull, MaxPull, k);
                }
                else if (state == 2 && was != 2 && dist < ThrowRange)
                {
                    _launch += aim.normalized * ThrowSpeed + Vector3.up * 4f; // yeet
                }
            }
            if (_pull.magnitude > MaxPull) _pull = _pull.normalized * MaxPull;
        }

        /// One-off shove (friendly fire knockback), applied on the next physics step.
        public static void Knockback(Vector3 velocity) => _launch += velocity;

        private static bool _logged;
        /// Harmony postfix on Player.FixedUpdate: the game has just set your velocity for this step.
        public static void AfterMove(Player p)
        {
            if (_pull == Vector3.zero && _launch == Vector3.zero) return;
            try
            {
                var rb = p.rb;
                if (rb == null) return;
                var v = rb.linearVelocity + _pull + _launch;
                if (_pull != Vector3.zero && v.y < _pull.y) v.y = _pull.y + 0.5f; // lift off the floor so friction doesn't win
                rb.linearVelocity = v;
                if (!_logged) { _logged = true; Plugin.Log.LogInfo($"Vacuum pull applied: {_pull} launch {_launch}"); }
                _launch = Vector3.zero;
            }
            catch { }
        }
    }
}
