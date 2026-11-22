using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static CanEmAllMP.MenuUi;

namespace CanEmAllMP
{
    /// In-game multiplayer screen, opened from a MULTIPLAYER button in the pause menu while in a session.
    /// Built like the main-menu one (a clone of the Settings screen): your colour, friendly fire (host decides),
    /// and the player list with kills / deaths / ping and a KICK button (host).
    public static class PauseUi
    {
        private const int MaxRows = 8;

        private static NetSession Net => MpManager.Net;

        private static GameObject _btn, _screen, _paused;
        private static TMP_Text _header, _ffLabel, _psLabel;
        private static Image[] _psSw; private static GameObject[] _psSel;
        private static Image[] _sw, _ffSw;
        private static GameObject[] _swSel, _ffSel;
        private static readonly Color FfOff = new Color(0.9f, 0.2f, 0.2f), FfOn = new Color(0.35f, 0.8f, 0.2f);

        private class Row
        {
            public GameObject Go, Kick;
            public Image Dot;
            public TMP_Text Name, Stats, KickLabel;
            public int Id = -1;
            public float ConfirmUntil;
        }
        private static readonly List<Row> _rows = new List<Row>();
        private static float _nextRefresh, _nextInject;

        private static bool _pausedWasOpen;

        private static void Relayout(Transform t)
        {
            try { var rt = RT(t); if (rt != null) LayoutRebuilder.ForceRebuildLayoutImmediate(rt); }
            catch (Exception e) { Plugin.Log.LogWarning("[UI] pause layout: " + e.Message); }
        }

        public static bool Open => _screen != null && _screen.activeSelf;

        public static void Tick()
        {
            if (Time.unscaledTime >= _nextInject && (_btn == null || _screen == null))
            {
                try { Inject(); }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("[UI] pause menu MULTIPLAYER failed: " + e);
                    _nextInject = Time.unscaledTime + 30f;
                }
            }

            if (_btn != null)
            {
                bool pausedOpen = _paused != null && _paused.activeInHierarchy;
                if (pausedOpen && !_pausedWasOpen)
                {
                    var t = Label(_btn.transform);
                    if (t != null && t.text != Loc.T("MULTIPLAYER")) Loc.Set(t, "MULTIPLAYER");
                    Relayout(_btn.transform.parent);
                }
                _pausedWasOpen = pausedOpen;
            }

            try { TickCodeBadge(); } catch (Exception e) { if (_badgeErr++ < 3) Plugin.Log.LogWarning("[UI] lobby code badge: " + e.Message); }

            if (!Open) return;
            // resumed (Esc) or left the session: close
            if (!GameApi.IsPaused || !GameApi.InGame) { _screen.SetActive(false); return; }
            if (!Net.Active) { Back(); return; }
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.25f;
            Refresh();
        }

        // ---------------------------------------------------------------- lobby code on the pause screen

        private static GameObject _badge;
        private static TMP_Text _badgeText;
        private static string _badgeShown;
        private static int _badgeErr;

        /// Top-left of the pause screen: the lobby code (in the PAUSED title's style), click to copy.
        private static void TickCodeBadge()
        {
            if (_paused == null) return;
            string code = Net.Online ? (Net.IsHost ? Net.LobbyCode : OnlineLink.NormalizeCode(Plugin.LastCode.Value)) : null;
            if (_badge == null)
            {
                if (code == null) return;
                var tt = Child(_paused.transform, "Title");
                var title = tt != null ? tt.GetComponent<TMP_Text>() : null;
                if (title == null) title = FindText(_paused.transform, "PAUSED");
                if (title == null) { _badgeErr = 99; return; }
                var go = Clone(title.gameObject, _paused.transform);
                if (go == null) return;
                go.name = "MP_LobbyCode";
                Strip(go);
                _badgeText = go.GetComponent<TMP_Text>();
                var rt = RT(go);
                Place(rt, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(45f, -30f), new Vector2(700f, 140f));
                _badgeText.alignment = TextAlignmentOptions.TopLeft;
                _badgeText.enableWordWrapping = false;
                _badgeText.fontSize = 64f;
                _badgeText.raycastTarget = true;
                go.AddComponent<Button>();
                OnClick(go, () =>
                {
                    var c = Net.Online ? (Net.IsHost ? Net.LobbyCode : OnlineLink.NormalizeCode(Plugin.LastCode.Value)) : null;
                    if (c == null) return;
                    try { GUIUtility.systemCopyBuffer = c; } catch { }
                    MpManager.Instance?.Toast("Lobby code copied");
                });
                _badge = go;
                _badgeShown = null;
            }
            bool show = code != null;
            if (_badge.activeSelf != show) _badge.SetActive(show);
            if (show && _badgeShown != code + Loc.Lang)
            {
                _badgeShown = code + Loc.Lang;
                _badgeText.text = "<size=38%>" + Loc.T("LOBBY CODE  -  CLICK TO COPY") + "</size>\n" + code;
            }
        }

