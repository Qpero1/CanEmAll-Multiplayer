#nullable disable
// Emote wheel (hold Q): point at a dance and let go. Other players see your miner dance until you move,
// attack, jump, switch weapon, or pick "Stop". You stay in first person, so a line on screen shows it's on.

using System;
using CanEmAllMP;
using UnityEngine;
using UnityEngine.InputSystem;

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
            try
            {
                if (Open) DrawWheel();
                else if (Gestures.Emote > 0 && Gestures.Emote <= Names.Length)
                    Panel(new Rect(Screen.width / 2f - 150, Screen.height - 120, 300, 28),
                        $"<b>{Names[Gestures.Emote - 1]}</b>  (move to stop)");
            }
            catch (Exception e) { if (_err++ < 3) ModelsPlugin.L.LogWarning("emote wheel: " + e.Message); }
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
