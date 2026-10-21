using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace AlternativeDamageModel
{
    /// <summary>
    /// The game's ships by their names in the game's language, for the settings window: UnitMetadataProvider.UnitData
    /// ((category, ini name) -> (id, SimpleObjectParameters)), surface ships and submarines; the name is the class name
    /// (SimpleObjectParameters.ClassName - native or NATO, as the game shows it), else the default unit name, else the ini name.
    /// Equal names get the unit name / ini name added. Read once, when first needed (the game has its unit data by then).
    /// </summary>
    static class ShipNames
    {
        static Dictionary<string, string> _byIni;        // ini -> shown name
        static Dictionary<string, string> _byName;       // shown name -> ini
        static List<string> _sorted;
        static Dictionary<string, float> _tons;          // ini -> displacement

        static void Load()
        {
            if (_byIni != null && _byIni.Count > 0) return;
            _byIni = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _sorted = new List<string>();
            _tons = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Type tProv = null, tSingleton = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name != "Seapower-Scripts") continue;
                    foreach (var t in a.GetTypes())
                    {
                        if (t.Name == "UnitMetadataProvider") tProv = t;
                        else if (t.Name == "Singleton`1" && t.Namespace == "SeaPower") tSingleton = t;
                    }
                }
                if (tProv == null || tSingleton == null) return;
                var gen = tSingleton.MakeGenericType(tProv);
                var exists = gen.GetMethod("InstanceExists", BindingFlags.Static | BindingFlags.Public);
                if (exists != null && !(bool)exists.Invoke(null, new object[] { false })) return;   // not made yet: no new one
                var inst = gen.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public).GetValue(null, null);
                var data = inst == null ? null : tProv.GetProperty("UnitData").GetValue(inst, null) as IEnumerable;
                if (data == null) return;
                var raw = new List<KeyValuePair<string, string[]>>();   // ini -> class, default
                foreach (var kv in data)
                {
                    var kt = kv.GetType();
                    var key = kt.GetProperty("Key").GetValue(kv, null); var value = kt.GetProperty("Value").GetValue(kv, null);
                    string cat = key.GetType().GetField("Item1").GetValue(key) as string, ini = key.GetType().GetField("Item2").GetValue(key) as string;
                    var sop = value.GetType().GetField("Item2").GetValue(value);
                    if (sop == null || string.IsNullOrEmpty(ini)) continue;
                    // only the ships' own ini files (vessels/<id>.ini): not their extra loadouts ("<id>@<loadout>")
                    if (!string.Equals(cat, "vessels", StringComparison.OrdinalIgnoreCase) || ini.IndexOf('@') >= 0) continue;
                    string tn = sop.GetType().Name;
                    if (tn.IndexOf("Vessel", StringComparison.Ordinal) < 0 && tn.IndexOf("Submarine", StringComparison.Ordinal) < 0) continue;
                    string cls = Prop(sop, "ClassName"), def = Prop(sop, "DefaultName");
                    raw.Add(new KeyValuePair<string, string[]>(ini, new[] { cls, def }));
                    try { var fd = sop.GetType().GetField("_displacement"); if (fd != null) _tons[ini] = Convert.ToSingle(fd.GetValue(sop)); } catch { }
                }
                // names: the class; the same class twice -> with the unit's name; still the same -> with the ini name
                var count = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in raw) { string n = First(r.Value[0], r.Value[1], r.Key); int c; count.TryGetValue(n, out c); count[n] = c + 1; }
                foreach (var r in raw)
                {
                    string n = First(r.Value[0], r.Value[1], r.Key);
                    if (count[n] > 1 && !string.IsNullOrEmpty(r.Value[1]) && !string.Equals(r.Value[1], n, StringComparison.OrdinalIgnoreCase)) n = n + " (" + r.Value[1] + ")";
                    if (_byName.ContainsKey(n)) n = n + " [" + r.Key + "]";
                    _byIni[r.Key] = n; _byName[n] = r.Key;
                }
                _sorted = new List<string>(_byName.Keys);
                _sorted.Sort(StringComparer.CurrentCultureIgnoreCase);
            }
            catch (Exception e) { SettingsWindow.Trace("ShipNames: " + e.Message); }
        }

        /// <summary>The ships (shown name, displacement in tons; tons &gt; 0), sorted by displacement.</summary>
        public static List<KeyValuePair<string, float>> Sizes()
        {
            Load();
            var l = new List<KeyValuePair<string, float>>();
            foreach (var kv in _byIni) { float t; if (_tons.TryGetValue(kv.Key, out t) && t > 0f) l.Add(new KeyValuePair<string, float>(kv.Value, t)); }
            l.Sort((a, b) => a.Value.CompareTo(b.Value));
            return l;
        }

        /// <summary>The game's UnitMetadataProvider (null when it is not made yet).</summary>
        public static object Provider()
        {
            try
            {
                Type tProv = null, tSingleton = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name != "Seapower-Scripts") continue;
                    foreach (var t in a.GetTypes())
                    {
                        if (t.Name == "UnitMetadataProvider") tProv = t;
                        else if (t.Name == "Singleton`1" && t.Namespace == "SeaPower") tSingleton = t;
                    }
                }
                if (tProv == null || tSingleton == null) return null;
                var gen = tSingleton.MakeGenericType(tProv);
                var exists = gen.GetMethod("InstanceExists", BindingFlags.Static | BindingFlags.Public);
                if (exists != null && !(bool)exists.Invoke(null, new object[] { false })) return null;
                return gen.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public).GetValue(null, null);
            }
            catch { return null; }
        }

        static string Prop(object o, string name)
        {
            try { var p = o.GetType().GetProperty(name); return p == null ? null : p.GetValue(o, null) as string; } catch { return null; }
        }

        static string First(params string[] s) { foreach (var x in s) if (!string.IsNullOrEmpty(x)) return x.Trim(); return ""; }

        /// <summary>The name shown for an ini name (the ini name itself when the game does not know it).</summary>
        public static string Display(string ini)
        {
            Load();
            string n;
            return ini != null && _byIni.TryGetValue(ini, out n) ? n : ini;
        }

        /// <summary>A shown name (or an ini name typed in) -> the ini name.</summary>
        public static string ToIni(string text)
        {
            Load();
            if (string.IsNullOrEmpty(text)) return "";
            string ini;
            return _byName.TryGetValue(text, out ini) ? ini : text;
        }

        /// <summary>Names containing the text (in the name or in the ini name), sorted; at most 'max'.</summary>
        public static List<string> Filter(string text, int max)
        {
            Load();
            var list = new List<string>();
            text = (text ?? "").Trim();
            foreach (var n in _sorted)
            {
                if (text.Length > 0 && n.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0 && _byName[n].IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                list.Add(n);
                if (list.Count >= max) break;
            }
            return list;
        }
    }
}