        // ---------------------------------------------------------------- building

        private static void Inject()
        {
            Menu m = null;
            try { m = Menu.Instance; } catch { }
            if (m == null || m.menuPanel == null) return;
            var canvas = m.menuPanel.transform.parent;
            var paused = Child(canvas, "Paused");
            var settings = Child(canvas, "Settings");
            if (paused == null || settings == null) return;
            _paused = paused.gameObject;

            if (_btn == null)
            {
                var buttons = Child(paused, "Buttons");
                // find SETTINGS by its gear icon (the labels are only filled in once the menu has been shown)
                Button src = null;
                for (int i = 0; buttons != null && i < buttons.childCount && src == null; i++)
                {
                    var c = buttons.GetChild(i);
                    var gear = SpriteOf(Child(c, "Icon"));
                    if (gear != null && gear.name == "settings") src = c.GetComponent<Button>();
                }
                if (src == null && buttons != null && buttons.childCount >= 3) src = buttons.GetChild(1).GetComponent<Button>();
                if (src == null) { Plugin.Log.LogWarning("[UI] no SETTINGS in the pause menu"); _nextInject = Time.unscaledTime + 5f; return; }
                var b = Clone(src.gameObject, buttons);
                b.name = "MP_PauseMultiplayer";
                b.transform.SetSiblingIndex(src.transform.GetSiblingIndex() + 1); // under SETTINGS, above QUIT
                Strip(b);
                var t = Label(b.transform); if (t != null) { Loc.Set(t, "MULTIPLAYER"); t.enableWordWrapping = false; Loc.Fit(t); }
                var credits = Find(m.menuPanel.transform, "Credits");
                var spr = SpriteOf(Child(credits, "Icon"));
                var ic = Child(b.transform, "Icon"); if (ic != null && spr != null) ic.GetComponent<Image>().sprite = spr;
                OnClick(b, Show);
                b.SetActive(true);
                _btn = b;
                Plugin.Log.LogInfo("[UI] pause menu MULTIPLAYER button added");
            }

            if (_screen == null) Build(canvas, settings);
        }

