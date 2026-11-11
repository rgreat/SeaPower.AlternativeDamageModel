using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Localization. The English text is the key: Loc.T("English text") gives the text in the language of the game (or [General] Language in the .cfg),
    /// the English itself when there is no translation. The translations are plain text files lang/&lt;code&gt;.ini in the mod folder, one for each
    /// language of the game (en cn de es fr ja ko ru vn):
    ///     key = text      (";" starts a comment line; \n in a text is a line break; {0}, {1} are filled in by the mod - keep them)
    /// The key of a text is made of the text itself (see Key): the file en.ini has every text with its key, copy it to translate. A text that
    /// changed in a new version of the mod is English until its line is updated. The settings window's help texts (the .cfg descriptions) go
    /// through the same Loc.T. Nothing is written by the mod; a missing file or line is English. tools/ has the scripts that make the files.
    /// </summary>
    static class Loc
    {
        public static Action<string> Trace = s => { };
        /// <summary>Language from the .cfg ("" = the game's).</summary>
        public static Func<string> Forced = () => "";

        static Dictionary<string, string> _tr = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly Dictionary<string, string> Cache = new Dictionary<string, string>(StringComparer.Ordinal);
        static string _lang = "en", _forced = "";
        static int _checkedAt = -1;
        static bool _loaded;

        /// <summary>The key of a text: its first words in lower case and the hash of the whole (FNV-1a over UTF-8): "alternative_damage_control.1a2b3c".</summary>
        public static string Key(string en)
        {
            var sb = new StringBuilder();
            bool sep = true;
            foreach (char c in en)
            {
                if (sb.Length >= 40) break;
                if (char.IsLetterOrDigit(c) && c < 128) { sb.Append(char.ToLowerInvariant(c)); sep = false; }
                else if (!sep) { sb.Append('_'); sep = true; }
            }
            uint h = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(en)) { h ^= b; h *= 16777619; }
            return sb.ToString().TrimEnd('_') + "." + h.ToString("x8").Substring(2);
        }

        /// <summary>The text in the current language (English when it is not translated).</summary>
        public static string T(string en)
        {
            if (string.IsNullOrEmpty(en)) return en ?? "";
            Check();
            string v;
            if (Cache.TryGetValue(en, out v)) return v;
            string t;
            v = _tr.TryGetValue(Key(en), out t) && t.Length > 0 ? t : en;
            if (Cache.Count > 4000) Cache.Clear();
            Cache[en] = v;
            return v;
        }

        /// <summary>The text with {0}, {1} ... filled in (the English one if the translation does not fit).</summary>
        public static string F(string en, params object[] a)
        {
            var s = T(en);
            try { return string.Format(CultureInfo.InvariantCulture, s, a); }
            catch { try { return string.Format(CultureInfo.InvariantCulture, en, a); } catch { return s; } }
        }

        public static string Language { get { Check(); return _lang; } }

        static bool _checking;

        static void Check()
        {
            if (_checking) return;
            int now = Environment.TickCount;
            if (_loaded && now - _checkedAt < 1500 && _checkedAt >= 0) return;
            _checking = true;
            try
            {
                _checkedAt = now;
                string forced = "";
                try { forced = (Forced() ?? "").Trim(); } catch { }
                string lang = forced.Length > 0 ? forced : GameLanguage();
                if (!_loaded || lang != _lang || forced != _forced)
                {
                    _lang = lang; _forced = forced; _loaded = true;
                    _tr = Load(lang);
                    Cache.Clear();
                    Trace("language '" + lang + "': " + _tr.Count + " texts");
                }
            }
            catch (Exception e) { Trace("Loc: " + e.Message); }
            finally { _checking = false; }
        }

        // ------------------------------------------------------------ the game's language

        static Type _tHandler, _tSingleton;

        static string GameLanguage()
        {
            try
            {
                Assembly asm = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == "Seapower-Scripts") { asm = a; break; }
                if (asm == null) return _lang;
                if (_tHandler == null) { _tHandler = asm.GetType("SeaPower.LanguageResourceHandler"); _tSingleton = asm.GetType("SeaPower.Singleton`1"); }
                var inst = _tSingleton.MakeGenericType(_tHandler).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public).GetValue(null, null);
                if (inst == null) return _lang;
                var l = _tHandler.GetProperty("Language").GetValue(inst, null) as string;
                return string.IsNullOrEmpty(l) ? "en" : l;
            }
            catch { return _lang; }
        }

        /// <summary>A name the game itself has (ui.ini [DamageControl]): its own translation, e.g. G("damagecontrolauto", "Auto").
        /// Not found (no handler yet) = the mod's text.</summary>
        public static string G(string key, string en)
        {
            try
            {
                Assembly asm = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == "Seapower-Scripts") { asm = a; break; }
                if (asm != null)
                {
                    if (_tHandler == null) { _tHandler = asm.GetType("SeaPower.LanguageResourceHandler"); _tSingleton = asm.GetType("SeaPower.Singleton`1"); }
                    var inst = _tSingleton.MakeGenericType(_tHandler).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public).GetValue(null, null);
                    var dict = inst == null ? null : _tHandler.GetProperty("LanguageDictionary").GetValue(inst, null);
                    if (dict != null)
                    {
                        var tryGet = dict.GetType().GetMethod("TryGetValue");
                        if (tryGet != null)
                        {
                            var args = new object[] { key, null };
                            if ((bool)tryGet.Invoke(dict, args) && args[1] is string v && v.Length > 0) return v;
                        }
                    }
                }
            }
            catch { }
            return T(en);
        }

        // ------------------------------------------------------------ files

        static string _dir;

        public static string Dir
        {
            get
            {
                if (_dir == null)
                {
                    string p = "";
                    try { p = Path.GetDirectoryName(typeof(Loc).Assembly.Location) ?? ""; } catch { }
                    _dir = Path.Combine(p, "lang");
                }
                return _dir;
            }
        }

        static string Unesc(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length) { char n = s[++i]; sb.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n); }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static Dictionary<string, string> Load(string lang)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(lang) || lang == "en" || lang.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return d;   // English is the code's own
            var path = Path.Combine(Dir, lang + ".ini");
            try
            {
                if (!File.Exists(path)) return d;
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    d[line.Substring(0, eq).Trim()] = Unesc(line.Substring(eq + 1).Trim());
                }
            }
            catch (Exception e) { Trace("lang " + lang + ": " + e.Message); }
            return d;
        }
    }
}
