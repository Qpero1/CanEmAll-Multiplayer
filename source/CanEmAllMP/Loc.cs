using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

namespace CanEmAllMP
{
    /// Translations for everything the mod shows, in the game's 9 languages
    /// (EN, ES, DE, FR, IT, RU, ZH, JA, KO - the same order as the game's own language file).
    ///
    /// Keys are the English text. Text built with values ("{0} joined") is matched as a template, so strings
    /// made anywhere (statuses, toasts, messages from the host) are translated where they are shown.
    /// The table is res/loc.tsv: one line per string, tab separated, English first.
    public static class Loc
    {
        public const int Count = 9;
        static readonly string[] BackWord = { "BACK", "ATRÁS", "ZURÜCK", "RETOUR", "INDIETRO", "НАЗАД", "返回", "戻る", "뒤로" };

        static Dictionary<string, string[]> _exact;
        static List<(Regex re, string[] tr, int args, int lit)> _templates;
        static readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        static int _lang = -1;

        /// 0 = English ... 8 = Korean. Worked out from the game's own word for BACK, so it always matches the game.
        public static int Lang
        {
            get
            {
                if (_lang >= 0) return _lang;
                return Detect();
            }
        }

        static float _nextDetect;
        static int Detect()
        {
            int l = 0;
            try
            {
                string back = GameWord("back");
                if (!string.IsNullOrEmpty(back))
                    for (int i = 0; i < BackWord.Length; i++) if (string.Equals(back.Trim(), BackWord[i], StringComparison.OrdinalIgnoreCase)) { l = i; break; }
            }
            catch { }
            return l;
        }

