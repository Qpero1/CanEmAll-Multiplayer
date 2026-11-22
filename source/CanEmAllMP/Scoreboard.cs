using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static CanEmAllMP.MenuUi;

namespace CanEmAllMP
{
    /// Hold TAB in a multiplayer game: a game-style panel with every player's kills, deaths and ping.
    /// Built from a clone of the Settings screen's content box (everything else stripped, incl. its UIScreen so the
    /// game doesn't treat it as an open menu), parked in the middle of the screen and never clickable.
    public static class Scoreboard
    {
        private const int MaxRows = 8;
        private const float RowH = 28f;
        private static NetSession Net => MpManager.Net;

        private static GameObject _root;
        private static RectTransform _box;
        private static TMP_Text _title;
        private class Row { public GameObject Go, Kick; public Image Dot; public TMP_Text Name, Stats, KickLabel; public int Id = -1; public float ConfirmUntil; }
        private static readonly List<Row> _rows = new List<Row>();
        private static float _nextTry, _nextRefresh;

        /// Tab + Left Alt held: the mouse is free (to click KICK); camera and weapons ignore it meanwhile.
        public static bool CursorFree { get; private set; }
        private static CursorLockMode _prevLock;
        private static bool _prevVisible;

        public static void Tick()
        {
            bool want = false, alt = false;
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                want = kb != null && kb.tabKey.isPressed && Net.Active && GameApi.InGame && !GameApi.IsPaused && !MpManager.MenuOpen;
                alt = want && kb.leftAltKey.isPressed;
            }
            catch { }
            if (alt && !CursorFree)
            {
                CursorFree = true;
                _prevLock = Cursor.lockState; _prevVisible = Cursor.visible;
            }
            if (CursorFree)
            {
                if (alt) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
                else { CursorFree = false; Cursor.lockState = _prevLock; Cursor.visible = _prevVisible; }
            }

            if (want && _root == null && Time.unscaledTime >= _nextTry)
            {
                _nextTry = Time.unscaledTime + 5f;
                try { Build(); }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("[UI] scoreboard: " + e);
                    try { if (_root != null) UnityEngine.Object.Destroy(_root); } catch { }
                    _root = null;
                }
            }
            if (_root == null) return;
            if (_root.activeSelf != want) { _root.SetActive(want); if (want) _nextRefresh = 0f; }
            if (!want || Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.25f;
            Refresh();
        }

