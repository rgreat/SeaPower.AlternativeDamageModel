using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlternativeDamageModel
{
    /// <summary>
    /// What the damage control teams do (game logic, no UI):
    ///  - a ship-wide priority mode: Auto (the game's order), Fire first, Flooding first. It changes the order of tasks in a
    ///    compartment (Compartments.CheckDamageControl) and where Auto DC sends a free team (Compartments.CheckDCTeamsAllocation);
    ///  - per team, while Auto DC is off: a task given by hand to one team in one compartment, kept while the team stays there.
    ///    The game itself has no teams, only a count per compartment; the task is kept on the mod's team object (DcTeams.Team.Task),
    ///    so it stays with that team when others arrive or leave, and is gone when the team leaves the section.
    /// Priority and hand-given tasks need the detailed view (IsEnabled); the optimised Auto DC and idle teams staying where they are
    /// need only the damage model (AutoOn) - the replaced CheckDamageControl then runs with mode Auto and no tasks by hand.
    /// CheckDamageControl is replaced while the mod is on; with mode Auto and no hand-given tasks it does exactly what the game does:
    /// the first task in the order that has work is done by all teams of the compartment, none -> with Auto DC the team returns.
    /// The game's Repair* methods take their strength from DamageControlTeamsNumbers[i]; for a group of teams the count is set to
    /// the group's size for the call and put back right after (no notification in between).
    /// </summary>
    static class DcControl
    {
        public const int TaskNone = 0, TaskIdle = 1, TaskLeak = 2, TaskFire = 3, TaskPump = 4, TaskHull = 5, TaskSystems = 6, TaskCounter = 7;
        public const int ModeAuto = 0, ModeFire = 1, ModeFlooding = 2;

        static readonly int[][] Orders =
        {
            new[] { TaskLeak, TaskFire, TaskPump, TaskSystems, TaskHull },   // Auto (a growing fire goes first in DcAuto)
            new[] { TaskFire, TaskLeak, TaskPump, TaskSystems, TaskHull },   // fire first
            new[] { TaskLeak, TaskPump, TaskFire, TaskSystems, TaskHull },   // flooding first
        };

        public static Func<bool> IsEnabled = () => false;   // DC Priority, tasks by hand (the detailed view)
        public static Func<bool> AutoOn = () => false;      // the optimised Auto DC (the damage model)
        /// <summary>Optimised Auto DC: a team with nothing to do waits where it is (no walk back to the reserve).</summary>
        public static Func<object, bool> StayWhenIdle = c => false;
        public static Action<string> Trace = s => { };
        public static float LeakPump = 0.5f;      // [FireAndFlooding] LeakPumpShare: pumping out while plugging holes, × the pumping speed
        public static float CounterRate = 2f;     // [FireAndFlooding] CounterFloodSpeed: × the pumping speed
        public static float CounterLimit = 50f;   // [FireAndFlooding] CounterFloodLimit: water in one compartment, %, up to which it is let in

        sealed class Ship
        {
            public int Mode;
            public bool LimitInit;
            public bool NoSpeedLimit;   // "Limit Speed" off
            public readonly List<int>[] Pending = new List<int>[8];  // per compartment: tasks from a saved game, put on the teams there
        }
        static readonly ConditionalWeakTable<object, Ship> Ships = new ConditionalWeakTable<object, Ship>();

        // game members
        static bool _ok;
        static PropertyInfo _teams, _autoDc;
        static FieldInfo _sinking, _port, _star, _sys, _floodRate, _growRate, _count;
        static PropertyInfo _floodDelta, _severity;
        static FieldInfo _curFlood, _maxFlood, _baseObj, _crewF, _repairF;
        static MethodInfo _getTransform;
        static PropertyInfo _fixedDt;
        static MethodInfo _returnTeam, _sendTeam;
        static readonly Func<object, int, bool>[] Repairs = new Func<object, int, bool>[8];

        public static bool Init(Assembly asm)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var tComp = asm.GetType("SeaPower.Compartments");
                var tFc = asm.GetType("SeaPower.FloodingCompartment");
                var tSc = asm.GetType("SeaPower.SystemCompartment");
                _teams = tComp.GetProperty("DamageControlTeamsNumbers", all);
                _autoDc = tComp.GetProperty("AutoDC", all);
                _sinking = tComp.GetField("_isSinking", all);
                _port = tComp.GetField("_portCompartments", all);
                _star = tComp.GetField("_starboardCompartments", all);
                _sys = tComp.GetField("_systemCompartments", all);
                _count = tComp.GetField("_compartmentsCount", all);
                _floodRate = tFc.GetField("_floodingRate", all);
                _floodDelta = tFc.GetProperty("FloodingDelta", all);
                _severity = tSc.GetProperty("FireSeverity", all);
                _growRate = tSc.GetField("_fireGrowRate", all);
                _returnTeam = tComp.GetMethod("ReturnDCTeamToPool", all);
                _sendTeam = tComp.GetMethod("SendDCTeamToCompartment", all);
                _leakOrig = Caller(tComp.GetMethod("RepairFloodingRate", all));
                Repairs[TaskLeak] = _leakOrig == null ? null : LeakWithPump;
                Repairs[TaskFire] = Caller(tComp.GetMethod("RepairFire", all));
                _pumpOrig = Caller(tComp.GetMethod("RepairFlooding", all));
                _drainSide = tComp.GetField("_drainPortSide", all);
                Repairs[TaskPump] = _pumpOrig == null ? null : PumpMoreWater;
                Repairs[TaskHull] = Caller(tComp.GetMethod("RepairStructure", all));
                Repairs[TaskSystems] = Caller(tComp.GetMethod("RepairSystems", all));
                Repairs[TaskCounter] = CounterFlood;
                _curFlood = tFc.GetField("_currentFlooding", all); _maxFlood = tFc.GetField("_maxFlooding", all);
                _baseObj = tComp.GetField("_baseObject", all);
                _getTransform = _baseObj == null ? null : _baseObj.FieldType.GetMethod("getTransform", all);
                _crewF = _baseObj == null ? null : _baseObj.FieldType.GetField("_crew", all);
                _repairF = _crewF == null ? null : _crewF.FieldType.GetField("_floodingRepair", all);
                var tGt = asm.GetType("SeaPower.GameTime");
                _fixedDt = tGt == null ? null : tGt.GetProperty("fixedDeltaTime", all);
                if (_curFlood == null || _maxFlood == null || _getTransform == null || _crewF == null || _repairF == null || _fixedDt == null)
                { Repairs[TaskCounter] = null; Trace("DcControl: counterflooding is not available (game members not found)"); }
                _ok = _teams != null && _autoDc != null && _sinking != null && _port != null && _star != null && _sys != null && _count != null &&
                      _floodRate != null && _floodDelta != null && _severity != null && _growRate != null && _returnTeam != null && _sendTeam != null &&
                      Repairs[TaskLeak] != null && Repairs[TaskFire] != null && Repairs[TaskPump] != null && Repairs[TaskHull] != null && Repairs[TaskSystems] != null;
            }
            catch (Exception e) { Trace("DcControl.Init: " + e); _ok = false; }
            return _ok;
        }

        // ------------------------------------------------------------ counterflooding (by hand only)

        /// <summary>The ship's list in degrees: positive = the port side is down (as the game reads it: Euler z of the hull).</summary>
        static float ListOf(object comp)
        {
            var tr = _getTransform.Invoke(_baseObj.GetValue(comp), null) as UnityEngine.Transform;
            if (tr == null) return 0f;
            float z = tr.localEulerAngles.z;
            return z > 180f ? z - 360f : z;
        }

        /// <summary>The side of compartment i that counterflooding lets the water into: +1 port, -1 starboard, 0 = none. The side opposite to the heel
        /// (list over 0.3 degrees); on an even keel the side with less water (so the trim can be corrected from the end sections without a list).</summary>
        public static int CounterSide(object comp, int i)
        {
            if (!_ok || comp == null || Repairs[TaskCounter] == null || i < 0 || i > 4) return 0;
            try
            {
                float list = ListOf(comp);
                if (list > 0.3f) return -1;
                if (list < -0.3f) return 1;
                var port = _port.GetValue(comp) as Array; var star = _star.GetValue(comp) as Array;
                return (float)_curFlood.GetValue(port.GetValue(i)) <= (float)_curFlood.GetValue(star.GetValue(i)) ? 1 : -1;
            }
            catch { return 0; }
        }

        /// <summary>Lets water into one side of compartment i at CounterRate × the pumping speed (the same crew skill, the same number of teams).
        /// False = nothing to do (that side is at the limit): the team then works by priority.</summary>
        static bool CounterFlood(object comp, int i)
        {
            int side = CounterSide(comp, i);
            if (side == 0) return false;
            var teams = _teams.GetValue(comp, null) as int[];
            var arr = (side > 0 ? _port : _star).GetValue(comp) as Array;
            var fc = arr.GetValue(i);
            float cur = (float)_curFlood.GetValue(fc);
            float cap = Math.Min(CounterLimit, (float)_maxFlood.GetValue(fc));
            if (cur >= cap) return false;
            float rate = AdmPlugin.PumpOutMul((float)_repairF.GetValue(_crewF.GetValue(_baseObj.GetValue(comp))));   // as fast as pumping (with PumpOutSpeed), times CounterRate
            float add = teams[i] * rate * Convert.ToSingle(_fixedDt.GetValue(null, null)) * CounterRate;
            _curFlood.SetValue(fc, Math.Min(cap, cur + add));
            return true;
        }

        /// <summary>Water pumped out per team per second, % (the crew's skill and PumpOutSpeed): for the tooltips.</summary>
        public static float PumpPerTeam(object comp)
        {
            try { return AdmPlugin.PumpOutMul((float)_repairF.GetValue(_crewF.GetValue(_baseObj.GetValue(comp)))); } catch { return 0f; }
        }

        /// <summary>The trim in degrees: positive = the bow is down (Euler x of the hull, as the game reads it).</summary>
        static float TrimOf(object comp)
        {
            var tr = _getTransform.Invoke(_baseObj.GetValue(comp), null) as UnityEngine.Transform;
            if (tr == null) return 0f;
            float x = tr.localEulerAngles.x;
            return x > 180f ? x - 360f : x;
        }

        /// <summary>
        /// Pumping out for a team that is ordered to counterflood: only from a side (and a section) where it does not make the list / trim worse.
        /// Taking water out of a side lifts it: that is bad for the list when this is the side that is already up (opposite to the heel), and for the trim when
        /// the end of this section is already up (a forward section with the bow up, an after one with the stern up). The middle section does not touch the trim.
        /// Pumps as the game does (teams × crew skill × PumpOutSpeed × dt, down to the side's minimum) and returns false when nothing may be pumped.
        /// </summary>
        static bool PumpSafe(object comp, int i) { return PumpSafe(comp, i, 1f); }

        static Func<object, int, bool> _leakOrig, _pumpOrig;
        static FieldInfo _drainSide;
        static bool _safeLeak;

        /// <summary>Pumping out (the game's RepairFlooding) starts from the side with more water above its minimum. The side changes to the other one
        /// when this one is at its minimum, or when the other has more water and the last change was at least SwitchEvery seconds of pumping ago.</summary>
        const float SwitchEvery = 10f;
        static readonly ConditionalWeakTable<object, float[]> SideAge = new ConditionalWeakTable<object, float[]>();

        static bool PumpMoreWater(object comp, int i)
        {
            try
            {
                var arr = _drainSide == null ? null : _drainSide.GetValue(comp) as bool[];
                if (arr != null && i < arr.Length)
                {
                    float dp = (float)_floodDelta.GetValue((_port.GetValue(comp) as Array).GetValue(i), null);
                    float ds = (float)_floodDelta.GetValue((_star.GetValue(comp) as Array).GetValue(i), null);
                    var age = SideAge.GetValue(comp, c => new float[] { 99f, 99f, 99f, 99f, 99f, 99f, 99f, 99f });
                    age[i] += Convert.ToSingle(_fixedDt.GetValue(null, null));
                    if (dp > 1e-4f || ds > 1e-4f)
                    {
                        bool cur = arr[i], want = cur;
                        float dc = cur ? dp : ds, dother = cur ? ds : dp;
                        if (dc <= 1e-4f) want = !cur;                                   // this side is at its minimum
                        else if (dother > dc && age[i] >= SwitchEvery) want = !cur;     // the other has more water: not more often than SwitchEvery
                        if (want != cur) { arr[i] = want; age[i] = 0f; }
                    }
                }
            }
            catch { }
            return _pumpOrig(comp, i);
        }

        /// <summary>Plugging holes (the game's RepairFloodingRate) and, at the same time, pumping out at LeakPump × the pumping speed (for a team ordered to counterflood: only where that does not worsen the list / trim).</summary>
        static bool LeakWithPump(object comp, int i)
        {
            if (SpeedEffect.TooFastToPlug(comp)) return false;   // too fast: the team works by priority
            bool worked = _leakOrig(comp, i);
            if (worked && LeakPump > 0f) { if (_safeLeak) PumpSafe(comp, i, LeakPump); else PumpFree(comp, i, LeakPump); }
            return worked;
        }

        /// <summary>Pumping as the game does (the side with more water first), times share; no checks of the list / trim.</summary>
        static void PumpFree(object comp, int i, float share)
        {
            try
            {
                var ports = _port.GetValue(comp) as Array; var stars = _star.GetValue(comp) as Array;
                object fp = ports.GetValue(i), fs = stars.GetValue(i);
                float wp = (float)_curFlood.GetValue(fp), ws = (float)_curFlood.GetValue(fs);
                float dp = (float)_floodDelta.GetValue(fp, null), ds = (float)_floodDelta.GetValue(fs, null);
                if (dp <= 1e-4f && ds <= 1e-4f) return;
                bool port = dp >= ds;
                var side = _drainSide == null ? null : _drainSide.GetValue(comp) as bool[];
                if (side != null && i < side.Length) port = side[i] ? dp > 1e-4f : ds <= 1e-4f;   // the side the pumping works on (PumpMoreWater)
                var teams = _teams.GetValue(comp, null) as int[];
                float rate = AdmPlugin.PumpOutMul((float)_repairF.GetValue(_crewF.GetValue(_baseObj.GetValue(comp))));
                float dec = teams[i] * rate * Convert.ToSingle(_fixedDt.GetValue(null, null)) * share;
                var f = port ? fp : fs; float cur = port ? wp : ws, d = port ? dp : ds;
                _curFlood.SetValue(f, cur - Math.Min(d, dec));
            }
            catch (Exception e) { Trace("PumpFree: " + e.Message); }
        }

        static bool PumpSafe(object comp, int i, float share)
        {
            if (Repairs[TaskCounter] == null) return Repairs[TaskPump](comp, i);   // no way to tell: the game's pumping
            try
            {
                float list = ListOf(comp), trim = TrimOf(comp);
                if (i < 2 && trim < -0.2f) return false;    // forward section, bow already up
                if (i > 2 && trim > 0.2f) return false;     // after section, stern already up
                var ports = _port.GetValue(comp) as Array; var stars = _star.GetValue(comp) as Array;
                object fp = ports.GetValue(i), fs = stars.GetValue(i);
                float wp = (float)_curFlood.GetValue(fp), ws = (float)_curFlood.GetValue(fs);
                float minP = wp - (float)_floodDelta.GetValue(fp, null), minS = ws - (float)_floodDelta.GetValue(fs, null);   // FloodingDelta = water above the minimum
                bool canPort = wp > minP + 1e-4f && !(list < -0.3f);   // the port side is the high one when the list is to starboard
                bool canStar = ws > minS + 1e-4f && !(list > 0.3f);
                if (!canPort && !canStar) return false;
                bool port = canPort && (!canStar || wp - minP >= ws - minS);
                var teams = _teams.GetValue(comp, null) as int[];
                float rate = AdmPlugin.PumpOutMul((float)_repairF.GetValue(_crewF.GetValue(_baseObj.GetValue(comp))));
                float dec = teams[i] * rate * Convert.ToSingle(_fixedDt.GetValue(null, null)) * share;
                var f = port ? fp : fs; float cur = port ? wp : ws, min = port ? minP : minS;
                _curFlood.SetValue(f, Math.Max(min, cur - dec));
                return true;
            }
            catch (Exception e) { Trace("PumpSafe: " + e.Message); return false; }
        }

        /// <summary>A fast call of a private bool Method(int) of Compartments; falls back to reflection.</summary>
        static Func<object, int, bool> Caller(MethodInfo m)
        {
            if (m == null) return null;
            try { return HarmonyLib.AccessTools.MethodDelegate<Func<object, int, bool>>(m); }
            catch (Exception e)
            {
                Trace("DcControl: delegate for " + m.Name + " failed (" + e.Message + "); using reflection");
                return (o, i) => (bool)m.Invoke(o, new object[] { i });
            }
        }

        // ------------------------------------------------------------ state, read by the UI

        public static int GetMode(object comp)
        {
            Ship s;
            return comp != null && Ships.TryGetValue(comp, out s) ? s.Mode : ModeAuto;
        }

        /// <summary>"Limit Speed" button: the game cuts the ship's speed by the flooding (10-25 kn). Default on; off = no cut.</summary>
        public static bool LimitSpeed(object comp)
        {
            if (comp == null) return true;
            var s = Ships.GetOrCreateValue(comp);
            // the start value, set once when the ship is first asked (the game's first speed update after the spawn): a small AI ship starts with
            // the button off ([FireAndFlooding] SmallAiNoSpeedLimit); after that it is the button's own state
            if (!s.LimitInit) { bool free = SpeedEffect.NoCutFor(comp); if (free) s.NoSpeedLimit = true; if (!SpeedEffect.Failed) s.LimitInit = true; }
            return !s.NoSpeedLimit;
        }

        public static void SetLimitSpeed(object comp, bool on)
        {
            if (comp == null) return;
            var sh = Ships.GetOrCreateValue(comp);
            sh.LimitInit = true; sh.NoSpeedLimit = !on;
            Trace("Limit Speed -> " + (on ? "on" : "off"));
        }

        public static void SetMode(object comp, int mode)
        {
            if (comp == null || mode < ModeAuto || mode > ModeFlooding) return;
            Ships.GetOrCreateValue(comp).Mode = mode;
            Trace("DC priority -> " + (mode == ModeFire ? "fire" : mode == ModeFlooding ? "flooding" : "auto"));
        }

        /// <summary>Per team in compartment i (slot k = k-th team there): the task given by hand (0 = by priority), what it did
        /// last tick, and the team object itself.</summary>
        public static void GetTeams(object comp, int i, int n, out int[] forced, out int[] actual)
        {
            object[] refs;
            GetTeams(comp, i, n, out forced, out actual, out refs);
        }

        public static void GetTeams(object comp, int i, int n, out int[] forced, out int[] actual, out object[] refs)
        {
            forced = new int[Math.Max(0, n)];
            actual = new int[Math.Max(0, n)];
            refs = new object[Math.Max(0, n)];
            if (comp == null || i < 0 || i > 4) return;
            var list = DcTeams.SectionTeams(comp, i);
            if (list == null) return;
            for (int k = 0; k < n && k < list.Count; k++) { forced[k] = list[k].Task; actual[k] = list[k].Doing; refs[k] = list[k]; }
        }

        /// <summary>Gives a team a task by hand (0 = back to the priority order). Only while Auto DC is off.</summary>
        public static void SetForced(object comp, object teamRef, int task)
        {
            var t = teamRef as DcTeams.Team;
            if (comp == null || t == null || t.State != DcTeams.St.InSection || AutoDc(comp)) return;
            t.Task = task;
            Trace("DC team in compartment " + t.Section + " -> task " + task);
        }

        // ------------------------------------------------------------ saved games

        /// <summary>
        /// "mode:t,t|t||..." - the priority mode and, per compartment, the tasks given by hand of its teams in order (0 = by
        /// priority). Null when there is nothing to keep (Auto and no tasks by hand).
        /// </summary>
        public static string Save(object comp)
        {
            if (comp == null) return null;
            Ship s;
            int mode = Ships.TryGetValue(comp, out s) ? s.Mode : ModeAuto;
            var sb = new System.Text.StringBuilder();
            sb.Append(mode).Append(':');   // not ';' - the game's ini reader takes ';' for a comment
            bool any = mode != ModeAuto;
            for (int i = 0; i < 5; i++)
            {
                if (i > 0) sb.Append('|');
                var list = DcTeams.SectionTeams(comp, i);
                if (list == null) continue;
                for (int k = 0; k < list.Count; k++) { if (k > 0) sb.Append(','); sb.Append(list[k].Task); if (list[k].Task != TaskNone) any = true; }
            }
            return any ? sb.ToString() : null;
        }

        public static void Load(object comp, string text)
        {
            if (comp == null || string.IsNullOrEmpty(text)) return;
            try
            {
                var s = Ships.GetOrCreateValue(comp);
                var parts = text.Split(':');
                int mode;
                if (int.TryParse(parts[0], out mode) && mode >= ModeAuto && mode <= ModeFlooding) s.Mode = mode;
                if (parts.Length > 1)
                {
                    var cs = parts[1].Split('|');
                    for (int i = 0; i < cs.Length && i < 5; i++)
                    {
                        var list = new List<int>();
                        foreach (var t in cs[i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            int v;
                            list.Add(int.TryParse(t, out v) && v >= TaskNone && v <= TaskCounter && v != TaskIdle ? v : TaskNone);
                        }
                        s.Pending[i] = list;   // put on the teams at the first check (they are made from the game's counts then)
                    }
                }
                Trace("DC state loaded: " + text);
            }
            catch (Exception e) { Trace("DcControl.Load: " + e.Message); }
        }

        public static bool AutoDc(object comp)
        {
            try { return (bool)_autoDc.GetValue(comp, null); } catch { return true; }
        }

        // ------------------------------------------------------------ hooks

        /// <summary>Prefix of Compartments.CheckDamageControl(int i): false = done here.</summary>
        public static bool CheckPrefix(object comp, int i)
        {
            if (!_ok || comp == null || i < 0 || i > 4) return true;
            bool ui = IsEnabled();
            if (!ui && !AutoOn()) return true;
            var teams = _teams.GetValue(comp, null) as int[];
            if (teams == null || i >= teams.Length) return true;
            int n = teams[i];
            var objs = _objs;
            if (!DcTeams.SectionTeams(comp, i, objs)) return true;
            Ship s;
            Ships.TryGetValue(comp, out s);
            if (s != null && s.Pending[i] != null)
            {
                for (int k = 0; k < s.Pending[i].Count && k < objs.Count; k++) objs[k].Task = s.Pending[i][k];
                s.Pending[i] = null;
            }
            bool autoDc = AutoDc(comp);
            if (autoDc) foreach (var t in objs) t.Task = TaskNone;   // hand-given tasks are for manual control only
            if (n <= 0) return false;   // the game does nothing either
            int mode = ui && s != null ? Math.Max(0, Math.Min(2, s.Mode)) : ModeAuto;

            if (_free == null || _free.Length < n) { _free = new int[Math.Max(8, n)]; _done = new bool[Math.Max(8, n)]; _did = new int[Math.Max(8, n)]; _rest = new int[Math.Max(8, n)]; }
            bool returnTeam = false;
            try
            {
                // teams with a task given by hand, grouped by task
                int nFree = 0, nRest = 0;
                for (int k = 0; k < n; k++) { _done[k] = false; _did[k] = TaskIdle; }
                for (int k = 0; k < n; k++)
                {
                    int t = ui && k < objs.Count ? objs[k].Task : TaskNone;
                    if (t == TaskNone || _done[k]) continue;
                    int size = 0;
                    for (int m = k; m < n; m++) if (m < objs.Count && objs[m].Task == t && !_done[m]) { _done[m] = true; size++; }
                    teams[i] = size;
                    bool worked = Repairs[t] != null && Repairs[t](comp, i);
                    for (int m = k; m < n; m++)
                        if (m < objs.Count && objs[m].Task == t)
                        {
                            if (worked) _did[m] = t;
                            else if (t == TaskCounter) _rest[nRest++] = m;   // nothing to counterflood: by priority, but it pumps only where that does not worsen the list / trim
                            else _free[nFree++] = m;   // nothing to do: by priority
                        }
                }
                for (int k = 0; k < n; k++) if (!_done[k]) _free[nFree++] = k;

                // teams ordered to counterflood that have nothing to let in: the other tasks by priority, but pumping out only where it does not make the list / trim worse
                if (nRest > 0)
                {
                    teams[i] = nRest;
                    int did = TaskIdle;
                    _safeLeak = true;
                    try
                    {
                        foreach (var t in Orders[mode])
                            if (t == TaskPump ? PumpSafe(comp, i) : Repairs[t](comp, i)) { did = t; break; }
                    }
                    finally { _safeLeak = false; }
                    for (int m = 0; m < nRest; m++) _did[_rest[m]] = did;
                }

                // the rest: the first task in the order that has work
                if (nFree > 0)
                {
                    teams[i] = nFree;
                    int did = TaskIdle;
                    foreach (var t in Orders[mode])
                        if (Repairs[t](comp, i)) { did = t; break; }
                    for (int m = 0; m < nFree; m++) _did[_free[m]] = did;
                    returnTeam = did == TaskIdle && nFree == n && autoDc && !StayWhenIdle(comp);   // as the game: nothing to do -> back to the pool
                }
            }
            finally { teams[i] = n; }   // our temporary counts never stay
            for (int k = 0; k < n && k < objs.Count; k++) objs[k].Doing = _did[k];
            if (returnTeam) _returnTeam.Invoke(comp, new object[] { i });
            return false;
        }
        static int[] _free, _did, _rest;   // reused buffers (main thread only)
        static readonly List<DcTeams.Team> _objs = new List<DcTeams.Team>();
        static bool[] _done;

        /// <summary>
        /// Prefix of Compartments.CheckDCTeamsAllocation: with Fire / Flooding first, Auto DC sends a free team to the worst fire /
        /// the worst water before anything else; in every other case the game's own choice runs.
        /// </summary>
        public static bool AllocPrefix(object comp)
        {
            if (!_ok || comp == null) return true;
            if (AutoOn() && !DcAuto.Allocate(comp)) return false;   // the optimised Auto DC did it
            if (!IsEnabled()) return true;
            Ship s;
            if (!Ships.TryGetValue(comp, out s) || s.Mode == ModeAuto) return true;
            try
            {
                if ((bool)_sinking.GetValue(comp)) return true;
                var teams = _teams.GetValue(comp, null) as int[];
                if (teams == null || teams.Length < 6 || teams[5] < 1) return true;
                int count = (int)_count.GetValue(comp);
                var port = _port.GetValue(comp) as Array; var star = _star.GetValue(comp) as Array; var sys = _sys.GetValue(comp) as Array;
                int best = -1; float bestV = 0f;
                if (s.Mode == ModeFire)
                {
                    for (int j = 0; j < count; j++) { float v = (float)_severity.GetValue(sys.GetValue(j), null); if (v > bestV) { bestV = v; best = j; } }
                    if (best < 0)
                        for (int j = 0; j < count; j++) { float v = (float)_growRate.GetValue(sys.GetValue(j)); if (v > bestV) { bestV = v; best = j; } }
                }
                else
                {
                    // leaks come first in the game's order already; after them: water to pump out, before fires
                    for (int j = 0; j < count; j++)
                        if ((float)_floodRate.GetValue(port.GetValue(j)) > 0f || (float)_floodRate.GetValue(star.GetValue(j)) > 0f) return true;
                    for (int j = 0; j < count; j++)
                    {
                        float v = Math.Max((float)_floodDelta.GetValue(port.GetValue(j), null), (float)_floodDelta.GetValue(star.GetValue(j), null));
                        if (v > bestV) { bestV = v; best = j; }
                    }
                }
                if (best < 0) return true;
                _sendTeam.Invoke(comp, new object[] { best });
                return false;
            }
            catch (Exception e) { Trace("AllocPrefix: " + e.Message); return true; }
        }
    }
}