        /// The game's own translation for one of its ids (e.g. "settings"), or null.
        public static string GameWord(string id)
        {
            try
            {
                var lm = LanguageManager.Instance;
                if (lm == null) return null;
                var w = lm.Text(id);
                return string.IsNullOrEmpty(w) || w == id ? null : w;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- lookup

        public static string T(string en)
        {
            if (string.IsNullOrEmpty(en)) return en;
            int l = Lang;
            if (l <= 0) return en;
            Load();
            if (_cache.TryGetValue(en, out var hit)) return hit;
            string r = Translate(en, l, 0);
            if (_cache.Count > 4000) _cache.Clear();
            _cache[en] = r;
            return r;
        }

        /// string.Format on the translated template: F("{0} joined", name).
        public static string F(string en, params object[] args)
        {
            string fmt = T(en);
            try { return string.Format(fmt, args); } catch { try { return string.Format(en, args); } catch { return en; } }
        }

        static string Translate(string en, int l, int depth)
        {
            if (_exact.TryGetValue(en, out var tr)) return Pick(tr, l) ?? en;

            // keep surrounding spaces / newlines, translate the inside
            string trimmed = en.Trim();
            if (trimmed.Length != en.Length && trimmed.Length > 0 && _exact.TryGetValue(trimmed, out tr))
            {
                var p = Pick(tr, l);
                if (p != null) return en.Replace(trimmed, p);
            }
            if (depth > 4) return en;

            foreach (var (re, ttr, n, _) in _templates)
            {
                var m = re.Match(en);
                if (!m.Success) continue;
                string fmt = Pick(ttr, l);
                if (fmt == null) return en;
                var args = new object[n];
                for (int i = 0; i < n; i++) args[i] = Arg(m.Groups["a" + i].Value, l, depth + 1);
                try { return string.Format(fmt, args); } catch { return en; }
            }
            return en;
        }

        /// A value inside a template: translate it if it is itself a known string ("Slot 1", "ON"),
        /// or a game id (skill names like "StartingCashI").
        static string Arg(string v, int l, int depth)
        {
            if (string.IsNullOrEmpty(v)) return v;
            var t = Translate(v, l, depth);
            if (!ReferenceEquals(t, v) && t != v) return t;
            // game ids are CamelCase ("StartingCashI"); plain words could be player names, so leave those alone
            bool camel = false;
            for (int i = 1; i < v.Length && !camel; i++) camel = char.IsUpper(v[i]) && char.IsLower(v[i - 1]);
            if (camel && v.Length < 40 && v.IndexOf(' ') < 0 && char.IsLetter(v[0]))
            {
                var g = GameWord(v);
                if (g != null) return g;
            }
            return v;
        }

        static string Pick(string[] tr, int l)
        {
            if (tr == null || l < 1 || l > tr.Length) return null;
            var s = tr[l - 1];
            return string.IsNullOrEmpty(s) ? null : s;
        }

        // ---------------------------------------------------------------- table

        static void Load()
        {
            if (_exact != null) return;
            _exact = new Dictionary<string, string[]>();
            _templates = new List<(Regex, string[], int, int)>();
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("loc");
                if (s == null) { Plugin.Log.LogWarning("[loc] no translation table"); return; }
                using var rd = new StreamReader(s, Encoding.UTF8);
                string line;
                int n = 0;
                while ((line = rd.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    var parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    string en = Unescape(parts[0]);
                    var tr = new string[Count - 1];
                    for (int i = 1; i < parts.Length && i < Count; i++) tr[i - 1] = Unescape(parts[i]);
                    _exact[en] = tr;
                    n++;
                    int args = 0;
                    while (en.Contains("{" + args + "}")) args++;
                    if (args > 0 && Regex.Replace(en, @"\{\d\}", "").Trim(' ', ':', '-', '(', ')').Length > 0)
                    {
                        // "{0} joined" -> ^(?<a0>.+?) joined$
                        var sb = new StringBuilder("^");
                        int pos = 0;
                        foreach (Match m in Regex.Matches(en, @"\{(\d)\}"))
                        {
                            sb.Append(Regex.Escape(en.Substring(pos, m.Index - pos)));
                            sb.Append("(?<a").Append(m.Groups[1].Value).Append(">.+?)");
                            pos = m.Index + m.Length;
                        }
                        sb.Append(Regex.Escape(en.Substring(pos))).Append('$');
                        int lit = Regex.Replace(en, @"\{\d\}", "").Length;
                        _templates.Add((new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), tr, args, lit));
                    }
                }
                // most fixed text first: "{0} (overwrite)" beats "{0} - Day {1}" for "Slot 1 - Day 4 (overwrite)",
                // and "Connected as player {0} ({1})" beats "Connected as player {0}"
                _templates.Sort((a, b) => b.lit.CompareTo(a.lit));
                Plugin.Log.LogInfo($"[loc] {n} strings, {_templates.Count} templates");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[loc] load: " + e.Message); }
        }

        static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\t", "\t");

        // ---------------------------------------------------------------- live UI

        static readonly List<(TMP_Text t, string en)> _texts = new List<(TMP_Text, string)>();
        static readonly List<(TMP_Dropdown dd, List<string> en)> _drops = new List<(TMP_Dropdown, List<string>)>();

        /// Raised after the language changes (UIs re-fill text they build themselves).
        public static event Action Changed;

        /// Sets a fixed label and keeps it translated if the language changes later.
        public static void Set(TMP_Text t, string en)
        {
            if (t == null) return;
            t.text = T(en);
            for (int i = 0; i < _texts.Count; i++) if (_texts[i].t == t) { _texts[i] = (t, en); return; }
            _texts.Add((t, en));
        }

        /// Stop keeping a label translated (its text is now set by other code).
        public static void Unbind(TMP_Text t)
        {
            for (int i = _texts.Count - 1; i >= 0; i--) if (_texts[i].t == t) _texts.RemoveAt(i);
        }

        /// Fills a dropdown with translated options (English list kept for language changes).
        public static void SetOptions(TMP_Dropdown dd, List<string> en)
        {
            if (dd == null) return;
            var opts = new Il2CppSystem.Collections.Generic.List<string>();
            foreach (var s in en) opts.Add(T(s));
            dd.ClearOptions();
            dd.AddOptions(opts);
            for (int i = 0; i < _drops.Count; i++) if (_drops[i].dd == dd) { _drops[i] = (dd, en); return; }
            _drops.Add((dd, en));
        }

        /// Lets a label shrink (never grow) so a longer translation stays inside its box.
        public static void Fit(TMP_Text t, float minScale = 0.55f)
        {
            if (t == null) return;
            float size = t.fontSize;
            if (t.enableAutoSizing) return;
            t.fontSizeMax = size;
            t.fontSizeMin = Mathf.Max(6f, size * minScale);
            t.enableAutoSizing = true;
            if (!t.enableWordWrapping) t.overflowMode = TextOverflowModes.Ellipsis;
        }

        /// A header of columns placed with &lt;pos&gt; tags ("KILLS  DEATHS  PING"): translated, and the font made smaller
        /// if any translated label would run into the next column. pos = where each column starts, width = the box.
        public static string Columns(TMP_Text t, float baseSize, float[] pos, float width, params string[] en)
        {
            var tr = new string[en.Length];
            for (int i = 0; i < en.Length; i++) tr[i] = T(en[i]);
            if (t != null)
            {
                float scale = 1f;
                try
                {
                    t.enableAutoSizing = false;
                    t.fontSize = baseSize;
                    for (int i = 0; i < tr.Length && i < pos.Length; i++)
                    {
                        float slot = (i + 1 < pos.Length ? pos[i + 1] : width) - pos[i] - 5f;
                        float w = t.GetPreferredValues(tr[i]).x;
                        if (w > slot && w > 0f) scale = Mathf.Min(scale, slot / w);
                    }
                }
                catch { }
                t.fontSize = baseSize * Mathf.Max(scale, 0.5f);
            }
            var sb = new StringBuilder(tr[0]);
            for (int i = 1; i < tr.Length && i < pos.Length; i++) sb.Append("<pos=").Append(pos[i]).Append('>').Append(tr[i]);
            return sb.ToString();
        }

        /// Call every frame: notices a language switch in the game's settings.
        public static void Tick()
        {
            if (Time.unscaledTime < _nextDetect) return;
            _nextDetect = Time.unscaledTime + 0.5f;
            int l = Detect();
            if (l == _lang) return;
            bool first = _lang < 0;
            _lang = l;
            _cache.Clear();
            if (!first) Plugin.Log.LogInfo("[loc] language changed to #" + l);
            for (int i = _texts.Count - 1; i >= 0; i--)
            {
                var (t, en) = _texts[i];
                try { if (t == null) { _texts.RemoveAt(i); continue; } t.text = T(en); } catch { _texts.RemoveAt(i); }
            }
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                var (dd, en) = _drops[i];
                try
                {
                    if (dd == null) { _drops.RemoveAt(i); continue; }
                    var opts = dd.options;
                    for (int k = 0; k < opts.Count && k < en.Count; k++) opts[k].text = T(en[k]);
                    dd.RefreshShownValue();
                }
                catch { _drops.RemoveAt(i); }
            }
            try { Changed?.Invoke(); } catch (Exception e) { Plugin.Log.LogWarning("[loc] refresh: " + e.Message); }
        }
    }
}
