using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CanEmAllMP
{
    /// The hitmarker sound when you hit another player (friendly fire, or the F9 test dummy).
    ///
    /// This game's build strips the Unity APIs for making an AudioClip from code (SetData needs a stripped
    /// ReadOnlySpan helper, the PCM-reader Create overload can't be unstripped), so the sound is played by
    /// Windows itself: winmm PlaySound with an in-memory WAV. Works the same under Proton.
    /// The sound is embedded as raw 16-bit mono 44.1 kHz PCM (resource "hitmarker").
    public static class HitSound
    {
        [DllImport("winmm.dll", SetLastError = true)]
        private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);
        private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

        private static short[] _pcm;
        private static byte[] _wav;              // the WAV for _wavVolume, pinned while Windows may be playing it
        private static GCHandle _pin;
        private static float _wavVolume = -1f;
        private static bool _failed;
        private static float _failedAt = -100f, _lastAt = -1f;

        public static void Play()
        {
            float vol = Plugin.HitSoundVolume.Value;
            if (vol <= 0f) return;
            if (_failed && Time.unscaledTime - _failedAt < 10f) return;
            if (Time.unscaledTime - _lastAt < 0.05f) return;   // shotgun pellets: one tick per shot
            _lastAt = Time.unscaledTime;
            try { _failed = !PlayNow(vol); if (_failed) _failedAt = Time.unscaledTime; }
            catch (Exception e) { _failed = true; _failedAt = Time.unscaledTime; Plugin.Log.LogWarning("[MP] hit sound: " + e); }
        }

        /// F9 "Test hit sound": play it now and say what happened.
        public static string Test()
        {
            try
            {
                _failed = false;
                float vol = Plugin.HitSoundVolume.Value;
                bool ok = PlayNow(vol > 0f ? vol : 0.7f);
                string r = ok ? $"Hit sound played ({(_pcm?.Length ?? 0) / 44100f:0.00}s, volume setting {vol:0.##})"
                              : $"Hit sound: Windows refused to play it (error {Marshal.GetLastWin32Error()})";
                Plugin.Log.LogInfo("[MP] " + r);
                return r;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[MP] hit sound test: " + e);
                return "Hit sound failed: " + e.Message;
            }
        }

        private static bool PlayNow(float vol)
        {
            if (_pcm == null) LoadPcm();
            if (_pcm == null) return false;
            if (_wav == null || Math.Abs(vol - _wavVolume) > 0.001f) BuildWav(vol);
            return PlaySound(_pin.AddrOfPinnedObject(), IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        }

        private static void LoadPcm()
        {
            using var s = typeof(HitSound).Assembly.GetManifestResourceStream("hitmarker");
            if (s == null) { Plugin.Log.LogWarning("[MP] hit sound resource missing"); return; }
            var bytes = new byte[s.Length];
            int got = 0;
            while (got < bytes.Length) { int n = s.Read(bytes, got, bytes.Length - got); if (n <= 0) break; got += n; }
            var pcm = new short[got / 2];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(bytes[2 * i] | (bytes[2 * i + 1] << 8));
            _pcm = pcm;
            Plugin.Log.LogInfo($"[MP] hit sound ready ({pcm.Length} samples)");
        }

        /// The WAV at a volume: the setting (0..2) times 0.0785 is the gain; above 1 it's soft-clipped (tanh) so louder stays clean instead of crackling.
        private static void BuildWav(float vol)
        {
            float gain = Mathf.Clamp(vol, 0f, 2f) * 0.0785f;  // default 0.7 -> 0.055 (half of the "0.2" level)
            var samples = new short[_pcm.Length];
            float norm = gain > 1f ? 1f / (float)Math.Tanh(gain) : 1f;
            for (int i = 0; i < samples.Length; i++)
            {
                float x = _pcm[i] / 32768f;
                float y = gain > 1f ? (float)Math.Tanh(gain * x) * norm * 0.98f : x * gain;
                samples[i] = (short)Mathf.Clamp(Mathf.RoundToInt(y * 32767f), -32768, 32767);
            }
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            int dataLen = samples.Length * 2;
            w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }); w.Write(36 + dataLen);
            w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
            w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' }); w.Write(16);
            w.Write((short)1); w.Write((short)1); w.Write(44100); w.Write(44100 * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); w.Write(dataLen);
            foreach (var v in samples) w.Write(v);
            w.Flush();
            // Windows reads the buffer while it plays (async): stop it before swapping buffers, keep the new one pinned
            if (_pin.IsAllocated) { PlaySound(IntPtr.Zero, IntPtr.Zero, 0); _pin.Free(); }
            _wav = ms.ToArray();
            _pin = GCHandle.Alloc(_wav, GCHandleType.Pinned);
            _wavVolume = vol;
        }
    }
}
