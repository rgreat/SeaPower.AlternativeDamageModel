using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace AlternativeDamageModel
{
    /// <summary>
    /// [HullIntegrity] ArmorClasses: the hull armour classes of the World War II mod (LightCruiser, HeavyCruiser, BattleCruiser, BattleCruiser2, SuperHeavy).
    /// The game knows only none / minor / moderate / heavy; any other [General] ArmorType of a hull becomes "none" and every shell goes through.
    /// Here the five classes (any letter case; "Battlecruiser22" is a typo of the mod and means BattleCruiser2) become armour types 4..8 of their own
    /// with their own penetration tables (the numbers of the mod's damage.ini): ObjectBase.getMinMaxDamageMultiplier answers for them.
    /// If the patch cannot be installed, the hull gets the nearest game class (LightCruiser: moderate, the others: heavy).
    /// The same for the ArmorType of a module (BaseSystem.LoadBaseParameters, ModuleRecord.TryGetArmorType) - the classes are all over the mod's turrets, magazines, engines.
    /// A hull without ArmorType in [General] gets it from [TaskForce] (usn_bb_iowa45 and usn_bb_missouri2 of the mod have it there; the game reads [General] only: minor).
    /// </summary>
    static class ArmorClasses
    {
        public static bool On;

        static readonly string[] Names = { "lightcruiser", "heavycruiser", "battlecruiser", "battlecruiser2", "superheavy" };
        // Each class is placed between the game's own classes (so the whole row follows the game's penetration matrix, base game or a mod's): lerp(stock A, stock B, t),
        // SuperHeavy = stock heavy x Scale. The numbers come from the WW2 mod's damage.ini: its classes sit on its own minor/moderate/heavy by the mean damage multiplier
        // (SuperHeavy < heavy < BattleCruiser2 < BattleCruiser < moderate ~ HeavyCruiser < LightCruiser < minor), tuned by the user on the base game's scale to means of about
        // SuperHeavy 0.39, heavy 0.43, BattleCruiser2 0.50, BattleCruiser 0.56, HeavyCruiser 0.62, moderate 0.67, LightCruiser 0.76, minor 0.87.
        static readonly int[] StockA = { 2, 3, 3, 3, 3 };         // armour type (1 minor, 2 moderate, 3 heavy) the class starts at
        static readonly int[] StockB = { 1, 2, 2, 2, 3 };         // and goes towards
        static readonly float[] StockT = { 0.458f, 0.793f, 0.540f, 0.286f, 0f };
        static readonly float[] Scale = { 1f, 1f, 1f, 1f, 0.902f };
        // the base game's matrix (damage.ini): min, max per penetration minor, moderate, heavy, always; used when the live one cannot be read
        static readonly float[][] Stock =
        {
            null,
            new[] { 0.5f, 0.75f, 0.85f, 1.0f, 0.9f, 1.0f, 0.95f, 1.0f },
            new[] { 0.1f, 0.25f, 0.5f, 0.75f, 0.85f, 1.0f, 0.9f, 1.0f },
            new[] { 0.0f, 0.01f, 0.1f, 0.25f, 0.5f, 0.75f, 0.85f, 1.0f },
        };
        static float[][] _tables;          // per class, 8 numbers, for the matrix object _tablesFor
        static object _tablesFor;
        static FieldInfo _fGlobDv, _fDvMatrix;
        const int First = 4;   // armour type of the first class
        [ThreadStatic] static int _found;   // class (index + 1) read for the hull being loaded; 0 = none
        static readonly Dictionary<object, string> Kept = new Dictionary<object, string>();
        [ThreadStatic] static int _foundMod;   // the same for a module (class index + 1)
        [ThreadStatic] static bool _fromTaskForce;
        static FieldInfo _fArmor, _fIni;
        static MethodInfo _mRead;

        public static string Nearest(string lower)
        {
            int i = Index(lower);
            return i < 0 ? lower : (i == 0 ? "moderate" : "heavy");
        }

        static int Index(string lower)
        {
            if (lower == null) return -1;
            lower = lower.Trim();
            if (lower == "battlecruiser22") lower = "battlecruiser2";
            return Array.IndexOf(Names, lower);
        }

        /// <summary>Replaces the ToLower() of the hull's ArmorType in the loader: remembers the class and, for the mod's classes, hands the game the nearest of its own.</summary>
        public static string Read(string raw)
        {
            string low = raw == null ? "" : raw.ToLowerInvariant();
            if (!On) { _found = 0; return low; }
            int i = Index(low);
            _found = i < 0 ? 0 : i + 1;
            return i < 0 ? low : Nearest(low);
        }

        /// <summary>Hull: ini.readValue("General", "ArmorType", "minor", flag); when [General] has none, [TaskForce] is tried (an error in some files of the mod).</summary>
        public static string ReadHull(object ini, string section, string key, string def, bool flag)
        {
            if (_mRead == null || _mRead.DeclaringType != ini.GetType())
                _mRead = ini.GetType().GetMethod("readValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(string), typeof(string), typeof(bool) }, null);
            _fromTaskForce = false;
            if (!On || _mRead == null) return _mRead == null ? def : (string)_mRead.Invoke(ini, new object[] { section, key, def, flag });
            string v = (string)_mRead.Invoke(ini, new object[] { section, key, "", flag });
            if (!string.IsNullOrEmpty(v)) return v;
            v = (string)_mRead.Invoke(ini, new object[] { "TaskForce", key, "", false });
            if (!string.IsNullOrEmpty(v)) { _fromTaskForce = true; return v; }
            return def;
        }

        /// <summary>Module: replaces the ToLower() of its ArmorType (ini and modules.ini). An empty value changes nothing.</summary>
        public static string ReadModule(string raw)
        {
            string low = raw == null ? "" : raw.ToLowerInvariant();
            if (!On || low.Length == 0) return low;
            int i = Index(low);
            _foundMod = i < 0 ? 0 : i + 1;
            return i < 0 ? low : Nearest(low);
        }

        public static string NameOf(object armor)
        {
            int a = Convert.ToInt32(armor);
            return a >= First && a < First + Names.Length ? Names[a - First] : null;
        }

        static IEnumerable<CodeInstruction> LoaderTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var read = typeof(ArmorClasses).GetMethod("Read", BindingFlags.Static | BindingFlags.Public);
            var readHull = typeof(ArmorClasses).GetMethod("ReadHull", BindingFlags.Static | BindingFlags.Public);
            int n = 0, nr = 0;
            for (int i = 0; i < list.Count - 6; i++)
            {
                if (list[i].opcode != OpCodes.Ldstr || !"ArmorType".Equals(list[i].operand)) continue;
                for (int j = i + 1; j < i + 6; j++)
                {
                    var mi = list[j].operand as MethodInfo;
                    if (mi == null) continue;
                    if (mi.Name == "readValue" && mi.ReturnType == typeof(string) && mi.GetParameters().Length == 4)
                    { list[j].opcode = OpCodes.Call; list[j].operand = readHull; nr++; }
                    else if (mi.Name == "ToLower" && mi.DeclaringType == typeof(string))
                    { list[j].opcode = OpCodes.Call; list[j].operand = read; n++; break; }
                }
            }
            if (n != 1 || nr != 1) AdmPlugin.Warn("ObjectBaseLoader: " + n + " ArmorType reads patched (" + nr + " reads), 1 expected (game updated?): the armour classes of the WW2 mod are off.");
            AdmPlugin.Trace("armour classes: hull " + n + " ArmorType read patched, " + nr + " ini read");
            return list;
        }

        // a module: BaseSystem.LoadBaseParameters (ini) and ModuleRecord.TryGetArmorType (modules.ini)
        static IEnumerable<CodeInstruction> ModuleTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var rm = typeof(ArmorClasses).GetMethod("ReadModule", BindingFlags.Static | BindingFlags.Public);
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var mi = list[i].operand as MethodInfo;
                if (mi == null || mi.DeclaringType != typeof(string) || (mi.Name != "ToLower" && mi.Name != "ToLowerInvariant") || mi.GetParameters().Length != 0) continue;
                // only the one after ldstr "ArmorType" (ini) or the ArmorType field of the record
                bool ok = false;
                for (int k = Math.Max(0, i - 8); k < i; k++)
                {
                    if (list[k].opcode == OpCodes.Ldstr && "ArmorType".Equals(list[k].operand)) ok = true;
                    var f = list[k].operand as FieldInfo;
                    if (f != null && f.Name == "ArmorType" && f.FieldType == typeof(string)) ok = true;
                }
                if (!ok) continue;
                list[i].opcode = OpCodes.Call; list[i].operand = rm; n++;
            }
            AdmPlugin.Trace("armour classes: " + n + " module ArmorType read patched");
            return list;
        }

        static void ModulePrefix() { _foundMod = 0; }

        static FieldInfo _fSysArmor;
        static void ModulePostfix(object __instance)
        {
            try
            {
                if (!On || _foundMod == 0 || __instance == null) return;
                if (_fSysArmor == null) _fSysArmor = __instance.GetType().GetField("_armorType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_fSysArmor == null) return;
                _fSysArmor.SetValue(__instance, Enum.ToObject(_fSysArmor.FieldType, First + _foundMod - 1));
            }
            catch (Exception e) { AdmPlugin.Trace("ArmorClasses module: " + e.Message); }
            finally { _foundMod = 0; }
        }

        static void LoaderPrefix() { _found = 0; }

        static void LoaderPostfix(object __0)
        {
            try
            {
                object obp = __0;
                if (On && _fromTaskForce && obp != null)
                {
                    if (_fIni == null) _fIni = obp.GetType().GetField("_objectIniName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    AdmPlugin.Trace("armour classes: " + (_fIni == null ? "?" : Convert.ToString(_fIni.GetValue(obp))) + ": no ArmorType in [General], taken from [TaskForce]");
                }
                _fromTaskForce = false;
                if (!On || _found == 0 || obp == null) return;
                if (_fArmor == null) _fArmor = obp.GetType().GetField("_armorType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_fArmor == null) return;
                _fArmor.SetValue(obp, Enum.ToObject(_fArmor.FieldType, First + _found - 1));
            }
            catch (Exception e) { AdmPlugin.Trace("ArmorClasses: " + e.Message); }
            finally { _found = 0; }
        }

        // Compartments.Init multiplies the integrity of a hull by 1.2 / 1.25 / 1.3 for minor / moderate / heavy (a switch on _armorType; the default branch: nothing).
        // The new classes (set by the user): SuperHeavy as heavy, BattleCruiser2 / BattleCruiser / HeavyCruiser as moderate, LightCruiser as minor.
        static readonly float[] Bonus = { 1.2f, 1.25f, 1.25f, 1.25f, 1.3f };
        static FieldInfo _fCompObp, _fCompMax, _fCompArmor;

        static IEnumerable<CodeInstruction> InitTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            int at = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Switch) continue;
                var f = i >= 2 ? list[i - 2].operand as FieldInfo : null;
                bool afterArmor = false;
                for (int k = Math.Max(0, i - 4); k < i; k++) { var fi = list[k].operand as FieldInfo; if (fi != null && fi.Name == "_armorType") afterArmor = true; }
                if (!afterArmor) continue;
                var targets = list[i].operand as Array;
                if (targets == null || targets.Length != 4) continue;
                object first = targets.GetValue(0);
                for (int j = 0; j < list.Count && at < 0; j++)
                    foreach (object l in (System.Collections.IEnumerable)LabelsOf(list[j])) if (first.Equals(l)) { at = j; break; }
                break;
            }
            if (at < 0) { AdmPlugin.Warn("Compartments.Init: the armour switch not found (game updated?): the integrity bonus of the new armour classes is off."); return list; }
            var call = new CodeInstruction(OpCodes.Ldarg_0);
            call.MoveLabelsFrom(list[at]);
            list.InsertRange(at, new[] { call, new CodeInstruction(OpCodes.Call, typeof(ArmorClasses).GetMethod("IntegrityBonus", BindingFlags.Static | BindingFlags.Public)) });
            AdmPlugin.Trace("armour classes: integrity bonus inserted in Compartments.Init at " + at);
            return list;
        }

        static object LabelsOf(CodeInstruction ci)
        {
            var f = typeof(CodeInstruction).GetField("labels", BindingFlags.Instance | BindingFlags.Public);
            if (f != null) return f.GetValue(ci);
            var pr = typeof(CodeInstruction).GetProperty("labels", BindingFlags.Instance | BindingFlags.Public);
            return pr == null ? new object[0] : pr.GetValue(ci, null);
        }

        /// <summary>The integrity factor the new classes give this hull (1 = none: off, or the game's own class). Saved with the hull multiplier.</summary>
        public static float BonusOf(object comp)
        {
            try
            {
                if (!On || comp == null) return 1f;
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                if (_fCompObp == null) { _fCompObp = comp.GetType().GetField("_obp", all); _fCompMax = comp.GetType().GetField("_maxIntegrity", all); }
                var obp = _fCompObp == null ? null : _fCompObp.GetValue(comp);
                if (obp == null) return 1f;
                if (_fCompArmor == null) _fCompArmor = obp.GetType().GetField("_armorType", all);
                if (_fCompArmor == null) return 1f;
                int i = Convert.ToInt32(_fCompArmor.GetValue(obp)) - First;
                return i >= 0 && i < Bonus.Length ? Bonus[i] : 1f;
            }
            catch { return 1f; }
        }

        public static void IntegrityBonus(object comp)
        {
            try
            {
                if (!On || comp == null) return;
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                if (_fCompObp == null) { _fCompObp = comp.GetType().GetField("_obp", all); _fCompMax = comp.GetType().GetField("_maxIntegrity", all); }
                if (_fCompObp == null || _fCompMax == null) return;
                var obp = _fCompObp.GetValue(comp);
                if (obp == null) return;
                if (_fCompArmor == null) _fCompArmor = obp.GetType().GetField("_armorType", all);
                if (_fCompArmor == null) return;
                int a = Convert.ToInt32(_fCompArmor.GetValue(obp)) - First;
                if (a < 0 || a >= Bonus.Length) return;
                _fCompMax.SetValue(comp, (float)_fCompMax.GetValue(comp) * Bonus[a]);
            }
            catch (Exception e) { AdmPlugin.Trace("ArmorClasses integrity: " + e.Message); }
        }

        static float[] Anchor(object matrix, int type)
        {
            try
            {
                var d = matrix as System.Collections.IDictionary;
                if (d != null && d.Contains(type))
                {
                    var arr = (Vector2[])d[type];
                    if (arr != null && arr.Length >= 4)
                    {
                        var r = new float[8];
                        for (int i = 0; i < 4; i++) { r[i * 2] = arr[i].x; r[i * 2 + 1] = arr[i].y; }
                        return r;
                    }
                }
            }
            catch { }
            return Stock[type];
        }

        static readonly string[] Pretty = { "LightCruiser", "HeavyCruiser", "BattleCruiser", "BattleCruiser2", "SuperHeavy" };
        static readonly float[] StockBonus = { 1f, 1.2f, 1.25f, 1.3f };

        /// <summary>The id of one of the new classes by its armour type (4..8), else null.</summary>
        public static string CustomId(int type) { return type >= First && type < First + Names.Length ? Pretty[type - First] : null; }
        /// <summary>Strongest first: SuperHeavy, heavy, BattleCruiser2, BattleCruiser, HeavyCruiser, moderate, LightCruiser, minor.</summary>
        public static readonly int[] StrongestFirst = { 8, 3, 7, 6, 5, 2, 4, 1 };

        /// <summary>The armour of a ship's hull for the Damage Control tab: the class (the game's: 0..3, ours: 4..8), the hull integrity bonus and, per shell
        /// penetration (minor, moderate, heavy, always), the range of the damage multiplier (min, max) - 1 - max is the chance that the armour stops the shell.</summary>
        public static bool Info(object comp, out int type, out string custom, out float bonus, out float[] pass)
        {
            type = 0; custom = null; bonus = 1f; pass = null;
            try
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                if (comp == null) return false;
                if (_fCompObp == null) { _fCompObp = comp.GetType().GetField("_obp", all); _fCompMax = comp.GetType().GetField("_maxIntegrity", all); }
                var obp = _fCompObp == null ? null : _fCompObp.GetValue(comp);
                if (obp == null) return false;
                if (_fCompArmor == null) _fCompArmor = obp.GetType().GetField("_armorType", all);
                if (_fCompArmor == null) return false;
                type = Convert.ToInt32(_fCompArmor.GetValue(obp));
                if (type >= First && type < First + Names.Length)
                {
                    custom = Pretty[type - First]; bonus = Bonus[type - First];
                    pass = TableOf(type - First);
                    return true;
                }
                if (type < 0 || type > 3) return false;
                bonus = StockBonus[type];
                if (type == 0) { pass = new[] { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f }; return true; }
                object matrix = null;
                try { if (_fGlobDv != null && _fDvMatrix != null) { var dv = _fGlobDv.GetValue(null); if (dv != null) matrix = _fDvMatrix.GetValue(dv); } } catch { }
                pass = Anchor(matrix, type);
                return true;
            }
            catch (Exception e) { AdmPlugin.Trace("ArmorClasses.Info: " + e.Message); return false; }
        }

        static float[] TableOf(int cls)
        {
            object matrix = null;
            try
            {
                if (_fGlobDv != null && _fDvMatrix != null) { var dv = _fGlobDv.GetValue(null); if (dv != null) matrix = _fDvMatrix.GetValue(dv); }
            }
            catch { }
            if (_tables == null || !ReferenceEquals(matrix, _tablesFor))
            {
                var tabs = new float[Names.Length][];
                for (int c = 0; c < Names.Length; c++)
                {
                    float[] a = Anchor(matrix, StockA[c]), b = Anchor(matrix, StockB[c]), r = new float[8];
                    for (int i = 0; i < 8; i++)
                    {
                        float v = (a[i] + (b[i] - a[i]) * StockT[c]) * Scale[c];
                        r[i] = Mathf.Clamp01(v);
                    }
                    for (int i = 0; i < 8; i += 2) if (r[i] > r[i + 1]) r[i] = r[i + 1];
                    tabs[c] = r;
                }
                _tables = tabs; _tablesFor = matrix;
                if (matrix != null)
                {
                    var sb = new System.Text.StringBuilder("armour classes: penetration tables from the game's matrix:");
                    for (int c = 0; c < Names.Length; c++)
                    {
                        sb.Append(' ').Append(Names[c]).Append('=');
                        for (int i = 0; i < 8; i += 2) sb.Append(i == 0 ? "" : "|").Append(tabs[c][i].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(tabs[c][i + 1].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    AdmPlugin.Trace(sb.ToString());
                }
            }
            return _tables[cls];
        }

        // ObjectBase.getMinMaxDamageMultiplier(ArmorType armorType, Penetration penetration): answers for the new types
        static bool MinMaxPrefix(object armorType, object penetration, ref Vector2 __result)
        {
            int a = Convert.ToInt32(armorType);
            if (!On || a < First || a >= First + Names.Length) return true;
            int p = Convert.ToInt32(penetration);
            if (p < 0) p = 0; else if (p > 3) p = 3;
            var t = TableOf(a - First);
            __result = new Vector2(t[p * 2], t[p * 2 + 1]);
            return false;
        }

        public static void Install(Harmony h, Assembly asm, ref int ok, ref int failed)
        {
            var all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var tGlob = asm.GetType("SeaPower.Globals");
            var tDv = asm.GetType("SeaPower.DamageValues");
            _fGlobDv = tGlob == null ? null : tGlob.GetField("_damageValues", all);
            _fDvMatrix = tDv == null ? null : tDv.GetField("_penVsArmorMatrix", all);
            if (_fGlobDv == null || _fDvMatrix == null) AdmPlugin.Warn("Globals._damageValues._penVsArmorMatrix not found: the armour classes use the base game's numbers.");
            var tOb = asm.GetType("SeaPower.ObjectBase");
            var m = tOb == null ? null : tOb.GetMethod("getMinMaxDamageMultiplier", all);
            if (m == null) { AdmPlugin.Warn("ObjectBase.getMinMaxDamageMultiplier not found (game updated?): the armour classes of the WW2 mod are off."); failed++; return; }
            var tLoader = asm.GetType("SeaPower.ObjectBaseLoader");
            var ld = tLoader == null ? null : tLoader.GetMethod("loadObjectFromInternalResources", all);
            if (ld == null) { AdmPlugin.Warn("ObjectBaseLoader.loadObjectFromInternalResources not found (game updated?): the armour classes of the WW2 mod are off."); failed++; return; }
            try
            {
                var me = typeof(ArmorClasses);
                const BindingFlags sb = BindingFlags.Static | BindingFlags.NonPublic;
                h.Patch(ld, prefix: new HarmonyMethod(me.GetMethod("LoaderPrefix", sb)), postfix: new HarmonyMethod(me.GetMethod("LoaderPostfix", sb)),
                        transpiler: new HarmonyMethod(me.GetMethod("LoaderTranspiler", sb)));
                var tBs = asm.GetType("SeaPower.BaseSystem");
                var lb = tBs == null ? null : tBs.GetMethod("LoadBaseParameters", all);
                var tRec = asm.GetType("SeaPower.ModuleRecord");
                var tg = tRec == null ? null : tRec.GetMethod("TryGetArmorType", all);
                if (lb != null)
                    h.Patch(lb, prefix: new HarmonyMethod(me.GetMethod("ModulePrefix", sb)), postfix: new HarmonyMethod(me.GetMethod("ModulePostfix", sb)),
                            transpiler: new HarmonyMethod(me.GetMethod("ModuleTranspiler", sb)));
                else AdmPlugin.Warn("BaseSystem.LoadBaseParameters not found (game updated?): the armour classes of modules are off.");
                if (tg != null) h.Patch(tg, transpiler: new HarmonyMethod(me.GetMethod("ModuleTranspiler", sb)));
                else AdmPlugin.Warn("ModuleRecord.TryGetArmorType not found (game updated?): the armour classes of modules.ini are off.");
                var tComp = asm.GetType("SeaPower.Compartments");
                var ci = tComp == null ? null : tComp.GetMethod("Init", all);
                if (ci != null) h.Patch(ci, transpiler: new HarmonyMethod(me.GetMethod("InitTranspiler", sb)));
                else AdmPlugin.Warn("Compartments.Init not found: the integrity bonus of the new armour classes is off.");
                h.Patch(m, prefix: new HarmonyMethod(typeof(ArmorClasses).GetMethod("MinMaxPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                ok++;
                AdmPlugin.Trace("patched ObjectBase.getMinMaxDamageMultiplier (armour classes)");
            }
            catch (Exception e) { AdmPlugin.Warn("Patching armour classes failed: " + e.Message); failed++; }
        }
    }
}
