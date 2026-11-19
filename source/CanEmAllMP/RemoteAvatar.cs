using System.Collections.Generic;
using UnityEngine;

namespace CanEmAllMP
{
    /// <summary>
    /// Visual stand-in for another player: a capsule body + a "head" box that shows where they look,
    /// smoothed with a small interpolation buffer (rendered ~100ms behind the newest snapshot).
    /// </summary>
    public class RemoteAvatar
    {
        public readonly int Id;
        public GameObject Root;
        private Transform _head;
        private readonly List<(float t, PlayerSnapshot s)> _buffer = new List<(float, PlayerSnapshot)>();
        private const float InterpDelay = 0.1f;
        public Vector3 Position => Root != null ? Root.transform.position : Vector3.zero;
        public int Weapon;
        public int Action;   // see PlayerSnapshot.Action
        public int Gestures; // see PlayerSnapshot.Gestures
        public int Emote;    // see PlayerSnapshot.Emote
        public float Pitch;  // where they're looking up/down
        public bool Dead;    // spectating (died this day): hidden
        public bool Connected => Root != null && _buffer.Count > 0;

        public static Color ColorFor(int id) => Palette.ForPlayer(id);
        private readonly List<(Renderer r, float mul)> _tinted = new List<(Renderer, float)>();
        private int _colorIdx = -1;

        /// Re-tint to the player's current colour (after they pick a new one).
        public void RefreshColor()
        {
            int ci = Palette.IndexOfPlayer(Id);
            if (ci == _colorIdx) return;
            _colorIdx = ci;
            var c = Palette.Of(ci);
            foreach (var (r, mul) in _tinted) { try { if (r != null) r.material.color = new Color(c.r * mul, c.g * mul, c.b * mul, 1f); } catch { } }
        }
        private CapsuleCollider _collider;

        /// Solid body so bullets stop on other players (friendly fire); off = walk/shoot straight through them.
        public void SetCollider(bool on)
        {
            if (Root == null) return;
            if (on && _collider == null)
            {
                _collider = Root.AddComponent<CapsuleCollider>();
                _collider.center = new Vector3(0, 0.95f, 0);
                _collider.height = 1.9f;
                _collider.radius = 0.4f;
            }
            if (_collider != null) _collider.enabled = on;
        }

        /// Is a world point on this player's body?
        public bool Contains(Vector3 p)
        {
            if (Root == null || !Root.activeSelf) return false;
            var feet = Root.transform.position;
            float y = Mathf.Clamp(p.y, feet.y + 0.3f, feet.y + 1.6f);
            var axis = new Vector3(feet.x, y, feet.z);
            return (p - axis).sqrMagnitude <= 0.6f * 0.6f;
        }

        public RemoteAvatar(int id)
        {
            Id = id;
            Build();
        }

        private void Build()
        {
            Root = new GameObject("MP_RemotePlayer_" + Id);
            Object.DontDestroyOnLoad(Root);
            _colorIdx = Palette.IndexOfPlayer(Id);
            var color = Palette.Of(_colorIdx);

            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(Root.transform, false);
            body.transform.localPosition = new Vector3(0, 1f, 0);
            body.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
            Tint(body, color); _tinted.Add((body.GetComponent<Renderer>(), 1f));

            var head = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(head.GetComponent<Collider>());
            head.transform.SetParent(Root.transform, false);
            head.transform.localPosition = new Vector3(0, 1.7f, 0);
            head.transform.localScale = new Vector3(0.45f, 0.35f, 0.45f);
            Tint(head, color * 0.8f); _tinted.Add((head.GetComponent<Renderer>(), 0.8f));
            _head = head.transform;

            // "visor" so you can tell which way they're facing
            var visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(visor.GetComponent<Collider>());
            visor.transform.SetParent(_head, false);
            visor.transform.localPosition = new Vector3(0, 0.05f, 0.5f);
            visor.transform.localScale = new Vector3(0.8f, 0.35f, 0.1f);
            Tint(visor, Color.black);

            Root.SetActive(false); // shown once the first snapshot arrives
        }

        private static void Tint(GameObject go, Color c)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;
            // material instance so each player gets its own colour
            var m = r.material;
            if (m != null) m.color = c;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }

        public void Push(PlayerSnapshot s)
        {
            _buffer.Add((Time.time, s));
            if (_buffer.Count > 30) _buffer.RemoveAt(0);
            Weapon = s.Weapon;
            Action = s.Action;
            Gestures = s.Gestures;
            Emote = s.Emote;
            bool dead = (s.Action & 8) != 0;
            if (dead != Dead)
            {
                Dead = dead;
                // the body stays on the ground (models add-on ragdolls it); nothing to shoot or bump into
                SetCollider(Extras.FriendlyFire && !Dead);
            }
            if (Root != null && !Root.activeSelf)
            {
                Root.SetActive(true);
                Apply(s.Pos, s.Yaw, s.Pitch);
            }
        }

        public void Tick()
        {
            if (Root == null || _buffer.Count == 0) return;
            float renderTime = Time.time - InterpDelay;

            // find the two snapshots around renderTime
            int i = _buffer.Count - 1;
            while (i > 0 && _buffer[i - 1].t > renderTime) i--;
            if (i == 0 || _buffer.Count == 1)
            {
                var s = _buffer[0].s;
                Apply(s.Pos, s.Yaw, s.Pitch);
                return;
            }
            var a = _buffer[i - 1];
            var b = _buffer[i];
            float k = Mathf.Clamp01((renderTime - a.t) / Mathf.Max(0.0001f, b.t - a.t));
            if (renderTime > b.t) k = 1f;
            Apply(Vector3.Lerp(a.s.Pos, b.s.Pos, k),
                  Mathf.LerpAngle(a.s.Yaw, b.s.Yaw, k),
                  Mathf.LerpAngle(a.s.Pitch, b.s.Pitch, k));

            // drop snapshots we no longer need
            while (_buffer.Count > 2 && _buffer[1].t < renderTime) _buffer.RemoveAt(0);
        }

        private void Apply(Vector3 pos, float yaw, float pitch)
        {
            Root.transform.position = pos;
            Root.transform.rotation = Quaternion.Euler(0, yaw, 0);
            Pitch = pitch;
            if (_head != null) _head.localRotation = Quaternion.Euler(pitch, 0, 0);
        }

        public void Destroy()
        {
            if (Root != null) Object.Destroy(Root);
            Root = null;
        }
    }
}
