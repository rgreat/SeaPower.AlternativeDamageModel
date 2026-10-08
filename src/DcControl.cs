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
    ///    The game itself has no teams, only a count per compartment; the mod keeps one slot per team (task or 0 = by priority).
    /// CheckDamageControl is replaced while the mod is on; with mode Auto and no hand-given tasks it does exactly what the game does:
    /// the first task in the order that has work is done by all teams of the compartment, none -> with Auto DC the team returns.
    /// The game's Repair* methods take their strength from DamageControlTeamsNumbers[i]; for a group of teams the count is set to
    /// the group's size for the call and put back right after (no notification in between).
    /// </summary>
    static class DcControl
    {
        public const int TaskNone = 0, TaskIdle = 1, TaskLeak = 2, TaskFire = 3, TaskPump = 4, TaskHull = 5, TaskSystems = 6;
        public const int ModeAuto = 0, ModeFire = 1, ModeFlooding = 2;

        static readonly int[][] Orders =
        {
            new[] { TaskLeak, TaskFire, TaskPump, TaskHull, TaskSystems },   // Auto: the game's order
            new[] { TaskFire, TaskLeak, TaskPump, TaskHull, TaskSystems },   // fire first
            new[] { TaskLeak, TaskPump, TaskFire, TaskHull, TaskSystems },   // flooding first
        };

        public static Func<bool> IsEnabled = () => false;
        /// <summary>Optimised Auto DC: a team with nothing to do waits where it is (no walk back to the reserve).</summary>
        public static Func<object, bool> StayWhenIdle = c => false;
        public static Action<string> Trace = s => { };

        sealed class Ship
        {
            public int Mode;
            public readonly List<int>[] Forced = new List<int>[8];  // per compartment: per team, the task given by hand (0 = by priority)
            public readonly int[][] Actual = new int[8][];           // per compartment: per team, what it did last tick
        }
        static readonly ConditionalWeakTable<object, Ship> Ships = new ConditionalWeakTable<object, Ship>();

        // game members
        static bool _ok;
        static PropertyInfo _teams, _autoDc;
        static FieldInfo _sinking, _port, _star, _sys, _floodRate, _growRate, _count;
        static PropertyInfo _floodDelta, _severity;
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
                Repairs[TaskLeak] = Caller(tComp.GetMethod("RepairFloodingRate", all));
                Repairs[TaskFire] = Caller(tComp.GetMethod("RepairFire", all));
                Repairs[TaskPump] = Caller(tComp.GetMethod("RepairFlooding", all));
                Repairs[TaskHull] = Caller(tComp.GetMethod("RepairStructure", all));
                Repairs[TaskSystems] = Caller(tComp.GetMethod("RepairSystems", all));
                _ok = _teams != null && _autoDc != null && _sinking != null && _port != null && _star != null && _sys != null && _count != null &&
                      _floodRate != null && _floodDelta != null && _severity != null && _growRate != null && _returnTeam != null && _sendTeam != null &&
                      Repairs[TaskLeak] != null && Repairs[TaskFire] != null && Repairs[TaskPump] != null && Repairs[TaskHull] != null && Repairs[TaskSystems] != null;
            }
            catch (Exception e) { Trace("DcControl.Init: " + e); _ok = false; }
            return _ok;
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

        public static void SetMode(object comp, int mode)
        {
            if (comp == null || mode < ModeAuto || mode > ModeFlooding) return;
            Ships.GetOrCreateValue(comp).Mode = mode;
            Trace("DC priority -> " + (mode == ModeFire ? "fire" : mode == ModeFlooding ? "flooding" : "auto"));
        }

        /// <summary>Per team in compartment i: the task given by hand (0 = by priority) and what it did last tick.</summary>
        public static void GetTeams(object comp, int i, int n, out int[] forced, out int[] actual)
        {
            forced = new int[Math.Max(0, n)];
            actual = new int[Math.Max(0, n)];
            Ship s;
            if (comp == null || i < 0 || i >= 8 || !Ships.TryGetValue(comp, out s)) return;
            var f = s.Forced[i];
            var a = s.Actual[i];
            for (int k = 0; k < n; k++)
            {
                if (f != null && k < f.Count) forced[k] = f[k];
                if (a != null && k < a.Length) actual[k] = a[k];
            }
        }

        /// <summary>Gives team k of compartment i a task by hand (0 = back to the priority order). Only while Auto DC is off.</summary>
        public static void SetForced(object comp, int i, int k, int task)
        {
            if (comp == null || i < 0 || i >= 8 || k < 0) return;
            if (AutoDc(comp)) return;
            var s = Ships.GetOrCreateValue(comp);
            var f = s.Forced[i] ?? (s.Forced[i] = new List<int>());
            while (f.Count <= k) f.Add(TaskNone);
            f[k] = task;
            Trace("DC team " + k + " in compartment " + i + " -> task " + task);
        }

        // ------------------------------------------------------------ saved games

        /// <summary>
        /// "mode:t,t|t||..." - the priority mode and, per compartment, the tasks given by hand (0 = by priority).
        /// Null when there is nothing to keep (Auto and no tasks by hand).
        /// </summary>
        public static string Save(object comp)
        {
            Ship s;
            if (comp == null || !Ships.TryGetValue(comp, out s)) return null;
            var sb = new System.Text.StringBuilder();
            sb.Append(s.Mode).Append(':');   // not ';' - the game's ini reader takes ';' for a comment
            bool any = s.Mode != ModeAuto;
            for (int i = 0; i < 5; i++)
            {
                if (i > 0) sb.Append('|');
                var f = s.Forced[i];
                if (f == null) continue;
                for (int k = 0; k < f.Count; k++) { if (k > 0) sb.Append(','); sb.Append(f[k]); if (f[k] != TaskNone) any = true; }
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
                    for (int i = 0; i < cs.Length && i < 8; i++)
                    {
                        var list = new List<int>();
                        foreach (var t in cs[i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            int v;
                            list.Add(int.TryParse(t, out v) && v >= TaskNone && v <= TaskSystems && v != TaskIdle ? v : TaskNone);
                        }
                        s.Forced[i] = list;
                    }
                }
                Trace("DC state loaded: " + text);
            }
            catch (Exception e) { Trace("DcControl.Load: " + e.Message); }
        }

        /// <summary>Team k leaves compartment i (moved elsewhere by hand): its own slot goes, the others keep their tasks.</summary>
        public static void RemoveSlot(object comp, int i, int k)
        {
            Ship s;
            if (comp == null || i < 0 || i >= 8 || !Ships.TryGetValue(comp, out s)) return;
            var f = s.Forced[i];
            if (f != null && k >= 0 && k < f.Count) f.RemoveAt(k);
        }

        public static bool AutoDc(object comp)
        {
            try { return (bool)_autoDc.GetValue(comp, null); } catch { return true; }
        }

        // ------------------------------------------------------------ hooks

        /// <summary>Prefix of Compartments.CheckDamageControl(int i): false = done here.</summary>
        public static bool CheckPrefix(object comp, int i)
        {
            if (!_ok || comp == null || !IsEnabled() || i < 0 || i >= 8) return true;
            var teams = _teams.GetValue(comp, null) as int[];
            if (teams == null || i >= teams.Length) return true;
            int n = teams[i];
            var s = Ships.GetOrCreateValue(comp);

            // one slot per team in the compartment: a team leaving takes a "by priority" slot first, then the last given task
            var f = s.Forced[i] ?? (s.Forced[i] = new List<int>());
            while (f.Count < n) f.Add(TaskNone);
            while (f.Count > Math.Max(0, n))
            {
                int idx = f.LastIndexOf(TaskNone);
                f.RemoveAt(idx >= 0 ? idx : f.Count - 1);
            }
            bool autoDc = AutoDc(comp);
            if (autoDc) for (int k = 0; k < f.Count; k++) f[k] = TaskNone;   // hand-given tasks are for manual control only
            var actual = s.Actual[i];
            if (actual == null || actual.Length != n) actual = s.Actual[i] = new int[Math.Max(0, n)];
            if (n <= 0) return false;   // the game does nothing either

            bool returnTeam = false;
            try
            {
                // teams with a task given by hand, grouped by task
                var free = new List<int>();
                var done = new bool[n];
                for (int k = 0; k < n; k++)
                {
                    int t = f[k];
                    if (t == TaskNone || done[k]) continue;
                    var group = new List<int>();
                    for (int m = k; m < n; m++) if (f[m] == t && !done[m]) { group.Add(m); done[m] = true; }
                    teams[i] = group.Count;
                    bool worked = Repairs[t] != null && Repairs[t](comp, i);
                    foreach (var m in group) { if (worked) actual[m] = t; else free.Add(m); }  // nothing to do: by priority
                }
                for (int k = 0; k < n; k++) if (f[k] == TaskNone) free.Add(k);

                // the rest: the first task in the order that has work
                if (free.Count > 0)
                {
                    teams[i] = free.Count;
                    int did = TaskIdle;
                    foreach (var t in Orders[Math.Max(0, Math.Min(2, s.Mode))])
                        if (Repairs[t](comp, i)) { did = t; break; }
                    foreach (var m in free) actual[m] = did;
                    returnTeam = did == TaskIdle && free.Count == n && autoDc && !StayWhenIdle(comp);   // as the game: nothing to do -> back to the pool
                }
            }
            finally { teams[i] = n; }   // our temporary counts never stay
            if (returnTeam) _returnTeam.Invoke(comp, new object[] { i });
            return false;
        }

        /// <summary>
        /// Prefix of Compartments.CheckDCTeamsAllocation: with Fire / Flooding first, Auto DC sends a free team to the worst fire /
        /// the worst water before anything else; in every other case the game's own choice runs.
        /// </summary>
        public static bool AllocPrefix(object comp)
        {
            if (!_ok || comp == null || !IsEnabled()) return true;
            if (!DcAuto.Allocate(comp)) return false;   // the optimised Auto DC did it
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
