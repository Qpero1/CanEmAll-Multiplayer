using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static CanEmAllMP.MenuUi;

namespace CanEmAllMP
{
    /// Game-style spectating banner (replaces the IMGUI box): a big "SPECTATING" title at the top and a cream card
    /// with the watched player's colour + name and the controls. Built from a clone of the Settings screen like
    /// the other mod screens; not clickable; only anchored so the crosshair hider never matches it.
    public static class SpectateUi
    {
        private static NetSession Net => MpManager.Net;
        private static GameObject _root;
        private static TMP_Text _name, _hint;
        private static Image _dot;
        private static float _nextTry, _nextRefresh;
        public static bool Working => _root != null;

        public static void Tick()
        {
            bool want = Spectate.Dead && GameApi.InGame && !GameApi.IsPaused;
            if (want && _root == null && Time.unscaledTime >= _nextTry)
            {
                _nextTry = Time.unscaledTime + 10f;
                try { Build(); }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("[UI] spectate banner: " + e);
                    try { if (_root != null) UnityEngine.Object.Destroy(_root); } catch { }
                    _root = null;
                }
            }
            if (_root == null) return;
            if (_root.activeSelf != want) { _root.SetActive(want); _nextRefresh = 0f; }
            if (!want || Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.2f;
            try
            {
                var t = Spectate.Target();
                if (t != null)
                {
                    _name.text = Net.NameOf(t.Id);
                    _dot.color = Palette.ForPlayer(t.Id);
                    if (!_dot.gameObject.activeSelf) _dot.gameObject.SetActive(true);
                }
                else
                {
                    _name.text = Loc.T("Nobody to watch");
                    if (_dot.gameObject.activeSelf) _dot.gameObject.SetActive(false);
                }
            }
            catch { }
        }

        private static void Build()
        {
            var m = Menu.Instance;
            if (m == null || m.menuPanel == null) return;
            var canvas = m.menuPanel.transform.parent;
            var settings = Child(canvas, "Settings");
            if (settings == null) return;

            var root = Clone(settings.gameObject, canvas);
            root.name = "MP_SpectateBanner";
            root.SetActive(false);
            root.transform.SetSiblingIndex(settings.GetSiblingIndex() + 1);
            _root = root;
            var rt = root.transform;

            // an invisible, click-through holder that isn't a game "screen"
            var comps = root.GetComponents(Il2CppInterop.Runtime.Il2CppType.Of<MonoBehaviour>());
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i]?.TryCast<MonoBehaviour>();
                if (c != null && c.GetIl2CppType().Name == "UIScreen") UnityEngine.Object.DestroyImmediate(c);
            }
            var bg = root.GetComponent<Image>(); if (bg != null) bg.enabled = false;
            var cg = root.GetComponent<CanvasGroup>(); if (cg != null) { cg.blocksRaycasts = false; cg.interactable = false; cg.alpha = 1f; }
            Kill(Child(rt, "Buttons")); Kill(Child(rt, "Back")); Kill(Child(rt, "Audio")); Kill(Child(rt, "Graphics"));

            // title
            var title = Label(Child(rt, "Title"));
            Place(RT(title.transform), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -22f), new Vector2(700f, 56f));
            Loc.Set(title, "SPECTATING");
            title.alignment = TextAlignmentOptions.Midline;
            title.fontSize = 44;
            title.enableWordWrapping = false; Loc.Fit(title, 0.5f);

            // the card
            var box = Child(rt, "General");
            var tRow = Child(box, "InvertYAxis") ?? Child(box, "Headbob");
            var tLine = Child(box, "Line");
            var ddImg = Find(tRow, "Dropdown")?.GetComponent<Image>();
            var lbl = Label(tRow);
            var lg = box.GetComponent<VerticalLayoutGroup>(); if (lg != null) lg.enabled = false;
            var brt = RT(box);
            Place(brt, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -84f), new Vector2(460f, 78f));

            // name with the player's colour dot
            var nameGo = Clone(lbl.gameObject, box);
            nameGo.name = "Name";
            _name = nameGo.GetComponent<TMP_Text>();
            Place(RT(nameGo), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(64f, -12f), new Vector2(370f, 30f));
            _name.fontSize = 24; _name.alignment = TextAlignmentOptions.MidlineLeft;
            _name.enableWordWrapping = false; _name.overflowMode = TextOverflowModes.Ellipsis;

            var dot = Clone(tLine.gameObject, box);
            dot.name = "Dot";
            Place(RT(dot), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(34f, -18f), new Vector2(20f, 20f));
            _dot = dot.GetComponent<Image>();
            if (ddImg != null) { _dot.sprite = ddImg.sprite; _dot.type = Image.Type.Sliced; _dot.pixelsPerUnitMultiplier = ddImg.pixelsPerUnitMultiplier * 1.5f; }
            _dot.raycastTarget = false;

            // controls
            var hintGo = Clone(lbl.gameObject, box);
            hintGo.name = "Hint";
            _hint = hintGo.GetComponent<TMP_Text>();
            Place(RT(hintGo), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(34f, 12f), new Vector2(400f, 22f));
            _hint.fontSize = 13; _hint.alignment = TextAlignmentOptions.MidlineLeft; _hint.enableWordWrapping = false;
            _hint.color = new Color(_hint.color.r, _hint.color.g, _hint.color.b, 0.75f);
            Loc.Set(_hint, "CLICK / SPACE for the next player  -  you're back when the day ends");
            Loc.Fit(_hint, 0.65f);

            // drop the settings rows
            var keep = new HashSet<IntPtr> { nameGo.Pointer, dot.Pointer, hintGo.Pointer };
            var old = new List<Transform>();
            for (int i = 0; i < box.childCount; i++) { var c = box.GetChild(i); if (!keep.Contains(c.gameObject.Pointer)) old.Add(c); }
            foreach (var o in old) Kill(o);
            Strip(root);
            Plugin.Log.LogInfo("[UI] spectate banner built");
        }
    }
}