        private static void Build(Transform canvas, Transform settings)
        {
            var scr = Clone(settings.gameObject, canvas);
            if (scr == null) throw new Exception("clone failed");
            scr.name = "MP_PauseScreen";
            scr.SetActive(false);
            scr.transform.SetSiblingIndex(settings.GetSiblingIndex() + 1);
            _screen = scr;
            var st = scr.transform;
            try
            {
                var title = Label(Child(st, "Title")); if (title != null) { Loc.Set(title, "MULTIPLAYER"); title.enableWordWrapping = false; Loc.Fit(title, 0.5f); var trt = RT(title.transform); if (trt != null) trt.sizeDelta = new Vector2(600f, trt.sizeDelta.y); }
                Kill(Child(st, "Audio")); Kill(Child(st, "Graphics")); Kill(Child(st, "Buttons"));
                var back = Child(st, "Back");
                OnClick(back.gameObject, Back);

                var box = Child(st, "General");
                var tLine = Child(box, "Line");
                var tRow = Child(box, "InvertYAxis") ?? Child(box, "Headbob");
                if (tLine == null || tRow == null) throw new Exception("row templates missing");
                var old = new List<Transform>();
                for (int i = 0; i < box.childCount; i++) old.Add(box.GetChild(i));
                var ddImg = Find(tRow, "Dropdown")?.GetComponent<Image>();
                var outline = Find(tRow, "Outline");

                Line(box, tLine);
                var colRow = NewRow(box, tRow, "Your colour", false);
                Swatches(Child(colRow, "Image"), back, ddImg, outline, Palette.Colors, idx =>
                {
                    if (Net.Active && Net.ColorTaken(idx) && idx != Net.MyColor) return;
                    Plugin.PlayerColor.Value = idx; Net.RequestColor(idx);
                    Refresh();
                }, out _sw, out _swSel);
                Line(box, tLine);
                var ffRow = NewRow(box, tRow, "Friendly fire", false);
                _ffLabel = Label(ffRow);
                Swatches(Child(ffRow, "Image"), back, ddImg, outline, new[] { FfOff, FfOn }, idx =>
                {
                    if (!Net.IsHost) return;
                    bool on = idx == 1;
                    if (on != Plugin.FriendlyFire.Value)
                    {
                        Plugin.FriendlyFire.Value = on;
                        Extras.SetFriendlyFire(on, true);
                        Extras.Announce("Friendly fire " + (on ? "ON" : "OFF"));
                    }
                    Refresh();
                }, out _ffSw, out _ffSel);
                Line(box, tLine);
                var psRow = NewRow(box, tRow, "Multiplayer pause", false);
                _psLabel = Label(psRow);
                Swatches(Child(psRow, "Image"), back, ddImg, outline, new[] { FfOff, FfOn }, idx =>
                {
                    if (!Net.IsHost) return;
                    Plugin.SharedPause.Value = idx == 1;   // MpManager sends it on the next frame
                    Refresh();
                }, out _psSw, out _psSel);
                Line(box, tLine);

                // header: "Players (2/8)" ... "KILLS  DEATHS  PING"
                var head = TextRow(box, tRow, "Players");
                _header = Label(Child(box, "MP_Players"));
                Loc.Unbind(_header);
                SetStatsLayout(head);
                _head = head; _headLang = -1;
                Line(box, tLine);

                _rows.Clear();
                for (int i = 0; i < MaxRows; i++) _rows.Add(PlayerRow(box, tRow, tLine, back, ddImg, i));

                foreach (var o in old) Kill(o);
                Strip(scr);
                Plugin.Log.LogInfo("[UI] pause MULTIPLAYER screen built");
            }
            catch
            {
                try { UnityEngine.Object.Destroy(scr); } catch { }
                _screen = null;
                throw;
            }
        }

