#nullable disable
// Emote wheel (hold Q): point at a dance and let go. Other players see your miner dance until you move,
// attack, jump, switch weapon, or pick "Stop". You stay in first person, so a line on screen shows it's on.

using System;
using CanEmAllMP;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace CanEmAllMPModels
{
    public class EmoteWheel : MonoBehaviour
    {
        public EmoteWheel(IntPtr ptr) : base(ptr) { }

        public static bool Open;

        private static readonly string[] Names = { "Dance 1", "Dance 2", "Dance 3", "Dance 4", "Dance 5" };
        private const float Radius = 150f, DeadZone = 45f;

        private static int _hover = -1;
        private static CursorLockMode _prevLock;
        private static bool _prevVisible;
        private static Vector2 _lookAccum;          // used if the cursor can't be warped to the centre
        private static Vector3 _startPos;
        private static int _startWeapon, _startAttacks;
        private static int _err;

        public void Update()
        {
            try { Tick(); }
            catch (Exception e) { if (_err++ < 3) ModelsPlugin.L.LogWarning("emote wheel: " + e.Message); }
            try { UpdateUi(); }
            catch (Exception e) { if (_err++ < 3) ModelsPlugin.L.LogWarning("emote wheel ui: " + e.Message); _uiFailed = true; HideUi(); }
        }

        private static void Tick()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null) return;
            var key = kb[ModelsPlugin.EmoteKey.Value];

            if (!Open)
            {
                if (key.wasPressedThisFrame && GameApi.CanAct && !TuneMenu.Open)
                    OpenWheel();
            }
            else
            {
                if (!GameApi.InGame || GameApi.IsPaused)
                {
                    // the game opened a menu over the wheel: just go away, the menu owns the cursor now
                    Open = false;
                    return;
                }
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                _hover = HoverIndex(mouse);
                bool click = mouse != null && mouse.leftButton.wasPressedThisFrame;
                if (key.wasReleasedThisFrame || click || !GameApi.InGame) Choose(_hover);
                else if (kb[Key.Escape].wasPressedThisFrame) CloseWheel();
            }

            // a dance stops as soon as you do something else
            if (Gestures.Emote > 0 && !Open)
            {
                bool stop = !GameApi.InGame;
                if (!stop && GameApi.TryGetLocalPose(out var pos, out _, out _))
                {
                    var d = pos - _startPos;
                    if (new Vector2(d.x, d.z).magnitude > 0.35f || Mathf.Abs(d.y) > 0.4f) stop = true; // walked / jumped
                }
                if (GameApi.CurrentWeaponGlobalIndex != _startWeapon) stop = true;
                if (Gestures.AttackCount != _startAttacks) stop = true;
                if (stop) Gestures.Emote = 0;
            }
        }

        private static void OpenWheel()
        {
            Open = true;
            _hover = -1;
            _lookAccum = Vector2.zero;
            _prevLock = Cursor.lockState; _prevVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            try { Mouse.current?.WarpCursorPosition(new Vector2(Screen.width / 2f, Screen.height / 2f)); } catch { }
        }

        private static void CloseWheel()
        {
            Open = false;
            Cursor.lockState = _prevLock;
            Cursor.visible = _prevVisible;
        }

        /// index = dance (0..4), Names.Length = the "Stop" slot at the bottom, -1 = middle (nothing)
        private static int HoverIndex(Mouse mouse)
        {
            if (mouse == null) return -1;
            var centre = new Vector2(Screen.width / 2f, Screen.height / 2f);
            var d = mouse.position.ReadValue() - centre;
            if (d.magnitude < DeadZone)
            {
                // cursor warp unsupported: fall back to mouse movement
                _lookAccum += mouse.delta.ReadValue();
                if (_lookAccum.magnitude > 400f) _lookAccum = _lookAccum.normalized * 400f;
                d = _lookAccum;
                if (d.magnitude < DeadZone) return -1;
            }
            // screen y is up; wheel slots go clockwise from the top
            float ang = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;   // 0 = up, 90 = right
            if (ang < 0) ang += 360f;
            int slots = Names.Length + 1;
            float step = 360f / slots;
            return Mathf.RoundToInt(ang / step) % slots;
        }

        private static void Choose(int index)
        {
            CloseWheel();
            if (index < 0) return;                         // let go in the middle: nothing changes
            if (index >= Names.Length) { Gestures.Emote = 0; return; } // "Stop"
            Gestures.Emote = index + 1;
            GameApi.TryGetLocalPose(out _startPos, out _, out _);
            _startWeapon = GameApi.CurrentWeaponGlobalIndex;
            _startAttacks = Gestures.AttackCount;
        }

        public void OnGUI()
        {
            if (_ui != null && !_uiFailed) return; // the game-style wheel is showing
            try
            {
                if (Open) DrawWheel();
                else if (Gestures.Emote > 0 && Gestures.Emote <= Names.Length)
                    Panel(new Rect(Screen.width / 2f - 150, Screen.height - 120, 300, 28),
                        $"<b>{Loc.T(Names[Gestures.Emote - 1])}</b>  " + Loc.T("(move to stop)"));
            }
            catch (Exception e) { if (_err++ < 3) ModelsPlugin.L.LogWarning("emote wheel: " + e.Message); }
        }

        // ------------------------------------------------------------------ game-style wheel (uGUI)
        // Same look as the game's menus: dark green rounded tiles with white text (orange when picked, like the
        // game's dropdown highlight), a cream centre with dark text, the game's own font and sprites.

        private static readonly Color Green = new Color(0.035f, 0.216f, 0.125f, 0.94f);
        private static readonly Color Orange = new Color(0.996f, 0.608f, 0.216f, 1f);
        private static readonly Color Cream = new Color(0.969f, 0.859f, 0.749f, 0.96f);
        private static readonly Color Ink = new Color(0.059f, 0.098f, 0.165f, 1f);
        private const float UiRadius = 230f;

        private static GameObject _ui, _wheel, _status;
        private static Image[] _tiles;
        private static TMP_Text[] _tileText;
        private static TMP_Text _centreText;
        private static int _tileLang = -1;

        /// Wheel labels in the game's language (redone if the language is changed).
        private static void RelabelTiles()
        {
            _tileLang = Loc.Lang;
            if (_centreText != null) _centreText.text = Loc.T("EMOTES");
            if (_tileText == null) return;
            for (int i = 0; i < _tileText.Length; i++)
                if (_tileText[i] != null) _tileText[i].text = i < Names.Length ? Loc.T(Names[i]).ToUpperInvariant() : Loc.T("STOP");
        }
        private static TMP_Text _statusText, _hint;
        private static bool _uiFailed;
        private static float _nextTry;
        private static TMP_FontAsset _font, _fontBold;
        private static Sprite _round;
        private static float _roundPpu = 1f;

        private static void HideUi() { try { if (_ui != null) _ui.SetActive(false); } catch { } }

        /// The game-style UI is up (other overlays skip their old IMGUI hints).
        public static bool StyledUi => _ui != null && !_uiFailed;

        private static void UpdateUi()
        {
            bool dancing = !Open && Gestures.Emote > 0 && Gestures.Emote <= Names.Length;
            if (!Open && !dancing) { HideUi(); return; }
            if (_uiFailed) return;
            if (_ui == null)
            {
                if (Time.unscaledTime < _nextTry) return;
                _nextTry = Time.unscaledTime + 2f;
                if (!FindAssets()) return;          // not in game yet: the IMGUI fallback draws meanwhile
                Build();
            }
            if (!_ui.activeSelf) _ui.SetActive(true);
            if (_wheel.activeSelf != Open) _wheel.SetActive(Open);
            if (_status.activeSelf != dancing) _status.SetActive(dancing);
            if (dancing)
            {
                bool cam = SelfView.Active;
                _statusText.text = Loc.T(Names[Gestures.Emote - 1]).ToUpperInvariant() + "  <size=70%>" + Loc.T("MOVE TO STOP") + "</size>"
                                 + (cam ? "\n<size=62%><font-weight=400>" + Loc.T("MOUSE: LOOK AROUND   -   SCROLL: ZOOM") + "</font-weight></size>" : "");
                var srt = _status.GetComponent<RectTransform>();
                var want = new Vector2(cam ? 420 : 330, cam ? 70 : 46);
                if (srt.sizeDelta != want) srt.sizeDelta = want;
            }
            if (Open)
            {
                for (int i = 0; i < _tiles.Length; i++)
                {
                    bool hot = i == _hover;
                    _tiles[i].color = hot ? Orange : Green;
                    _tiles[i].rectTransform.localScale = Vector3.one * (hot ? 1.08f : 1f);
                }
                _hint.text = Loc.F("Point at one and let go of {0}   -   Esc to cancel", ModelsPlugin.EmoteKey.Value);
                if (_tileLang != Loc.Lang) RelabelTiles();
            }
        }

        private static bool FindAssets()
        {
            if (_font == null)
            {
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_FontAsset>()))
                {
                    var f = o?.TryCast<TMP_FontAsset>();
                    if (f == null) continue;
                    string n = f.name.ToLowerInvariant();
                    if (n.Contains("cnextrabold")) _fontBold = f;
                    else if (n.Contains("cnbold")) _font = f;
                }
                if (_font == null) _font = _fontBold;
                if (_fontBold == null) _fontBold = _font;
            }
            if (_round == null)
            {
                // take the rounded sprite (and its corner size) from one of the game's own dropdowns
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Image>()))
                {
                    var img = o?.TryCast<Image>();
                    if (img == null || img.sprite == null || img.sprite.name != "roundquad") continue;
                    _round = img.sprite; _roundPpu = img.pixelsPerUnitMultiplier;
                    break;
                }
            }
            return _font != null && _round != null;
        }

        private static RectTransform Rt(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            return rt != null ? rt : go.AddComponent<RectTransform>();
        }

        private static GameObject Box(Transform parent, string name, Vector2 pos, Vector2 size, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = Rt(go);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var img = go.AddComponent<Image>();
            img.sprite = _round; img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = _roundPpu;
            img.color = color; img.raycastTarget = false;
            return go;
        }

        private static TMP_Text Text(Transform parent, string text, float size, Color color, bool bold)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            var rt = Rt(go);
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(10, 0); rt.offsetMax = new Vector2(-10, 0);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.font = bold ? _fontBold : _font;
            t.text = text; t.fontSize = size; t.color = color;
            t.alignment = TextAlignmentOptions.Center; t.enableWordWrapping = false; t.richText = true; t.raycastTarget = false;
            t.enableAutoSizing = true; t.fontSizeMax = size; t.fontSizeMin = size * 0.55f; // longer translations shrink to fit
            return t;
        }

        private static void Build()
        {
            _ui = new GameObject("MP_EmoteWheel");
            UnityEngine.Object.DontDestroyOnLoad(_ui);
            var canvas = _ui.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 400;
            var scaler = _ui.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            var root = _ui.transform;

            _wheel = new GameObject("Wheel");
            _wheel.transform.SetParent(root, false);
            var wrt = Rt(_wheel); wrt.anchorMin = Vector2.zero; wrt.anchorMax = Vector2.one; wrt.offsetMin = wrt.offsetMax = Vector2.zero;

            var centre = Box(_wheel.transform, "Centre", Vector2.zero, new Vector2(170, 64), Cream);
            _centreText = Text(centre.transform, "EMOTES", 26, Ink, true);

            int slots = Names.Length + 1;
            float step = 360f / slots;
            _tiles = new Image[slots]; _tileText = new TMP_Text[slots];
            for (int i = 0; i < slots; i++)
            {
                float a = i * step * Mathf.Deg2Rad;              // 0 = top, clockwise (same as HoverIndex)
                var pos = new Vector2(Mathf.Sin(a) * UiRadius, Mathf.Cos(a) * UiRadius);
                var tile = Box(_wheel.transform, "Slot" + i, pos, new Vector2(190, 60), Green);
                _tiles[i] = tile.GetComponent<Image>();
                _tileText[i] = Text(tile.transform, "", 22, Color.white, true);
            }
            RelabelTiles();
            var hint = Box(_wheel.transform, "Hint", new Vector2(0, -UiRadius - 85), new Vector2(560, 40), Cream);
            _hint = Text(hint.transform, "", 17, Ink, false);

            _status = Box(root, "Dancing", Vector2.zero, new Vector2(330, 46), Green);
            var srt = _status.GetComponent<RectTransform>();
            srt.anchorMin = srt.anchorMax = new Vector2(0.5f, 0f); srt.anchoredPosition = new Vector2(0, 130);
            _statusText = Text(_status.transform, "", 20, Color.white, true);

            _wheel.SetActive(false); _status.SetActive(false);
            ModelsPlugin.L.LogInfo("emote wheel: game-style UI built");
        }

        private static GUIStyle _centered;
        private static GUIStyle Centered()
        {
            if (_centered == null)
            {
                _centered = new GUIStyle { alignment = TextAnchor.MiddleCenter, richText = true, fontSize = 16 };
                _centered.normal.textColor = Color.white;
            }
            return _centered;
        }

        private static void Panel(Rect r, string text)
        {
            GUI.Box(r, "");
            GUI.Label(r, text, Centered());
        }

        private static void DrawWheel()
        {
            float cx = Screen.width / 2f, cy = Screen.height / 2f;
            int slots = Names.Length + 1;
            float step = 360f / slots;
            Panel(new Rect(cx - 70, cy - 18, 140, 36), "Emotes");
            for (int i = 0; i < slots; i++)
            {
                float a = i * step * Mathf.Deg2Rad;              // 0 = top, clockwise
                float x = cx + Mathf.Sin(a) * Radius, y = cy - Mathf.Cos(a) * Radius;
                string label = i < Names.Length ? Names[i] : "Stop";
                bool hot = i == _hover;
                var r = hot ? new Rect(x - 70, y - 24, 140, 48) : new Rect(x - 60, y - 20, 120, 40);
                if (hot) GUI.Box(r, ""); // doubled box = darker highlight
                Panel(r, hot ? $"<b><color=#ffd34d>{label}</color></b>" : label);
            }
            Panel(new Rect(cx - 200, cy + Radius + 40, 400, 28),
                $"Point at one and let go of {ModelsPlugin.EmoteKey.Value}  (Esc to cancel)");
        }
    }
}
