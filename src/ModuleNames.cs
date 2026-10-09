using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace AlternativeDamageModel
{
    /// <summary>
    /// The module types of Sea Power_Data/StreamingAssets/original/systems/modules.ini (the sections a system's ModuleType names),
    /// for the settings window's "Single system types", with the game's Integrity of each. Read from the game
    /// (SeaPower.SystemLibraries.Modules, so mods that change modules.ini count); the 0.8.4 values below when that is not possible.
    /// Types with Integrity 0 (Inert) are left out: there is nothing to make stronger.
    /// </summary>
    static class ModuleNames
    {
        struct Info { public float Integrity; public bool Scales; }

        static readonly Dictionary<string, Info> Known = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase)
        {
            { "Default", I(10) }, { "Inert", I(0) }, { "HitZone", I(15) },
            { "ControlSystem", I(10) }, { "CIC", I(30) }, { "Bridge", I(10) }, { "Airfield", I(200) }, { "FlightDeck", I(4, true) }, { "Cable", I(5) },
            { "Engine", I(5, true) }, { "Battery", I(20) }, { "Propulsion", I(2, true) }, { "Rudder", I(10) },
            { "SmallSensor", I(10) }, { "MediumSensor", I(20) }, { "BigSensor", I(30) }, { "SonarSensor", I(40) },
            { "SmallLauncher", I(10) }, { "MediumLauncher", I(20) }, { "BigLauncher", I(30) }, { "VLS", I(25) },
            { "OpenMount_Light", I(1) }, { "OpenMount", I(4) }, { "SmallTurret", I(25) }, { "MediumTurret", I(40) }, { "HeavyTurret", I(50) }, { "CIWS", I(10) },
            { "SmallMagazine", I(10) }, { "MediumMagazine", I(20) }, { "LargeMagazine", I(30) },
            { "CivilianCargo", I(30) }, { "DangerousCargo", I(30) }, { "FlammableCargo", I(30) }, { "MilitaryCargo", I(30) },
            { "HighExplosiveCargo", I(30) }, { "VeryDangerousCargo", I(30) }, { "VehiclesCargo", I(30) }, { "MilitaryVehicles", I(40) },
            { "Passengers", I(1) },
            { "Sensor", I(13) }, { "Weapon", I(23) }, { "WeaponMagazine", I(33) },
        };

        static Info I(float v, bool scales = false) { return new Info { Integrity = v, Scales = scales }; }

        // ------------------------------------------------------------ the game's library (reflection)
        static object _lib; static MethodInfo _get; static PropertyInfo _names; static bool _tried;
        static FieldInfo _fInt, _fScales;

        static bool Library()
        {
            if (_lib != null) return true;
            if (_tried) return false;
            _tried = true;
            try
            {
                Assembly asm = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == "Seapower-Scripts") { asm = a; break; }
                if (asm == null) { _tried = false; return false; }   // not loaded yet: try again later
                var t = asm.GetType("SeaPower.SystemLibraries");
                var f = t == null ? null : t.GetField("Modules", BindingFlags.Static | BindingFlags.Public);
                _lib = f == null ? null : f.GetValue(null);
                if (_lib == null) return false;
                _get = _lib.GetType().GetMethod("Get", new[] { typeof(string) });
                _names = _lib.GetType().GetProperty("SectionNames");
                if (_get == null) { _lib = null; return false; }
                return true;
            }
            catch { _lib = null; return false; }
        }

        static bool FromGame(string name, out Info info)
        {
            info = default(Info);
            try
            {
                if (!Library()) return false;
                var rec = _get.Invoke(_lib, new object[] { name });
                if (rec == null) return false;
                if (_fInt == null) { _fInt = rec.GetType().GetField("Integrity"); _fScales = rec.GetType().GetField("ScalesWithDisplacement"); }
                if (_fInt == null) return false;
                info.Integrity = Convert.ToSingle(_fInt.GetValue(rec));
                info.Scales = _fScales != null && (bool)_fScales.GetValue(rec);
                return true;
            }
            catch { return false; }
        }

        static bool Lookup(string name, out Info info)
        {
            if (string.IsNullOrEmpty(name)) { info = default(Info); return false; }
            return FromGame(name, out info) || Known.TryGetValue(name, out info);
        }

        /// <summary>modules.ini Integrity of the type (before the game's displacement scaling), or 0 when the type is unknown.</summary>
        public static float GameIntegrity(string name) { Info i; return Lookup(name, out i) ? i.Integrity : 0f; }

        /// <summary>The game multiplies this type's Integrity by displacement^0.25 (ScalesWithDisplacement).</summary>
        public static bool ScalesWithDisplacement(string name) { Info i; return Lookup(name, out i) && i.Scales; }

        static List<string> _all;

        static List<string> All()
        {
            if (_all != null) return _all;
            var list = new List<string>();
            try
            {
                if (Library() && _names != null)
                {
                    var names = _names.GetValue(_lib, null) as IEnumerable;
                    if (names != null)
                        foreach (var o in names)
                        {
                            var n = (o as string ?? "").Trim();
                            if (n.Length == 0 || n.IndexOf(' ') >= 0 || n.IndexOf('-') >= 0) continue;   // "[ ---- General Systems ---- ]"
                            if (GameIntegrity(n) > 0f && !list.Exists(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) list.Add(n);
                        }
                }
            }
            catch { }
            if (list.Count > 0) { _all = list; return list; }   // the game's list is final
            foreach (var kv in Known) if (kv.Value.Integrity > 0f) list.Add(kv.Key);   // the fallback: tried again next time
            return list;
        }

        public static List<string> Filter(string text, int max)
        {
            var list = new List<string>();
            text = (text ?? "").Trim();
            foreach (var n in All())
            {
                if (text.Length > 0 && n.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                list.Add(n);
                if (list.Count >= max) break;
            }
            return list;
        }

        /// <summary>The type as written in modules.ini (case of the list), or the text itself.</summary>
        public static string ToKey(string text)
        {
            text = (text ?? "").Trim();
            foreach (var n in All()) if (string.Equals(n, text, StringComparison.OrdinalIgnoreCase)) return n;
            return text;
        }
    }
}