        // the stats columns sit left of the KICK button
        private static void SetStatsLayout(TMP_Text t)
        {
            Place(RT(t.transform), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-110f, 0f), new Vector2(320f, 24f));
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.richText = true;
        }

        private static string Cols(string a, string b, string c, string d, string e) => $"{a}<pos=48>{b}<pos=108>{c}<pos=186>{d}<pos=250>{e}";

        private static Row PlayerRow(Transform box, Transform tRow, Transform tLine, Transform back, Image ddImg, int i)
        {
            var r = new Row();
            var row = NewRow(box, tRow, "Player", false);
            row.name = "MP_PlayerRow" + i;
            var rr = RT(row); rr.sizeDelta = new Vector2(rr.sizeDelta.x, 26f);
            r.Go = row.gameObject;

            // colour dot (a cloned separator line, re-skinned)
            var dot = Clone(tLine.gameObject, row);
            dot.name = "Dot";
            Place(RT(dot), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(14f, 14f));
            r.Dot = dot.GetComponent<Image>();
            if (ddImg != null) { r.Dot.sprite = ddImg.sprite; r.Dot.type = Image.Type.Sliced; r.Dot.pixelsPerUnitMultiplier = ddImg.pixelsPerUnitMultiplier * 2f; }
            r.Dot.raycastTarget = false;

            r.Name = Label(row);
            Loc.Unbind(r.Name);
            r.Name.enableAutoSizing = false;
            var nr = RT(r.Name.transform);
            nr.anchoredPosition = new Vector2(nr.anchoredPosition.x + 22f, nr.anchoredPosition.y);
            nr.sizeDelta = new Vector2(190f, nr.sizeDelta.y);
            r.Name.fontSize = 15; r.Name.enableWordWrapping = false; r.Name.overflowMode = TextOverflowModes.Ellipsis;

            var st = Clone(r.Name.gameObject, row).GetComponent<TMP_Text>();
            st.gameObject.name = "Stats";
            SetStatsLayout(st);
            st.fontSize = 14;
            r.Stats = st;

            // KICK (the BACK button, shrunk by sizing: its hover animation resets scale)
            var holder = Child(row, "Image");
            var k = Clone(back.gameObject, holder);
            k.name = "Kick";
            Kill(Child(k.transform, "Icon"));
            const float s = 0.5f;
            var kt = Label(k.transform);
            if (kt != null) { Loc.Set(kt, "KICK"); Stretch(RT(kt.transform)); kt.alignment = TextAlignmentOptions.Center; kt.fontSize = kt.fontSize * 0.65f; kt.enableWordWrapping = false; Loc.Fit(kt, 0.5f); }
            var ki = k.GetComponent<Image>(); if (ki != null) ki.pixelsPerUnitMultiplier = ki.pixelsPerUnitMultiplier / s;
            var rc = RT(Child(k.transform, "Raycast")); if (rc != null) rc.sizeDelta = rc.sizeDelta * s;
            Place(RT(k), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(170f * s, 64f * s));
            r.Kick = k; r.KickLabel = kt;
            OnClick(k, () => OnKick(r));
            return r;
        }

        // ---------------------------------------------------------------- behaviour

        private static void Show()
        {
            if (!Net.Active)
            {
                MpManager.Instance?.Toast("Not in a multiplayer game - host from the main menu: MULTIPLAYER > HOST GAME");
                return;
            }
            if (_screen == null) return;
            if (_paused != null) _paused.SetActive(false);
            _screen.SetActive(true);
            foreach (var r in _rows) r.ConfirmUntil = 0f;
            Refresh();
        }

        private static void Back()
        {
            if (_screen != null) _screen.SetActive(false);
            if (_paused != null && GameApi.IsPaused && GameApi.InGame) _paused.SetActive(true);
        }

        private static void OnKick(Row r)
        {
            if (!Net.IsHost || r.Id < 0 || r.Id == Net.LocalId) return;
            if (Time.unscaledTime > r.ConfirmUntil)
            {
                r.ConfirmUntil = Time.unscaledTime + 3f; // click again to confirm
                if (r.KickLabel != null) Loc.Set(r.KickLabel, "SURE?");
                return;
            }
            string name = Net.NameOf(r.Id);
            Net.Kick(r.Id);
            Extras.Announce(name + " was kicked");
            r.ConfirmUntil = 0f;
        }

        /// Ping to the host: the host measures everyone and sends it round; the host itself has none.
        internal static string PingText(int id)
        {
            if (id == 0) return Loc.T("host");
            if (Net.IsHost) { int p = Net.PingOf(id); return p >= 0 ? p + "ms" + PathTag(Net.PathOf(id)) : "-"; }
            if (id == Net.LocalId) return Net.PingMs + "ms" + PathTag(Net.OnlinePath);
            return PlayerStats.All.TryGetValue(id, out var s) && s.Ping >= 0 ? s.Ping + "ms" : "-";
        }

        /// Shown after the ping when the connection isn't direct, so a high ping explains itself.
        private static string PathTag(string path) =>
            string.IsNullOrEmpty(path) || path == "direct" ? "" : path.Contains("relay") ? " relay" : " " + path;

        private static TMP_Text _head; private static int _headLang = -1;

        private static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        private static void Refresh()
        {
            try
            {
                bool host = Net.IsHost;
                int sel = Net.MyColor;
                if (_sw != null)
                    for (int i = 0; i < _sw.Length; i++)
                    {
                        bool taken = i != sel && Net.ColorTaken(i);
                        var c = Palette.Colors[i]; c.a = taken ? 0.25f : 1f;
                        if (_sw[i] != null) _sw[i].color = c;
                        if (_swSel[i] != null && _swSel[i].activeSelf != (i == sel)) _swSel[i].SetActive(i == sel);
                    }
                if (_ffSw != null)
                {
                    int fsel = (host ? Plugin.FriendlyFire.Value : Extras.FriendlyFire) ? 1 : 0;
                    var cols = new[] { FfOff, FfOn };
                    for (int i = 0; i < _ffSw.Length; i++)
                    {
                        var c = cols[i]; c.a = !host && i != fsel ? 0.25f : 1f;
                        if (_ffSw[i] != null) _ffSw[i].color = c;
                        if (_ffSel[i] != null && _ffSel[i].activeSelf != (i == fsel)) _ffSel[i].SetActive(i == fsel);
                    }
                    if (_ffLabel != null) { var fl = Loc.T(host ? "Friendly fire" : "Friendly fire (host decides)"); if (_ffLabel.text != fl) _ffLabel.text = fl; }
                }
                if (_psSw != null)
                {
                    int psel = (host ? Plugin.SharedPause.Value : CoopSync.SharedPause) ? 1 : 0;
                    var cols = new[] { FfOff, FfOn };
                    for (int i = 0; i < _psSw.Length; i++)
                    {
                        var c = cols[i]; c.a = !host && i != psel ? 0.25f : 1f;
                        if (_psSw[i] != null) _psSw[i].color = c;
                        if (_psSel[i] != null && _psSel[i].activeSelf != (i == psel)) _psSel[i].SetActive(i == psel);
                    }
                    if (_psLabel != null) { var pl = Loc.T(host ? "Multiplayer pause (off = game keeps running)" : "Multiplayer pause (host decides)"); if (_psLabel.text != pl) _psLabel.text = pl; }
                }

                var ids = new List<int>(Net.Players.Keys);
                ids.Sort();
                // the host knows its limit; joiners just see the count
                string cap = host && Net.MaxPlayers > 0 ? "/" + Net.MaxPlayers : "";
                if (_header != null) _header.text = Loc.F("Players ({0})", ids.Count + cap);
                if (_head != null && _headLang != Loc.Lang)
                {
                    _headLang = Loc.Lang;
                    _head.text = Loc.Columns(_head, 12f, new[] { 0f, 48f, 108f, 186f, 250f }, 320f, "KILLS", "DEATHS", "GAMBLED", "WON", "PING");
                }
                for (int i = 0; i < _rows.Count; i++)
                {
                    var r = _rows[i];
                    bool show = i < ids.Count;
                    if (r.Go.activeSelf != show) r.Go.SetActive(show);
                    if (!show) { r.Id = -1; continue; }
                    int id = ids[i];
                    if (r.Id != id) { r.Id = id; r.ConfirmUntil = 0f; }
                    bool me = id == Net.LocalId;
                    r.Dot.color = Palette.ForPlayer(id);
                    string tag = id == 0 ? " " + Loc.T("(host)") : "";
                    r.Name.text = Net.NameOf(id) + tag + (me ? " " + Loc.T("(you)") : "");
                    PlayerStats.All.TryGetValue(id, out var s);
                    // the host knows everyone's ping; a joiner only knows their own (to the host)
                    string pingTxt = PingText(id);
                    r.Stats.text = Cols((s?.Kills ?? 0).ToString(), (s?.Deaths ?? 0).ToString(),
                        PlayerStats.Money(s?.Gambled ?? 0), PlayerStats.Money(s?.Won ?? 0), pingTxt);
                    bool kickable = host && !me;
                    if (r.Kick.activeSelf != kickable) r.Kick.SetActive(kickable);
                    if (kickable && r.KickLabel != null && Time.unscaledTime > r.ConfirmUntil && r.KickLabel.text != Loc.T("KICK")) Loc.Set(r.KickLabel, "KICK");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[UI] pause refresh: " + e.Message); }
        }
    }
}
