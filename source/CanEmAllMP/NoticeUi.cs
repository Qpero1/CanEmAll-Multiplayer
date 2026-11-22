using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static CanEmAllMP.MenuUi;

namespace CanEmAllMP
{
    /// Game-style pop-ups at the bottom middle of the screen: the waiting message (elevator / continue / exit)
    /// and short toasts ("X joined", "Friendly fire ON"...). Cream cards cloned from the Settings content box,
    /// sized to their text, never clickable. If they can't be built, the old IMGUI text is drawn instead.
    public static class NoticeUi
    {
        private static GameObject _root;
        private static Card _wait, _toast;
        private static float _nextTry;
        public static bool Working => _root != null;

        private class Card { public GameObject Go; public RectTransform Rt; public TMP_Text Text; }

        public static void Tick(string waitText, string toastText)
        {
            bool any = !string.IsNullOrEmpty(waitText) || !string.IsNullOrEmpty(toastText);
            if (any && _root == null && Time.unscaledTime >= _nextTry)
            {
                _nextTry = Time.unscaledTime + 10f;
                try { Build(); }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("[UI] notices: " + e);
                    try { if (_root != null) UnityEngine.Object.Destroy(_root); } catch { }
                    _root = null;
                }
            }
            if (_root == null) return;
            if (_root.activeSelf != any) _root.SetActive(any);
            if (!any) return;
            float y = 36f;
            y = Show(_wait, waitText, y);
            Show(_toast, toastText, y);
        }

        private static float Show(Card c, string text, float y)
        {
            bool on = !string.IsNullOrEmpty(text);
            if (c.Go.activeSelf != on) c.Go.SetActive(on);
            if (!on) return y;
            if (c.Text.text != text) c.Text.text = text;
            float w = 0f;
            try { w = c.Text.GetPreferredValues(text, 2000f, 40f).x; } catch { }
            if (w <= 0f) w = text.Length * 9f;
            c.Rt.sizeDelta = new Vector2(Mathf.Clamp(w + 56f, 160f, 980f), 46f);
            c.Rt.anchoredPosition = new Vector2(0f, y);
            return y + 54f;
        }

        private static void Build()
        {
            var m = Menu.Instance;
            if (m == null || m.menuPanel == null) return;
            var canvas = m.menuPanel.transform.parent;
            var settings = Child(canvas, "Settings");
            if (settings == null) return;

            var root = Clone(settings.gameObject, canvas);
            root.name = "MP_Notices";
            root.SetActive(false);
            root.transform.SetSiblingIndex(settings.GetSiblingIndex() + 1);
            _root = root;
            var rt = root.transform;
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
            var tRow = Child(box, "InvertYAxis") ?? Child(box, "Headbob");
            var lbl = Label(tRow);
            var lg = box.GetComponent<VerticalLayoutGroup>(); if (lg != null) lg.enabled = false;

            // text template: the row label, centred
            var text = Clone(lbl.gameObject, box);
            text.name = "Text";
            var old = new List<Transform>();
            for (int i = 0; i < box.childCount; i++) { var c = box.GetChild(i); if (c.gameObject.Pointer != text.Pointer) old.Add(c); }
            foreach (var o in old) Kill(o);
            var t = text.GetComponent<TMP_Text>();
            var trt = RT(text); trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.pivot = new Vector2(0.5f, 0.5f);
            trt.anchoredPosition = Vector2.zero; trt.sizeDelta = new Vector2(-40f, 0f);
            t.alignment = TextAlignmentOptions.Midline; t.fontSize = 17; t.enableWordWrapping = false; t.text = "";
            Loc.Unbind(t); t.enableAutoSizing = true; t.fontSizeMax = 17; t.fontSizeMin = 11; // very long translations shrink instead of spilling

            var brt = RT(box);
            brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 0f); brt.pivot = new Vector2(0.5f, 0f);
            _wait = new Card { Go = box.gameObject, Rt = brt, Text = t };
            box.name = "Wait";

            var second = Clone(box.gameObject, rt);
            second.name = "Toast";
            _toast = new Card { Go = second, Rt = RT(second), Text = Child(second.transform, "Text").GetComponent<TMP_Text>() };

            Strip(root);
            Plugin.Log.LogInfo("[UI] notices built");
        }
    }
}
