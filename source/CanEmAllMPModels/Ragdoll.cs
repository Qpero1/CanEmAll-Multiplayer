#nullable disable
// A dead player's body: their miner turned into a physics ragdoll (rigidbodies, capsule colliders and character
// joints added to the Mixamo bones at runtime). It falls where they died and stays until the day ends.
//
// Notes for this game (Unity 6 + Il2CppInterop): Object.Instantiate and Animator.GetBoneTransform are stripped
// (their interop replacements need a generic Unity helper the game doesn't have), so the live model itself goes
// limp and bones are found by their Mixamo names.

using System;
using System.Collections.Generic;
using CanEmAllMP;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CanEmAllMPModels
{
    internal static class Ragdoll
    {
        private static int _err;
        private static bool _logged;

        // bone, its parent in the ragdoll, the bone its collider reaches towards, share of the mass,
        // how far it may bend away from its parent, how far it may twist around itself (degrees, hard stops)
        private static readonly (string bone, string parent, string toward, float mass, float bend, float twist)[] Parts =
        {
            ("Hips",         null,          "Spine1",       0.22f,   0f,  0f),
            ("Spine2",       "Hips",        "Neck",         0.20f,  30f, 15f),
            ("Head",         "Spine2",      "HeadTop_End",  0.08f,  40f, 35f),
            ("LeftArm",      "Spine2",      "LeftForeArm",  0.04f,  90f, 25f),
            ("LeftForeArm",  "LeftArm",     "LeftHand",     0.03f, 110f, 15f),
            ("RightArm",     "Spine2",      "RightForeArm", 0.04f,  90f, 25f),
            ("RightForeArm", "RightArm",    "RightHand",    0.03f, 110f, 15f),
            ("LeftUpLeg",    "Hips",        "LeftLeg",      0.10f,  75f, 15f),
            ("LeftLeg",      "LeftUpLeg",   "LeftFoot",     0.06f, 100f, 10f),
            ("RightUpLeg",   "Hips",        "RightLeg",     0.10f,  75f, 15f),
            ("RightLeg",     "RightUpLeg",  "RightFoot",    0.06f, 100f, 10f),
        };

        // The game has no Unity joints (its own creatures use a "FakeRagdoll"), so CharacterJoint can't be added.
        // Instead every physics step each limb is pulled back onto its attachment point on its parent and kept
        // within how far it may bend: floppy, but in one piece.
        private class Link
        {
            public Rigidbody Child, Parent;
            public Vector3 Anchor;      // child's origin, in the parent's rotation frame (world units)
            public Quaternion Rest;     // child's rotation relative to the parent at the start
            public Vector3 Axis;        // the bone's own direction, in its local space (twist is around this)
            public float Bend, Twist;
        }
        private class Rig
        {
            public GameObject Model;
            public int Owner = -1;          // whose body (player id); -1 = local only (the F9 test dummy)
            public bool Puppet;             // joiner: following the host's poses
            public float StartY;            // where it died (a body down the tunnel stops 10 km below this)
            public bool Frozen;
            public float NextSend;
            public readonly List<Link> Links = new List<Link>();
            public readonly List<Rigidbody> Bodies = new List<Rigidbody>();
        }
        private static readonly List<Rig> Active = new List<Rig>();

        private static Dictionary<string, Transform> Bones(GameObject model)
        {
            var map = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            var ts = model.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < ts.Length; i++)
            {
                string n = null;
                try { n = ts[i].name; } catch { }
                if (string.IsNullOrEmpty(n)) continue;
                int colon = n.LastIndexOf(':');
                string key = colon >= 0 ? n.Substring(colon + 1) : n;   // "mixamorig:LeftArm" -> "LeftArm"
                if (!map.ContainsKey(key)) map[key] = ts[i];
            }
            return map;
        }

        /// Make s.Model limp where it stands; velocity = how it's moving at the moment of death.
        public static void Enter(ModelState s, Vector3 velocity, int owner = -1)
        {
            if (s.Corpse != null || s.Model == null) return;
            var model = s.Model;
            try
            {
                model.transform.SetParent(null, true);   // stays where it fell, whatever the player root does
                s.Corpse = model;
                s.RagdollParts.Clear();

                var bones = Bones(model);
                var anim = model.GetComponent<Animator>();
                if (anim != null) anim.enabled = false;   // physics drives the bones from here

                var rig = new Rig { Model = model, Owner = owner, StartY = model.transform.position.y };
                var bodies = new Dictionary<string, Rigidbody>();
                var colliders = new List<Collider>();
                const float totalMass = 60f;
                foreach (var p in Parts)
                {
                    if (!bones.TryGetValue(p.bone, out var b)) continue;
                    b.gameObject.layer = 2; // "Ignore Raycast": bullets and the spectate camera go through bodies
                    var rb = b.gameObject.AddComponent<Rigidbody>();
                    if (rb == null) continue;
                    s.RagdollParts.Add(rb);
                    rb.mass = totalMass * p.mass;
                    rb.linearDamping = 0.2f;
                    rb.angularDamping = p.bone == "Hips" || p.bone == "Spine2" ? 4f : 2.5f; // trunk rolls less
                    rb.maxAngularVelocity = 8f;
                    rb.interpolation = RigidbodyInterpolation.Interpolate;
                    rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
                    rb.linearVelocity = velocity;
                    bodies[p.bone] = rb;
                    rig.Bodies.Add(rb);

                    // collider from this bone towards the next one, in the bone's own space (so the model scale applies)
                    bones.TryGetValue(p.toward, out var to);
                    Vector3 local = to != null ? b.InverseTransformPoint(to.position) : new Vector3(0, 0.1f, 0);
                    float len = Mathf.Max(local.magnitude, 0.01f);
                    bool trunk = p.bone == "Hips" || p.bone == "Spine2";
                    if (p.bone == "Head")
                    {
                        var sc = b.gameObject.AddComponent<SphereCollider>();
                        if (sc != null) { s.RagdollParts.Add(sc); colliders.Add(sc); sc.radius = len * 0.55f; sc.center = local * 0.5f; }
                    }
                    else
                    {
                        var cap = b.gameObject.AddComponent<CapsuleCollider>();
                        if (cap != null)
                        {
                            s.RagdollParts.Add(cap); colliders.Add(cap);
                            var abs = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
                            cap.direction = abs.x >= abs.y && abs.x >= abs.z ? 0 : abs.y >= abs.z ? 1 : 2;
                            cap.center = local * 0.5f;
                            cap.radius = len * (trunk ? 0.55f : 0.22f);
                            cap.height = len + cap.radius;
                        }
                    }
                }

                // parts of the same body don't collide with each other (they overlap at every joint)
                for (int i = 0; i < colliders.Count; i++)
                    for (int j = i + 1; j < colliders.Count; j++)
                        try { Physics.IgnoreCollision(colliders[i], colliders[j], true); } catch { }

                foreach (var p in Parts)
                {
                    if (p.parent == null || !bodies.TryGetValue(p.bone, out var c) || !bodies.TryGetValue(p.parent, out var par)) continue;
                    var pt = par.transform; var ct = c.transform;
                    Vector3 axis = bones.TryGetValue(p.toward, out var tw) ? ct.InverseTransformPoint(tw.position) : Vector3.up;
                    rig.Links.Add(new Link
                    {
                        Child = c, Parent = par, Bend = p.bend, Twist = p.twist,
                        Axis = axis.sqrMagnitude > 1e-10f ? axis.normalized : Vector3.up,
                        Anchor = Quaternion.Inverse(pt.rotation) * (ct.position - pt.position),
                        Rest = Quaternion.Inverse(pt.rotation) * ct.rotation,
                    });
                }
                if (bodies.TryGetValue("Hips", out var hips)) hips.AddTorque(UnityEngine.Random.onUnitSphere * 1.5f, ForceMode.VelocityChange);
                Active.Add(rig);

                if (!_logged) { _logged = true; ModelsPlugin.L.LogInfo($"Ragdoll: {rig.Bodies.Count} bodies, {rig.Links.Count} links (bones found: {bones.Count})"); }
                if (rig.Bodies.Count < 3) Fallback(model); // couldn't find the skeleton: at least lie down
            }
            catch (Exception e)
            {
                if (_err++ < 3) ModelsPlugin.L.LogWarning("ragdoll: " + e);
                Fallback(model);
            }
        }

        /// Every physics step (SelfView.FixedUpdate): hold every ragdoll together, and let vacuums pull them.
        public static void Step()
        {
            if (Active.Count == 0) return;
            CollectVacuums();
            for (int r = Active.Count - 1; r >= 0; r--)
            {
                var rig = Active[r];
                if (rig.Model == null) { Active.RemoveAt(r); continue; }
                try
                {
                    // the host simulates every body and sends it; joiners follow the host once its poses arrive
                    if (rig.Owner >= 0 && !CorpseSync.HostSimulates && CorpseSync.Poses.TryGetValue(rig.Owner, out var pose)
                        && pose.Pos.Length == rig.Bodies.Count)
                    {
                        Follow(rig, pose);
                        continue;
                    }
                    if (rig.Puppet) continue; // was following and the poses stopped: stay put
                    if (rig.Frozen) continue;
                    if (rig.Bodies.Count > 0 && rig.Bodies[0] != null && rig.Bodies[0].position.y < rig.StartY - 10000f)
                    {
                        // fell down the tunnel: stop it there instead of falling forever
                        foreach (var b in rig.Bodies) if (b != null) { b.linearVelocity = Vector3.zero; b.isKinematic = true; }
                        rig.Frozen = true;
                        if (rig.Owner >= 0 && MpManager.Net.IsHost) Send(rig);
                        continue;
                    }
                    Solve(rig); Suck(rig);
                    if (rig.Owner >= 0 && MpManager.Net.IsHost && Time.time >= rig.NextSend) { rig.NextSend = Time.time + 1f / 15f; Send(rig); }
                }
                catch (Exception e) { if (_err++ < 3) ModelsPlugin.L.LogWarning("ragdoll step: " + e.Message); Active.RemoveAt(r); }
            }
        }

        // Steers with velocities (no teleporting, which pushed parts into the floor and made them jitter):
        // pulls each limb's end back onto its socket, takes out the stretching motion, and only snaps it back if
        // something really tore it away. A body at rest is left alone so it can sleep.
        private static void Solve(Rig rig)
        {
            float dt = Mathf.Max(Time.fixedDeltaTime, 0.001f);
            bool moving = false;
            foreach (var b in rig.Bodies) if (b != null && !b.IsSleeping() && (b.linearVelocity.sqrMagnitude > 0.0025f || b.angularVelocity.sqrMagnitude > 0.01f)) { moving = true; break; }

            for (int iter = 0; iter < 3; iter++)
            {
                foreach (var l in rig.Links)
                {
                    var c = l.Child; var p = l.Parent;
                    if (c == null || p == null) continue;
                    var pRot = p.rotation;
                    var socket = p.position + pRot * l.Anchor;
                    var err = socket - c.position;
                    float e2 = err.sqrMagnitude;
                    if (!moving && e2 < 0.0004f) continue; // resting and attached: leave it asleep
                    float wc = p.mass / (p.mass + c.mass), wp = 1f - wc;

                    if (e2 > 0.3f * 0.3f) { c.position += err; continue; } // torn off: put it back

                    // relative motion of the child's socket point away from the parent's
                    var socketVel = p.linearVelocity + Vector3.Cross(p.angularVelocity, socket - p.position);
                    var rel = c.linearVelocity - socketVel;
                    var fix = err * (iter == 0 ? 0.35f / dt : 0f) - rel * 0.5f;
                    c.linearVelocity += fix * wc;
                    p.linearVelocity -= fix * wp;

                    if (iter == 0 && l.Bend > 0f) Limit(l, p, c, pRot);
                }
            }
        }

        /// Hard stops, like a real joint: split the limb's rotation (relative to how it started) into bending and
        /// twisting around the bone, clamp each to its maximum, and if it hit a stop, put it there and take away
        /// its extra spin so it can't bounce back and twist again. Rotating about its own joint point doesn't move
        /// the joint, so this doesn't push anything into the floor.
        private static void Limit(Link l, Rigidbody p, Rigidbody c, Quaternion pRot)
        {
            // joint friction: a limb's spin drifts towards its parent's (stops endless wobbling)
            c.angularVelocity = Vector3.Lerp(c.angularVelocity, p.angularVelocity, 0.12f);

            var delta = Quaternion.Inverse(l.Rest) * (Quaternion.Inverse(pRot) * c.rotation); // change since the start, limb space
            if (delta.w < 0f) delta = new Quaternion(-delta.x, -delta.y, -delta.z, -delta.w);

            // twist = the part of the rotation around the bone itself; swing = the rest (bending)
            var a = l.Axis;
            var proj = Vector3.Dot(new Vector3(delta.x, delta.y, delta.z), a) * a;
            var twist = new Quaternion(proj.x, proj.y, proj.z, delta.w);
            float tn = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
            twist = tn > 1e-6f ? new Quaternion(twist.x / tn, twist.y / tn, twist.z / tn, twist.w / tn) : Quaternion.identity;
            var swing = delta * Quaternion.Inverse(twist);

            bool clamped = false;
            float twistAng = 2f * Mathf.Acos(Mathf.Clamp(twist.w, -1f, 1f)) * Mathf.Rad2Deg;
            if (twistAng > 180f) twistAng -= 360f;
            if (Mathf.Abs(twistAng) > l.Twist)
            {
                float sign = Vector3.Dot(new Vector3(twist.x, twist.y, twist.z), a) >= 0f ? 1f : -1f;
                twist = Quaternion.AngleAxis(sign * l.Twist, a);
                clamped = true;
            }
            float swingAng = Quaternion.Angle(Quaternion.identity, swing);
            if (swingAng > l.Bend)
            {
                swing = Quaternion.Slerp(Quaternion.identity, swing, l.Bend / swingAng);
                clamped = true;
            }
            if (!clamped) return;

            c.rotation = pRot * l.Rest * (swing * twist);
            c.angularVelocity = p.angularVelocity; // hit the stop: no bouncing back
        }

        private static void Send(Rig rig)
        {
            int n = rig.Bodies.Count;
            var pos = new Vector3[n]; var rot = new Quaternion[n];
            for (int i = 0; i < n; i++)
            {
                var b = rig.Bodies[i];
                if (b == null) return;
                pos[i] = b.position; rot[i] = b.rotation;
            }
            CorpseSync.SendPose(rig.Owner, pos, rot);
        }

        /// Joiner: no own physics, glide each bone onto the host's latest pose.
        private static void Follow(Rig rig, CorpseSync.Pose pose)
        {
            if (!rig.Puppet)
            {
                rig.Puppet = true;
                foreach (var b in rig.Bodies) if (b != null) { b.isKinematic = true; b.interpolation = RigidbodyInterpolation.Interpolate; }
            }
            for (int i = 0; i < rig.Bodies.Count; i++)
            {
                var b = rig.Bodies[i];
                if (b == null) continue;
                // far off (first pose / big jump): snap, otherwise smooth towards it
                var p = (b.position - pose.Pos[i]).sqrMagnitude > 4f ? pose.Pos[i] : Vector3.Lerp(b.position, pose.Pos[i], 0.35f);
                b.MovePosition(p);
                b.MoveRotation(Quaternion.Slerp(b.rotation, pose.Rot[i], 0.35f));
            }
        }

        // ---------------------------------------------------------------- vacuums pull bodies

        private struct Nozzle { public Vector3 Pos, Aim; }
        private static readonly List<Nozzle> Nozzles = new List<Nozzle>();
        private const float SuckCone = 35f, SuckAccel = 45f, SuckStop = 1.2f;
        private static float _suckRange = 10f;

        /// Every vacuum that's sucking right now: yours and other players' (each game runs its own copy of the
        /// bodies, so every game applies every vacuum and they stay roughly the same everywhere).
        private static void CollectVacuums()
        {
            Nozzles.Clear();
            try
            {
                var v = Vacuum.Instance;
                if (v != null && v.suckRange > 1f) _suckRange = Mathf.Max(v.suckRange * 1.3f, 8f);
            }
            catch { }
            try
            {
                if (!Spectate.Dead && (GameApi.CurrentWeaponAction & 3) == 1)
                {
                    var cam = Player.Instance?.mainCamera;
                    if (cam != null) Nozzles.Add(new Nozzle { Pos = cam.transform.position + cam.transform.forward * 0.6f, Aim = cam.transform.forward });
                }
            }
            catch { }
            try
            {
                var mp = MpManager.Instance;
                if (mp != null)
                    foreach (var a in mp.Avatars)
                    {
                        if (a?.Root == null || !a.Root.activeSelf || a.Dead || (a.Action & 3) != 1) continue;
                        var aim = Quaternion.Euler(a.Pitch, a.Root.transform.eulerAngles.y, 0f) * Vector3.forward;
                        Nozzles.Add(new Nozzle { Pos = a.Root.transform.position + Vector3.up * 1.6f + aim * 0.6f, Aim = aim });
                    }
            }
            catch { }
        }

        private static void Suck(Rig rig)
        {
            if (Nozzles.Count == 0) return;
            foreach (var n in Nozzles)
            {
                foreach (var b in rig.Bodies)
                {
                    if (b == null) continue;
                    var to = b.position - n.Pos;
                    float dist = to.magnitude;
                    if (dist > _suckRange || dist < 0.01f || Vector3.Angle(n.Aim, to) > SuckCone) continue;
                    b.WakeUp();
                    if (dist < SuckStop) { b.linearVelocity *= 0.85f; continue; } // held at the nozzle
                    float k = Mathf.Lerp(1f, 0.35f, dist / _suckRange);
                    b.AddForce(-to / dist * SuckAccel * k, ForceMode.Acceleration);
                }
            }
        }

        /// No physics: just lay it flat on its back.
        private static void Fallback(GameObject corpse)
        {
            try
            {
                var a = corpse.GetComponent<Animator>(); if (a != null) a.enabled = false;
                corpse.transform.rotation = corpse.transform.rotation * Quaternion.Euler(-90f, 0f, 0f);
                corpse.transform.position += Vector3.up * 0.25f;
            }
            catch { }
        }

        /// Take the physics off and stand the miner back up under its player (root = null: just remove it).
        public static void Exit(ModelState s, Transform root)
        {
            if (s.Corpse == null) return;
            foreach (var r in Active) if (r.Model == s.Corpse && r.Owner >= 0) CorpseSync.Clear(r.Owner);
            Active.RemoveAll(r => r.Model == s.Corpse);
            foreach (var c in s.RagdollParts) { try { if (c != null) Object.DestroyImmediate(c); } catch { } }
            s.RagdollParts.Clear();
            s.Corpse = null;
            var m = s.Model;
            if (m == null) return;
            try
            {
                if (root == null) { Object.Destroy(m); s.Model = null; return; }
                m.transform.SetParent(root, false);
                var a = m.GetComponent<Animator>();
                if (a != null) { a.enabled = true; try { a.Rebind(); } catch { } }
                AvatarPatches.ApplyFit(s);
            }
            catch (Exception e) { if (_err++ < 3) ModelsPlugin.L.LogWarning("ragdoll exit: " + e.Message); }
        }
    }

    /// Your own body when you die (you're spectating, so you can see it lying there too).
    internal static class LocalCorpse
    {
        private static ModelState _s;
        private static GameObject _root;
        private static float _ragdollAt;
        private static bool _wasDead;

        public static void Tick()
        {
            bool dead = Spectate.Dead;
            if (dead && !_wasDead) Spawn();
            if (!dead && _wasDead) Remove();
            _wasDead = dead;

            // let the animator pose it for a moment (and the death knockback start), then go limp
            if (_s != null && _s.Corpse == null && _ragdollAt > 0f && Time.time >= _ragdollAt)
            {
                _ragdollAt = 0f;
                Vector3 v = Spectate.DeathVelocity;
                try { var rb = Player.Instance?.rb; if (rb != null && rb.linearVelocity.sqrMagnitude > v.sqrMagnitude) v = rb.linearVelocity; } catch { }
                Ragdoll.Enter(_s, v, MpManager.Net.LocalId);
            }
        }

        private static void Spawn()
        {
            try
            {
                if (!ModelsPlugin.Enabled.Value || !GameApi.TryGetLocalPose(out var pos, out var yaw, out _)) return;
                _root = new GameObject("MP_MyBody");
                _root.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
                var model = Bundle.Spawn("PlayerModel", _root.transform);
                if (model == null) { Object.Destroy(_root); _root = null; return; }
                var m = AvatarPatches.MeasureModel(_root, model);
                _s = new ModelState { Model = model, RawHeight = m.height, RawFeet = m.feet };
                AvatarPatches.ApplyFit(_s);
                AvatarPatches.FixShaders(_root, model);
                PlayerTint.Track(model, PlayerTint.LocalColor);
                var anim = model.GetComponent<Animator>();
                if (anim != null) { anim.applyRootMotion = false; anim.cullingMode = AnimatorCullingMode.AlwaysAnimate; try { anim.Rebind(); } catch { } }
                _s.Anim = anim;
                _ragdollAt = Time.time + 0.12f;
            }
            catch (Exception e) { ModelsPlugin.L.LogWarning("own body: " + e.Message); }
        }

        private static void Remove()
        {
            if (_s != null) Ragdoll.Exit(_s, null);
            if (_s?.Model != null) Object.Destroy(_s.Model);
            if (_root != null) Object.Destroy(_root);
            _s = null; _root = null; _ragdollAt = 0f;
        }
    }
}
