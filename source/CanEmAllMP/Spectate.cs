using System;
using System.Collections.Generic;
using LiteNetLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CanEmAllMP
{
    /// Co-op death. The game's death (cutelings, grinder, tube, rockets) is GameManager.Suicide, which starts
    /// SuicideCoroutine and ends the day. While a teammate is still alive we stop it and you spectate instead:
    /// hidden, no input, can't be hurt, camera on a living teammate. When nobody is left the death goes through
    /// and the day ends for everyone. You're back when the day ends (teleported into the elevator).
    ///
    /// Suicide can be compiled straight into the game's own callers (no hook runs), so deaths are caught three ways:
    /// the Suicide prefix, the first step of SuicideCoroutine (always a real call), and IsSuiciding turning on.
    public static class Spectate
    {
        public static bool Dead { get; private set; }
        public static bool AllowRealDeath;   // set while letting the real (day-ending) death through
        public static Vector3 DeathVelocity; // for the body: knockback at the moment of death

        private static float _delay = 1.5f;
        private static int _mail = (int)MailId.YoureNotFood;
        private static int _deadDay = -1;
        private static int _watching = -1;
        private static Transform _cam;
        private static Vector3 _camLocalPos;
        private static Quaternion _camLocalRot;
        private static float _yaw, _pitch = 15f, _dist = 4f;
        private static readonly List<Renderer> Hidden = new List<Renderer>();
        private static readonly List<CanvasRenderer> HiddenUi = new List<CanvasRenderer>();
        private static float _beatAt;
        private const float MaxFall = 10000f;  // a dead body down the tunnel stops after this many metres
        private static float _deathY;
        private static bool _frozen;
        private static float _rehideAt;
        private static bool _announced;      // this real death was already sent to the others
        private static bool _wasSuiciding;
        private static int _lastElevatorState = -1;

        private static NetSession Net => MpManager.Net;
        private static IEnumerable<RemoteAvatar> Others => MpManager.Instance != null ? MpManager.Instance.Avatars : Array.Empty<RemoteAvatar>();

        public static bool LocalSuiciding
        {
            get { try { var gm = GameManager.Instance; return gm != null && gm.IsSuiciding; } catch { return false; } }
        }

        public static bool AnyTeammateAlive()
        {
            foreach (var a in Others) if (!a.Dead && a.Connected) return true;
            return false;
        }

        /// For the elevator wait: dead players don't have to walk in or out.
        public static bool IsPlayerDead(int id)
        {
            if (id == Net.LocalId) return Dead;
            var a = MpManager.Instance?.AvatarById(id);
            return a != null && a.Dead;
        }

        private static bool ShouldBlock() => !AllowRealDeath && !Patches.SuicideFromNet && CoopSync.Active && AnyTeammateAlive();

        /// Prefix of GameManager.Suicide. false = swallow it and spectate instead.
        public static bool BeforeDeath(float delay, MailId mailId)
        {
            if (!ShouldBlock()) return true;
            _delay = delay; _mail = (int)mailId;
            Plugin.Log.LogInfo($"[MP] death ({mailId}) while a teammate is alive: spectating instead");
            Die();
            return false;
        }

        /// First step of SuicideCoroutine (runs inside StartCoroutine, so even when Suicide itself was inlined).
        /// false = stop the death here.
        public static bool OnDeathSequenceStart(GameManager gm, float delay, MailId mailId)
        {
            if (ShouldBlock())
            {
                _delay = delay; _mail = (int)mailId;
                Plugin.Log.LogInfo($"[MP] death sequence ({mailId}) stopped: a teammate is alive, spectating");
                try { gm.IsSuiciding = false; } catch { }
                Die();
                return false;
            }
            Announce(delay, (int)mailId);
            return true;
        }

        /// Tell everyone a real (day-ending) death started here. Once per death.
        public static void Announce(float delay, int mail)
        {
            if (_announced || Patches.SuicideFromNet || !CoopSync.Active) return;
            _announced = true;
            PlayerStats.RealDeath(Net.LocalId, "announce");
            Plugin.Log.LogInfo($"[MP] real death here ({(MailId)mail}): ending the day for everyone");
            Net.Begin(Msg.Suicide).Put(delay);
            Net.W.Put(mail);
            Net.W.Put(Net.LocalId);
            Net.Send(DeliveryMethod.ReliableOrdered);
        }

        /// A teammate's real death arrived: get out of spectating so the game's own death/day-end runs normally.
        public static void OnNetDeath()
        {
            _announced = true; // don't echo it back
            if (Dead) Revive("the day is ending");
        }

        /// Friendly rocket etc: die right now (spectate, or end the day if you were the last one).
        public static void Kill()
        {
            if (Dead) return;
            var gm = GameManager.Instance;
            if (gm == null || gm.IsSuiciding) return;
            try { gm.Suicide(1.5f, MailId.YoureNotFood); } catch (Exception e) { Plugin.Log.LogWarning("kill failed: " + e.Message); }
        }

        private static void Die()
        {
            if (Dead) return;
            Dead = true;
            Gestures.Emote = 0;
            var p = Player.Instance;
            try { DeathVelocity = p?.rb != null ? p.rb.linearVelocity : Vector3.zero; } catch { DeathVelocity = Vector3.zero; }
            try { _deathY = p != null ? p.transform.position.y : 0f; } catch { _deathY = 0f; }
            _frozen = false;
            try { p?.ResetDamage(); } catch { }
            // (not PlayerInput.DeactivateInput: the game's CanPlayerInput reads it and the day loop stops the clock,
            //  which froze a dead host's whole day. Controls are blocked by the Harmony prefixes instead.)
            try { _deadDay = SaveManager.Instance.CurrentSaveData.day; } catch { _deadDay = -1; }
            HideWeapon();
            _logHides = true; HideHud(); _logHides = false;
            var cam = p?.mainCamera;
            if (cam != null) { _cam = cam.transform; _camLocalPos = _cam.localPosition; _camLocalRot = _cam.localRotation; }
            _watching = -1;
            NextTarget();
            MpManager.Instance?.Feed("You died! Spectating until the day ends");
            Plugin.Log.LogInfo("[MP] died: spectating");
        }

        public static void Revive(string why)
        {
            if (!Dead) return;
            Dead = false;
            var p = Player.Instance;
            if (_frozen) { try { if (p?.rb != null) p.rb.isKinematic = false; } catch { } _frozen = false; }
            try { p?.ResetDamage(); } catch { }
            if (_cam != null) { _cam.localPosition = _camLocalPos; _cam.localRotation = _camLocalRot; }
            _cam = null;
            foreach (var r in Hidden) { try { if (r != null) r.enabled = true; } catch { } }
            Hidden.Clear();
            foreach (var cr in HiddenUi) { try { if (cr != null) cr.cull = false; } catch { } }
            HiddenUi.Clear();
            Plugin.Log.LogInfo("[MP] revived: " + why);
        }

        // ------------------------------------------------------------------ hiding first person + HUD

        /// Everything first-person: the held weapon, the arms/hands and anything else hanging off the camera.
        private static void HideWeapon()
        {
            try
            {
                var p = Player.Instance;
                HideUnder(GameApi.WeaponAt(GameApi.CurrentWeaponGlobalIndex)?.transform);
                HideUnder(p?.mainCamera?.transform);
                HideUnder(p?.playerHand);
                HideUnder(p?.mainCameraParent);
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

        /// The combat HUD (weapon slots, ammo, switch hint, stamina, grab prompts, crosshair). Money and the clock stay.
        private static void HideHud()
        {
            var p = Player.Instance;
            try { HideUi(TopPanel(p?.staminaSlider?.transform)); } catch { }
            try { HideUi(p?.grabDropUI); HideUi(p?.throwUI); HideUi(p?.rotateUI); } catch { }
            try
            {
                var inv = Inventory.Instance;
                HideUi(inv?.weaponsPanel); HideUi(inv?.vacuumPanel);
                HideUi(TopPanel(inv?.switchIcon?.transform));
            }
            catch { }
            try { HideUi(TopPanel(BulletCounter.Instance?.transform)); } catch { }
            try { HideCrosshair(); } catch { }
        }

        /// Hides a HUD element by not drawing it (CanvasRenderer.cull), never by deactivating it: some HUD objects
        /// carry game logic (deactivating them froze a dead host's day clock and truck).
        private static void HideUi(GameObject g)
        {
            if (g == null || HoldsKeptHud(g.transform)) return;
            var crs = g.GetComponentsInChildren<CanvasRenderer>(true);
            int n = 0;
            for (int i = 0; i < crs.Length; i++)
            {
                var cr = crs[i];
                if (cr == null || cr.cull || IsClock(cr.transform)) continue;
                cr.cull = true;
                HiddenUi.Add(cr);
                n++;
            }
            if (n > 0 && _logHides) Plugin.Log.LogInfo($"[MP] spectating: hid {Path(g.transform)} ({n} parts)");
        }

        private static bool _logHides;

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 6; i++, t = t.parent) { try { parts.Insert(0, t.name); } catch { parts.Insert(0, "?"); } }
            return string.Join("/", parts);
        }

        /// The crosshair has no field in the game: it's the small element anchored at the centre of the HUD canvas.
        private static void HideCrosshair()
        {
            var gm = GameManager.Instance;
            var root = gm?.timerParent != null ? gm.timerParent.transform.root : null;
            if (root == null) return;
            var rts = root.GetComponentsInChildren<RectTransform>(false);
            for (int i = 0; i < rts.Length; i++)
            {
                var rt = rts[i];
                if (rt == null) continue;
                var c = new Vector2(0.5f, 0.5f);
                if ((rt.anchorMin - c).sqrMagnitude > 0.0001f || (rt.anchorMax - c).sqrMagnitude > 0.0001f) continue;
                if (rt.anchoredPosition.sqrMagnitude > 30f * 30f) continue;
                var size = rt.sizeDelta;
                if (size.x < 2f || size.y < 2f || size.x > 120f || size.y > 120f) continue;
                // its parent must cover the screen (so it really sits in the middle of the view)
                var pr = rt.parent as Transform;
                var prt = pr != null ? pr.GetComponent<RectTransform>() : null;
                if (prt != null && prt.parent != null && !(prt.anchorMin == Vector2.zero && prt.anchorMax == Vector2.one) && prt.GetComponent<Canvas>() == null) continue;
                HideUi(rt.gameObject);
            }
        }

        /// The element's own panel: its ancestor that sits directly under a Canvas (not the whole HUD canvas).
        private static GameObject TopPanel(Transform t)
        {
            if (t == null) return null;
            var cur = t;
            while (cur.parent != null && cur.parent.GetComponent<Canvas>() == null) cur = cur.parent;
            if (cur.parent == null) return t.gameObject; // no canvas found: just the element itself
            // never take the money or the clock with it: if that panel holds them, hide only the element's own group
            if (HoldsKeptHud(cur)) return t.parent != null && !HoldsKeptHud(t.parent) ? t.parent.gameObject : t.gameObject;
            return cur.gameObject;
        }

        /// Part of the day clock (frame, time digits, day count, sun/moon): always stays visible.
        private static bool IsClock(Transform t)
        {
            try
            {
                var gm = GameManager.Instance;
                if (gm == null || t == null) return false;
                if (gm.timerParent != null && t.IsChildOf(gm.timerParent.transform)) return true;
                if (gm.timerText != null && t.IsChildOf(gm.timerText.transform)) return true;
                if (gm.dayCountText != null && t.IsChildOf(gm.dayCountText.transform)) return true;
                if (gm.sunTimer != null && t.IsChildOf(gm.sunTimer.transform)) return true;
                if (gm.nightTimer != null && t.IsChildOf(gm.nightTimer.transform)) return true;
            }
            catch { }
            return false;
        }

        private static bool HoldsKeptHud(Transform panel)
        {
            try
            {
                var gm = GameManager.Instance;
                if (gm?.timerParent != null && gm.timerParent.transform.IsChildOf(panel)) return true;
                if (gm?.timerText != null && gm.timerText.transform.IsChildOf(panel)) return true;
                if (gm?.dayCountText != null && gm.dayCountText.transform.IsChildOf(panel)) return true;
                if (panel.GetComponentInChildren<MoneyTextValue>(true) != null) return true;
            }
            catch { }
            return false;
        }

        // ------------------------------------------------------------------ spectating

        internal static RemoteAvatar Target()
        {
            foreach (var a in Others) if (a.Id == _watching && !a.Dead && a.Connected) return a;
            return null;
        }

        private static void NextTarget()
        {
            var list = new List<RemoteAvatar>();
            foreach (var a in Others) if (!a.Dead && a.Connected) list.Add(a);
            if (list.Count == 0) { _watching = -1; return; }
            list.Sort((x, y) => x.Id.CompareTo(y.Id));
            int i = list.FindIndex(a => a.Id == _watching);
            var next = list[(i + 1) % list.Count];
            _watching = next.Id;
            _yaw = next.Root != null ? next.Root.transform.eulerAngles.y : 0f;
        }

        /// Every frame from MpManager.Update (alive or dead).
        public static void Tick()
        {
            var gm = GameManager.Instance;
            bool suiciding = LocalSuiciding;

            // backstop: a real death started without either hook seeing it
            if (suiciding && !_wasSuiciding && !_announced && Net.Connected) Announce(_delay, _mail);
            if (!suiciding) _announced = false;
            _wasSuiciding = suiciding;

            int elev = -1;
            try { if (gm != null) elev = (int)gm.elevatorState; } catch { }
            int prevElev = _lastElevatorState;
            _lastElevatorState = elev;

            if (!Dead) return;
            if (!CoopSync.Active || !GameApi.InGame) { Revive("left the session"); return; }
            if (suiciding) { Revive("the day is ending"); return; }
            try { int day = SaveManager.Instance.CurrentSaveData.day; if (_deadDay >= 0 && day != _deadDay) { Revive("new day"); return; } } catch { }

            // end of the day: everyone else walked into the elevator -> back to life, inside it
            bool insideDone = false;
            try { insideDone = elev == CoopSync.Inside && gm.triggerCompleted; } catch { }
            if (insideDone || (prevElev == CoopSync.Inside && elev != CoopSync.Inside && elev != -1))
            {
                Revive("end of the day");
                try
                {
                    var p = Player.Instance; var start = gm?.elevatorStartPoint;
                    if (p != null && start != null && Vector3.Distance(p.transform.position, start.position) > 2.5f) p.Teleport(start);
                }
                catch (Exception e) { Plugin.Log.LogWarning("teleport into the elevator failed: " + e.Message); }
                return;
            }

            if (!AnyTeammateAlive())
            {
                // everyone's down: now the day really ends (and is sent to everyone)
                Plugin.Log.LogInfo("[MP] nobody left alive: ending the day");
                Revive("everyone died");
                AllowRealDeath = true;
                try { gm?.Suicide(_delay, (MailId)_mail); } catch (Exception e) { Plugin.Log.LogWarning("final death failed: " + e.Message); }
                finally { AllowRealDeath = false; }
                return;
            }

            if (Time.unscaledTime >= _rehideAt) { _rehideAt = Time.unscaledTime + 0.25f; HideWeapon(); HideHud(); } // the game turns things back on
            // fell down the tunnel: stop the (hidden) body once it's far enough down, instead of falling forever
            if (!_frozen)
            {
                try
                {
                    var pl = Player.Instance;
                    if (pl?.rb != null && pl.transform.position.y < _deathY - MaxFall)
                    {
                        pl.rb.linearVelocity = Vector3.zero;
                        pl.rb.isKinematic = true;
                        _frozen = true;
                        Plugin.Log.LogInfo("[MP] dead body stopped falling");
                    }
                }
                catch { }
            }
            if (Time.unscaledTime >= _beatAt)
            {
                _beatAt = Time.unscaledTime + 3f;
                try
                {
                    string line = gm == null ? "no GameManager" :
                        string.Format("timeScale={0} paused={1} timer={2:0.0} elevator={3} triggerDone={4} gmActive={5} canInput={6}",
                            Time.timeScale, gm.IsPaused, gm.Timer, elev, gm.triggerCompleted, gm.isActiveAndEnabled, gm.CanPlayerInput);
                    Plugin.Log.LogInfo("[MP] dead: " + line);
                }
                catch { }
            }
            if (Target() == null) NextTarget();
            var m = Mouse.current; var kb = Keyboard.current;
            if ((m != null && m.leftButton.wasPressedThisFrame) || (kb != null && kb[Key.Space].wasPressedThisFrame)) NextTarget();
            if (m != null)
            {
                var d = m.delta.ReadValue();
                _yaw += d.x * 0.15f;
                _pitch = Mathf.Clamp(_pitch - d.y * 0.15f, -10f, 70f);
                float sc = m.scroll.ReadValue().y;
                if (Mathf.Abs(sc) > 0.01f) _dist = Mathf.Clamp(_dist - Mathf.Sign(sc) * 0.4f, 2f, 9f);
            }
        }

        /// MpManager.LateUpdate: put the camera behind the teammate being watched.
        public static void LateTick()
        {
            if (!Dead || _cam == null || LocalSuiciding) return;
            var t = Target();
            if (t?.Root == null) return;
            var focus = t.Root.transform.position + Vector3.up * 1.4f;
            var dir = Quaternion.Euler(_pitch, _yaw, 0f) * Vector3.back;
            float dist = _dist;
            int mask = ~(1 << 2);
            try { mask &= ~(1 << Player.Instance.gameObject.layer); } catch { }
            // (ragdoll bodies are on "Ignore Raycast", so they don't pull the camera in)
            if (Physics.SphereCast(focus, 0.25f, dir, out RaycastHit hit, _dist, mask, QueryTriggerInteraction.Ignore)
                && !(MpManager.Instance?.IsAvatarRoot(hit.collider.transform.root) ?? false))
                dist = Mathf.Max(0.5f, hit.distance - 0.1f);
            _cam.position = focus + dir * dist;
            _cam.rotation = Quaternion.LookRotation(focus - _cam.position, Vector3.up);
        }

        public static void DrawHud()
        {
            if (!Dead || SpectateUi.Working) return; // the game-style banner is up (this is the fallback)
            var t = Target();
            string who = t != null ? Net.NameOf(t.Id) : Loc.T("nobody");
            GUI.Box(new Rect(Screen.width / 2f - 240, 40, 480, 52), "");
            GUI.Label(new Rect(Screen.width / 2f - 230, 44, 460, 22), Loc.F("You died. Spectating {0}. Back when the day ends.", who));
            GUI.Label(new Rect(Screen.width / 2f - 230, 66, 460, 22), Loc.T("Click / Space: next player    Mouse: look    Scroll: zoom"));
        }
    }
}
