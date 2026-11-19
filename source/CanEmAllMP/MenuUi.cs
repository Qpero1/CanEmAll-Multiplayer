using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CanEmAllMP
{
    /// Game-style UI for the mod, built by cloning the game's own menu elements and swapping their text.
    /// - a MULTIPLAYER button in the main menu (cloned from SETTINGS)
    /// - a MULTIPLAYER screen (cloned from the Settings screen) with name / colour / address / port / host / join /
    ///   friendly fire / status.
    ///
    /// Object.Instantiate is stripped from this game, so cloning goes through Object.Internal_CloneSingleWithParent_Injected,
    /// which returns the clone's own GCHandle (only read it - never free it).
    /// Things are cloned under the (inactive) screen so the game's scripts on them never wake up; the ones that would
    /// fight us (TranslateText rewriting labels, MoneyInputField formatting "$") are destroyed before it's shown.
    public static class MenuUi
    {
        private static bool _injectTried, _dumped2;
        private static GameObject _mpButton;
        private static float _menuSince = -1f, _injectedAt = -1f;
        private static int _checks;

        private static NetSession Net => MpManager.Net;

        public static GameObject Clone(GameObject src, Transform parent)
        {
            if (src == null) return null;
            IntPtr h = UnityEngine.Object.Internal_CloneSingleWithParent_Injected(src.GetCachedPtr(), parent != null ? parent.GetCachedPtr() : IntPtr.Zero, false);
            if (h == IntPtr.Zero) return null;
            IntPtr obj = IL2CPP.il2cpp_gchandle_get_target(h);
            // the handle is the clone's own persistent handle (the game's Unmarshal only reads it) - never free it
            return obj != IntPtr.Zero ? new GameObject(obj) : null;
        }

        public static void Tick()
        {
            TickScreen();
            TickArmedHost();

            Menu m = null;
            try { m = Menu.Instance; } catch { }
            bool onMenu = m != null && m.menuPanel != null && (m.menuPanel.activeInHierarchy || ScreenOpen);
            // on the main menu the mouse is always free (a kick mid-game can leave it locked/hidden)
            if (onMenu && HostSave.InMainMenu && !GameApi.InGame)
            {
                try { if (Cursor.lockState != CursorLockMode.None || !Cursor.visible) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } } catch { }
            }
            if (onMenu && HostSave.InMainMenu && !GameApi.InGame && !_hostArmed && !Net.Active)
            {
                MultiplayerSession = false;
                if (SaveSlots.ResetToLocal(true))
                    try { if (m.continueGameButton != null) m.continueGameButton.SetActive(SaveSlots.Exists(0)); } catch { }
            }
            if (m == null || m.menuPanel == null || !m.menuPanel.activeInHierarchy) { _menuSince = -1f; return; }
            float now = Time.unscaledTime;
            if (_menuSince < 0f) _menuSince = now;
            // let the game build and lay out its own menu first
            if (now - _menuSince < 0.75f) return;
            if (!_injectTried || _mpButton == null)
            {
                _injectTried = true;
                try { InjectButton(m); _injectedAt = now; _checks = 0; } catch (Exception e) { Plugin.Log.LogWarning("MULTIPLAYER button failed: " + e); }
            }
            if (_mpButton != null && _injectedAt >= 0f && _checks < 1 && now - _injectedAt > 0.3f)
            {
                _checks++;
                try { var rt = _mpButton.transform.parent.TryCast<RectTransform>(); if (rt != null) LayoutRebuilder.ForceRebuildLayoutImmediate(rt); }
                catch (Exception e) { Plugin.Log.LogWarning("[UI] menu layout rebuild threw: " + e.Message); }
            }
            if (!_dumped2 && _injectedAt >= 0f && now - _injectedAt > 2.5f)
            {
                _dumped2 = true;
                try { Dump(m); } catch (Exception e) { Plugin.Log.LogWarning("UI dump failed: " + e); }
            }
        }

        // ---------------------------------------------------------------- helpers

        internal static Transform Child(Transform t, string name)
        {
            if (t == null) return null;
            for (int i = 0; i < t.childCount; i++) { var c = t.GetChild(i); if (c.name == name) return c; }
            return null;
        }

        internal static Transform Find(Transform t, string name)
        {
            if (t == null) return null;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c.name == name) return c;
                var r = Find(c, name);
                if (r != null) return r;
            }
            return null;
        }

        internal static void Kill(Transform t) { if (t != null) UnityEngine.Object.DestroyImmediate(t.gameObject); }

        /// Remove game scripts that would rewrite/format what we put in.
        internal static void Strip(GameObject root)
        {
            var comps = root.GetComponentsInChildren(Il2CppType.Of<MonoBehaviour>(), true);
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i]?.TryCast<MonoBehaviour>();
                if (c == null) continue;
                string tn = c.GetIl2CppType().Name;
                if (tn == "TranslateText" || tn == "MoneyInputField" || tn.IndexOf("Locali", StringComparison.OrdinalIgnoreCase) >= 0)
                    UnityEngine.Object.DestroyImmediate(c);
            }
        }

        internal static RectTransform RT(GameObject g) => g != null ? g.transform.TryCast<RectTransform>() : null;
        internal static RectTransform RT(Transform t) => t != null ? t.TryCast<RectTransform>() : null;

        internal static void Place(RectTransform r, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            if (r == null) return;
            r.anchorMin = anchor; r.anchorMax = anchor; r.pivot = pivot; r.anchoredPosition = pos; r.sizeDelta = size;
        }

        internal static void Stretch(RectTransform r)
        {
            if (r == null) return;
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.pivot = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = Vector2.zero; r.sizeDelta = Vector2.zero;
        }

        internal static TMP_Text Label(Transform t) => t != null ? t.GetComponentInChildren<TMP_Text>(true) : null;

        internal static void OnClick(GameObject g, Action a)
        {
            var b = g != null ? g.GetComponent<Button>() : null;
            if (b == null) return;
            b.onClick = new Button.ButtonClickedEvent();
            b.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(() =>
            {
                try { a(); } catch (Exception e) { Plugin.Log.LogWarning("[UI] click: " + e); }
            })));
        }

        internal static Sprite SpriteOf(Transform t) { var i = t != null ? t.GetComponent<Image>() : null; return i != null ? i.sprite : null; }

        // ---------------------------------------------------------------- MULTIPLAYER button

        private static void InjectButton(Menu m)
        {
            var settingsText = FindText(m.menuPanel.transform, "SETTINGS");
            if (settingsText == null) { Plugin.Log.LogWarning("[UI] no SETTINGS text in the menu"); return; }
            var btn = settingsText.GetComponentInParent<Button>();
            var src = btn != null ? btn.gameObject : settingsText.transform.parent.gameObject;
            var clone = Clone(src, src.transform.parent);
            if (clone == null) { Plugin.Log.LogWarning("[UI] clone returned nothing"); return; }
            clone.name = "MP_MultiplayerButton";
            clone.transform.SetSiblingIndex(src.transform.GetSiblingIndex()); // just above SETTINGS (below NEW GAME)
            Strip(clone); // TranslateText would put "SETTINGS" back
            var t = Label(clone.transform);
            if (t != null) t.text = "MULTIPLAYER";
            var icon = Child(clone.transform, "Icon");
            var credIcon = Find(m.menuPanel.transform, "Credits");
            var spr = SpriteOf(Child(credIcon, "Icon"));
            if (icon != null && spr != null) icon.GetComponent<Image>().sprite = spr;
            OnClick(clone, () => OpenScreen());
            _mpButton = clone;
            Plugin.Log.LogInfo($"[UI] MULTIPLAYER button cloned from {Path(src.transform)}");
        }

        internal static TMP_Text FindText(Transform root, string upper)
        {
            var texts = root.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < texts.Length; i++)
                if (texts[i] != null && (texts[i].text ?? "").Trim().ToUpperInvariant() == upper) return texts[i];
            return null;
        }

        // ---------------------------------------------------------------- MULTIPLAYER screen

        private static GameObject _screen;
        private static TMP_InputField _name, _addr, _port;
        private static TMP_Text _info, _status, _players, _hostLbl, _joinLbl, _ffLabel;
        private static GameObject _hostBtn, _joinBtn;
        private static TMP_Dropdown _slot;
        private static Image[] _sw, _ffSw;
        private static GameObject[] _swSel, _ffSel;
        private static readonly Color FfOff = new Color(0.9f, 0.2f, 0.2f), FfOn = new Color(0.35f, 0.8f, 0.2f);
        private static bool _hostArmed;
        private static float _hostArmedAt;

        /// True for a game started from MULTIPLAYER > HOST GAME (or joined). CONTINUE / NEW GAME stay single player:
        /// the F8 panel only offers hosting inside a multiplayer session. Cleared when back on the main menu.
        public static bool MultiplayerSession;

        public static bool ScreenOpen => _screen != null && _screen.activeSelf;

        public static void OpenScreen()
        {
            Menu m = null;
            try { m = Menu.Instance; } catch { }
            if (m == null) { MpManager.Instance?.OpenMenuFromUi(); return; }
            if (_screen == null)
            {
                try { Build(m); }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("[UI] building the MULTIPLAYER screen failed, using the old panel: " + e);
                    try { if (_screen != null) UnityEngine.Object.Destroy(_screen); } catch { }
                    _screen = null;
                }
            }
            if (_screen == null) { MpManager.Instance?.OpenMenuFromUi(); return; }
            LoadFields();
            m.menuPanel.SetActive(false);
            _screen.SetActive(true);
            Refresh();
        }

        public static void CloseScreen()
        {
            if (_screen == null) return;
            SaveFields();
            _screen.SetActive(false);
            try { var m = Menu.Instance; if (m != null && !GameApi.InGame) m.menuPanel.SetActive(true); } catch { }
        }

        private static void Build(Menu m)
        {
            var canvas = m.menuPanel.transform.parent;
            var settings = Child(canvas, "Settings");
            if (settings == null) throw new Exception("no Settings screen");

            // Settings is inactive, so its clone (and everything we clone under it) stays asleep until we show it
            var scr = Clone(settings.gameObject, canvas);
            if (scr == null) throw new Exception("clone failed");
            scr.name = "MP_Screen";
            scr.SetActive(false);
            scr.transform.SetSiblingIndex(settings.GetSiblingIndex() + 1); // keep the game's Fade overlay on top
            _screen = scr;
            var st = scr.transform;

            var title = Label(Child(st, "Title"));
            if (title != null) title.text = "MULTIPLAYER";

            Kill(Child(st, "Audio"));
            Kill(Child(st, "Graphics"));

            // left column: HOST GAME / JOIN GAME (were the General / Audio / Graphics tabs)
            var tabs = Child(st, "Buttons");
            _hostBtn = Child(tabs, "General")?.gameObject;
            _joinBtn = Child(tabs, "Audio")?.gameObject;
            Kill(Child(tabs, "Graphics"));
            var playIcon = SpriteOf(Child(Find(m.menuPanel.transform, "NewGame"), "Icon"));
            var profIcon = SpriteOf(Child(Find(m.menuPanel.transform, "Credits"), "Icon"));
            _hostLbl = SetupTab(_hostBtn, "HOST GAME", playIcon);
            _joinLbl = SetupTab(_joinBtn, "JOIN GAME", profIcon);
            OnClick(_hostBtn, OnHostClicked);
            OnClick(_joinBtn, OnJoinClicked);

            var back = Child(st, "Back");
            OnClick(back?.gameObject, CloseScreen);

            // the content box: rebuild it from its own row templates
            var box = Child(st, "General");
            var tLine = Child(box, "Line");
            var tRow = Child(box, "InvertYAxis") ?? Child(box, "Headbob");
            if (tLine == null || tRow == null) throw new Exception("row templates missing");
            var old = new List<Transform>();
            for (int i = 0; i < box.childCount; i++) old.Add(box.GetChild(i));

            // input field template: the game's only text box (the slot machine's BET field)
            TMP_InputField betField = null;
            var fields = canvas.GetComponentsInChildren<TMP_InputField>(true);
            if (fields != null && fields.Length > 0) betField = fields[0];
            if (betField == null) throw new Exception("no input field to copy");

            var ddImg = Find(tRow, "Dropdown")?.GetComponent<Image>();
            var outline = Find(tRow, "Outline");

            Line(box, tLine);
            _name = InputRow(box, tRow, betField, ddImg, "Name", "Player", 24, false, 231f, 0f);
            Line(box, tLine);
            SwatchRow(box, tRow, back, ddImg, outline);
            Line(box, tLine);
            SlotRow(box, tRow);
            Line(box, tLine);
            _addr = InputRow(box, tRow, betField, ddImg, "Address", "e.g. 123.45.67.89", 64, false, 231f, -100f);
            PasteButton(_addr.transform.parent, back);
            Line(box, tLine);
            _port = InputRow(box, tRow, betField, ddImg, "Port", "7777", 5, true, 120f, 0f);
            Line(box, tLine);
            FriendlyFireRow(box, tRow, back, ddImg, outline);
            Line(box, tLine);
            _status = TextRow(box, tRow, "Status");
            Line(box, tLine);
            MaxRow(box, tRow);
            Line(box, tLine);
            _info = InfoRow(box, tRow, InfoText());

            foreach (var o in old) Kill(o);
            Strip(scr);
            Plugin.Log.LogInfo("[UI] MULTIPLAYER screen built");
        }

        private static TMP_Text SetupTab(GameObject b, string text, Sprite icon)
        {
            if (b == null) return null;
            var t = Label(b.transform);
            if (t != null)
            {
                t.text = text;
                t.enableWordWrapping = false;
                var r = RT(t.transform); if (r != null) r.sizeDelta = new Vector2(150f, r.sizeDelta.y);
            }
            var ic = Child(b.transform, "Icon");
            if (ic != null && icon != null) ic.GetComponent<Image>().sprite = icon;
            return t;
        }

        internal static void Line(Transform box, Transform tLine) => Clone(tLine.gameObject, box);

        internal static Transform NewRow(Transform box, Transform tRow, string label, bool keepDropdown)
        {
            var row = Clone(tRow.gameObject, box).transform;
            row.name = "MP_" + label;
            var l = Label(row);
            if (l != null) l.text = label;
            if (!keepDropdown) Kill(Child(Child(row, "Image"), "Dropdown"));
            return row;
        }

        private static TMP_InputField InputRow(Transform box, Transform tRow, TMP_InputField tField, Image ddImg, string label, string placeholder, int limit, bool numeric, float width, float x)
        {
            var row = NewRow(box, tRow, label, false);
            var holder = Child(row, "Image");
            var go = Clone(tField.gameObject, holder);
            go.name = "Input";
            Place(RT(go), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(x, 0f), new Vector2(width, 30f));
            var img = go.GetComponent<Image>();
            if (img != null && ddImg != null) { img.sprite = ddImg.sprite; img.type = Image.Type.Sliced; img.color = ddImg.color; img.pixelsPerUnitMultiplier = ddImg.pixelsPerUnitMultiplier; }
            var f = go.GetComponent<TMP_InputField>();
            f.onValueChanged = new TMP_InputField.OnChangeEvent();
            f.onEndEdit = new TMP_InputField.SubmitEvent();
            f.onSubmit = new TMP_InputField.SubmitEvent();
            f.lineType = TMP_InputField.LineType.SingleLine;
            f.contentType = numeric ? TMP_InputField.ContentType.IntegerNumber : TMP_InputField.ContentType.Standard;
            f.characterLimit = limit;
            if (f.textComponent != null)
            {
                f.textComponent.color = Color.white; f.textComponent.fontSize = 14;
                f.textComponent.alignment = TextAlignmentOptions.MidlineLeft;
            }
            var ph = f.placeholder != null ? f.placeholder.TryCast<TMP_Text>() : null;
            if (ph != null) { ph.text = placeholder; ph.color = new Color(1f, 1f, 1f, 0.35f); ph.fontSize = 14; ph.alignment = TextAlignmentOptions.MidlineLeft; }
            return f;
        }

        private static void PasteButton(Transform holder, Transform back)
        {
            if (back == null) return;
            var b = Clone(back.gameObject, holder);
            b.name = "Paste";
            Kill(Child(b.transform, "Icon"));
            const float k = 0.6f; // shrink by sizing (ButtonAnimation resets localScale to 1 on hover)
            var t = Label(b.transform);
            if (t != null)
            {
                t.text = "PASTE";
                Stretch(RT(t.transform));
                t.alignment = TextAlignmentOptions.Center;
                t.fontSize = t.fontSize * k;
            }
            var img = b.GetComponent<Image>();
            if (img != null) img.pixelsPerUnitMultiplier = img.pixelsPerUnitMultiplier / k;
            var rc = RT(Child(b.transform, "Raycast"));
            if (rc != null) rc.sizeDelta = rc.sizeDelta * k;
            var r = RT(b);
            Place(r, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, 0f), new Vector2(150f * k, 64f * k));
            OnClick(b, () =>
            {
                string c = null;
                try { c = GUIUtility.systemCopyBuffer; } catch { }
                if (!string.IsNullOrWhiteSpace(c)) _addr.text = c.Trim();
            });
        }

        /// A row of small clickable colour squares (cloned from a game button), with a white ring on the selected one.
        internal static void Swatches(Transform holder, Transform back, Image ddImg, Transform outline, Color[] colors, Action<int> click,
                                     out Image[] imgs, out GameObject[] rings)
        {
            int n = colors.Length;
            imgs = new Image[n]; rings = new GameObject[n];
            const float size = 24f, gap = 5f;
            float total = n * size + (n - 1) * gap;
            for (int i = 0; i < n; i++)
            {
                var g = Clone(back.gameObject, holder);
                g.name = "Swatch" + i;
                Kill(Child(g.transform, "Text (TMP)")); Kill(Child(g.transform, "Icon")); Kill(Child(g.transform, "Raycast"));
                Place(RT(g), new Vector2(1f, 0.5f), new Vector2(0f, 0.5f), new Vector2(-total + i * (size + gap), 0f), new Vector2(size, size));
                var img = g.GetComponent<Image>();
                if (ddImg != null) { img.sprite = ddImg.sprite; img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = ddImg.pixelsPerUnitMultiplier; }
                img.color = colors[i];
                img.raycastTarget = true; // the button's click area was its Raycast child, which we removed
                imgs[i] = img;
                if (outline != null)
                {
                    var o = Clone(outline.gameObject, g.transform);
                    var orr = RT(o); Stretch(orr); orr.sizeDelta = new Vector2(8f, 8f);
                    var oi = o.GetComponent<Image>(); if (oi != null) { oi.color = Color.white; oi.raycastTarget = false; }
                    rings[i] = o;
                }
                int idx = i;
                OnClick(g, () => click(idx));
            }
        }

        private static void SwatchRow(Transform box, Transform tRow, Transform back, Image ddImg, Transform outline)
        {
            var row = NewRow(box, tRow, "Colour", false);
            Swatches(Child(row, "Image"), back, ddImg, outline, Palette.Colors, idx =>
            {
                if (Net.Active && Net.ColorTaken(idx) && idx != Net.MyColor) return;
                Plugin.PlayerColor.Value = idx; Net.RequestColor(idx);
                Refresh();
            }, out _sw, out _swSel);
        }

        private static void FriendlyFireRow(Transform box, Transform tRow, Transform back, Image ddImg, Transform outline)
        {
            var row = NewRow(box, tRow, "Friendly fire", false);
            _ffLabel = Label(row);
            Swatches(Child(row, "Image"), back, ddImg, outline, new[] { FfOff, FfOn }, idx =>
            {
                if (Net.IsClient) return; // the host decides
                bool on = idx == 1;
                if (on != Plugin.FriendlyFire.Value)
                {
                    Plugin.FriendlyFire.Value = on;
                    Extras.SetFriendlyFire(on, true);
                    if (Net.IsHost) Extras.Announce("Friendly fire " + (on ? "ON" : "OFF"));
                }
                Refresh();
            }, out _ffSw, out _ffSel);
        }

        /// The game's dropdown list is sized for its own short lists and doesn't clip, so longer lists spill out:
        /// size the list to its options.
        internal static void FitDropdown(TMP_Dropdown dd)
        {
            try
            {
                var t = dd != null ? dd.template : null;
                if (t == null) return;
                int n = dd.options != null ? dd.options.Count : 2;
                t.sizeDelta = new Vector2(t.sizeDelta.x, n * 28f + 10f);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[UI] dropdown size: " + e.Message); }
        }

        // max players: 2..8, then Unlimited (0)
        private static TMP_Dropdown _max;
        private static void MaxRow(Transform box, Transform tRow)
        {
            var row = NewRow(box, tRow, "Max players", true);
            var dd = Find(row, "Dropdown");
            var comps = dd != null ? dd.GetComponents(Il2CppType.Of<Component>()) : null;
            if (comps != null)
                for (int i = 0; i < comps.Length && _max == null; i++) _max = comps[i]?.TryCast<TMP_Dropdown>();
            if (_max == null) { Plugin.Log.LogWarning("[UI] max players: dropdown type not usable"); return; }
            var opts = new Il2CppSystem.Collections.Generic.List<string>();
            for (int n = 2; n <= 8; n++) opts.Add(n.ToString());
            opts.Add("Unlimited");
            _max.ClearOptions();
            _max.AddOptions(opts);
            FitDropdown(_max);
            int v = Plugin.MaxPlayers.Value;
            _max.SetValueWithoutNotify(v <= 0 ? 7 : Mathf.Clamp(v, 2, 8) - 2);
            _max.RefreshShownValue();
            _max.onValueChanged = new TMP_Dropdown.DropdownEvent();
            _max.onValueChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<int>>(new Action<int>(i =>
            {
                try { Plugin.MaxPlayers.Value = i >= 7 ? 0 : i + 2; } catch { }
            })));
        }

        // dropdown index -> save slot: Slot 1, Slot 2, Slot 3, Local save
        private static int SlotOfIndex(int i) => i >= 0 && i < SaveSlots.Count ? i + 1 : 0;
        private static int IndexOfSlot(int s) => s >= 1 && s <= SaveSlots.Count ? s - 1 : SaveSlots.Count;

        private static void SlotRow(Transform box, Transform tRow)
        {
            var row = NewRow(box, tRow, "Host save", true);
            var dd = Find(row, "Dropdown");
            var comps = dd != null ? dd.GetComponents(Il2CppType.Of<Component>()) : null;
            if (comps != null)
                for (int i = 0; i < comps.Length && _slot == null; i++) _slot = comps[i]?.TryCast<TMP_Dropdown>();
            if (_slot == null) { Plugin.Log.LogWarning("[UI] save slot: dropdown type not usable"); return; }
            _slot.onValueChanged = new TMP_Dropdown.DropdownEvent();
            _slot.onValueChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<int>>(new Action<int>(v =>
            {
                try { Plugin.HostSlot.Value = SlotOfIndex(v); } catch { }
            })));
            RefreshSlots();
        }

        /// Rebuild the slot names ("Slot 1 - Day 4", ...) from the files on disk.
        private static void RefreshSlots()
        {
            if (_slot == null) return;
            try
            {
                var opts = new Il2CppSystem.Collections.Generic.List<string>();
                for (int i = 1; i <= SaveSlots.Count; i++) opts.Add(SaveSlots.Describe(i));
                opts.Add(SaveSlots.Describe(0));
                _slot.ClearOptions();
                _slot.AddOptions(opts);
                FitDropdown(_slot);
                _slot.SetValueWithoutNotify(IndexOfSlot(Plugin.HostSlot.Value));
                _slot.RefreshShownValue();
            }
            catch (Exception e) { Plugin.Log.LogWarning("[UI] save slots: " + e.Message); }
        }

        internal static TMP_Text TextRow(Transform box, Transform tRow, string label)
        {
            var row = NewRow(box, tRow, label, false);
            var l = Label(row);
            var v = Clone(l.gameObject, row).GetComponent<TMP_Text>();
            v.gameObject.name = "Value";
            Place(RT(v.transform), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(420f, 24f));
            v.alignment = TextAlignmentOptions.MidlineRight;
            v.fontSize = 14; v.enableWordWrapping = false; v.overflowMode = TextOverflowModes.Ellipsis;
            v.text = "";
            return v;
        }

        private static string _infoPort;
        private static string InfoText()
        {
            string port = _port != null && !string.IsNullOrWhiteSpace(_port.text) ? _port.text.Trim() : Plugin.Port.Value.ToString();
            _infoPort = port;
            return "HOST: press HOST GAME - your save loads and the session opens. Forward UDP port " + port + " on your router and give friends your public IP.\n" +
                   "JOIN: type the host's address and press JOIN GAME - you'll play on the host's save.";
        }

        private static TMP_Text InfoRow(Transform box, Transform tRow, string text)
        {
            var row = NewRow(box, tRow, "Info", false);
            Kill(Child(row, "Image"));
            var rr = RT(row); rr.sizeDelta = new Vector2(rr.sizeDelta.x, 62f);
            var l = Label(row);
            Stretch(RT(l.transform));
            l.text = text; l.fontSize = 13; l.enableWordWrapping = true;
            l.alignment = TextAlignmentOptions.TopLeft;
            l.color = new Color(l.color.r, l.color.g, l.color.b, 0.8f);
            return l;
        }

        // ---------------------------------------------------------------- behaviour

        private static void LoadFields()
        {
            var mp = MpManager.Instance;
            if (mp == null) return;
            _name.text = mp.FieldName;
            _addr.text = mp.FieldAddress;
            _port.text = mp.FieldPort;
            RefreshSlots();
        }

        private static void SaveFields()
        {
            try { MpManager.Instance?.ApplyFields(_name.text, _addr.text, _port.text); } catch { }
        }

        private static void OnHostClicked()
        {
            SaveFields();
            if (Net.Active) { MpManager.Instance?.Disconnect(); return; }
            if (!GameApi.InGame)
            {
                if (!SaveSlots.RedirectWorks(Plugin.HostSlot.Value))
                {
                    Net.Status = "Multiplayer save slots aren't working - pick Local save (see log)";
                    return;
                }
                SaveSlots.Use(Plugin.HostSlot.Value); // the lobby hosts this save
                if (!Net.Host(Plugin.Port.Value)) return; // Status says why (port in use...)
                MultiplayerSession = true;
                MpManager.Instance?.Feed($"Lobby open on UDP {Plugin.Port.Value}. Friends join with your public IP.");
                _screen.SetActive(false);
                LobbyUi.Show();
                return;
            }
            if (GameApi.InGame)
            {
                if (Net.Host(Plugin.Port.Value)) MpManager.Instance?.Feed($"Hosting on UDP {Plugin.Port.Value}. Friends join with your public IP.");
                return;
            }
            HostFromMenu();
        }

        /// Hosting needs a loaded save: load it (like CONTINUE, or NEW GAME without a save), then open the session.
        public static void HostFromMenu()
        {
            var m = Menu.Instance;
            if (m == null) return;
            if (!SaveSlots.RedirectWorks(Plugin.HostSlot.Value))
            {
                Net.Status = "Multiplayer save slots aren't working - pick Local save (see log)";
                return;
            }
            bool hasSave = SaveSlots.Use(Plugin.HostSlot.Value); // point the game at the chosen save and load it
            _hostArmed = true; _hostArmedAt = Time.unscaledTime;
            MultiplayerSession = true;
            if (_screen != null) _screen.SetActive(false);
            m.menuPanel.SetActive(true);
            if (hasSave) m.ContinueGame(); else m.NewGame();
        }

        private static void TickArmedHost()
        {
            if (!_hostArmed) return;
            if (Net.Active) { _hostArmed = false; return; }
            if (Time.unscaledTime - _hostArmedAt > 120f) { _hostArmed = false; return; }
            if (!GameApi.InGame) return;
            _hostArmed = false;
            if (Net.Host(Plugin.Port.Value)) MpManager.Instance?.Feed($"Hosting on UDP {Plugin.Port.Value}. Friends join with your public IP.");
        }

        private static void OnJoinClicked()
        {
            SaveFields();
            if (Net.Active) return;
            if (string.IsNullOrWhiteSpace(Plugin.LastAddress.Value)) { Net.Status = "Type the host's address first"; return; }
            MultiplayerSession = true;
            if (Net.Join(Plugin.LastAddress.Value.Trim(), Plugin.Port.Value))
            {
                _screen.SetActive(false);
                LobbyUi.Show();
            }
        }

        /// Back from the lobby (left / host gone): show the MULTIPLAYER screen again.
        public static void ReturnFromLobby()
        {
            try
            {
                if (_screen == null || GameApi.InGame) return;
                var m = Menu.Instance;
                if (m != null) m.menuPanel.SetActive(false);
                _screen.SetActive(true);
                LoadFields();
                Refresh();
            }
            catch (Exception e) { Plugin.Log.LogWarning("[UI] back from lobby: " + e.Message); }
        }

        /// Pieces the lobby copies from the MULTIPLAYER screen.
        internal static GameObject ScreenObject => _screen;

        private static float _nextRefresh;

        private static void TickScreen()
        {
            if (_screen == null || !_screen.activeSelf) return;
            if (GameApi.InGame) { _screen.SetActive(false); return; } // joined: the host's save is loading
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                bool typing = (_name != null && _name.isFocused) || (_addr != null && _addr.isFocused) || (_port != null && _port.isFocused);
                if (kb != null && kb.escapeKey.wasPressedThisFrame && !typing) { CloseScreen(); return; }
            }
            catch { }
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.2f;
            Refresh();
        }

        private static void Refresh()
        {
            try
            {
                bool active = Net.Active;
                if (_hostLbl != null) _hostLbl.text = active ? (Net.IsHost ? "STOP HOSTING" : "DISCONNECT") : "HOST GAME";
                if (_joinBtn != null && _joinBtn.activeSelf == active) _joinBtn.SetActive(!active);

                if (_info != null && _port != null && (_port.text ?? "").Trim() != _infoPort) _info.text = InfoText();
                if (_status != null) _status.text = _hostArmed ? "Loading your save to host..." : (Net.Status ?? "");
                if (_players != null)
                {
                    if (!active) _players.text = "-";
                    else
                    {
                        var names = new List<string>();
                        foreach (var p in Net.Players.Values) names.Add(p.Id == Net.LocalId ? p.Name + " (you)" : p.Name);
                        _players.text = string.Join(", ", names);
                    }
                }

                if (_sw != null)
                {
                    int sel = active ? Net.MyColor : Palette.Clamp(Plugin.PlayerColor.Value < 0 ? 0 : Plugin.PlayerColor.Value);
                    for (int i = 0; i < _sw.Length; i++)
                    {
                        bool taken = active && i != sel && Net.ColorTaken(i);
                        var c = Palette.Colors[i]; c.a = taken ? 0.25f : 1f;
                        if (_sw[i] != null) _sw[i].color = c;
                        if (_swSel[i] != null && _swSel[i].activeSelf != (i == sel)) _swSel[i].SetActive(i == sel);
                    }
                }

                if (_ffSw != null)
                {
                    bool client = Net.IsClient;
                    int sel = (client ? Extras.FriendlyFire : Plugin.FriendlyFire.Value) ? 1 : 0;
                    var cols = new[] { FfOff, FfOn };
                    for (int i = 0; i < _ffSw.Length; i++)
                    {
                        var c = cols[i]; c.a = client && i != sel ? 0.25f : 1f;
                        if (_ffSw[i] != null) _ffSw[i].color = c;
                        if (_ffSel[i] != null && _ffSel[i].activeSelf != (i == sel)) _ffSel[i].SetActive(i == sel);
                    }
                    if (_ffLabel != null) _ffLabel.text = client ? "Friendly fire (host decides)" : "Friendly fire";
                }
                if (_slot != null && _slot.interactable == active) _slot.interactable = !active;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[UI] refresh: " + e.Message); }
        }

        // ---------------------------------------------------------------- hierarchy dump

        private static void Dump(Menu m)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Can 'Em All main menu UI dump (for building the mod's UI)");
            sb.AppendLine("menuPanel = " + Path(m.menuPanel.transform));
            try { if (m.continueGameButton != null) sb.AppendLine("continueGameButton = " + Path(m.continueGameButton.transform)); } catch { }
            var root = m.menuPanel.transform.root;
            int count = 0;
            Walk(root, 0, sb, ref count);
            string file = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "canemall_ui_dump.txt");
            File.WriteAllText(file, sb.ToString());
            Plugin.Log.LogInfo($"[UI] dumped {count} UI objects to {file}");
        }

        private static void Walk(Transform t, int depth, StringBuilder sb, ref int count)
        {
            if (t == null || depth > 16 || count > 6000) return;
            count++;
            sb.Append(' ', depth * 2).Append(t.gameObject.activeSelf ? "" : "(off) ").Append(t.name);
            try
            {
                var comps = t.GetComponents(Il2CppType.Of<Component>());
                sb.Append("  [");
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i]; if (c == null) continue;
                    string n = c.GetIl2CppType().Name;
                    if (n == "Transform" || n == "CanvasRenderer") continue;
                    sb.Append(n).Append(' ');
                }
                sb.Append(']');
                var rt = t.TryCast<RectTransform>();
                if (rt != null)
                    sb.Append($" anchors={V(rt.anchorMin)}-{V(rt.anchorMax)} pivot={V(rt.pivot)} pos={V(rt.anchoredPosition)} size={V(rt.sizeDelta)}");
                var tmp = t.GetComponent<TMP_Text>();
                if (tmp != null)
                {
                    string txt = (tmp.text ?? "").Replace("\n", "\\n");
                    if (txt.Length > 60) txt = txt.Substring(0, 60) + "...";
                    sb.Append($" text=\"{txt}\" font={(tmp.font != null ? tmp.font.name : "-")} size={tmp.fontSize} color={tmp.color} align={tmp.alignment}");
                }
                var img = t.GetComponent<Image>();
                if (img != null) sb.Append($" sprite={(img.sprite != null ? img.sprite.name : "-")} color={img.color} type={img.type}");
                var lg = t.GetComponent<HorizontalOrVerticalLayoutGroup>();
                if (lg != null) sb.Append($" layout spacing={lg.spacing} pad={lg.padding.left},{lg.padding.right},{lg.padding.top},{lg.padding.bottom} childAlign={lg.childAlignment}");
            }
            catch (Exception e) { sb.Append(" (" + e.GetType().Name + ")"); }
            sb.AppendLine();
            for (int i = 0; i < t.childCount; i++)
            {
                try { Walk(t.GetChild(i), depth + 1, sb, ref count); }
                catch (Exception e) { sb.Append(' ', depth * 2 + 2).AppendLine("(child failed: " + e.GetType().Name + ")"); }
            }
        }

        private static string V(Vector2 v) => $"({v.x:0.##},{v.y:0.##})";

        private static string Path(Transform t)
        {
            var sb = new StringBuilder();
            for (int i = 0; t != null && i < 10; i++, t = t.parent) sb.Insert(0, "/" + t.name);
            return sb.ToString();
        }
    }
}
