using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            // Find SETTINGS by object name / gear icon, never by its label: the label is translated.
            var src = FindSettingsButton(m.menuPanel.transform);
            if (src == null) { Plugin.Log.LogWarning("[UI] no SETTINGS button in the menu"); return; }
            var clone = Clone(src, src.transform.parent);
            if (clone == null) { Plugin.Log.LogWarning("[UI] clone returned nothing"); return; }
            clone.name = "MP_MultiplayerButton";
            clone.transform.SetSiblingIndex(src.transform.GetSiblingIndex()); // just above SETTINGS (below NEW GAME)
            Strip(clone); // TranslateText would put "SETTINGS" back
            var t = Label(clone.transform);
            if (t != null) { Loc.Set(t, "MULTIPLAYER"); Loc.Fit(t); }
            var icon = Child(clone.transform, "Icon");
            var credIcon = Find(m.menuPanel.transform, "Credits");
            var spr = SpriteOf(Child(credIcon, "Icon"));
            if (icon != null && spr != null) icon.GetComponent<Image>().sprite = spr;
            OnClick(clone, () => OpenScreen());
            _mpButton = clone;
            Plugin.Log.LogInfo($"[UI] MULTIPLAYER button cloned from {Path(src.transform)}");
        }

        private static GameObject FindSettingsButton(Transform menu)
        {
            var buttons = Child(menu, "Buttons");
            var byName = Child(buttons, "Settings");
            if (byName != null && byName.GetComponent<Button>() != null) return byName.gameObject;
            var all = menu.GetComponentsInChildren<Button>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var spr = SpriteOf(Child(all[i].transform, "Icon"));
                if (spr != null && spr.name == "settings" && all[i].gameObject.name != "MP_MultiplayerButton") return all[i].gameObject;
            }
            var t = FindText(menu, (Loc.GameWord("settings") ?? "").ToUpperInvariant());
            if (t == null) t = FindText(menu, "SETTINGS");
            if (t == null) return null;
            var b = t.GetComponentInParent<Button>();
            return b != null ? b.gameObject : t.transform.parent.gameObject;
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
        private static bool _locHooked;
        private static TMP_InputField _name, _addr, _port;
        private static TMP_Text _info, _status, _players, _hostLbl, _joinLbl, _ffLabel;
        private static GameObject _hostBtn, _joinBtn;
        private static TMP_Dropdown _slot, _conn;
        private static TMP_Text _addrLbl, _actLbl;
        private static GameObject _actBtn;
        private static int _tab; // 0 = HOST, 1 = JOIN, 2 = SAVES
        private static GameObject _savesBtn;
        private static readonly List<(GameObject line, GameObject row, Func<bool> show)> _rows = new List<(GameObject, GameObject, Func<bool>)>();
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
            if (!_locHooked)
            {
                _locHooked = true;
                Loc.Changed += () => { if (_screen != null) { RefreshSlots(); RefreshSaves(); ApplyMode(); } };
            }
            var st = scr.transform;

            var title = Label(Child(st, "Title"));
            if (title != null) { Loc.Set(title, "MULTIPLAYER"); Loc.Fit(title, 0.5f); title.enableWordWrapping = false; var trt = RT(title.transform); if (trt != null) trt.sizeDelta = new Vector2(600f, trt.sizeDelta.y); }

            Kill(Child(st, "Audio"));
            Kill(Child(st, "Graphics"));

            // left column: HOST GAME / JOIN GAME (were the General / Audio / Graphics tabs)
            var tabs = Child(st, "Buttons");
            _hostBtn = Child(tabs, "General")?.gameObject;
            _joinBtn = Child(tabs, "Audio")?.gameObject;
            _savesBtn = Child(tabs, "Graphics")?.gameObject;
            var gearIcon = SpriteOf(Child(_hostBtn != null ? _hostBtn.transform : null, "Icon"));
            var playIcon = SpriteOf(Child(Find(m.menuPanel.transform, "NewGame"), "Icon"));
            var profIcon = SpriteOf(Child(Find(m.menuPanel.transform, "Credits"), "Icon"));
            _hostLbl = SetupTab(_hostBtn, "HOST", playIcon);
            _joinLbl = SetupTab(_joinBtn, "JOIN", profIcon);
            OnClick(_hostBtn, () => SelectTab(0));
            OnClick(_joinBtn, () => SelectTab(1));
            SetupTab(_savesBtn, "SAVES", gearIcon);
            OnClick(_savesBtn, () => SelectTab(2));

            var back = Child(st, "Back");
            OnClick(back?.gameObject, CloseScreen);

            // bottom-right action button (HOST LOBBY / JOIN LOBBY), cloned from BACK
            if (back != null)
            {
                _actBtn = Clone(back.gameObject, st);
                _actBtn.name = "MP_Action";
                var ar = RT(_actBtn);
                Place(ar, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-35f, 25f), new Vector2(230f, ar != null ? ar.sizeDelta.y : 64f));
                _actLbl = Label(_actBtn.transform);
                if (_actLbl != null) { _actLbl.enableWordWrapping = false; var tr = RT(_actLbl.transform); if (tr != null) tr.sizeDelta = new Vector2(170f, tr.sizeDelta.y); Loc.Fit(_actLbl); }
                var aic = Child(_actBtn.transform, "Icon");
                if (aic != null && playIcon != null) aic.GetComponent<Image>().sprite = playIcon;
                OnClick(_actBtn, OnActionClicked);
            }

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

            // every row gets its separator line; which rows show depends on the tab (and Online / Direct)
            _rows.Clear();
            void Row(Func<bool> show, Action make)
            {
                var line = Line(box, tLine);
                int before = box.childCount;
                make();
                var row = box.childCount > before ? box.GetChild(box.childCount - 1).gameObject : null;
                _rows.Add((line, row, show));
            }
            bool HostTab() => _tab == 0;
            bool JoinTab() => _tab == 1;
            bool SavesTab() => _tab == 2;
            bool Play() => _tab != 2;
            bool Direct() => NetSession.UseDirect && _tab != 2;
            bool JoinByAddress() => _tab == 1 && !NetSession.UseSteam;
            bool JoinSteam() => _tab == 1 && NetSession.UseSteam;
            Row(Play, () => _name = InputRow(box, tRow, betField, ddImg, "Name", "Player", 24, false, 231f, 0f));
            Row(Play, () => SwatchRow(box, tRow, back, ddImg, outline));
            Row(Play, () => ConnRow(box, tRow));
            Row(SavesTab, () => _svSlot = ActionDropdown(box, tRow, back, "Save", "CLEAR", () => SaveAction(0), out _svClearLbl, v => { _svSlotIdx = v; RefreshSaves(); }));
            Row(SavesTab, () => _svTarget = ActionDropdown(box, tRow, back, "Copy it to", "COPY", () => SaveAction(1), out _svCopyLbl, v => _svTargetIdx = v));
            Row(SavesTab, () => _svBackup = ActionDropdown(box, tRow, back, "Backup", "RESTORE", () => SaveAction(2), out _svRestoreLbl, v => _svBackupIdx = v));
            Row(HostTab, () => SlotRow(box, tRow));
            Row(JoinSteam, () => FriendRow(box, tRow, back));
            Row(JoinByAddress, () =>
            {
                _addr = InputRow(box, tRow, betField, ddImg, "Address", "e.g. 123.45.67.89", 64, false, 231f, -100f);
                _addrLbl = Label(_addr.transform.parent.parent);
                PasteButton(_addr.transform.parent, back);
            });
            Row(Direct, () => _port = InputRow(box, tRow, betField, ddImg, "Port", "7777", 5, true, 120f, 0f));
            Row(HostTab, () => FriendlyFireRow(box, tRow, back, ddImg, outline));
            Row(HostTab, () => PauseRow(box, tRow, back, ddImg, outline));
            Row(HostTab, () => MaxRow(box, tRow));
            Row(() => true, () => _status = TextRow(box, tRow, "Status", true));
            Row(() => true, () => _info = InfoRow(box, tRow, InfoText()));
            _boxRt = RT(box); _boxLg = box.GetComponent<VerticalLayoutGroup>();

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
                Loc.Set(t, text);
                t.enableWordWrapping = false;
                var r = RT(t.transform); if (r != null) r.sizeDelta = new Vector2(150f, r.sizeDelta.y);
                Loc.Fit(t);
            }
            var ic = Child(b.transform, "Icon");
            if (ic != null && icon != null) ic.GetComponent<Image>().sprite = icon;
            return t;
        }

        internal static GameObject Line(Transform box, Transform tLine) => Clone(tLine.gameObject, box);

        internal static Transform NewRow(Transform box, Transform tRow, string label, bool keepDropdown)
        {
            var row = Clone(tRow.gameObject, box).transform;
            row.name = "MP_" + label;
            var l = Label(row);
            if (l != null) { Loc.Set(l, label); Loc.Fit(l, 0.6f); }
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
            if (ph != null) { Loc.Set(ph, placeholder); Loc.Fit(ph, 0.6f); ph.color = new Color(1f, 1f, 1f, 0.35f); ph.fontSize = 14; ph.alignment = TextAlignmentOptions.MidlineLeft; }
            return f;
        }

        private static void PasteButton(Transform holder, Transform back)
        {
            SmallButton(holder, back, "PASTE", () =>
            {
                string c = null;
                try { c = GUIUtility.systemCopyBuffer; } catch { }
                if (!string.IsNullOrWhiteSpace(c)) _addr.text = c.Trim();
            });
        }

        /// A small button (cloned from BACK) at the right end of a row's control area.
        internal static TMP_Text SmallButton(Transform holder, Transform back, string text, Action click)
        {
            if (back == null || holder == null) return null;
            var b = Clone(back.gameObject, holder);
            b.name = "Btn_" + text;
            Kill(Child(b.transform, "Icon"));
            const float k = 0.6f; // shrink by sizing (ButtonAnimation resets localScale to 1 on hover)
            var t = Label(b.transform);
            if (t != null)
            {
                Loc.Set(t, text);
                Stretch(RT(t.transform));
                t.alignment = TextAlignmentOptions.Center;
                t.fontSize = t.fontSize * k;
                t.enableWordWrapping = false;
                Loc.Fit(t, 0.5f);
            }
            var img = b.GetComponent<Image>();
            if (img != null) img.pixelsPerUnitMultiplier = img.pixelsPerUnitMultiplier / k;
            var rc = RT(Child(b.transform, "Raycast"));
            if (rc != null) rc.sizeDelta = rc.sizeDelta * k;
            var r = RT(b);
            Place(r, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, 0f), new Vector2(150f * k, 64f * k));
            OnClick(b, click);
            return t;
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

        private static Image[] _psSw; private static GameObject[] _psSel; private static TMP_Text _psLabel;

        private static void PauseRow(Transform box, Transform tRow, Transform back, Image ddImg, Transform outline)
        {
            var row = NewRow(box, tRow, "Multiplayer pause", false);
            _psLabel = Label(row);
            Swatches(Child(row, "Image"), back, ddImg, outline, new[] { FfOff, FfOn }, idx =>
            {
                if (Net.IsClient) return; // the host decides
                Plugin.SharedPause.Value = idx == 1;
                Refresh();
            }, out _psSw, out _psSel);
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
                // longer translations: shrink instead of spilling out of the box
                if (dd.captionText != null) { dd.captionText.enableWordWrapping = false; Loc.Fit(dd.captionText, 0.6f); }
                if (dd.itemText != null) { dd.itemText.enableWordWrapping = false; Loc.Fit(dd.itemText, 0.6f); }
                t.sizeDelta = new Vector2(t.sizeDelta.x, n * 28f + 8f);
                // the template's two corner pieces stick out above the list when it opens upwards
                // (they look like a half-hidden extra slot): hide them
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (c.name == "Image" || c.name == "Image.1") c.gameObject.SetActive(false);
                }
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
            var opts = new List<string>();
            for (int n = 2; n <= 8; n++) opts.Add(n.ToString());
            opts.Add("Unlimited");
            Loc.SetOptions(_max, opts);
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

        // connection type: Online (lobby code) / Direct IP
        private static void ConnRow(Transform box, Transform tRow)
        {
            var row = NewRow(box, tRow, "Connection", true);
            var dd = Find(row, "Dropdown");
            var comps = dd != null ? dd.GetComponents(Il2CppType.Of<Component>()) : null;
            if (comps != null)
                for (int i = 0; i < comps.Length && _conn == null; i++) _conn = comps[i]?.TryCast<TMP_Dropdown>();
            if (_conn == null) { Plugin.Log.LogWarning("[UI] connection: dropdown type not usable"); return; }
            var opts = new List<string> { "Online (lobby code)", "Steam (friends)", "Direct IP (port forward)" };
            Loc.SetOptions(_conn, opts);
            FitDropdown(_conn);
            _conn.SetValueWithoutNotify(ConnIndex());
            _conn.RefreshShownValue();
            _conn.onValueChanged = new TMP_Dropdown.DropdownEvent();
            _conn.onValueChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<int>>(new Action<int>(i =>
            {
                try
                {
                    if (Net.Active) { _conn.SetValueWithoutNotify(ConnIndex()); return; }
                    SaveFields(); // keep what was typed for the old mode
                    Plugin.Connection.Value = i == 1 ? "Steam" : i == 2 ? "Direct" : "Online";
                    _friendsAt = 0f;
                    LoadFields();
                    Refresh();
                }
                catch (Exception e) { Plugin.Log.LogWarning("[UI] connection change: " + e.Message); }
            })));
        }

        private static int ConnIndex() => NetSession.UseSteam ? 1 : NetSession.UseDirect ? 2 : 0;

        // ---------------------------------------------------------------- Steam friends (JOIN tab, Steam mode)

        private static TMP_Dropdown _friends;
        private static List<SteamApi.Host> _friendHosts = new List<SteamApi.Host>();
        private static float _friendsAt;
        private static string _friendsKey = "";

        private static void FriendRow(Transform box, Transform tRow, Transform back)
        {
            var row = NewRow(box, tRow, "Friend", true);
            var dd = Find(row, "Dropdown");
            var comps = dd != null ? dd.GetComponents(Il2CppType.Of<Component>()) : null;
            if (comps != null)
                for (int i = 0; i < comps.Length && _friends == null; i++) _friends = comps[i]?.TryCast<TMP_Dropdown>();
            if (_friends == null) { Plugin.Log.LogWarning("[UI] friends: dropdown type not usable"); return; }
            var drt = RT(dd);
            if (drt != null) { drt.anchoredPosition = new Vector2(-100f, drt.anchoredPosition.y); }
            _friends.onValueChanged = new TMP_Dropdown.DropdownEvent();
            _friends.onValueChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<int>>(new Action<int>(v =>
            {
                try { if (v >= 0 && v < _friendHosts.Count) Plugin.LastSteamHost.Value = _friendHosts[v].Id.ToString(); } catch { }
            })));
            SmallButton(Child(row, "Image"), back, "REFRESH", () => { _friendsAt = 0f; RefreshFriends(true); });
            RefreshFriends(true);
        }

        /// Friends who are hosting right now (Steam rich presence), re-read every few seconds while the JOIN tab shows.
        private static void RefreshFriends(bool force)
        {
            if (_friends == null) return;
            if (!force && Time.unscaledTime < _friendsAt) return;
            _friendsAt = Time.unscaledTime + 3f;
            try
            {
                var opts = new List<string>();
                if (!SteamApi.Available) { _friendHosts.Clear(); opts.Add(SteamApi.Error ?? "Steam isn't running"); }
                else
                {
                    _friendHosts = SteamApi.FriendHosts(true);
                    foreach (var h in _friendHosts)
                        opts.Add(h.Version == NetSession.ModVersion ? h.Name : Loc.F("{0} (mod v{1})", h.Name, h.Version));
                    if (opts.Count == 0) opts.Add("No friends hosting right now");
                }
                string key = string.Join("|", opts);
                if (key == _friendsKey) return;   // unchanged: don't reset the open list
                _friendsKey = key;
                Loc.SetOptions(_friends, opts);
                FitDropdown(_friends);
                int sel = 0;
                for (int i = 0; i < _friendHosts.Count; i++) if (_friendHosts[i].Id.ToString() == Plugin.LastSteamHost.Value) sel = i;
                if (_friendHosts.Count > 0) Plugin.LastSteamHost.Value = _friendHosts[sel].Id.ToString();
                _friends.SetValueWithoutNotify(sel);
                _friends.RefreshShownValue();
                _friends.interactable = _friendHosts.Count > 0;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[UI] friends: " + e.Message); }
        }

        /// Address row = lobby code in Online mode; the port row only matters for Direct.
        private static void ApplyMode()
        {
            bool online = NetSession.UseOnline;
            try
            {
                if (_conn != null && _conn.value != ConnIndex()) { _conn.SetValueWithoutNotify(ConnIndex()); _conn.RefreshShownValue(); }
                if (_addrLbl != null) Loc.Set(_addrLbl, online ? "Lobby code" : "Address");
                if (_addr != null)
                {
                    _addr.characterLimit = online ? 8 : 64;
                    var ph = _addr.placeholder != null ? _addr.placeholder.TryCast<TMP_Text>() : null;
                    if (ph != null) Loc.Set(ph, online ? "e.g. AX8SWK" : "e.g. 123.45.67.89");
                }
                ApplyRows();
                if (_info != null) _info.text = InfoText();
            }
            catch (Exception e) { Plugin.Log.LogWarning("[UI] mode: " + e.Message); }
        }

        // ---------------------------------------------------------------- SAVES tab

        private static TMP_Dropdown _svSlot, _svTarget, _svBackup;
        private static TMP_Text _svClearLbl, _svCopyLbl, _svRestoreLbl;
        private static int _svSlotIdx, _svTargetIdx = 1, _svBackupIdx;
        private static string _svMsg = "";
        private static List<FileInfo> _svBackups = new List<FileInfo>();
        private static int _confirm = -1; private static float _confirmUntil;

        /// A row with a dropdown and a small button to its right.
        private static TMP_Dropdown ActionDropdown(Transform box, Transform tRow, Transform back, string label, string button, Action click,
                                                   out TMP_Text buttonLabel, Action<int> changed)
        {
            buttonLabel = null;
            var row = NewRow(box, tRow, label, true);
            var holder = Child(row, "Image");
            var ddT = Find(row, "Dropdown");
            TMP_Dropdown dd = null;
            var comps = ddT != null ? ddT.GetComponents(Il2CppType.Of<Component>()) : null;
            if (comps != null) for (int i = 0; i < comps.Length && dd == null; i++) dd = comps[i]?.TryCast<TMP_Dropdown>();
            if (dd == null) { Plugin.Log.LogWarning("[UI] saves: dropdown type not usable"); return null; }
            var drt = RT(ddT);
            if (drt != null) { drt.anchoredPosition = new Vector2(-145f, drt.anchoredPosition.y); drt.sizeDelta = new Vector2(320f, drt.sizeDelta.y); } // leave room for the button on the right
            dd.onValueChanged = new TMP_Dropdown.DropdownEvent();
            dd.onValueChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<int>>(new Action<int>(v =>
            {
                try { _confirm = -1; changed(v); } catch (Exception e) { Plugin.Log.LogWarning("[UI] saves: " + e.Message); }
            })));
            buttonLabel = SmallButton(holder, back, button, click);
            return dd;
        }

        private static string SlotName(int slot) => slot <= 0 ? "Local save" : "Slot " + slot;

        /// Rebuild the three lists from the files on disk.
        private static void RefreshSaves()
        {
            try
            {
                if (_svSlot != null)
                {
                    var opts = new List<string>();
                    for (int i = 1; i <= SaveSlots.Count; i++) opts.Add(SaveSlots.Describe(i));
                    opts.Add(SaveSlots.Describe(0));
                    Loc.SetOptions(_svSlot, opts); FitDropdown(_svSlot);
                    _svSlot.SetValueWithoutNotify(_svSlotIdx); _svSlot.RefreshShownValue();
                }
                if (_svTarget != null)
                {
                    var opts = new List<string>();
                    for (int i = 1; i <= SaveSlots.Count; i++) opts.Add(SlotName(i) + (SaveSlots.Exists(i) ? " (overwrite)" : " (empty)"));
                    opts.Add(SlotName(0) + (SaveSlots.Exists(0) ? " (overwrite)" : " (empty)"));
                    Loc.SetOptions(_svTarget, opts); FitDropdown(_svTarget);
                    _svTarget.SetValueWithoutNotify(_svTargetIdx); _svTarget.RefreshShownValue();
                }
                if (_svBackup != null)
                {
                    _svBackups = SaveBackups.List(SlotOfIndex(_svSlotIdx));
                    var opts = new List<string>();
                    if (_svBackups.Count == 0) opts.Add("No backups yet");
                    foreach (var f in _svBackups.Take(12)) opts.Add(SaveBackups.Describe(f));
                    _svBackupIdx = Mathf.Clamp(_svBackupIdx, 0, Math.Max(0, Math.Min(12, _svBackups.Count) - 1));
                    Loc.SetOptions(_svBackup, opts); FitDropdown(_svBackup);
                    _svBackup.SetValueWithoutNotify(_svBackupIdx); _svBackup.RefreshShownValue();
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[UI] saves refresh: " + e.Message); }
        }

        /// 0 = clear, 1 = copy, 2 = restore. First click arms it ("SURE?"), the second does it.
        private static void SaveAction(int which)
        {
            if (Net.Active) { _svMsg = "Leave the session first"; return; }
            if (_confirm != which || Time.unscaledTime > _confirmUntil)
            {
                _confirm = which; _confirmUntil = Time.unscaledTime + 3f;
                SetConfirmLabels();
                return;
            }
            _confirm = -1; SetConfirmLabels();
            int slot = SlotOfIndex(_svSlotIdx);
            try
            {
                switch (which)
                {
                    case 0:
                        if (!SaveSlots.Exists(slot)) { _svMsg = SlotName(slot) + " is already empty"; break; }
                        SaveBackups.BackupNow(slot, "before clearing");
                        SaveSlots.Delete(slot);
                        _svMsg = SlotName(slot) + " cleared (a backup was kept)";
                        break;
                    case 1:
                        int to = SlotOfIndex(_svTargetIdx);
                        if (to == slot) { _svMsg = "Pick a different slot to copy to"; break; }
                        if (!SaveSlots.Exists(slot)) { _svMsg = SlotName(slot) + " is empty - nothing to copy"; break; }
                        if (SaveSlots.Exists(to)) SaveBackups.BackupNow(to, "before overwriting");
                        SaveSlots.Copy(slot, to);
                        _svMsg = $"Copied {SlotName(slot)} to {SlotName(to)}";
                        break;
                    case 2:
                        if (_svBackupIdx >= _svBackups.Count) { _svMsg = "No backup to restore"; break; }
                        var f = _svBackups[_svBackupIdx];
                        if (SaveSlots.Exists(slot)) SaveBackups.BackupNow(slot, "before restoring");
                        File.Copy(f.FullName, SaveSlots.PathFor(slot), true);
                        _svMsg = $"Restored {SlotName(slot)} from {SaveBackups.Describe(f)}";
                        break;
                }
                Plugin.Log.LogInfo("[saves] " + _svMsg);
            }
            catch (Exception e) { _svMsg = "Failed: " + e.Message; Plugin.Log.LogWarning("[saves] " + e); }
            // the game keeps the normal save in memory and the menu's CONTINUE button depends on it
            try { SaveSlots.ResetToLocal(true); var m = Menu.Instance; if (m != null && m.continueGameButton != null) m.continueGameButton.SetActive(SaveSlots.Exists(0)); } catch { }
            RefreshSaves();
            RefreshSlots();
        }

        private static void TickConfirm()
        {
            if (_confirm >= 0 && Time.unscaledTime > _confirmUntil) { _confirm = -1; SetConfirmLabels(); }
        }

        private static void SetConfirmLabels()
        {
            if (_svClearLbl != null) Loc.Set(_svClearLbl, _confirm == 0 ? "SURE?" : "CLEAR");
            if (_svCopyLbl != null) Loc.Set(_svCopyLbl, _confirm == 1 ? "SURE?" : "COPY");
            if (_svRestoreLbl != null) Loc.Set(_svRestoreLbl, _confirm == 2 ? "SURE?" : "RESTORE");
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
                var opts = new List<string>();
                for (int i = 1; i <= SaveSlots.Count; i++) opts.Add(SaveSlots.Describe(i));
                opts.Add(SaveSlots.Describe(0));
                Loc.SetOptions(_slot, opts);
                FitDropdown(_slot);
                _slot.SetValueWithoutNotify(IndexOfSlot(Plugin.HostSlot.Value));
                _slot.RefreshShownValue();
            }
            catch (Exception e) { Plugin.Log.LogWarning("[UI] save slots: " + e.Message); }
        }

        internal static TMP_Text TextRow(Transform box, Transform tRow, string label, bool wrap = false)
        {
            var row = NewRow(box, tRow, label, false);
            if (wrap) { var rr = RT(row); rr.sizeDelta = new Vector2(rr.sizeDelta.x, Mathf.Max(rr.sizeDelta.y, 48f)); }
            var l = Label(row);
            var v = Clone(l.gameObject, row).GetComponent<TMP_Text>();
            v.gameObject.name = "Value";
            Place(RT(v.transform), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(420f, wrap ? 44f : 24f));
            v.alignment = TextAlignmentOptions.MidlineRight;
            v.fontSize = 14; v.enableWordWrapping = wrap; v.overflowMode = TextOverflowModes.Ellipsis;
            v.enableAutoSizing = true; v.fontSizeMin = wrap ? 10 : 9; v.fontSizeMax = 14;
            v.text = "";
            return v;
        }

        private static string _infoPort;
        private static string InfoText()
        {
            string port = _port != null && !string.IsNullOrWhiteSpace(_port.text) ? _port.text.Trim() : Plugin.Port.Value.ToString();
            _infoPort = InfoKey(port);
            if (_tab == 2)
                return Loc.T("Multiplayer saves (and your normal save). Backups are made automatically while you host, and before anything here deletes or overwrites a save - so a mistake can always be undone with RESTORE. Click a button twice to confirm.");
            bool online = NetSession.UseOnline;
            bool steam = NetSession.UseSteam;
            string pauseInfo = "\n" + Loc.T("Multiplayer pause: green = pausing pauses everyone. Red = Esc only opens your own menu and the game keeps running, so nobody can pause the day (like Risk of Rain 2).");
            if (_tab == 0 && steam)
                return Loc.T("Press HOST LOBBY - your save loads and your Steam friends see you in their JOIN tab (Connection: Steam). Everyone needs the game on Steam. Press START GAME in the lobby when everyone's in.") + pauseInfo;
            if (steam)
                return Loc.T("Pick a Steam friend who is hosting and press JOIN LOBBY. They show up here while they host with Connection: Steam. You'll play on the host's save - your own save is never touched.");
            if (_tab == 0)
                return (online
                    ? Loc.T("Press HOST LOBBY - your save loads and you get a lobby code (copied to your clipboard). Send it to your friends - no port forwarding needed. Press START GAME in the lobby when everyone's in.")
                    : Loc.F("Press HOST LOBBY - your save loads and the session opens. Forward UDP port {0} on your router and give friends your public IP.", port)) + pauseInfo;
            return online
                ? Loc.T("Paste the host's lobby code and press JOIN LOBBY. You'll play on the host's save - your own save is never touched.")
                : Loc.T("Type the host's address (and port) and press JOIN LOBBY. You'll play on the host's save - your own save is never touched.");
        }

        private static string InfoKey(string port) => port + "|" + Plugin.Connection.Value + "|" + _tab + "|" + Loc.Lang;

        private static TMP_Text InfoRow(Transform box, Transform tRow, string text)
        {
            var row = NewRow(box, tRow, "Info", false);
            Kill(Child(row, "Image"));
            var rr = RT(row); rr.sizeDelta = new Vector2(rr.sizeDelta.x, 96f);
            var l = Label(row);
            Loc.Unbind(l);
            Stretch(RT(l.transform));
            l.text = text; l.fontSize = 13; l.enableWordWrapping = true;
            l.enableAutoSizing = true; l.fontSizeMax = 13; l.fontSizeMin = 9; l.overflowMode = TextOverflowModes.Truncate;
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
            ApplyMode();
            RefreshSlots();
        }

        private static void SaveFields()
        {
            try { MpManager.Instance?.ApplyFields(_name.text, _addr.text, _port.text); } catch { }
        }

        /// HOST / JOIN tabs: show that tab's options; the bottom-right button does the action.
        private static void SelectTab(int tab) => SelectTab(tab, false);
        private static void SelectTab(int tab, bool force)
        {
            if (Net.Active && !force) return; // can't switch while hosting / joined
            if (!force) SaveFields();
            _tab = tab;
            if (tab == 2) RefreshSaves();
            ApplyRows();
            if (_info != null) _info.text = InfoText();
            if (!force) Refresh();
        }

        private static void ApplyRows()
        {
            foreach (var (line, row, show) in _rows)
            {
                bool on = show();
                if (line != null && line.activeSelf != on) line.SetActive(on);
                if (row != null && row.activeSelf != on) row.SetActive(on);
            }
            // the selected tab looks normal, the other one is dimmed
            TintTab(_hostBtn, _tab == 0);
            TintTab(_joinBtn, _tab == 1);
            TintTab(_savesBtn, _tab == 2);
            if (_actBtn != null && _actBtn.activeSelf == (_tab == 2)) _actBtn.SetActive(_tab != 2);
            var ic = _actBtn != null ? Child(_actBtn.transform, "Icon") : null;
            var tabBtn = _tab == 0 ? _hostBtn : _tab == 1 ? _joinBtn : _hostBtn;
            var src = tabBtn != null ? Child(tabBtn.transform, "Icon") : null;
            if (ic != null && src != null) ic.GetComponent<Image>().sprite = src.GetComponent<Image>().sprite;
            FitInfo();
        }

        private static RectTransform _boxRt;
        private static VerticalLayoutGroup _boxLg;

        /// The info text gets whatever height is left in the panel, stopping above the HOST LOBBY / JOIN LOBBY
        /// button (it sits over the panel's bottom-right corner); the text shrinks if it needs more room.
        private static void FitInfo()
        {
            try
            {
                if (_info == null || _boxRt == null || _boxRt.rect.height < 100f) return;
                var infoRow = RT(_info.transform.parent);
                if (infoRow == null) return;
                float top = _boxLg != null ? _boxLg.padding.top : 0f;
                float bottom = _boxLg != null ? _boxLg.padding.bottom : 0f;
                float spacing = _boxLg != null ? _boxLg.spacing : 0f;
                if (_actBtn != null && _actBtn.activeSelf)
                {
                    var ab = RT(_actBtn);
                    float btnTop = ab != null ? ab.anchoredPosition.y + ab.sizeDelta.y : 89f;   // above the screen's bottom edge
                    float boxBottom = _boxRt.offsetMin.y > 0f ? _boxRt.offsetMin.y : 25f;
                    bottom = Mathf.Max(bottom, btnTop - boxBottom + 6f);
                }
                float used = 0f; int n = 0;
                var box = _boxRt.transform;
                for (int i = 0; i < box.childCount; i++)
                {
                    var c = box.GetChild(i);
                    if (!c.gameObject.activeSelf) continue;
                    n++;
                    if (c == infoRow.transform) continue;
                    var crt = RT(c);
                    if (crt != null) used += crt.sizeDelta.y;
                }
                float h = _boxRt.rect.height - top - bottom - used - spacing * Mathf.Max(0, n - 1);
                h = Mathf.Clamp(h, 36f, 200f);
                if (Mathf.Abs(infoRow.sizeDelta.y - h) > 0.5f) infoRow.sizeDelta = new Vector2(infoRow.sizeDelta.x, h);
                _info.fontSizeMin = 8f;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[UI] info size: " + e.Message); }
        }

        private static void TintTab(GameObject b, bool selected)
        {
            var img = b != null ? b.GetComponent<Image>() : null;
            if (img != null) img.color = selected ? Color.white : new Color(0.62f, 0.62f, 0.62f, 1f);
        }

        private static void OnActionClicked()
        {
            if (Net.Active) { SaveFields(); MpManager.Instance?.Disconnect(); return; }
            if (_tab == 0) OnHostClicked(); else if (_tab == 1) OnJoinClicked();
        }

        private static void OnHostClicked()
        {
            SaveFields();
            if (Net.Active) { MpManager.Instance?.Disconnect(); return; }
            if (NetSession.UseSteam && !SteamApi.Available) { Net.Status = SteamApi.Error ?? "Steam isn't running"; return; }
            if (!GameApi.InGame)
            {
                if (!SaveSlots.RedirectWorks(Plugin.HostSlot.Value))
                {
                    Net.Status = "Multiplayer save slots aren't working - pick Local save (see log)";
                    return;
                }
                SaveSlots.Use(Plugin.HostSlot.Value); // the lobby hosts this save
                if (!Net.StartHost()) return; // Status says why (port in use...)
                MultiplayerSession = true;
                MpManager.Instance?.Note(Net.Online ? "Lobby open - getting a lobby code..." : $"Lobby open on UDP {Plugin.Port.Value}. Friends join with your public IP.");
                _screen.SetActive(false);
                LobbyUi.Show();
                return;
            }
            if (GameApi.InGame)
            {
                if (Net.StartHost()) MpManager.Instance?.Note(Net.HostingText);
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
            if (Net.StartHost()) MpManager.Instance?.Note(Net.HostingText);
        }

        private static void OnJoinClicked()
        {
            SaveFields();
            if (Net.Active) return;
            if (NetSession.UseSteam)
            {
                if (!SteamApi.Available) { Net.Status = SteamApi.Error ?? "Steam isn't running"; return; }
                if (_friendHosts.Count == 0) { Net.Status = "No friends hosting right now"; return; }
            }
            else if (NetSession.UseOnline)
            {
                if (OnlineLink.NormalizeCode(Plugin.LastCode.Value) == null) { Net.Status = "Type the host's 6-character lobby code first"; return; }
            }
            else if (string.IsNullOrWhiteSpace(Plugin.LastAddress.Value)) { Net.Status = "Type the host's address first"; return; }
            MultiplayerSession = true;
            if (Net.StartJoin())
            {
                _screen.SetActive(false);
                LobbyUi.Show();
            }
        }

        /// Steam "Join Game" (friends list / profile): join that friend through Steam straight from the main menu.
        public static void JoinSteamFriend(ulong hostId)
        {
            try
            {
                Plugin.Connection.Value = "Steam";
                Plugin.LastSteamHost.Value = hostId.ToString();
                MultiplayerSession = true;
                if (Net.StartJoin())
                {
                    if (_screen != null) _screen.SetActive(false);
                    LobbyUi.Show();
                }
                else
                {
                    MultiplayerSession = false;
                    MpManager.Instance?.Toast(Loc.T(Net.Status));
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[Steam] join: " + e); }
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
                if (active && _tab != (Net.IsHost ? 0 : 1)) SelectTab(Net.IsHost ? 0 : 1, true);
                if (_actLbl != null)
                {
                    string want = Loc.T(active ? (Net.IsHost ? "STOP HOSTING" : "DISCONNECT") : _tab == 0 ? "HOST LOBBY" : "JOIN LOBBY");
                    if (_actLbl.text != want) _actLbl.text = want;
                }

                if (_info != null && _port != null && InfoKey((_port.text ?? "").Trim()) != _infoPort) _info.text = InfoText();
                if (_tab == 1 && NetSession.UseSteam && !active) RefreshFriends(false);
                FitInfo();
                if (_conn != null && _conn.interactable == active) _conn.interactable = !active;
                if (_status != null) { var st = Loc.T(_tab == 2 ? _svMsg : _hostArmed ? "Loading your save to host..." : (Net.Status ?? "")); if (_status.text != st) _status.text = st; }
                if (_tab == 2) TickConfirm();
                if (_players != null)
                {
                    if (!active) _players.text = "-";
                    else
                    {
                        var names = new List<string>();
                        foreach (var p in Net.Players.Values) names.Add(p.Id == Net.LocalId ? p.Name + " " + Loc.T("(you)") : p.Name);
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
                    if (_ffLabel != null) { var fl = Loc.T(client ? "Friendly fire (host decides)" : "Friendly fire"); if (_ffLabel.text != fl) _ffLabel.text = fl; }
                }
                if (_psSw != null)
                {
                    bool client = Net.IsClient;
                    int sel = (client ? CoopSync.SharedPause : Plugin.SharedPause.Value) ? 1 : 0;
                    var cols = new[] { FfOff, FfOn };
                    for (int i = 0; i < _psSw.Length; i++)
                    {
                        var c = cols[i]; c.a = client && i != sel ? 0.25f : 1f;
                        if (_psSw[i] != null) _psSw[i].color = c;
                        if (_psSel[i] != null && _psSel[i].activeSelf != (i == sel)) _psSel[i].SetActive(i == sel);
                    }
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
