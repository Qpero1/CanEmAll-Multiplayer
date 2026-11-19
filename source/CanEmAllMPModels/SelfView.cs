#nullable disable
// While you dance (Q wheel) or point (T): spawns your own miner where you stand, plays the move on it, puts your
// first-person weapon away and swings the camera out into a third-person orbit (move the mouse to look around it,
// scroll to zoom). Moving / attacking ends the emote as before, and the camera snaps back to first person.

using System;
using System.Collections.Generic;
using CanEmAllMP;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace CanEmAllMPModels
{
    public class SelfView : MonoBehaviour
    {
        public SelfView(IntPtr ptr) : base(ptr) { }

        public static bool Active;

        private static GameObject _root;
        private static ModelState _self;
        private static int _shownEmote = -1;
        private static bool _pointing;
        private static Transform _cam;
        private static Vector3 _camLocalPos;
        private static Quaternion _camLocalRot;
        private static float _orbitYaw, _orbitPitch = 12f, _dist = 3.4f, _curDist = 3.4f, _yaw;
        private static int _mask = -1;
        private static readonly List<Renderer> Hidden = new List<Renderer>();
        private static int _err;

        private static bool Wanted =>
            ModelsPlugin.ThirdPersonEmotes.Value && GameApi.GameAllowsInput && !Spectate.Dead && (Gestures.Emote > 0 || Gestures.Pointing);

        public void Update()
        {
            try { PlayerTint.Tick(); } catch (Exception e) { if (_err++ < 3) ModelsPlugin.L.LogWarning("tint: " + e.Message); }
            try { LocalCorpse.Tick(); } catch (Exception e) { if (_err++ < 3) ModelsPlugin.L.LogWarning("own body: " + e.Message); }
            try
            {
                if (Wanted && !Active) Begin();
                else if (!Wanted && Active) End();
                if (Active) Animate();
            }
            catch (Exception e)
            {
                if (_err++ < 3) ModelsPlugin.L.LogWarning("third-person emote: " + e);
                if (Active) { try { End(); } catch { } }
            }
        }

        public void FixedUpdate()
        {
            Ragdoll.Step(); // holds ragdoll bodies together (the game has no Unity joints)
        }

        public void LateUpdate()
        {
            if (!Active) return;
            try { PlaceCamera(); }
            catch (Exception e) { if (_err++ < 3) ModelsPlugin.L.LogWarning("third-person camera: " + e.Message); }
        }

        private static void Begin()
        {
            if (!GameApi.TryGetLocalPose(out var pos, out var yaw, out var pitch)) return;
            var camObj = Camera.main;
            if (camObj == null) return;

            _root = new GameObject("MP_SelfView");
            _root.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
            var model = Bundle.Spawn("PlayerModel", _root.transform);
            if (model == null) { Object.Destroy(_root); _root = null; return; }
            var m = AvatarPatches.MeasureModel(_root, model);
            _self = new ModelState { Model = model, RawHeight = m.height, RawFeet = m.feet };
            AvatarPatches.ApplyFit(_self);
            AvatarPatches.FixShaders(_root, model);
            PlayerTint.Track(model, PlayerTint.LocalColor);
            var anim = model.GetComponent<Animator>();
            if (anim != null)
            {
                anim.applyRootMotion = false;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                try { anim.Rebind(); } catch { }
            }
            _self.Anim = anim;
            _shownEmote = -1;
            _pointing = false;

            Hidden.Clear();
            HideFirstPersonWeapon();

            _cam = camObj.transform;
            _camLocalPos = _cam.localPosition;
            _camLocalRot = _cam.localRotation;
            _orbitYaw = yaw + 180f;   // start in front, facing you
            _orbitPitch = 12f;
            _yaw = yaw;
            _curDist = _dist;
            // facing comes from the camera, which is about to swing round: freeze it (for us and for other players)
            GameApi.FrozenYaw = yaw; GameApi.FrozenPitch = pitch; GameApi.FreezeFacing = true;
            Active = true;
        }

        private static void End()
        {
            Active = false;
            GameApi.FreezeFacing = false;
            if (_cam != null)
            {
                _cam.localPosition = _camLocalPos;
                _cam.localRotation = _camLocalRot;
            }
            _cam = null;
            foreach (var r in Hidden) { try { if (r != null) r.enabled = true; } catch { } }
            Hidden.Clear();
            if (_root != null) Object.Destroy(_root);
            _root = null;
            _self = null;
        }

        /// Everything first-person: the held weapon and (since the game's update, separate from it) the hands and
        /// anything else hanging off the camera. Re-run while emoting: the game can switch them back on.
        private static void HideFirstPersonWeapon()
        {
            try
            {
                int idx = GameApi.CurrentWeaponGlobalIndex;
                if (idx >= 0) HideUnder(GameApi.WeaponAt(idx)?.transform);
                var p = Player.Instance;
                HideUnder(p?.playerHand);
                HideUnder(p?.mainCameraParent);
                HideUnder(Camera.main?.transform);
            }
            catch { }
        }

        private static void HideUnder(Transform t)
        {
            if (t == null) return;
            var rs = t.GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < rs.Length; i++)
                if (rs[i] != null && rs[i].enabled) { rs[i].enabled = false; if (!Hidden.Contains(rs[i])) Hidden.Add(rs[i]); }
        }

        private static float _nextHide;

        private static void Animate()
        {
            if (_root == null || _self == null) { End(); return; }
            if (Time.unscaledTime >= _nextHide) { _nextHide = Time.unscaledTime + 0.25f; HideFirstPersonWeapon(); }
            if (GameApi.TryGetLocalPose(out var pos, out _, out _))
                _root.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, _yaw, 0));

            var a = _self.Anim;
            if (a == null) return;
            Moves.SetGunPose(_self, ""); // weapon put away: plain stance
            a.SetFloat(AnimHash.Speed, 0f);

            int emote = Gestures.Emote;
            if (emote != _shownEmote)
            {
                _shownEmote = emote;
                if (emote > 0)
                {
                    int hash = AnimHash.Dance(emote);
                    if (a.HasState(0, hash)) a.CrossFade(hash, 0.2f, 0, 0f);
                }
                else a.CrossFade(AnimHash.Locomotion, 0.2f, 0);
            }
            bool pointing = Gestures.Pointing;
            if (pointing && !_pointing) Moves.Play(_self, AnimHash.Point);
            _pointing = pointing;

            // orbit with the mouse, zoom with the wheel
            var mouse = Mouse.current;
            if (mouse != null && !EmoteWheel.Open && !TuneMenu.Open)
            {
                var d = mouse.delta.ReadValue();
                _orbitYaw += d.x * 0.15f;
                _orbitPitch = Mathf.Clamp(_orbitPitch - d.y * 0.15f, -20f, 70f);
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f) _dist = Mathf.Clamp(_dist - Mathf.Sign(scroll) * 0.3f, 1.5f, 7f);
            }
        }

        private static void PlaceCamera()
        {
            if (_cam == null || _root == null) return;
            var target = _root.transform.position + Vector3.up * (ModelsPlugin.Height.Value * 0.55f);
            var rot = Quaternion.Euler(_orbitPitch, _orbitYaw, 0f);
            var dir = rot * Vector3.back;

            // don't go through walls: pull in to whatever is between the miner and the camera
            float want = _dist;
            if (Physics.SphereCast(target, 0.25f, dir, out RaycastHit hit, _dist, Mask(), QueryTriggerInteraction.Ignore))
                want = Mathf.Max(0.4f, hit.distance - 0.1f);
            // snap in instantly, ease back out
            _curDist = want < _curDist ? want : Mathf.Lerp(_curDist, want, 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime));
            _cam.position = target + dir * _curDist;
            _cam.rotation = Quaternion.LookRotation(target - _cam.position, Vector3.up);
        }

        /// Everything solid except the player's own body, enemies-in-the-vacuum and "Ignore Raycast".
        private static int Mask()
        {
            if (_mask != -1) return _mask;
            int m = ~(1 << 2);
            try { var p = Player.Instance; if (p != null) m &= ~(1 << p.gameObject.layer); } catch { }
            _mask = m;
            return m;
        }

        public void OnGUI()
        {
            if (!Active) return;
            try
            {
                GUI.Box(new Rect(Screen.width / 2f - 170, Screen.height - 84, 340, 26), "");
                GUI.Label(new Rect(Screen.width / 2f - 165, Screen.height - 82, 330, 24),
                    "Mouse: look around   Scroll: zoom   Move: stop");
            }
            catch { }
        }
    }
}
