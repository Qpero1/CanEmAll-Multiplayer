#nullable disable
// Other players' weapon sounds, played in 3D at their miner: shots, swings, the vacuum's suck/throw,
// flamethrower and chainsaw loops. Clips come from your own copy of each weapon, routed through the game's mixer.

using System;
using System.Collections.Generic;
using CanEmAllMP;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace CanEmAllMPModels
{
    internal static class WeaponSounds
    {
        private class Kit
        {
            public AudioClip[] Shots = Array.Empty<AudioClip>();
            public AudioClip Loop;          // while firing (flame) / sucking (vacuum)
            public AudioClip Idle;          // while just held (chainsaw)
            public AudioClip Throw;         // vacuum throw
            public float AutoInterval;      // >0: repeat shots while the trigger is held
            public UnityEngine.Audio.AudioMixerGroup Mixer;
            public float Volume = RemoteVolume;
        }

        /// Other players' weapons are quieter than your own (8%).
        private const float RemoteVolume = 0.08f;

        private static readonly Dictionary<int, Kit> Kits = new Dictionary<int, Kit>();
        private static readonly System.Random Rng = new System.Random();
        private static int _err;

        private static AudioClip[] Arr(Il2CppReferenceArray<AudioClip> a)
        {
            if (a == null) return Array.Empty<AudioClip>();
            var list = new List<AudioClip>();
            for (int i = 0; i < a.Length; i++) if (a[i] != null) list.Add(a[i]);
            return list.ToArray();
        }
        private static AudioClip[] One(AudioClip c) => c != null ? new[] { c } : Array.Empty<AudioClip>();

        private static Kit KitFor(int index)
        {
            if (Kits.TryGetValue(index, out var k)) return k;
            k = new Kit();
            try
            {
                var w = GameApi.WeaponAt(index);
                if (w != null)
                {
                    var src = w.GetComponentInChildren<AudioSource>(true);
                    if (src != null) { k.Mixer = src.outputAudioMixerGroup; k.Volume = Mathf.Clamp(src.volume, 0.3f, 1f) * RemoteVolume; }
                    var pistol = w.TryCast<Pistol>(); if (pistol != null) k.Shots = One(pistol.shootClip);
                    var smg = w.TryCast<SMG>(); if (smg != null) { k.Shots = Arr(smg.shootClip); k.AutoInterval = 0.09f; }
                    var ak = w.TryCast<AK47>(); if (ak != null) { k.Shots = Arr(ak.shootClip); k.AutoInterval = 0.11f; }
                    var sg = w.TryCast<Shotgun>(); if (sg != null) k.Shots = Arr(sg.shootClip);
                    var rl = w.TryCast<RocketLauncher>(); if (rl != null) k.Shots = One(rl.audioSource?.clip);
                    var ft = w.TryCast<FlameThrower>(); if (ft != null) k.Loop = ft.flameAudioSource?.clip;
                    var cs = w.TryCast<Chainsaw>(); if (cs != null) { k.Idle = cs.idleAudioSource?.clip; k.Loop = cs.movingAudioSource?.clip; }
                    var bat = w.TryCast<BaseballBat>(); if (bat != null) k.Shots = Arr(bat.swingClips);
                    var knife = w.TryCast<Knife>(); if (knife != null) k.Shots = Arr(knife.swingClips);
                    var vac = w.TryCast<Vacuum>(); if (vac != null) { k.Loop = vac.suckClip; k.Throw = vac.throwClip; }
                    ModelsPlugin.L.LogInfo($"Weapon #{index} sounds: {k.Shots.Length} shot(s){(k.Loop != null ? ", loop" : "")}{(k.Idle != null ? ", idle" : "")}{(k.Throw != null ? ", throw" : "")}");
                }
            }
            catch (Exception e) { if (_err++ < 3) ModelsPlugin.L.LogWarning("weapon sounds: " + e.Message); }
            Kits[index] = k;
            return k;
        }

        private static AudioSource Make(ModelState s, bool loop)
        {
            var go = new GameObject(loop ? "MP_WeaponLoop" : "MP_WeaponShot");
            go.transform.SetParent(s.Model.transform.parent, false);
            go.transform.localPosition = new Vector3(0, 1.3f, 0.4f);
            var a = go.AddComponent<AudioSource>();
            a.playOnAwake = false;
            a.loop = loop;
            a.spatialBlend = 1f;               // fully 3D
            a.rolloffMode = AudioRolloffMode.Logarithmic;
            a.minDistance = 3f;
            a.maxDistance = 60f;
            a.dopplerLevel = 0f;
            a.spread = 30f;
            return a;
        }

        /// A single attack (click): one shot / swing.
        public static void Attack(ModelState s, int weapon, bool vacThrow = false)
        {
            if (weapon < 0 || s.Model == null) return;
            try
            {
                var k = KitFor(weapon);
                var clip = vacThrow ? k.Throw : k.Shots.Length > 0 ? k.Shots[Rng.Next(k.Shots.Length)] : null;
                if (clip == null) return;
                if (s.ShotSrc == null) s.ShotSrc = Make(s, false);
                s.ShotSrc.outputAudioMixerGroup = k.Mixer;
                s.ShotSrc.pitch = 0.95f + (float)Rng.NextDouble() * 0.1f;
                s.ShotSrc.PlayOneShot(clip, k.Volume);
                s.NextAuto = Time.time + k.AutoInterval;
            }
            catch (Exception e) { if (_err++ < 3) ModelsPlugin.L.LogWarning("weapon sound: " + e.Message); }
        }

        /// Every frame: automatic fire while the trigger is held, and the looping sounds.
        public static void Tick(ModelState s, int weapon, int action)
        {
            if (s.Model == null) return;
            try
            {
                var k = weapon >= 0 ? KitFor(weapon) : null;
                bool firing = (action & 4) != 0;
                int vac = action & 3;

                if (k != null && k.AutoInterval > 0f && firing && Time.time >= s.NextAuto) Attack(s, weapon);

                AudioClip want = null;
                if (k != null)
                {
                    if (k.Throw != null) want = vac == 1 ? k.Loop : null;       // vacuum: suck loop
                    else if (k.Loop != null && firing) want = k.Loop;           // flamethrower / chainsaw revving
                    else want = k.Idle;                                         // chainsaw idling
                }
                if (want == null)
                {
                    if (s.LoopSrc != null && s.LoopSrc.isPlaying) s.LoopSrc.Stop();
                    return;
                }
                if (s.LoopSrc == null) s.LoopSrc = Make(s, true);
                if (s.LoopSrc.clip != want || !s.LoopSrc.isPlaying)
                {
                    s.LoopSrc.outputAudioMixerGroup = k.Mixer;
                    s.LoopSrc.clip = want;
                    s.LoopSrc.volume = k.Volume;
                    s.LoopSrc.Play();
                }
            }
            catch (Exception e) { if (_err++ < 3) ModelsPlugin.L.LogWarning("weapon loop sound: " + e.Message); }
        }
    }
}
