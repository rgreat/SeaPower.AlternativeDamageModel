using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Game bug in Compartments.RepairSystems(index): without a priority system it "repairs" the first system of the section's list that is repairable,
    /// not lost and not past its repair limit - also one at full integrity (the integrity is clamped back later). So it returns true for ever:
    /// the teams show the systems task while doing nothing (idle icon), a damaged system further down the list is never repaired, and the
    /// team never counts as idle (it is not sent back to the reserve to rest).
    /// Fix (prefix): with no priority system the first system that really needs repair (integrity below its maximum) is chosen; when it is not
    /// the one the game would take, it is put into the game's priority field for this call (the field only, no "!"), so the game's own code
    /// (with the speed multipliers) repairs it; the field is cleared again in the postfix. Systems before it get the game's side effects
    /// (repaired past their worst level: back in order and switched on). No system to repair: the call returns false (nothing to do).
    /// </summary>
    static class SysRepairFix
    {
        public static Action<string> Trace = s => { };
        static bool _init, _ok;
        static FieldInfo _fName;
        sealed class Seen { public int Last; }
        static readonly ConditionalWeakTable<object, Seen> Said = new ConditionalWeakTable<object, Seen>();
        static string Name(object s) { try { if (_fName == null) _fName = ReserveHits.Field(s.GetType(), "_systemName"); return _fName == null ? s.GetType().Name : Convert.ToString(_fName.GetValue(s)); } catch { return "?"; } }
        /// <summary>Trace once per section and case (1 = nothing to repair, 2 = a damaged system behind an intact one) what the game would have done.</summary>
        static void Say(object sc, int index, int kind, object gameTake, object pick)
        {
            var st = Said.GetOrCreateValue(sc);
            if (st.Last == kind) return;
            st.Last = kind;
            if (kind == 1) Trace("SysRepairFix: section " + index + ": nothing to repair - the game would 'repair' the intact system '" + Name(gameTake) + "' for ever (team kept busy)");
            else if (kind == 2) Trace("SysRepairFix: section " + index + ": the game would 'repair' the intact system '" + Name(gameTake) + "' instead of the damaged '" + Name(pick) + "' - repairing that one");
        }
        static FieldInfo _fCompSys, _fPrio, _fList, _fMax, _fAlways, _fRadarType;
        static PropertyInfo _pRepairable, _pDestroyed, _pCur, _pBig, _pInt, _pInop, _pIsOn;
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static void Init(object comp, object sc, object sys)
        {
            _init = true;
            try
            {
                _fCompSys = ReserveHits.Field(comp.GetType(), "_systemCompartments");
                _fPrio = ReserveHits.Field(sc.GetType(), "_systemPrioritisedForRepair");
                _fList = ReserveHits.Field(sc.GetType(), "_listOfSystems");
                Type tB = sys.GetType();
                while (tB != null && tB.Name != "BaseSystem") tB = tB.BaseType;
                if (tB == null) tB = sys.GetType();
                _fMax = ReserveHits.Field(tB, "_maxIntegrity"); _fAlways = ReserveHits.Field(tB, "_alwaysRepairable");
                _pRepairable = tB.GetProperty("Repairable", All); _pDestroyed = tB.GetProperty("IsDestroyed", All);
                _pCur = tB.GetProperty("CurrentDamageType", All); _pBig = tB.GetProperty("BiggestDamageType", All);
                _pInt = tB.GetProperty("CurrentIntegrity", All); _pInop = tB.GetProperty("Inoperable", All); _pIsOn = tB.GetProperty("IsOn", All);
                _ok = _fCompSys != null && _fPrio != null && _fList != null && _fMax != null && _fAlways != null && _pRepairable != null && _pDestroyed != null &&
                      _pCur != null && _pBig != null && _pInt != null;
                Trace("SysRepairFix: " + (_ok ? "ready" : "members not found - the game's systems repair runs unchanged"));
            }
            catch (Exception e) { _ok = false; Trace("SysRepairFix.Init: " + e.Message); }
        }

        static int Lv(object v) { try { return Convert.ToInt32(v); } catch { return 0; } }

        static void SetReactive(object rp, bool v)
        {
            if (rp == null) return;
            var p = rp.GetType().GetProperty("Value", All);
            if (p != null) p.SetValue(rp, v, null);
        }

        /// <summary>The game's side effect for a system repaired past its worst level: back in order (a radar other than a targeting one stays as it is).</summary>
        static void Restore(object s)
        {
            try
            {
                SetReactive(_pInop == null ? null : _pInop.GetValue(s, null), false);
                bool on = true;
                if (s.GetType().Name == "SensorSystemRadar")
                {
                    if (_fRadarType == null) _fRadarType = ReserveHits.Field(s.GetType(), "_type");
                    on = _fRadarType != null && Convert.ToString(_fRadarType.GetValue(s)) == "Targeting";
                    if (!on) return;
                }
                SetReactive(_pIsOn == null ? null : _pIsOn.GetValue(s, null), true);
            }
            catch { }
        }

        /// <summary>Prefix of Compartments.RepairSystems: false = skipped (result set).</summary>
        public static bool Prefix(object comp, int index, ref bool result, out object state)
        {
            state = null;
            try
            {
                if (_init && !_ok) return true;
                var arr = (_fCompSys ?? ReserveHits.Field(comp.GetType(), "_systemCompartments"))?.GetValue(comp) as Array;
                if (arr == null || index < 0 || index >= arr.Length) return true;
                var sc = arr.GetValue(index);
                if (sc == null) return true;
                if (!_init)
                {
                    var l0 = ReserveHits.Field(sc.GetType(), "_listOfSystems")?.GetValue(sc) as IEnumerable;
                    if (l0 == null) return true;
                    object first = null; foreach (var x in l0) { first = x; break; }
                    if (first == null) return true;
                    Init(comp, sc, first);
                    if (!_ok) return true;
                }
                if (_fPrio.GetValue(sc) != null) return true;   // a priority system: the game's own branch
                var list = _fList.GetValue(sc) as IEnumerable;
                if (list == null) return true;
                object gameTake = null, pick = null;
                foreach (var s in list)
                {
                    if (s == null) continue;
                    if (!(bool)_pRepairable.GetValue(s, null)) continue;
                    if ((bool)_pDestroyed.GetValue(s, null) && !(bool)_fAlways.GetValue(s)) continue;
                    if (Lv(_pBig.GetValue(s, null)) > Lv(_pCur.GetValue(s, null))) { if (pick == null) Restore(s); continue; }   // repaired as far as it can be
                    if (gameTake == null) gameTake = s;
                    float cur = Convert.ToSingle(_pInt.GetValue(s, null)), max = Convert.ToSingle(_fMax.GetValue(s));
                    if (cur < max - 0.001f) { pick = s; break; }
                }
                if (pick == null) { if (gameTake != null) Say(sc, index, 1, gameTake, null); result = false; return false; }   // nothing to repair (the game would "repair" an intact system for ever)
                if (ReferenceEquals(pick, gameTake)) { Said.GetOrCreateValue(sc).Last = 0; return true; }      // the game takes this one itself
                Say(sc, index, 2, gameTake, pick);
                _fPrio.SetValue(sc, pick);                             // the game's priority branch repairs it this once
                state = sc;
                return true;
            }
            catch (Exception e) { Trace("SysRepairFix.Prefix: " + e.Message); return true; }
        }

        /// <summary>
        /// The system of a section (SystemCompartment) that RepairSystems repairs when a team works on systems there: the priority system (when the
        /// game's priority branch takes it), else the first system that really needs repair (as Prefix chooses). Null: none, or unknown (not ready).
        /// </summary>
        public static object Target(object sc)
        {
            try
            {
                if (!_ok || sc == null) return null;
                var pr = _fPrio.GetValue(sc);
                if (pr != null && (bool)_pRepairable.GetValue(pr, null) && !((bool)_pDestroyed.GetValue(pr, null) && !(bool)_fAlways.GetValue(pr)) &&
                    Lv(_pBig.GetValue(pr, null)) <= Lv(_pCur.GetValue(pr, null))) return pr;
                var list = _fList.GetValue(sc) as IEnumerable;
                if (list == null) return null;
                foreach (var s in list)
                {
                    if (s == null) continue;
                    if (!(bool)_pRepairable.GetValue(s, null)) continue;
                    if ((bool)_pDestroyed.GetValue(s, null) && !(bool)_fAlways.GetValue(s)) continue;
                    if (Lv(_pBig.GetValue(s, null)) > Lv(_pCur.GetValue(s, null))) continue;
                    float cur = Convert.ToSingle(_pInt.GetValue(s, null)), max = Convert.ToSingle(_fMax.GetValue(s));
                    if (cur < max - 0.001f) return s;
                }
            }
            catch { }
            return null;
        }

        /// <summary>True when Target can be used (the members were found on the first repair call).</summary>
        public static bool Ready { get { return _ok; } }

        public static void Postfix(object state)
        {
            if (state == null) return;
            try { _fPrio.SetValue(state, null); } catch { }
        }
    }
}
