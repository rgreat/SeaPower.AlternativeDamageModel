using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Several repair priorities in one section. The game keeps one system per section (SystemCompartment.SystemPrioritisedForRepair): the "!" button
    /// of another system takes the priority away from the first. Here every system whose "!" is on stays prioritised; the section repairs them
    /// one after another, in the order they were set; the game's own field always holds the one being repaired.
    ///   - the setter of SystemPrioritisedForRepair keeps the old system's flag on (while the setter runs, IsPriority = false is skipped) and
    ///     remembers the new one; a null from the game (the system is repaired or lost) drops the old one from the list;
    ///   - the "!" turned off by hand takes the system out of the list;
    ///   - before RepairSystems the list is cleaned (repaired / lost / undamaged systems drop out, as the game does for one) and the first
    ///     system left is put into the game's field, so the game's own code repairs it.
    /// </summary>
    static class SysPriority
    {
        public static bool Enabled;
        public static Action<string> Trace = s => { };

        sealed class Set { public readonly List<object> Items = new List<object>(); }
        static readonly ConditionalWeakTable<object, Set> Sets = new ConditionalWeakTable<object, Set>();
        [ThreadStatic] static bool _inSetter;

        static bool _init, _ok;
        static FieldInfo _fPrio, _fSumComp, _fSumSystem, _fCompSys, _fSysSummary;
        static PropertyInfo _pIsPriority, _pRepairable, _pDestroyed, _pBiggest, _pCurrent, _pInop;
        static FieldInfo _fAlways;
        static PropertyInfo _pInopValue;
        static readonly BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static PropertyInfo Prop(Type t, string n)
        {
            for (; t != null; t = t.BaseType) { var p = t.GetProperty(n, All | BindingFlags.DeclaredOnly); if (p != null) return p; }
            return null;
        }

        static void Init(object comp)
        {
            _init = true;
            try
            {
                var tSc = comp.GetType();
                var asm = tSc.Assembly;
                _fPrio = ReserveHits.Field(tSc, "_systemPrioritisedForRepair");
                var tSum = asm.GetType("SeaPower.DamageSummary") ?? asm.GetType("SeapowerUI.DamageSummary");
                if (tSum == null) foreach (var t in asm.GetTypes()) if (t.Name == "DamageSummary") { tSum = t; break; }
                var tBase = asm.GetType("SeaPower.BaseSystem");
                if (tSum != null)
                {
                    _pIsPriority = Prop(tSum, "IsPriority");
                    _fSumComp = ReserveHits.Field(tSum, "compartment"); _fSumSystem = ReserveHits.Field(tSum, "system");
                }
                if (tBase != null)
                {
                    _fSysSummary = ReserveHits.Field(tBase, "damageSummary"); _fAlways = ReserveHits.Field(tBase, "_alwaysRepairable");
                    _pRepairable = Prop(tBase, "Repairable"); _pDestroyed = Prop(tBase, "IsDestroyed");
                    _pBiggest = Prop(tBase, "BiggestDamageType"); _pCurrent = Prop(tBase, "CurrentDamageType"); _pInop = Prop(tBase, "Inoperable");
                }
                _ok = _fPrio != null && _pIsPriority != null && _fSumComp != null && _fSumSystem != null && _fSysSummary != null && _pRepairable != null &&
                      _pDestroyed != null && _pBiggest != null && _pCurrent != null && _pInop != null;
                if (!_ok) Trace("SysPriority: members missing (game updated?)");
            }
            catch (Exception e) { Trace("SysPriority.Init: " + e.Message); }
        }

        static Set Get(object sc, bool create)
        {
            Set s;
            if (Sets.TryGetValue(sc, out s)) return s;
            if (!create) return null;
            s = new Set(); Sets.Add(sc, s); return s;
        }

        static object Summary(object system) { try { return system == null ? null : _fSysSummary.GetValue(system); } catch { return null; } }
        static void Flag(object system, bool on)
        {
            try { var sum = Summary(system); if (sum != null && (bool)_pIsPriority.GetValue(sum, null) != on) _pIsPriority.SetValue(sum, on, null); }
            catch (Exception e) { Trace("SysPriority.Flag: " + e.Message); }
        }

        static int Lv(object v) { try { return Convert.ToInt32(v); } catch { return 0; }}

        /// <summary>The section (SystemCompartment) has a system marked "!" for repair that still has to be repaired (Auto DC: systems first).</summary>
        public static bool Pending(object sc)
        {
            if (sc == null) return false;
            try
            {
                if (!_init) Init(sc);
                if (!_ok) return false;
                var s = _fPrio.GetValue(sc);
                if (s == null) return false;
                bool lost = (bool)_pDestroyed.GetValue(s, null) && !(_fAlways != null && (bool)_fAlways.GetValue(s)) || !(bool)_pRepairable.GetValue(s, null);
                return !lost && Lv(_pCurrent.GetValue(s, null)) > 0;
            }
            catch { return false; }
        }

        static PropertyInfo _pSetPrio;

        /// <summary>The section's "!" is on this system now.</summary>
        public static bool IsMarked(object sc, object system)
        {
            if (sc == null || system == null) return false;
            try { if (!_init) Init(sc); return _ok && ReferenceEquals(_fPrio.GetValue(sc), system); } catch { return false; }
        }

        /// <summary>Moves the "!" of a system from one section to another one that holds it too (a system spans several sections; each repairs only its own
        /// marked system): the old section's mark goes (as the game drops one, so its flag is cleared), then the new one is set. false = nothing moved.</summary>
        public static bool Move(object from, object to, object system)
        {
            if (from == null || to == null || system == null || ReferenceEquals(from, to)) return false;
            try
            {
                if (!_init) Init(from);
                if (!_ok || !ReferenceEquals(_fPrio.GetValue(from), system)) return false;
                if (_pSetPrio == null) _pSetPrio = Prop(from.GetType(), "SystemPrioritisedForRepair");
                if (_pSetPrio == null) return false;
                _pSetPrio.SetValue(from, null, null);
                return Mark(to, system);
            }
            catch (Exception e) { Trace("SysPriority.Move: " + e.Message); return false; }
        }

        /// <summary>Marks the system for priority repair in its section (as the "!" button does) if it can still be repaired. false = it cannot (lost / not repairable / no damage left).</summary>
        public static bool Mark(object sc, object system)
        {
            if (sc == null || system == null) return false;
            try
            {
                if (!_init) Init(sc);
                if (!_ok) return false;
                bool lost = (bool)_pDestroyed.GetValue(system, null) && !(_fAlways != null && (bool)_fAlways.GetValue(system)) || !(bool)_pRepairable.GetValue(system, null);
                if (lost || Lv(_pCurrent.GetValue(system, null)) <= 0) return false;
                var cur = _fPrio.GetValue(sc);
                if (ReferenceEquals(cur, system)) return true;
                if (_pSetPrio == null) _pSetPrio = Prop(sc.GetType(), "SystemPrioritisedForRepair");
                if (_pSetPrio == null) return false;
                _pSetPrio.SetValue(sc, system, null);
                return true;
            }
            catch (Exception e) { Trace("SysPriority.Mark: " + e.Message); return false; }
        }

        // ------------------------------------------------------------ patches

        /// <summary>Prefix of SystemCompartment.set_SystemPrioritisedForRepair.</summary>
        public static void SetterPrefix(object __instance, out object __state)
        {
            __state = null;
            if (!Enabled) return;
            if (!_init) Init(__instance);
            if (!_ok) return;
            try { __state = _fPrio.GetValue(__instance); } catch { }
            _inSetter = true;
        }

        /// <summary>Postfix of the same setter.</summary>
        public static void SetterPostfix(object __instance, object value, object __state)
        {
            if (!_inSetter) return;
            _inSetter = false;
            if (!Enabled || !_ok) return;
            try
            {
                var set = Get(__instance, true);
                if (value != null)
                {
                    if (__state != null && !set.Items.Contains(__state)) set.Items.Add(__state);   // chosen before the list existed
                    set.Items.Remove(value); set.Items.Add(value);                                  // the last one chosen goes to the end of the queue
                }
                else if (__state != null)
                {
                    set.Items.Remove(__state);   // the game is through with it (repaired / lost)
                    Flag(__state, false);
                }
            }
            catch (Exception e) { Trace("SysPriority.SetterPostfix: " + e.Message); }
        }

        /// <summary>Prefix of DamageSummary.set_IsPriority: while the compartment's setter runs, the old system keeps its flag.</summary>
        public static bool FlagPrefix(bool value)
        {
            return !(Enabled && _inSetter && !value);
        }

        /// <summary>Postfix of DamageSummary.set_IsPriority: the "!" turned off by hand.</summary>
        public static void FlagPostfix(object __instance, bool value)
        {
            if (!Enabled || _inSetter || value || !_ok) return;
            try
            {
                var sc = _fSumComp.GetValue(__instance); var sys = _fSumSystem.GetValue(__instance);
                if (sc == null || sys == null) return;
                var set = Get(sc, false);
                if (set != null) set.Items.Remove(sys);
                if (ReferenceEquals(_fPrio.GetValue(sc), sys)) _fPrio.SetValue(sc, set != null && set.Items.Count > 0 ? set.Items[0] : null);
            }
            catch (Exception e) { Trace("SysPriority.FlagPostfix: " + e.Message); }
        }

        /// <summary>Prefix of Compartments.RepairSystems(index): cleans the list and puts the first system into the game's field.</summary>
        public static void RepairPrefix(object __instance, int index)
        {
            if (!Enabled || !_ok) return;
            try
            {
                if (_fCompSys == null) _fCompSys = ReserveHits.Field(__instance.GetType(), "_systemCompartments");
                var arr = _fCompSys == null ? null : _fCompSys.GetValue(__instance) as Array;
                if (arr == null || index < 0 || index >= arr.Length) return;
                var sc = arr.GetValue(index);
                var set = sc == null ? null : Get(sc, false);
                if (set == null || set.Items.Count == 0) return;
                var cur = _fPrio.GetValue(sc);
                if (cur != null && !set.Items.Contains(cur)) set.Items.Insert(0, cur);
                object pick = null;
                for (int i = 0; i < set.Items.Count;)
                {
                    var s = set.Items[i];
                    bool lost = (bool)_pDestroyed.GetValue(s, null) && !(_fAlways != null && (bool)_fAlways.GetValue(s)) || !(bool)_pRepairable.GetValue(s, null);
                    int curLv = Lv(_pCurrent.GetValue(s, null)), bigLv = Lv(_pBiggest.GetValue(s, null));
                    if (lost || curLv == 0) { set.Items.RemoveAt(i); Flag(s, false); continue; }
                    if (bigLv > curLv)   // repaired one damage level past the worst it had: done, as the game ends it
                    {
                        var inop = _pInop.GetValue(s, null);
                        if (inop != null) { if (_pInopValue == null) _pInopValue = inop.GetType().GetProperty("Value", All); if (_pInopValue != null) _pInopValue.SetValue(inop, false, null); }
                        set.Items.RemoveAt(i); Flag(s, false); continue;
                    }
                    if (pick == null) pick = s;
                    i++;
                }
                if (!ReferenceEquals(_fPrio.GetValue(sc), pick)) _fPrio.SetValue(sc, pick);
            }
            catch (Exception e) { Trace("SysPriority.RepairPrefix: " + e.Message); }
        }
    }
}
