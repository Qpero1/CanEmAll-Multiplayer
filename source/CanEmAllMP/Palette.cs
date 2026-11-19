using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace CanEmAllMP
{
    /// Player colours (picked in the F8 menu) and the matching cursor images (embedded in the DLL).
    public static class Palette
    {
        public static readonly string[] Names = { "Blue", "Purple", "Yellow", "Green", "Red", "Orange", "Pink", "Cyan" };
        public static readonly Color[] Colors =
        {
            new Color(0.15f, 0.55f, 1f), new Color(0.6f, 0.2f, 1f), new Color(1f, 0.82f, 0.1f), new Color(0.45f, 0.85f, 0.1f),
            new Color(1f, 0.22f, 0.22f), new Color(1f, 0.55f, 0.1f), new Color(1f, 0.42f, 0.75f), new Color(0.15f, 0.85f, 0.85f),
        };
        public static int Count => Colors.Length;

        public static int Clamp(int i) => ((i % Count) + Count) % Count;
        public static Color Of(int index) => Colors[Clamp(index)];

        public static int IndexOfPlayer(int id)
        {
            var net = MpManager.Net;
            if (net != null && net.Players.TryGetValue(id, out var p) && p.Color >= 0) return Clamp(p.Color);
            return Clamp(id);
        }
        public static Color ForPlayer(int id) => Of(IndexOfPlayer(id));

        // ------------------------------------------------ cursor images
        private static Texture2D[] _tex;
        private static Vector2[] _hot;
        private static bool _loaded;

        private static void Load()
        {
            _loaded = true;
            _tex = new Texture2D[Count]; _hot = new Vector2[Count];
            for (int i = 0; i < Count; i++) _tex[i] = LoadTex("cursor" + i, out _hot[i]);
        }

        private static Texture2D LoadTex(string name, out Vector2 hot)
        {
            hot = Vector2.zero;
            try
            {
                using var s = typeof(Palette).Assembly.GetManifestResourceStream(name);
                if (s == null) return null;
                using var gz = new GZipStream(s, CompressionMode.Decompress);
                using var ms = new MemoryStream();
                gz.CopyTo(ms);
                var b = ms.ToArray();
                int w = BitConverter.ToInt32(b, 0), h = BitConverter.ToInt32(b, 4);
                hot = new Vector2(BitConverter.ToInt32(b, 8), BitConverter.ToInt32(b, 12));
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                var px = new Color32[w * h];
                for (int k = 0; k < px.Length; k++) { int o = 16 + k * 4; px[k] = new Color32(b[o], b[o + 1], b[o + 2], b[o + 3]); }
                tex.SetPixels32(px);
                tex.Apply(false, false);
                tex.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(tex);
                return tex;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[MP] cursor {name}: {e.Message}"); return null; }
        }

        // ------------------------------------------------ our own mouse pointer in our colour
        private static Texture2D[] _sys;
        private static Vector2[] _sysHot;
        private static int _sysApplied = -1;
        private static float _sysAt;

        /// Keep the system mouse pointer as our coloured cursor (re-applied now and then in case the game resets it).
        public static void TickSystemCursor(int colorIndex)
        {
            int i = Clamp(colorIndex);
            if (i == _sysApplied && Time.unscaledTime - _sysAt < 3f) return;
            // the images can be lost when the scene reloads (e.g. sent back to the menu after a kick): load them again
            bool lost = _sys != null && _sys[i] == null;
            if (_sys == null || lost)
            {
                _sys = new Texture2D[Count]; _sysHot = new Vector2[Count];
                for (int k = 0; k < Count; k++) _sys[k] = LoadTex("cursorS" + k, out _sysHot[k]);
            }
            _sysApplied = i; _sysAt = Time.unscaledTime;
            if (_sys[i] == null) return;
            try { Cursor.SetCursor(_sys[i], _sysHot[i], CursorMode.Auto); }
            catch (Exception e) { Plugin.Log.LogWarning("[MP] SetCursor: " + e.Message); _sysAt = float.MaxValue / 2; }
        }

        /// Re-apply the coloured cursor on the next tick (after a disconnect / scene change the game may have reset it).
        public static void RefreshCursor() { _sysApplied = -1; }

        /// Draw a player's cursor with its tip at (gx, gy) in GUI coordinates. Returns the drawn width.
        public static float DrawCursor(int colorIndex, float gx, float gy, float height = 34f)
        {
            if (!_loaded) Load();
            int i = Clamp(colorIndex);
            var t = _tex[i];
            if (t == null)
            {
                GUI.color = Color.black; GUI.DrawTexture(new Rect(gx - 1, gy - 1, 14, 14), Texture2D.whiteTexture);
                GUI.color = Of(i); GUI.DrawTexture(new Rect(gx, gy, 12, 12), Texture2D.whiteTexture);
                GUI.color = Color.white;
                return 14;
            }
            float s = height / t.height;
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(gx - _hot[i].x * s, gy - _hot[i].y * s, t.width * s, height), t);
            return t.width * s;
        }

        /// Colour swatch row; returns the clicked index or -1. "taken" swatches are dimmed and unclickable.
        public static int Swatches(Rect r, int selected, Func<int, bool> taken)
        {
            int clicked = -1;
            float size = Mathf.Min(r.height, (r.width - (Count - 1) * 6) / Count);
            for (int i = 0; i < Count; i++)
            {
                var sr = new Rect(r.x + i * (size + 6), r.y, size, size);
                bool isTaken = taken != null && taken(i) && i != selected;
                if (i == selected) { GUI.color = Color.white; GUI.DrawTexture(new Rect(sr.x - 3, sr.y - 3, sr.width + 6, sr.height + 6), Texture2D.whiteTexture); }
                GUI.color = Color.black; GUI.DrawTexture(new Rect(sr.x - 1, sr.y - 1, sr.width + 2, sr.height + 2), Texture2D.whiteTexture);
                var c = Colors[i]; if (isTaken) c = Color.Lerp(c, Color.gray, 0.75f);
                GUI.color = c; GUI.DrawTexture(sr, Texture2D.whiteTexture);
                GUI.color = Color.white;
                var e = Event.current;
                if (!isTaken && e != null && e.type == EventType.MouseDown && sr.Contains(e.mousePosition)) { clicked = i; e.Use(); }
            }
            GUI.color = Color.white;
            return clicked;
        }
    }
}
