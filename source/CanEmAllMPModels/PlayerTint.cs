using System;
using System.Collections.Generic;
using CanEmAllMP;
using UnityEngine;

namespace CanEmAllMPModels
{
    /// Tints the miner model in its player's colour: the near-white, low-saturation parts of the texture take the
    /// colour (keeping their shading), everything else stays as it is. Only the body: the renderers are recorded when
    /// the model is spawned, before any weapon hangs off its hand.
    /// Tinted textures/materials are made once per (material, colour) and shared.
    internal static class PlayerTint
    {
        private class Tracked
        {
            public GameObject Model;
            public List<Renderer> Body = new List<Renderer>();
            public List<Material[]> Original = new List<Material[]>();
            public Func<int> ColorOf;
            public int Applied = -1;
        }

        private static readonly List<Tracked> _models = new List<Tracked>();
        private static readonly Dictionary<(IntPtr, int), Material> _mats = new Dictionary<(IntPtr, int), Material>();
        private static readonly Dictionary<(IntPtr, int), Texture2D> _texs = new Dictionary<(IntPtr, int), Texture2D>();

        public static int LocalColor()
        {
            try
            {
                var net = MpManager.Net;
                return net.Active ? net.MyColor : Palette.Clamp(CanEmAllMP.Plugin.PlayerColor.Value < 0 ? 0 : CanEmAllMP.Plugin.PlayerColor.Value);
            }
            catch { return 0; }
        }

        /// Call right after the model is spawned (and its shaders fixed).
        public static void Track(GameObject model, Func<int> colorOf)
        {
            if (model == null) return;
            var t = new Tracked { Model = model, ColorOf = colorOf };
            var rends = model.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rends.Length; i++)
            {
                var r = rends[i];
                if (r == null) continue;
                Material[] mats;
                try
                {
                    var sm = r.sharedMaterials;
                    mats = new Material[sm.Length];
                    for (int j = 0; j < sm.Length; j++) mats[j] = sm[j];
                }
                catch { continue; }
                t.Body.Add(r); t.Original.Add(mats);
            }
            _models.Add(t);
            Apply(t);
        }

        /// Every frame: follow colour changes, forget destroyed models.
        public static void Tick()
        {
            for (int i = _models.Count - 1; i >= 0; i--)
            {
                var t = _models[i];
                if (t.Model == null) { _models.RemoveAt(i); continue; }
                Apply(t);
            }
        }

        private static void Apply(Tracked t)
        {
            int ci;
            try { ci = Palette.Clamp(t.ColorOf()); } catch { return; }
            if (ci == t.Applied) return;
            t.Applied = ci;
            var col = Palette.Of(ci);
            for (int i = 0; i < t.Body.Count; i++)
            {
                var r = t.Body[i];
                if (r == null) continue;
                var orig = t.Original[i];
                var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material>(orig.Length);
                for (int j = 0; j < orig.Length; j++) arr[j] = orig[j] != null ? Tinted(orig[j], ci, col) : null;
                try { r.sharedMaterials = arr; } catch (Exception e) { ModelsPlugin.L.LogWarning("[tint] " + e.Message); }
            }
        }

        private static Material Tinted(Material m, int ci, Color col)
        {
            if (_mats.TryGetValue((m.Pointer, ci), out var have) && have != null) return have;
            var copy = new Material(m);
            copy.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            Texture tex = null;
            try { tex = m.mainTexture; } catch { }
            if (tex != null)
            {
                var t2 = TintedTexture(tex, ci, col);
                if (t2 != null) copy.mainTexture = t2;
            }
            else
            {
                // untextured: tint the colour itself if it's whitish
                try { var c = m.color; copy.color = TintPixel(c, col); } catch { }
            }
            _mats[(m.Pointer, ci)] = copy;
            return copy;
        }

        // white-ish (bright, unsaturated) -> player colour, shading kept
        private static Color TintPixel(Color c, Color col)
        {
            float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), min = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            float sat = max > 0.001f ? (max - min) / max : 0f;
            // the model's "white" is a warm cream (saturation ~0.3), so allow some colour; skin and the orange stay
            float w = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.50f, 0.75f, max)) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.22f, 0.40f, sat)));
            if (w <= 0.001f) return c;
            var target = new Color(col.r * max, col.g * max, col.b * max, c.a);
            return Color.Lerp(c, target, w * 0.9f);
        }

        // head mask (face, hair, helmet): texture areas used by polygons skinned to the Head bone, made from the model
        // file (1024x1024, 1 bit per pixel, row 0 = top of the image, gzipped). Not tinted.
        private const int MaskSize = 1024;
        private static byte[] _mask;
        private static bool _maskTried;

        private static bool InHead(int x, int y, int w, int h)
        {
            if (!_maskTried)
            {
                _maskTried = true;
                try
                {
                    using var s = typeof(PlayerTint).Assembly.GetManifestResourceStream("headmask");
                    if (s != null)
                    {
                        using var gz = new System.IO.Compression.GZipStream(s, System.IO.Compression.CompressionMode.Decompress);
                        using var ms = new System.IO.MemoryStream();
                        gz.CopyTo(ms);
                        _mask = ms.ToArray();
                    }
                }
                catch (Exception e) { ModelsPlugin.L.LogWarning("[tint] head mask: " + e.Message); }
            }
            if (_mask == null) return false;
            int mx = x * MaskSize / w;
            int my = (h - 1 - y) * MaskSize / h; // texture rows go bottom-up
            int bit = my * MaskSize + mx;
            return (_mask[bit >> 3] & (0x80 >> (bit & 7))) != 0;
        }

        private static Texture2D TintedTexture(Texture src, int ci, Color col)
        {
            if (_texs.TryGetValue((src.Pointer, ci), out var have) && have != null) return have;
            try
            {
                int w = src.width, h = src.height;
                while (w > 1024 || h > 1024) { w /= 2; h /= 2; }
                // the bundle's textures aren't readable: draw into a render texture and read that back
                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var prev = RenderTexture.active;
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);

                var px = tex.GetPixels32();
                int n = px.Length, changed = 0;
                for (int i = 0; i < n; i++)
                {
                    if (InHead(i % w, i / w, w, h)) continue;
                    var p = px[i];
                    var c = TintPixel(new Color(p.r / 255f, p.g / 255f, p.b / 255f, p.a / 255f), col);
                    var q = new Color32((byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), p.a);
                    if (q.r != p.r || q.g != p.g || q.b != p.b) { px[i] = q; changed++; }
                }
                tex.SetPixels32(px);
                tex.wrapMode = src.wrapMode;
                tex.filterMode = src.filterMode;
                tex.Apply(true, true); // mipmaps, then drop the CPU copy
                tex.hideFlags = HideFlags.HideAndDontSave;
                _texs[(src.Pointer, ci)] = tex;
                ModelsPlugin.L.LogInfo($"[tint] {src.name} {w}x{h} -> {Palette.Names[ci]}: {changed * 100 / Math.Max(1, n)}% of the texture tinted");
                return tex;
            }
            catch (Exception e)
            {
                ModelsPlugin.L.LogWarning("[tint] texture: " + e.Message);
                _texs[(src.Pointer, ci)] = null;
                return null;
            }
        }
    }
}