        private static void Build()
        {
            Menu m = Menu.Instance;
            if (m == null || m.menuPanel == null) return;
            var canvas = m.menuPanel.transform.parent;
            var settings = Child(canvas, "Settings");
            if (settings == null) return;

            var root = Clone(settings.gameObject, canvas);
            root.name = "MP_Scoreboard";
            root.transform.SetSiblingIndex(settings.GetSiblingIndex() + 1);
            root.SetActive(false);
            _root = root;
            var rt = root.transform;

            // keep only the content box; the screen itself becomes an invisible, click-through holder
            var comps = root.GetComponents(Il2CppInterop.Runtime.Il2CppType.Of<MonoBehaviour>());
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i]?.TryCast<MonoBehaviour>();
                if (c != null && c.GetIl2CppType().Name == "UIScreen") UnityEngine.Object.DestroyImmediate(c);
            }
            var bg = root.GetComponent<Image>(); if (bg != null) bg.enabled = false;
            // clickable only for the host's KICK buttons (everything else has raycastTarget off, so the game's UI
            // underneath, e.g. the skill tree, still gets its clicks)
            var cg = root.GetComponent<CanvasGroup>(); if (cg != null) { cg.blocksRaycasts = true; cg.interactable = true; cg.alpha = 1f; }
            var back = Child(rt, "Back");
            if (back != null) back.gameObject.SetActive(false);   // template for KICK, removed after building
            Kill(Child(rt, "Title")); Kill(Child(rt, "Buttons"));
            Kill(Child(rt, "Audio")); Kill(Child(rt, "Graphics"));

            var box = Child(rt, "General");
            _box = RT(box);
            var tLine = Child(box, "Line");
            var tRow = Child(box, "InvertYAxis") ?? Child(box, "Headbob");
            if (tLine == null || tRow == null) throw new Exception("row templates missing");
            var ddImg = Find(tRow, "Dropdown")?.GetComponent<Image>();
            var old = new List<Transform>();
            for (int i = 0; i < box.childCount; i++) old.Add(box.GetChild(i));

            var lg = box.GetComponent<VerticalLayoutGroup>();
            if (lg != null) { lg.padding.left = 34; lg.padding.right = 34; lg.padding.top = 18; lg.padding.bottom = 18; lg.spacing = 3f; }

            var head = TextRow(box, tRow, "Players");
            _title = Label(Child(box, "MP_Players"));
            if (_title != null) { Loc.Unbind(_title); _title.fontSize = 18; _title.fontSizeMax = 18; var tr = RT(_title.transform); if (tr != null) tr.sizeDelta = new Vector2(Mathf.Max(tr.sizeDelta.x, 560f), tr.sizeDelta.y); }
            Columns(head);
            RT(head.transform).anchoredPosition = new Vector2(-100f, 0f);   // same place as the rows' numbers
            _head = head; _headLang = -1;
            Line(box, tLine);

            _rows.Clear();
            for (int i = 0; i < MaxRows; i++)
            {
                var row = NewRow(box, tRow, "Player", false);
                row.name = "MP_ScoreRow" + i;
                Kill(Child(row, "Image"));
                var r = new Row { Go = row.gameObject };
                var rr = RT(row); rr.sizeDelta = new Vector2(rr.sizeDelta.x, RowH);

                var dot = Clone(tLine.gameObject, row);
                Place(RT(dot), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(14f, 14f));
                r.Dot = dot.GetComponent<Image>();
                if (ddImg != null) { r.Dot.sprite = ddImg.sprite; r.Dot.type = Image.Type.Sliced; r.Dot.pixelsPerUnitMultiplier = ddImg.pixelsPerUnitMultiplier * 2f; }

                r.Name = Label(row);
                Loc.Unbind(r.Name);
                r.Name.enableAutoSizing = false;
                var nr = RT(r.Name.transform);
                nr.anchoredPosition = new Vector2(nr.anchoredPosition.x + 22f, nr.anchoredPosition.y);
                nr.sizeDelta = new Vector2(200f, nr.sizeDelta.y);
                r.Name.fontSize = 16; r.Name.enableWordWrapping = false; r.Name.overflowMode = TextOverflowModes.Ellipsis;

                r.Stats = Clone(r.Name.gameObject, row).GetComponent<TMP_Text>();
                r.Stats.gameObject.name = "Stats";
                Columns(r.Stats);
                r.Stats.fontSize = 15;
                RT(r.Stats.transform).anchoredPosition = new Vector2(-100f, 0f);   // room for KICK on the right

                if (back != null)
                {
                    var k = Clone(back.gameObject, row);
                    k.name = "Kick";
                    k.SetActive(false);
                    Kill(Child(k.transform, "Icon"));
                    const float ks = 0.5f;
                    var kt = Label(k.transform);
                    if (kt != null) { Loc.Set(kt, "KICK"); Stretch(RT(kt.transform)); kt.alignment = TextAlignmentOptions.Center; kt.fontSize = kt.fontSize * 0.65f; kt.enableWordWrapping = false; Loc.Fit(kt, 0.5f); }
                    var ki = k.GetComponent<Image>(); if (ki != null) ki.pixelsPerUnitMultiplier = ki.pixelsPerUnitMultiplier / ks;
                    var rc = RT(Child(k.transform, "Raycast")); if (rc != null) rc.sizeDelta = rc.sizeDelta * ks;
                    Place(RT(k), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(170f * ks, 64f * ks));
                    r.Kick = k; r.KickLabel = kt;
                    var rr2 = r;
                    OnClick(k, () => OnKick(rr2));
                }
                _rows.Add(r);
            }
            if (back != null) Kill(back);

            // nothing but the KICK buttons catches clicks
            foreach (var g in root.GetComponentsInChildren<Graphic>(true)) if (g != null) g.raycastTarget = false;
            foreach (var r in _rows)
                if (r.Kick != null) foreach (var g in r.Kick.GetComponentsInChildren<Graphic>(true)) if (g != null) g.raycastTarget = true;

            foreach (var o in old) Kill(o);
            Strip(root);
            Plugin.Log.LogInfo("[UI] scoreboard built");
        }

        private static void Columns(TMP_Text t)
        {
            Place(RT(t.transform), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(410f, 24f));
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.richText = true; t.enableWordWrapping = false;
        }

        /// Host: click KICK, then again within 3 s to confirm.
        private static void OnKick(Row r)
        {
            if (!Net.IsHost || r.Id <= 0) return;
            if (Time.unscaledTime > r.ConfirmUntil)
            {
                r.ConfirmUntil = Time.unscaledTime + 3f;
                if (r.KickLabel != null) Loc.Set(r.KickLabel, "SURE?");
                return;
            }
            r.ConfirmUntil = 0f;
            Net.Kick(r.Id);
        }

        private static TMP_Text _head; private static int _headLang = -1;

        private static string Cols(string a, string b, string c, string d, string e) => $"{a}<pos=62>{b}<pos=135>{c}<pos=230>{d}<pos=320>{e}";

        private static void Refresh()
        {
            try
            {
                var ids = new List<int>(Net.Players.Keys);
                ids.Sort();
                bool host = Net.IsHost;
                string cap = host && Net.MaxPlayers > 0 ? "/" + Net.MaxPlayers : "";
                if (_title != null) _title.text = Loc.F("Players ({0})", ids.Count + cap) + (host && ids.Count > 1 && !CursorFree ? "   <size=60%><alpha=#99>" + Loc.T("hold LEFT ALT to click KICK") + "</size>" : "");
                if (_head != null && _headLang != Loc.Lang)
                {
                    _headLang = Loc.Lang;
                    _head.text = Loc.Columns(_head, 12f, new[] { 0f, 62f, 135f, 230f, 320f }, 410f, "KILLS", "DEATHS", "GAMBLED", "WON", "PING");
                }
                for (int i = 0; i < _rows.Count; i++)
                {
                    var r = _rows[i];
                    bool show = i < ids.Count;
                    if (r.Go.activeSelf != show) r.Go.SetActive(show);
                    if (!show) continue;
                    int id = ids[i];
                    r.Dot.color = Palette.ForPlayer(id);
                    r.Name.text = Net.NameOf(id) + (id == Net.LocalId ? " " + Loc.T("(you)") : "");
                    PlayerStats.All.TryGetValue(id, out var s);
                    r.Stats.text = Cols((s?.Kills ?? 0).ToString(), (s?.Deaths ?? 0).ToString(),
                        PlayerStats.Money(s?.Gambled ?? 0), PlayerStats.Money(s?.Won ?? 0), PauseUi.PingText(id));
                    if (r.Id != id) { r.Id = id; r.ConfirmUntil = 0f; }
                    bool kickable = host && id != Net.LocalId;
                    if (r.Kick != null)
                    {
                        if (r.Kick.activeSelf != kickable) r.Kick.SetActive(kickable);
                        if (kickable && r.KickLabel != null && Time.unscaledTime > r.ConfirmUntil && r.KickLabel.text != Loc.T("KICK")) Loc.Set(r.KickLabel, "KICK");
                    }
                }
                // size the box to the players: centred, as tall as its rows
                if (_box != null)
                {
                    float h = 18 + 30 + 3 + 2 + 3 + ids.Count * (RowH + 3) + 18;
                    _box.anchorMin = _box.anchorMax = new Vector2(0.5f, 0.5f);
                    _box.pivot = new Vector2(0.5f, 0.5f);
                    _box.anchoredPosition = new Vector2(0f, 40f);
                    _box.sizeDelta = new Vector2(812f, h);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[UI] scoreboard refresh: " + e.Message); }
        }
    }
}
