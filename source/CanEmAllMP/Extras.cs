using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CanEmAllMP
{
    /// <summary>Global announcements (gambling), friendly fire, and other players' cursors on the upgrade screen.</summary>
    public static class Extras
    {
        private static NetSession Net => MpManager.Net;

        // ================================================================ announcements

        public static void Announce(string text)
        {
            if (!CoopSync.Active) return;
            MpManager.Instance?.Feed(text);
            MpManager.Instance?.Toast(text);
            Net.Begin(Msg.Announce).Put(text);
            Net.Send(DeliveryMethod.ReliableOrdered);
        }

        public static void ReceiveAnnounce(int from, NetDataReader r)
        {
            var text = r.GetString(200);
            if (Net.IsHost) { Net.Begin(Msg.Announce).Put(text); Net.SendExcept(from, DeliveryMethod.ReliableOrdered); }
            MpManager.Instance?.Feed(text);
            MpManager.Instance?.Toast(text);
        }

        // ================================================================ gambling

        private static bool _wasSpinning;
        private static long _lastBet;

        /// Postfix of SlotMachine.Spin: a spin actually started (the bet was taken).
        public static void OnSpinStarted(SlotMachine sm)
        {
            if (sm == null) return;
            _lastBet = sm.currentBet;
            _wasSpinning = true;
            Announce($"{Net.NameOf(Net.LocalId)} spent ${_lastBet:N0} on gambling");
        }

        public static void TickGambling()
        {
            if (!_wasSpinning) return;
            var sm = SlotMachine.Instance;
            if (sm == null) { _wasSpinning = false; return; }
            if (sm.IsSpinning) return;
            _wasSpinning = false;
            if (sm.LastWin)
            {
                long won = (long)Math.Round(_lastBet * (double)sm.LastMultiplier);
                Announce($"{Net.NameOf(Net.LocalId)} won ${won:N0} gambling (x{sm.LastMultiplier:0.#})");
            }
            else Announce($"{Net.NameOf(Net.LocalId)} lost ${_lastBet:N0} gambling");
        }

        // ================================================================ friendly fire

        public static bool FriendlyFire;           // host decides; clients receive it
        private const int FriendlyHitsPerDamage = 2; // 2x health against other players
        private static int _friendlyHits;
        private const float KnockbackSpeed = 7f, KnockbackUp = 2.5f;
        private static float _lastFriendlyHitAt;
        private static float _lastHitSent;

        public static void SetFriendlyFire(bool on, bool broadcast)
        {
            FriendlyFire = on;
            MpManager.Instance?.SetAvatarColliders(on);
            if (broadcast && Net.IsHost)
            {
                Net.Begin(Msg.FriendlyFire).Put(on);
                Net.Send(DeliveryMethod.ReliableOrdered);
            }
        }

        /// Postfix of GameManager.OnBulletImpact: every gun (and turret) reports where its bullet hit.
        public static void OnBulletImpact(Vector3 point)
        {
            return; // replaced by ShooterCheck (aim-based, works for every weapon); kept for reference
#pragma warning disable CS0162
            if (!FriendlyFire || !CoopSync.Active || MpManager.Instance == null) return;
            if (Time.unscaledTime - _lastHitSent < 0.12f) return;
            int target = MpManager.Instance.AvatarAt(point);
            if (target < 0) return;
            _lastHitSent = Time.unscaledTime;
            if (Net.IsHost) DeliverHit(target, Net.LocalId);
            else { Net.Begin(Msg.PlayerHit).Put(target); Net.Send(DeliveryMethod.ReliableOrdered); }
        }

#pragma warning restore CS0162

        // aim-based friendly fire: checked on the shooter's side when they attack
        private static readonly System.Collections.Generic.Dictionary<string, (float range, float radius)> HitShapes =
            new System.Collections.Generic.Dictionary<string, (float, float)>(StringComparer.OrdinalIgnoreCase)
            {
                { "Pistol", (90f, 0.45f) }, { "SMG", (80f, 0.45f) }, { "AK47", (90f, 0.45f) }, { "Shotgun", (35f, 0.7f) },
                { "RocketLauncher", (90f, 0.6f) }, { "FlameThrower", (7f, 0.9f) }, { "Chainsaw", (2.6f, 0.6f) },
                { "BaseballBat", (2.8f, 0.6f) }, { "Knife", (2.4f, 0.5f) }, { "", (2.2f, 0.5f) }, // "" = fists
            };
        private const float BodyHeight = 2.3f;

        /// Debug target that isn't a player (the models add-on's F9 tuning dummy): its feet, or null when there's none.
        /// Hitting it only plays the hit sound + crosshair locally; nothing is sent.
        public static Func<Vector3?> TestTarget;
        private static float _lastTestHit;

        private static void TestTargetCheck(string weaponType)
        {
            var feet = TestTarget?.Invoke();
            bool melee = weaponType == "BaseballBat" || weaponType == "Knife" || weaponType == "";
            if (feet == null || Time.unscaledTime - _lastTestHit < (melee ? 0.1f : 0.3f)) return;
            if (!HitShapes.TryGetValue(weaponType ?? "", out var shape)) return;
            var cam = Player.Instance?.mainCamera;
            if (cam == null) return;
            var o = cam.transform.position; var dir = cam.transform.forward;
            var f = feet.Value;
            float t = RayToSegment(o, dir, f + Vector3.up * 0.2f, f + Vector3.up * BodyHeight, out float dist);
            if (t <= 0f || t > shape.range || dist > shape.radius) return;
            _lastTestHit = Time.unscaledTime;
            Plugin.Log.LogInfo($"[test] hit the tuning dummy with {(weaponType == "" ? "fists" : weaponType)} at {t:0.0} m");
            HitSound.Play(); HitMarker.Show();
        }

        /// Called when the local player attacks (and repeatedly while holding fire on automatic weapons).
        public static void ShooterCheck(string weaponType)
        {
            try { TestTargetCheck(weaponType); } catch { }
            if (!FriendlyFire || !CoopSync.Active || MpManager.Instance == null) return;
            if (!HitShapes.TryGetValue(weaponType ?? "", out var shape)) return; // vacuum etc.
            bool melee = weaponType == "BaseballBat" || weaponType == "Knife" || weaponType == "";
            if (Time.unscaledTime - _lastHitSent < (melee ? 0.1f : 0.3f)) return;
            var p = Player.Instance;
            var cam = p?.mainCamera;
            if (cam == null) return;
            var o = cam.transform.position; var dir = cam.transform.forward;

            int best = -1; float bestT = float.MaxValue;
            foreach (var a in MpManager.Instance.Avatars)
            {
                if (a.Root == null || !a.Root.activeSelf || a.Dead) continue;
                var feet = a.Root.transform.position;
                float t = RayToSegment(o, dir, feet + Vector3.up * 0.2f, feet + Vector3.up * BodyHeight, out float dist);
                if (t <= 0f || t > shape.range || dist > shape.radius || t >= bestT) continue;
                best = a.Id; bestT = t;
            }
            bool rocket = string.Equals(weaponType, "RocketLauncher", StringComparison.OrdinalIgnoreCase);
            if (rocket) { RocketSplash(p, o, dir, best, bestT); return; }
            if (best < 0) return;

            // a wall in between stops it (other players' own colliders don't count as walls)
            int mask = ~0;
            try { mask = ~(1 << p.gameObject.layer); } catch { }
            var hits = Physics.RaycastAll(o, dir, bestT - 0.3f, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                var root = hits[i].collider != null ? hits[i].collider.transform.root : null;
                if (root == null || root == p.transform.root) continue;
                if (MpManager.Instance.IsAvatarRoot(root)) continue;
                return; // blocked
            }

            _lastHitSent = Time.unscaledTime;
            Plugin.Log.LogInfo($"Friendly fire: hit player {best} with {(weaponType == "" ? "fists" : weaponType)} at {bestT:0.0} m");
            MpManager.Instance.Note($"Hit {Net.NameOf(best)}");
            HitSound.Play(); HitMarker.Show();
            if (Net.IsHost) DeliverHit(best, Net.LocalId);
            else { Net.Begin(Msg.PlayerHit).Put(best); Net.Send(DeliveryMethod.ReliableOrdered); }
        }

        private const float RocketRadius = 3.5f;

        /// The rocket explodes at the first wall or teammate along your aim; every teammate in the blast dies.
        private static void RocketSplash(Player p, Vector3 o, Vector3 dir, int directId, float directT)
        {
            float impactT = 120f;
            int mask = ~0;
            try { mask = ~(1 << p.gameObject.layer); } catch { }
            var hits = Physics.RaycastAll(o, dir, impactT, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                var root = hits[i].collider != null ? hits[i].collider.transform.root : null;
                if (root == null || root == p.transform.root || MpManager.Instance.IsAvatarRoot(root)) continue;
                if (hits[i].distance < impactT) impactT = hits[i].distance;
            }
            if (directId >= 0 && directT < impactT) impactT = directT;
            var impact = o + dir * impactT;

            var killed = new System.Collections.Generic.List<int>();
            foreach (var a in MpManager.Instance.Avatars)
            {
                if (a.Root == null || !a.Root.activeSelf || a.Dead) continue;
                var feet = a.Root.transform.position;
                var closest = new Vector3(feet.x, Mathf.Clamp(impact.y, feet.y + 0.2f, feet.y + BodyHeight), feet.z);
                if (Vector3.Distance(impact, closest) <= RocketRadius) killed.Add(a.Id);
            }
            if (killed.Count == 0) return;
            _lastHitSent = Time.unscaledTime;
            foreach (int id in killed)
            {
                Plugin.Log.LogInfo($"Friendly fire: rocket killed player {id}");
                MpManager.Instance.Note($"Rocketed {Net.NameOf(id)}");
                HitSound.Play(); HitMarker.Show();
                if (Net.IsHost) DeliverHit(id, Net.LocalId, true);
                else { Net.Begin(Msg.PlayerHit).Put(id); Net.W.Put((byte)1); Net.Send(DeliveryMethod.ReliableOrdered); }
            }
        }

        /// Distance along the ray to the point closest to segment a-b, and how far apart they are there.
        private static float RayToSegment(Vector3 o, Vector3 d, Vector3 a, Vector3 b, out float dist)
        {
            var u = b - a; var w = o - a;
            float A = Vector3.Dot(d, d), B = Vector3.Dot(d, u), C = Vector3.Dot(u, u), D = Vector3.Dot(d, w), E = Vector3.Dot(u, w);
            float den = A * C - B * B;
            float s = den > 1e-6f ? Mathf.Clamp01((A * E - B * D) / den) : 0f;   // along the segment
            var q = a + u * s;
            float t = Vector3.Dot(q - o, d) / A;                                  // along the ray
            dist = Vector3.Distance(o + d * t, q);
            return t;
        }

        public static void ReceiveHit(int from, NetDataReader r)
        {
            int target = r.GetInt();
            if (Net.IsHost)
            {
                bool kill = r.AvailableBytes >= 1 && r.GetByte() != 0;
                DeliverHit(target, from, kill);
            }
            else
            {
                int shooter = r.GetInt();
                bool kill = r.AvailableBytes >= 1 && r.GetByte() != 0;
                ApplyHit(shooter, kill);
            }
        }

        private static void DeliverHit(int target, int shooter, bool kill = false)
        {
            if (!FriendlyFire) return;
            if (target == Net.LocalId) { ApplyHit(shooter, kill); return; }
            Net.Begin(Msg.PlayerHit).Put(target); Net.W.Put(shooter); Net.W.Put((byte)(kill ? 1 : 0));
            Net.SendTo(target, DeliveryMethod.ReliableOrdered);
        }

        private static void ApplyHit(int shooter, bool kill = false)
        {
            if (Spectate.Dead) return;
            if (kill)
            {
                var pl = Player.Instance;
                if (pl == null || !GameApi.InGame || !FriendlyFire) return;
                try
                {
                    var sa = MpManager.Instance?.AvatarById(shooter);
                    var away = sa?.Root != null ? pl.transform.position - sa.Root.transform.position : -pl.transform.forward;
                    away.y = 0f;
                    VacuumPull.Knockback(away.normalized * 14f + Vector3.up * 6f); // blown up
                }
                catch { }
                MpManager.Instance?.Note($"Rocketed by {Net.NameOf(shooter)}");
                Spectate.Kill();
                return;
            }
            var p = Player.Instance;
            if (p == null || !GameApi.InGame || !FriendlyFire) return;
            // every hit knocks you back, away from whoever shot you
            try
            {
                var shooterAv = MpManager.Instance?.AvatarById(shooter);
                if (shooterAv?.Root != null)
                {
                    var away = p.transform.position - shooterAv.Root.transform.position;
                    away.y = 0f;
                    if (away.sqrMagnitude < 0.01f) away = -p.transform.forward;
                    VacuumPull.Knockback(away.normalized * KnockbackSpeed + Vector3.up * KnockbackUp);
                }
            }
            catch { }
            // players have 2x health against each other: only every 2nd friendly hit does a real hit of damage
            if (Time.unscaledTime - _lastFriendlyHitAt > 30f) _friendlyHits = 0; // a break lets it wear off
            _lastFriendlyHitAt = Time.unscaledTime;
            _friendlyHits++;
            if (_friendlyHits >= FriendlyHitsPerDamage)
            {
                _friendlyHits = 0;
                try { p.Damage(); } catch (Exception e) { Plugin.Log.LogWarning("Friendly fire damage failed: " + e.Message); }
                MpManager.Instance?.Note($"Shot by {Net.NameOf(shooter)}: took damage");
            }
            else MpManager.Instance?.Note($"Shot by {Net.NameOf(shooter)} ({_friendlyHits}/{FriendlyHitsPerDamage})");
        }

        // ================================================================ cursors + info boxes on the upgrade screen

        private class RemoteCursor { public Vector2 Local; public float Time; public int Hover = -1; public Vector2 Screen; public bool OnScreen; public int Page = PageTree; }

        // Shared screens with cursors + info boxes: the skill tree and the permanent upgrade (sell factory) screen.
        public const int PageNone = 0, PageTree = 1, PagePerm = 2, PageMail = 3, PageGamble = 4, PageSell = 5;
        private static int _localHoverPage = PageTree;
        private static int _lastSentPage;

        private static RectTransform PermPanel
        {
            get
            {
                try
                {
                    var p = PermanentUpgradeManager.Instance?.permanentUpgradePanel;
                    if (p == null || !p.gameObject.activeInHierarchy || p.alpha < 0.5f) return null;
                    return p.transform.TryCast<RectTransform>();
                }
                catch { return null; }
            }
        }

        /// The end-of-day screen holds four tabs (Tree / Mail / Gambling / Sell) as sibling panels: which one is showing?
        private static Transform TabPanel(int page)
        {
            try
            {
                var v = View;
                var top = v != null ? v.transform.parent : null;
                if (top == null) return null;
                string name = page == PageTree ? null : page == PageMail ? "Mail" : page == PageGamble ? "Gambling" : page == PageSell ? "Sell" : null;
                if (page == PageTree) return v.transform;
                for (int i = 0; name != null && i < top.childCount; i++) { var c = top.GetChild(i); if (c.name == name) return c; }
            }
            catch { }
            return null;
        }

        private static bool TabShowing(int page)
        {
            var t = TabPanel(page);
            try { return t != null && t.gameObject.activeInHierarchy; } catch { return false; }
        }

        /// The whole end-of-day screen (the skill tree's parent panel). The game's own "tree open" flag only covers the
        /// SKILL TREE tab: switching to Mail / Gambling / Sell turns that panel off.
        private static bool DayScreenOpen
        {
            get
            {
                try
                {
                    var top = View?.transform.parent;
                    return top != null && top.gameObject.activeInHierarchy;
                }
                catch { return false; }
            }
        }

        public static int LocalPage
        {
            get
            {
                if (TreeOpen || DayScreenOpen)
                {
                    // the other tabs are drawn over the tree, so check them first
                    if (TabShowing(PageMail)) return PageMail;
                    if (TabShowing(PageGamble)) return PageGamble;
                    if (TabShowing(PageSell)) return PageSell;
                    return PageTree;
                }
                return PermPanel != null ? PagePerm : PageNone;
            }
        }

        private static bool IsTab(int page) => page == PageTree || page == PageMail || page == PageGamble || page == PageSell;

        /// The rect cursors are expressed in for a page (resolution independent), and the camera to map it.
        private static RectTransform PageSpace(int page, out Camera cam)
        {
            cam = null;
            if (page == PageTree) { var v = View; if (v == null) return null; cam = UiCamera(v); return v.content; }
            if (page == PagePerm) { var rt = PermPanel; if (rt == null) return null; cam = CanvasCamera(rt); return rt; }
            if (page == PageMail || page == PageGamble || page == PageSell)
            {
                var t = TabPanel(page);
                var rt = t != null ? t.TryCast<RectTransform>() : null;
                if (rt == null || !rt.gameObject.activeInHierarchy) return null;
                cam = CanvasCamera(rt);
                return rt;
            }
            return null;
        }

        private static Camera CanvasCamera(Component c)
        {
            try
            {
                var canvas = c.GetComponentInParent<Canvas>();
                if (canvas == null) return null;
                canvas = canvas.rootCanvas;
                return canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            }
            catch { return null; }
        }
        private static readonly Dictionary<int, RemoteCursor> _cursors = new Dictionary<int, RemoteCursor>();
        private static float _cursorTimer;
        private static Vector2 _lastSentLocal = new Vector2(float.NaN, float.NaN);
        private static int _lastSentHover = -1;
        private static int _localHover = -1;

        private static SkillTreeView View => GameManager.Instance?.skillTreeView;

        public static bool TreeOpen
        {
            get
            {
                try
                {
                    var gm = GameManager.Instance;
                    var panel = gm?.skillTreePanel?.panel;
                    return panel != null && panel.activeInHierarchy && gm.skillTreeView != null;
                }
                catch { return false; }
            }
        }

        private static Camera UiCamera(SkillTreeView v)
        {
            try
            {
                var canvas = v.canvas;
                if (canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
                return v.GetUICamera();
            }
            catch { return null; }
        }

        /// From OnGUI (which knows the mouse position): send our cursor in skill-tree coordinates + what we hover.
        public static void SendCursor(Vector2 guiMouse)
        {
            if (!CoopSync.Active) return;
            _cursorTimer += Time.unscaledDeltaTime;
            if (_cursorTimer < 1f / 15f) return;
            _cursorTimer = 0;
            int page = LocalPage;
            bool open = page != PageNone;
            Vector2 local = new Vector2(float.NaN, float.NaN);
            if (open)
            {
                var space = PageSpace(page, out var cam);
                var screen = new Vector2(guiMouse.x, Screen.height - guiMouse.y);
                if (space == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(space, screen, cam, out local)) open = false;
            }
            int hover = open && _localHoverPage == page ? _localHover : -1;
            if (!open)
            {
                if (float.IsNaN(_lastSentLocal.x)) return;      // already told everyone we're not on the screen
                local = new Vector2(float.NaN, float.NaN);
                page = PageNone;
            }
            else if ((local - _lastSentLocal).sqrMagnitude < 0.25f && hover == _lastSentHover && page == _lastSentPage) return;
            _lastSentLocal = local; _lastSentHover = hover; _lastSentPage = page;
            Net.Begin(Msg.Cursor).Put(Net.LocalId);
            Net.W.Put(local.x); Net.W.Put(local.y); Net.W.Put(hover); Net.W.Put((byte)page);
            Net.Send(DeliveryMethod.Sequenced);
        }

        public static void ReceiveCursor(int from, NetDataReader r)
        {
            int id = r.GetInt(); float x = r.GetFloat(), y = r.GetFloat();
            int hover = r.AvailableBytes >= 4 ? r.GetInt() : -1;
            int page = r.AvailableBytes >= 1 ? r.GetByte() : PageTree;
            if (Net.IsHost)
            {
                id = from;
                Net.Begin(Msg.Cursor).Put(id); Net.W.Put(x); Net.W.Put(y); Net.W.Put(hover); Net.W.Put((byte)page);
                Net.SendExcept(from, DeliveryMethod.Sequenced);
            }
            if (float.IsNaN(x)) { _cursors.Remove(id); return; }
            if (!_cursors.TryGetValue(id, out var c)) _cursors[id] = c = new RemoteCursor();
            c.Local = new Vector2(x, y); c.Time = Time.unscaledTime; c.Hover = hover; c.Page = page;
        }

        /// Every frame: where each remote cursor is on our screen, and keep their info boxes in step.
        public static void TickCursors()
        {
            int page = LocalPage;
            var space = PageSpace(page, out var cam);
            bool open = space != null;
            foreach (var c in _cursors.Values)
            {
                if (!open || c.Page != page) { c.OnScreen = false; continue; }   // only players on the same screen
                Vector3 world = space.TransformPoint(new Vector3(c.Local.x, c.Local.y, 0));
                c.Screen = RectTransformUtility.WorldToScreenPoint(cam, world);
                c.OnScreen = Time.unscaledTime - c.Time < 10f;
            }
            TickPopups(open);
        }

        // the tab buttons in the end-of-day screen's header, by page
        private static RectTransform TabButton(int page)
        {
            try
            {
                var top = View?.transform.parent;
                var header = top != null ? FindChild(top, "Header") : null;
                if (header == null) return null;
                string n = page == PageTree ? "SkillTreeButton" : page == PageMail ? "MailButton" : page == PageGamble ? "GamblingButton" : page == PageSell ? "SellFactoryButton" : null;
                var b = n != null ? FindChild(header, n) : null;
                return b != null && b.gameObject.activeInHierarchy ? b.TryCast<RectTransform>() : null;
            }
            catch { return null; }
        }

        private static Transform FindChild(Transform t, string name)
        {
            for (int i = 0; i < t.childCount; i++) { var c = t.GetChild(i); if (c.name == name) return c; }
            return null;
        }

        /// Which tab of the end-of-day screen everyone else is on: a little cursor in their colour next to that tab's name
        /// (always, also for the tab you're on).
        private static void DrawTabCursors(int myPage)
        {
            var perTab = new Dictionary<int, int>();
            // must be an IL2CPP array: a managed Vector3[] is copied on the way in and never filled
            var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            foreach (var kv in _cursors)
            {
                var c = kv.Value;
                if (!IsTab(c.Page)) continue; // kept until they leave the screen (an idle mouse sends nothing)
                var b = TabButton(c.Page);
                if (b == null) continue;
                perTab.TryGetValue(c.Page, out var n);
                perTab[c.Page] = n + 1;
                b.GetWorldCorners(corners); // 0 bottom-left, 2 top-right
                var cam = CanvasCamera(b);
                var tr = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
                var bl = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                float h = Mathf.Abs(tr.y - bl.y);
                float size = Mathf.Clamp(h * 0.6f, 16f, 30f);
                // above the button, lined up from its left edge (one per player)
                float left = Mathf.Min(bl.x, tr.x), top = Mathf.Max(bl.y, tr.y);
                float gx = left + 6f + n * size * 0.9f;
                float gy = Screen.height - top - size - 2f;
                Palette.DrawCursor(Palette.IndexOfPlayer(kv.Key), gx, gy, size);
            }
        }

        public static void DrawCursors(GUIStyle labelStyle)
        {
            if (_cursors.Count == 0 || LocalPage == PageNone) return;
            int myPage = LocalPage;
            if (IsTab(myPage)) { try { DrawTabCursors(myPage); } catch { } }
            foreach (var kv in _cursors)
            {
                var c = kv.Value;
                if (!c.OnScreen) continue;
                float gx = c.Screen.x, gy = Screen.height - c.Screen.y;
                var col = RemoteAvatar.ColorFor(kv.Key);
                float w = Palette.DrawCursor(Palette.IndexOfPlayer(kv.Key), gx, gy);
                float lx = gx + w * 0.75f, ly = gy + 26;
                GUI.color = Color.black; GUI.Label(new Rect(lx + 1, ly + 1, 200, 22), Net.NameOf(kv.Key), labelStyle);
                GUI.color = col; GUI.Label(new Rect(lx, ly, 200, 22), Net.NameOf(kv.Key), labelStyle);
            }
            GUI.color = Color.white;
        }

        // ---- info boxes: one copy of the game's skill pop-up per remote player, following their cursor
        private class RemotePopup { public SkillPopUp Popup; public int Shown = -1; }
        private static readonly Dictionary<int, RemotePopup> _popups = new Dictionary<int, RemotePopup>();
        private static readonly Dictionary<IntPtr, int> _popupOwner = new Dictionary<IntPtr, int>();
        private static SkillPopUp _original;

        private static bool IsClone(SkillPopUp p) => p != null && _popupOwner.ContainsKey(p.Pointer);

        public static void LocalPopupShown(SkillPopUp p, SkillNode node)
        {
            if (IsClone(p)) return;
            _original = p;
            _localHover = node?.skill != null ? (int)node.skill.skillId : -1;
            _localHoverPage = PageTree;
        }

        /// The local player hovers a permanent upgrade (SkillPopUp.Show(PermanentUpgrade)).
        public static void LocalPermPopupShown(SkillPopUp p, PermanentUpgrade up)
        {
            if (IsClone(p)) return;
            _original = p;
            _localHover = up?.skill != null ? (int)up.skill.skillId : -1;
            _localHoverPage = PagePerm;
        }

        public static void LocalPopupHidden(SkillPopUp p)
        {
            if (IsClone(p)) return;
            _localHover = -1;
        }

        /// The game only ever talks to SkillPopUp.Instance; if that ever points at one of our copies, put the real one back.
        private static void GuardInstance()
        {
            try
            {
                var inst = SkillPopUp.Instance;
                if (inst != null && IsClone(inst) && _original != null) SkillPopUp._Instance_k__BackingField = _original;
                if (_original == null && inst != null && !IsClone(inst)) _original = inst;
            }
            catch { }
        }

        /// Our hover = the real info box is showing (polled: more reliable than catching every Hide).
        private static void PollLocalHover()
        {
            try
            {
                if (_original == null) { _localHover = -1; return; }
                if (!_original.visible || !_original.gameObject.activeInHierarchy) _localHover = -1;
            }
            catch { _localHover = -1; }
        }

        public static bool RemotePopupPointer(SkillPopUp p, out Vector2 pos)
        {
            pos = default;
            if (p == null || !_popupOwner.TryGetValue(p.Pointer, out var id)) return false;
            if (_cursors.TryGetValue(id, out var c)) pos = c.Screen;
            return true;
        }

        private static SkillPopUp MakeClone(int playerId)
        {
            var orig = _original != null ? _original : SkillPopUp.Instance;
            if (orig == null) return null;
            // build it under an inactive holder so we can register it before its Awake runs
            var holder = new GameObject("MP_PopupHolder");
            holder.SetActive(false);
            var go = UnityEngine.Object.Instantiate(orig.gameObject, holder.transform, false);
            go.name = "MP_SkillPopUp_" + playerId;
            var clone = go.GetComponent<SkillPopUp>();
            if (clone == null) { UnityEngine.Object.Destroy(holder); return null; }
            _popupOwner[clone.Pointer] = playerId;
            go.transform.SetParent(orig.transform.parent, false);
            go.transform.SetSiblingIndex(orig.transform.GetSiblingIndex());   // under the local player's own box
            UnityEngine.Object.Destroy(holder);
            if (!go.activeSelf) go.SetActive(true);   // lets its Awake run once
            // its Awake made itself the singleton: put the real one back
            try { SkillPopUp._Instance_k__BackingField = orig; } catch { }
            if (_original == null) _original = orig;
            go.SetActive(false);
            return clone;
        }

        private static readonly HashSet<int> _shownThisTick = new HashSet<int>();
        private static readonly List<int> _cursorOrder = new List<int>();

        private static void TickPopups(bool treeOpen)
        {
            GuardInstance();
            PollLocalHover();
            if (!treeOpen) _localHover = -1;
            if (_popups.Count == 0 && _cursors.Count == 0) return;
            _shownThisTick.Clear();
            int page = LocalPage;
            if (_localHover >= 0 && _localHoverPage == page) _shownThisTick.Add(_localHover); // our own box shows that one
            _cursorOrder.Clear(); _cursorOrder.AddRange(_cursors.Keys); _cursorOrder.Sort();
            foreach (var cid in _cursorOrder)
            {
                var cur = _cursors[cid];
                int want = treeOpen && cur.OnScreen ? cur.Hover : -1;
                if (want >= 0 && !_shownThisTick.Add(want)) want = -1;    // someone's box already shows it
                var kv = new KeyValuePair<int, RemoteCursor>(cid, cur);
                if (!_popups.TryGetValue(kv.Key, out var rp))
                {
                    if (want < 0) continue;
                    var clone = MakeClone(kv.Key);
                    if (clone == null) continue;
                    _popups[kv.Key] = rp = new RemotePopup { Popup = clone };
                }
                if (rp.Popup == null) { _popups.Remove(kv.Key); break; }
                int key = want >= 0 ? want * 4 + page : -1;   // same id on another screen is a different box
                if (rp.Shown == key) continue;
                rp.Shown = key;
                try
                {
                    if (want < 0) rp.Popup.gameObject.SetActive(false);
                    else if (page == PagePerm)
                    {
                        var up = PermanentSync.Find(want);
                        if (up == null) rp.Popup.gameObject.SetActive(false);
                        else { rp.Popup.gameObject.SetActive(true); rp.Popup.Show(up); }
                    }
                    else
                    {
                        var node = SkillSync.FindNode(want);
                        if (node == null) rp.Popup.gameObject.SetActive(false);
                        else { rp.Popup.gameObject.SetActive(true); rp.Popup.Show(node); }
                    }
                    GuardInstance();
                }
                catch (Exception e) { Plugin.Log.LogWarning("[MP] remote info box: " + e.Message); }
            }
            // players whose cursor went away
            foreach (var kv in _popups)
                if ((!_cursors.ContainsKey(kv.Key) || !treeOpen) && kv.Value.Shown != -1 && kv.Value.Popup != null)
                { kv.Value.Shown = -1; try { kv.Value.Popup.gameObject.SetActive(false); } catch { } }
        }

        private static void DestroyPopup(int id)
        {
            if (!_popups.TryGetValue(id, out var rp)) return;
            _popups.Remove(id);
            if (rp.Popup != null) { _popupOwner.Remove(rp.Popup.Pointer); try { UnityEngine.Object.Destroy(rp.Popup.gameObject); } catch { } }
        }

        // ================================================================ money pop-ups for money others earned
        public static void SendMoneyPopup(int amount, MoneyManager.CoinTextType type)
        {
            if (amount == 0) return;
            Net.Begin(Msg.MoneyPopup).Put(amount); Net.W.Put((byte)type);
            Net.Send(DeliveryMethod.ReliableUnordered);
        }

        public static void ReceiveMoneyPopup(int from, NetDataReader r)
        {
            int amount = r.GetInt(); byte type = r.GetByte();
            if (Net.IsHost) { Net.Begin(Msg.MoneyPopup).Put(amount); Net.W.Put(type); Net.SendExcept(from, DeliveryMethod.ReliableUnordered); }
            if (!GameApi.InGame) return;
            try { MoneyManager.Instance?.SpawnFloatingText(amount, (MoneyManager.CoinTextType)type); } catch (Exception e) { if (_popupErr++ < 3) Plugin.Log.LogWarning("[MP] money pop-up: " + e.Message); }
        }
        private static int _popupErr;

        // ================================================================ day/night lighting follows the host
        private static int _lastDay = -1;     // host: last state sent (-1 = send again)
        private static int _pendingDay = -1;  // client: state to apply once the lighting exists

        public static void HostTickLighting()
        {
            if (!GameApi.InGame) return;
            var ls = LightingSwitcher.Instance;
            if (ls == null) return;
            int d = ls.IsDay ? 1 : 0;
            if (d == _lastDay) return;
            _lastDay = d;
            Net.Begin(Msg.Lighting).Put(d == 1);
            Net.Send(DeliveryMethod.ReliableOrdered);
        }

        public static void ResendLighting() { _lastDay = -1; }

        public static void ReceiveLighting(NetDataReader r) { _pendingDay = r.GetBool() ? 1 : 0; }

        public static void ClientTickLighting()
        {
            if (_pendingDay < 0 || !GameApi.InGame || !HostSave.GameStartedOrLocal) return;
            var ls = LightingSwitcher.Instance;
            if (ls == null) return;
            bool day = _pendingDay == 1;
            _pendingDay = -1;
            if (ls.IsDay == day) return;
            try { if (day) ls.SetDay(false); else ls.SetNight(false); } catch (Exception e) { Plugin.Log.LogWarning("[MP] lighting: " + e.Message); }
        }

        public static void PlayerLeft(int id) { _cursors.Remove(id); DestroyPopup(id); }
        public static void Reset()
        {
            _cursors.Clear(); _wasSpinning = false; _lastSentLocal = new Vector2(float.NaN, float.NaN); _lastSentHover = -1; _lastSentPage = PageNone;
            foreach (var id in new List<int>(_popups.Keys)) DestroyPopup(id);
            _lastDay = -1; _pendingDay = -1;
        }
    }
}
