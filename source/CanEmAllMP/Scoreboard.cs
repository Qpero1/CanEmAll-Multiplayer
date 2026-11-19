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
        private class Row { public GameObject Go; public Image Dot; public TMP_Text Name, Stats; }
        private static readonly List<Row> _rows = new List<Row>();
        private static float _nextTry, _nextRefresh;

        public static void Tick()
        {
            bool want = false;
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                want = kb != null && kb.tabKey.isPressed && Net.Active && GameApi.InGame && !GameApi.IsPaused && !MpManager.MenuOpen;
            }
            catch { }

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
            var cg = root.GetComponent<CanvasGroup>(); if (cg != null) { cg.blocksRaycasts = false; cg.interactable = false; cg.alpha = 1f; }
            Kill(Child(rt, "Title")); Kill(Child(rt, "Buttons")); Kill(Child(rt, "Back"));
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
            if (_title != null) { _title.fontSize = 18; }
            Columns(head);
            head.text = Cols("KILLS", "DEATHS", "PING");
            head.fontSize = 12;
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
                var nr = RT(r.Name.transform);
                nr.anchoredPosition = new Vector2(nr.anchoredPosition.x + 22f, nr.anchoredPosition.y);
                nr.sizeDelta = new Vector2(240f, nr.sizeDelta.y);
                r.Name.fontSize = 16; r.Name.enableWordWrapping = false; r.Name.overflowMode = TextOverflowModes.Ellipsis;

                r.Stats = Clone(r.Name.gameObject, row).GetComponent<TMP_Text>();
                r.Stats.gameObject.name = "Stats";
                Columns(r.Stats);
                r.Stats.fontSize = 16;
                _rows.Add(r);
            }

            foreach (var o in old) Kill(o);
            Strip(root);
            Plugin.Log.LogInfo("[UI] scoreboard built");
        }

        private static void Columns(TMP_Text t)
        {
            Place(RT(t.transform), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(240f, 24f));
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.richText = true; t.enableWordWrapping = false;
        }

        private static string Cols(string a, string b, string c) => $"{a}<pos=85>{b}<pos=175>{c}";

        private static void Refresh()
        {
            try
            {
                var ids = new List<int>(Net.Players.Keys);
                ids.Sort();
                if (_title != null) _title.text = $"Players ({ids.Count}/8)";
                for (int i = 0; i < _rows.Count; i++)
                {
                    var r = _rows[i];
                    bool show = i < ids.Count;
                    if (r.Go.activeSelf != show) r.Go.SetActive(show);
                    if (!show) continue;
                    int id = ids[i];
                    r.Dot.color = Palette.ForPlayer(id);
                    r.Name.text = Net.NameOf(id) + (id == Net.LocalId ? " (you)" : "");
                    PlayerStats.All.TryGetValue(id, out var s);
                    r.Stats.text = Cols((s?.Kills ?? 0).ToString(), (s?.Deaths ?? 0).ToString(), PauseUi.PingText(id));
                }
                // size the box to the players: centred, as tall as its rows
                if (_box != null)
                {
                    float h = 18 + 30 + 3 + 2 + 3 + ids.Count * (RowH + 3) + 18;
                    _box.anchorMin = _box.anchorMax = new Vector2(0.5f, 0.5f);
                    _box.pivot = new Vector2(0.5f, 0.5f);
                    _box.anchoredPosition = new Vector2(0f, 40f);
                    _box.sizeDelta = new Vector2(712f, h);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[UI] scoreboard refresh: " + e.Message); }
        }
    }
}
