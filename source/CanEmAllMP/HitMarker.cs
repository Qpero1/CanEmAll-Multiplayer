using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEngine.UI;

namespace CanEmAllMP
{
    /// The hit crosshair: when you hit another player, an "X" pops around the centre of the screen (where the
    /// game's crosshair is) and fades out. Image embedded as resource "hitcross" (same format as the cursors).
    public static class HitMarker
    {
        private const float Life = 0.3f, Pop = 0.07f, Size = 100f;   // big enough to sit around the game's crosshair
        private static GameObject _root;
        private static RawImage _img;
        private static float _shownAt = -10f;
        private static bool _failed;

        public static void Show()
        {
            if (_failed) return;
            try
            {
                if (_root == null) Build();
                if (_root == null) { _failed = true; return; }
                _shownAt = Time.unscaledTime;
                _root.SetActive(true);
                Tick();
            }
            catch (Exception e) { _failed = true; Plugin.Log.LogWarning("[MP] hit marker: " + e.Message); }
        }

        public static void Tick()
        {
            if (_root == null || !_root.activeSelf) return;
            float t = Time.unscaledTime - _shownAt;
            if (t > Life || !GameApi.InGame) { _root.SetActive(false); return; }
            float scale = t < Pop ? Mathf.Lerp(1.25f, 1f, t / Pop) : 1f;           // quick pop, then settle
            float alpha = t < Life * 0.4f ? 1f : 1f - (t - Life * 0.4f) / (Life * 0.6f); // hold, then fade
            _img.rectTransform.localScale = Vector3.one * scale;
            _img.color = new Color(1f, 1f, 1f, alpha);
        }

        private static void Build()
        {
            var tex = Load();
            if (tex == null) return;
            _root = new GameObject("MP_HitMarker");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 450;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            var go = new GameObject("X");
            go.transform.SetParent(_root.transform, false);
            _img = go.AddComponent<RawImage>();
            _img.texture = tex;
            _img.raycastTarget = false;
            var rt = _img.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(Size, Size);
            _root.SetActive(false);
        }

        private static Texture2D Load()
        {
            using var s = typeof(HitMarker).Assembly.GetManifestResourceStream("hitcross");
            if (s == null) { Plugin.Log.LogWarning("[MP] hit marker image missing"); return null; }
            using var gz = new GZipStream(s, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            var b = ms.ToArray();
            int w = BitConverter.ToInt32(b, 0), h = BitConverter.ToInt32(b, 4);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[w * h];
            for (int k = 0; k < px.Length; k++) { int o = 16 + k * 4; px[k] = new Color32(b[o], b[o + 1], b[o + 2], b[o + 3]); }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(tex);
            return tex;
        }
    }
}
