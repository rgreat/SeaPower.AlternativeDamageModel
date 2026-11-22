using System;
using System.Collections.Generic;
using System.Globalization;
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
            new[] { TaskLeak, TaskFire, TaskPump, TaskSystems, TaskHull },   // Auto: leak rate, fire, flooding, (flooded structure: CheckPrefix), systems, structure (DcAuto: leak rate > fire rate > fires ...; a fire of any strength keeps one team in its section, see Allocate below)
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
            public bool AutoCounter;    // Auto DC: the heel is dangerous, one team counterfloods in CounterSec
            public int CounterSec = -1; // the section of that team
            public bool CounterCrit;    // the heel is critical
            public readonly int[] SysQuota = new int[5];   // per section: teams for the priority systems repair (DcAuto's plan)
            public int CounterN = 1;   // DcAuto's plan: how many teams of the counterflooding section counterflood (1 + the free ones it got)
            public int FireAll;   // DcAuto's plan, bits: the burning sections of an engine out of order - all their teams fight the fire
            public readonly int[] HpTask = new int[5];       // per section: last of hull repair / pumping the free teams did
            public readonly float[] HpSince = new float[5];  // since when (Time.fixedTime)
            public readonly int[] LastAct = new int[5];      // last counterflooding action per section (for the trace)
            // trace: what the counterflooding team let in and what changed the water of that side besides it, per section (since the last line)
            public readonly float[] SetAt = { -1f, -1f, -1f, -1f, -1f }, FlAdd = new float[5], FlExt = new float[5], FlDiag = new float[5];
            public readonly bool[] SetPort = new bool[5];
            public readonly int[] LpTask = new int[5];       // the same for plugging holes / pumping
            public readonly float[] LpSince = new float[5];
            public readonly List<int>[] Pending = new List<int>[8];  // per compartment: tasks from a saved game, put on the teams there
        }
        static readonly ConditionalWeakTable<object, Ship> Ships = new ConditionalWeakTable<object, Ship>();

        // game members
        static bool _ok;
        static PropertyInfo _teams, _autoDc;
        static Prop<int[]> _gTeams; static Prop<bool> _gAutoDc; static Fld<Array> _gSys; static Prop<float> _gSev; static Fld<float> _gGrow;   // fast getters (Fast.cs) for the per-step paths
        static FieldInfo _sinking, _port, _star, _sys, _floodRate, _growRate, _count, _maxSev;
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
                _gTeams = _teams == null ? null : new Prop<int[]>(_teams); _gAutoDc = _autoDc == null ? null : new Prop<bool>(_autoDc);
                _sinking = tComp.GetField("_isSinking", all);
                _port = tComp.GetField("_portCompartments", all);
                _star = tComp.GetField("_starboardCompartments", all);
                _sys = tComp.GetField("_systemCompartments", all);
                _count = tComp.GetField("_compartmentsCount", all);
                _floodRate = tFc.GetField("_floodingRate", all);
                _floodDelta = tFc.GetProperty("FloodingDelta", all);
                _severity = tSc.GetProperty("FireSeverity", all);
                _growRate = tSc.GetField("_fireGrowRate", all);
                _gSys = _sys == null ? null : new Fld<Array>(_sys); _gSev = _severity == null ? null : new Prop<float>(_severity); _gGrow = _growRate == null ? null : new Fld<float>(_growRate);
                _maxSev = tSc.GetField("_maxFireSeverity", all);
                _returnTeam = tComp.GetMethod("ReturnDCTeamToPool", all);
                _sendTeam = tComp.GetMethod("SendDCTeamToCompartment", all);
                _leakOrig = Caller(tComp.GetMethod("RepairFloodingRate", all));
                Repairs[TaskLeak] = _leakOrig == null ? null : LeakWithPump;
                Repairs[TaskFire] = Caller(tComp.GetMethod("RepairFire", all));
                _pumpOrig = Caller(tComp.GetMethod("RepairFlooding", all));
                _drainSide = tComp.GetField("_drainPortSide", all);
                Repairs[TaskPump] = _pumpOrig == null ? null : PumpMoreWater;
                Repairs[TaskHull] = Caller(tComp.GetMethod("RepairStructure", all));
                var sysOrig = Caller(tComp.GetMethod("RepairSystems", all));
                Repairs[TaskSystems] = sysOrig == null ? null : (Func<object, int, bool>)((c, i) => !SectionEffects.Locked(c, i) && sysOrig(c, i));   // a flooded section's systems cannot be repaired
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
        static float ListOf(object comp) { var h = HeelNow(comp); return h == null ? 0f : h.List; }

        /// <summary>
        /// Per ship and physics step (Time.fixedTime): list, trim, the red zones (Red) and the LEVEL gauge - asked many times a step (every section's
        /// teams, pumping, counterflooding) and the same for all of them within the step.
        /// </summary>
        sealed class Heel
        {
            public float Ft = float.NaN, List, Trim; public UnityEngine.Transform T;
            public float RedFt = float.NaN, LR, TR, RemL, RemT, ToL, ToT;
            public float LvFt = float.NaN, Level;
        }
        static readonly ConditionalWeakTable<object, Heel> Heels = new ConditionalWeakTable<object, Heel>();

        static Heel HeelNow(object comp)
        {
            var h = Heels.GetOrCreateValue(comp);
            float ft = UnityEngine.Time.fixedTime;
            if (h.Ft == ft) return h;
            if (h.T == null) h.T = _getTransform.Invoke(_baseObj.GetValue(comp), null) as UnityEngine.Transform;   // the hull's transform: the same for the whole mission
            if (h.T == null) return null;
            var e = h.T.localEulerAngles;
            h.List = e.z > 180f ? e.z - 360f : e.z; h.Trim = e.x > 180f ? e.x - 360f : e.x;
            h.Ft = ft;
            return h;
        }

        // What a team ordered to counterflood does in a section, chosen to improve the list or the trim (not to move water for its own sake):
        //   flood port / flood starboard / pump out port / pump out starboard.
        // Flooding a side makes it heavier (port: list to port; a forward section: bow down; an after one: stern down); pumping a side out does the opposite.
        // The action is scored by how much it takes off the list and the trim (each in the part of its red zone it has reached, so the more dangerous
        // of the two heels counts more); an action that makes the heel worse scores below zero. Nothing improves = the team works by priority.
        // The chosen action is kept until it stops helping or cannot go on (the side is at its limit / minimum); a better one is taken only
        // after CounterHold seconds and only if it is clearly better, so the team does not swing between flooding and pumping.
        // No flooding at all when the water in the ship has reached the danger band of the LEVEL gauge.
        const int ActNone = 0, ActFloodPort = 1, ActFloodStar = 2, ActPumpPort = 3, ActPumpStar = 4;
        public const float CounterHold = 10f;   // s: the shortest time before the team changes its action for a better one
        sealed class CState { public readonly float[] Seen = new float[8]; public readonly int[] Act = new int[8]; public readonly float[] Age = new float[8] { 99f, 99f, 99f, 99f, 99f, 99f, 99f, 99f }; public readonly float[] NoneDiag = new float[8]; }
        static readonly ConditionalWeakTable<object, CState> CStates = new ConditionalWeakTable<object, CState>();
        static PropertyInfo _floodPct;

        /// <summary>The chosen action of the counterflooding teams of section i (ActNone when they do not work on it): for the tooltips.</summary>
        public static int CounterAct(object comp, int i)
        {
            CState st;
            return comp != null && i >= 0 && i < 8 && CStates.TryGetValue(comp, out st) ? st.Act[i] : ActNone;
        }

        /// <summary>The side of compartment i that counterflooding lets the water into: +1 port, -1 starboard, 0 = none (no action, or pumping).</summary>
        public static int CounterSide(object comp, int i)
        {
            if (!_ok || comp == null || Repairs[TaskCounter] == null || i < 0 || i > 4) return 0;
            int a = CounterAct(comp, i);
            return a == ActFloodPort ? 1 : a == ActFloodStar ? -1 : 0;
        }

        /// <summary>The side that counterflooding teams pump out in section i: +1 port, -1 starboard, 0 = none.</summary>
        public static int CounterPumpSide(object comp, int i)
        {
            if (!_ok || comp == null || Repairs[TaskCounter] == null || i < 0 || i > 4) return 0;
            int a = CounterAct(comp, i);
            return a == ActPumpPort ? 1 : a == ActPumpStar ? -1 : 0;
        }

        /// <summary>
        /// The ship's water as the LEVEL gauge shows it (0..100 %, 100 = sinks): the mean of the water in all compartment sides (what the game
        /// shows as the ship's flooding) over SinkAt. Not FloodingPercentage: read here, during the game's own Damage Control step, it gave
        /// about twice the value the game shows (39.6 against 21 in a trace) - the gauge, read outside that step, was right.
        /// </summary>
        public static float LevelGauge(object comp)
        {
            var h = Heels.GetOrCreateValue(comp);
            float ft = UnityEngine.Time.fixedTime;
            if (h.LvFt == ft) return h.Level;
            h.Level = LevelGaugeNow(comp); h.LvFt = ft;   // once a physics step (the water the teams move in this step counts from the next one)
            return h.Level;
        }

        static float LevelGaugeNow(object comp)
        {
            try
            {
                var ports = _port.GetValue(comp) as Array; var stars = _star.GetValue(comp) as Array;
                int cnt = Math.Min(Math.Min(ports.Length, stars.Length), (int)_count.GetValue(comp));
                if (cnt <= 0) return 0f;
                float sum = 0f;
                for (int k = 0; k < cnt; k++) sum += (float)_curFlood.GetValue(ports.GetValue(k)) + (float)_curFlood.GetValue(stars.GetValue(k));
                return Math.Min(100f, 100f * sum / (2 * cnt) / DamageControlUi.SinkAt(comp));
            }
            catch { return 0f; }
        }

        static float LevelNow(object comp)
        {
            try
            {
                if (_floodPct == null) _floodPct = comp.GetType().GetProperty("FloodingPercentage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return _floodPct == null ? 0f : Convert.ToSingle(_floodPct.GetValue(comp, null), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return 0f; }
        }

        const float ScoreMin = 0.02f;

        /// <summary>
        /// How much pumping out the water of section i takes off the heel (in parts of the red zone of the list / trim; below zero = makes it worse):
        /// the best of its two sides that hold water. 0 when the ship is level enough or nothing can be pumped there.
        /// </summary>
        public static float PumpGain(object comp, int i)
        {
            try
            {
                var sc = _scB; var cn = _cnB;
                if (!Score(comp, i, false, sc, cn)) return 0f;
                var ports = _port.GetValue(comp) as Array; var stars = _star.GetValue(comp) as Array;
                bool wp = (float)_floodDelta.GetValue(ports.GetValue(i), null) > 1e-4f, ws = (float)_floodDelta.GetValue(stars.GetValue(i), null) > 1e-4f;
                float g = float.MinValue;
                if (wp) g = Math.Max(g, sc[ActPumpPort]);
                if (ws) g = Math.Max(g, sc[ActPumpStar]);
                return g == float.MinValue ? 0f : g;
            }
            catch { return 0f; }
        }

        /// <summary>How much plugging the holes of section i takes off the heel: the water they let in would go to their side (below zero = the holes level the ship, plugging them does not).</summary>
        public static float LeakGain(object comp, int i)
        {
            try
            {
                var sc = _scB; var cn = _cnB;
                if (!Score(comp, i, false, sc, cn)) return 0f;
                var ports = _port.GetValue(comp) as Array; var stars = _star.GetValue(comp) as Array;
                bool lp = F0(_floodRate.GetValue(ports.GetValue(i))) > 0f, ls = F0(_floodRate.GetValue(stars.GetValue(i))) > 0f;
                float g = float.MinValue;
                if (lp) g = Math.Max(g, -sc[ActFloodPort]);
                if (ls) g = Math.Max(g, -sc[ActFloodStar]);
                return g == float.MinValue ? 0f : g;
            }
            catch { return 0f; }
        }
        static float F0(object v) { return v is float f ? f : Convert.ToSingle(v, CultureInfo.InvariantCulture); }

        static readonly ConditionalWeakTable<object, float[]> EdgeCache = new ConditionalWeakTable<object, float[]>();

        /// <summary>
        /// Ships with exact deck points (SubmergedFlooding): the heel as the deck edges see it. From the present pose, the tilt still left to each
        /// edge (port, starboard, stern, bow); lE = half the difference starboard - port (degrees; &gt; 0 = the port edge is nearer the sea, like a
        /// list to port), tE = half of stern - bow (&gt; 0 = the bow edge is nearer, like the bow down). Levelling them evens the edges' heights
        /// above the sea. lRef / tRef = the tilt left to the edges when they are even (the scale). False when there are no exact points.
        /// Once per physics step per ship.
        /// </summary>
        static bool EdgeError(object comp, float list, float trim, out float lE, out float tE, out float lRef, out float tRef)
        {
            lE = tE = 0f; lRef = 1f; tRef = 0.5f;
            if (!DamageControlUi.ModelOn()) return false;
            float ft = UnityEngine.Time.fixedTime;
            float[] c;
            if (!EdgeCache.TryGetValue(comp, out c)) { c = new float[6]; c[0] = -1f; EdgeCache.Add(comp, c); }
            if (c[0] != ft)
            {
                float mp, ms, mt, mb;
                c[0] = ft; c[1] = 0f;
                if (SubmergedFlooding.Margins(comp, list, trim, out mp, out ms, out mt, out mb) && mp < 89f && ms < 89f && mt < 89f && mb < 89f)
                {
                    float rp = mp - list, rs = ms + list, rt = mt + trim, rb = mb - trim;   // + list = port down, + trim = bow down
                    c[1] = 1f; c[2] = 0.5f * (rs - rp); c[3] = 0.5f * (rt - rb);
                    c[4] = Math.Max(1f, 0.5f * (rp + rs)); c[5] = Math.Max(0.5f, 0.5f * (rt + rb));
                }
            }
            if (c[1] == 0f) return false;
            lE = c[2]; tE = c[3]; lRef = c[4]; tRef = c[5];
            return true;
        }

        /// <summary>Scores the four actions for section i (index = Act*; below zero = makes the heel worse) and tells which ones can be done at all.
        /// active = the team is at it already (a narrower dead zone). False = the ship is level enough: nothing to do. No state is changed.</summary>
        static bool Score(object comp, int i, bool active, float[] score, bool[] can)
        {
            long _p0 = Prof.Start();
            try { return ScoreCore(comp, i, active, score, can); }
            finally { Prof.Stop(Prof.Score, _p0); }
        }

        static bool ScoreCore(object comp, int i, bool active, float[] score, bool[] can)
        {
            float list = ListOf(comp), trim = TrimOf(comp);
            float listRed, trimRed; Red(comp, out listRed, out trimRed);
            // a dead zone around an even keel (wider to start than to go on), so a small heel does not start the pumps and the floods
            float dbL = active ? 0.25f : 0.5f, dbT = active ? 0.15f : 0.3f;
            float eL = Math.Sign(list) * Math.Max(0f, Math.Abs(list) - dbL) / listRed;   // heel in parts of its red zone, dead zone taken off
            float eT = Math.Sign(trim) * Math.Max(0f, Math.Abs(trim) - dbT) / trimRed;
            float lE, tE, lRef, tRef;
            if (EdgeError(comp, list, trim, out lE, out tE, out lRef, out tRef))
            {
                // ships with exact deck points: not the angles but the deck edges - the side / end whose edge is nearer the sea is "down",
                // whatever the trim angle says (a low quarterdeck can be at the water with the bow a little down)
                eL = Math.Sign(lE) * Math.Max(0f, Math.Abs(lE) - dbL) / lRef;
                eT = Math.Sign(tE) * Math.Max(0f, Math.Abs(tE) - dbT) / tRef;
            }
            for (int a = 0; a < 5; a++) { score[a] = 0f; can[a] = false; }
            if (eL == 0f && eT == 0f) return false;
            int tsec = i < 2 ? 1 : i > 2 ? -1 : 0;   // flooding a forward section puts the bow down (+), an after one the stern (-); the middle one does not trim

            var ports = _port.GetValue(comp) as Array; var stars = _star.GetValue(comp) as Array;
            object fp = ports.GetValue(i), fs = stars.GetValue(i);
            float wp = (float)_curFlood.GetValue(fp), ws = (float)_curFlood.GetValue(fs);
            float capP = Math.Min(CounterLimit, 100f), capS = capP;   // _maxFlooding is ignored: a side always floods to 100%, the limit is CounterFloodLimit
            bool levelOk = LevelGauge(comp) < DamageControlUi.LevelDangerPct - (active ? 0f : 2f);   // stops at 80, starts again below 78: no flicker at the edge;   // no flooding in the danger band of the LEVEL gauge: judged by the gauge as it is drawn
            // a side that has reached its limit is not started again until the water is down by a margin (a team that plugs holes pumps a little at the same time: no flicker)
            float mg = active ? 1e-4f : 1f;
            can[ActFloodPort] = levelOk && wp < capP - mg;
            can[ActFloodStar] = levelOk && ws < capS - mg;
            // counterflooding = letting water IN; pumping out is the Pump task, not a counterflooding action (the pump actions stay in the code, switched off)
            can[ActPumpPort] = can[ActPumpStar] = false;
            // score = how much of the heel (in parts of the red zone) the action takes off
            score[ActFloodPort] = -(eL * 1 + eT * tsec);
            score[ActFloodStar] = -(eL * -1 + eT * tsec);
            score[ActPumpPort] = -(eL * -1 + eT * -tsec);
            score[ActPumpStar] = -(eL * 1 + eT * -tsec);
            return true;
        }

        /// <summary>The best action in the scores: equally good (within 10%), pumping out beats flooding (no more water in the ship).</summary>
        static int Best(float[] score, bool[] can, out float bs)
        {
            int best = ActNone; bs = ScoreMin;
            for (int a = 1; a <= 4; a++) if (can[a] && score[a] > bs) { best = a; bs = score[a]; }
            for (int a = ActPumpPort; a <= ActPumpStar; a++) if (can[a] && score[a] >= 0.9f * bs && score[a] > ScoreMin && best <= ActFloodStar) { best = a; bs = Math.Max(bs, score[a]); }
            return best;
        }

        [ThreadStatic] static float[] _sc; [ThreadStatic] static bool[] _cn;
        static readonly float[] _scB = new float[5]; static readonly bool[] _cnB = new bool[5];   // Score's buffers for the one-off uses (main thread; filled and read at once)

        /// <summary>Chooses (and remembers) the action for section i; dt = game seconds since the last call for it.</summary>
        static int Plan(object comp, int i, float dt)
        {
            var st = CStates.GetOrCreateValue(comp);
            float ft = UnityEngine.Time.fixedTime;
            if (st.Seen[i] != ft) { st.Seen[i] = ft; st.Age[i] += dt; }   // once a step, however many teams ask
            int cur = st.Act[i];
            if (_sc == null) { _sc = new float[5]; _cn = new bool[5]; }
            var score = _sc; var can = _cn;
            if (!Score(comp, i, cur != ActNone, score, can)) { st.Act[i] = ActNone; NoneWhy(comp, i, st, ft, "level enough (dead zone)", score, can); return ActNone; }
            float bs;
            int pick = Best(score, can, out bs);
            if (pick == ActNone) NoneWhy(comp, i, st, ft, "no action allowed / helps", score, can);
            if (cur != ActNone && can[cur] && score[cur] > ScoreMin)
            {
                // the action goes on unless a clearly better one is there and the team has held this one long enough
                if (pick == cur || st.Age[i] < CounterHold || bs < 1.5f * score[cur]) pick = cur;
            }
            if (pick != cur) st.Age[i] = 0f;
            st.Act[i] = pick;
            return pick;
        }

        /// <summary>Trace (every 10 s per section): the counterflooding team of section i has nothing to do - the scores, which actions are allowed and the level rule's numbers.</summary>
        static void NoneWhy(object comp, int i, CState st, float ft, string what, float[] score, bool[] can)
        {
            if (ft < st.NoneDiag[i]) return;
            st.NoneDiag[i] = ft + 10f;
            var ci = CultureInfo.InvariantCulture;
            Trace("Counterflooding in section " + i + ": nothing to do (" + what + "): flood port " + score[ActFloodPort].ToString("0.00", ci) + (can[ActFloodPort] ? " ok" : " no") +
                  ", flood stbd " + score[ActFloodStar].ToString("0.00", ci) + (can[ActFloodStar] ? " ok" : " no") +
                  "; level gauge " + LevelGauge(comp).ToString("0.0", ci) + " (stop " + DamageControlUi.LevelDangerPct.ToString("0", ci) + "), FloodingPercentage " + LevelNow(comp).ToString("0.00", ci) +
                  ", SinkAt " + DamageControlUi.SinkAt(comp).ToString("0.#", ci) + "; " + HeelInfo(comp) + ", " + CounterWhy(comp, i));
        }

        // ---- Auto DC: counterflooding in a dangerous heel
        public static bool AutoCounterfloodOn = true;   // [FireAndFlooding] AutoCounterflood
        const float HeelOn = 1.0f, HeelOff = 0.5f;      // parts of the red zone of LIST / TRIM: Auto DC starts counterflooding, stops (only while holes let water in)

        /// <summary>
        /// Where the red zones of LIST / TRIM start NOW: the angle at which a deck point on the side the ship leans to touches the sea, from the
        /// present pose (SubmergedFlooding.Margins), so the water already in the ship (a deeper draft, less freeboard) counts. Not above the
        /// ship's own (dry) limit; ships without exact points: the fixed limit of the gauge.
        /// </summary>
        static void Red(object comp, out float listRed, out float trimRed) { float a, b, c, d; Red(comp, out listRed, out trimRed, out a, out b, out c, out d); }

        /// <summary>As above; also the tilt still left to that edge (remL / remT, degrees, not below 0). The side / end is the one whose deck edge is
        /// NEARER the sea (the least tilt left), not the one the angle points to: a low quarterdeck at the water with the bow a little down is the stern's.</summary>
        static void Red(object comp, out float listRed, out float trimRed, out float remL, out float remT, out float toL, out float toT)
        {
            var h = Heels.GetOrCreateValue(comp);
            float ft = UnityEngine.Time.fixedTime;
            if (h.RedFt == ft) { listRed = h.LR; trimRed = h.TR; remL = h.RemL; remT = h.RemT; toL = h.ToL; toT = h.ToT; return; }
            RedNow(comp, out listRed, out trimRed, out remL, out remT, out toL, out toT);
            h.RedFt = ft; h.LR = listRed; h.TR = trimRed; h.RemL = remL; h.RemT = remT; h.ToL = toL; h.ToT = toT;
        }

        static void RedNow(object comp, out float listRed, out float trimRed, out float remL, out float remT, out float toL, out float toT)
        {
            // toL / toT: the list / trim angle TOWARD the chosen edge (negative when the ship leans the other way)
            listRed = DamageControlUi.ListRed(comp); trimRed = DamageControlUi.TrimRed(comp);
            float list0 = 0f, trim0 = 0f;
            try { list0 = ListOf(comp); trim0 = TrimOf(comp); } catch { }
            remL = Math.Max(0f, listRed - Math.Abs(list0)); remT = Math.Max(0f, trimRed - Math.Abs(trim0));
            toL = Math.Abs(list0); toT = Math.Abs(trim0);
            try
            {
                float list = list0, trim = trim0;
                float mp, ms, mt, mb;
                if (!SubmergedFlooding.Margins(comp, list, trim, out mp, out ms, out mt, out mb)) return;
                float rp = mp - list, rs = ms + list, rt = mt + trim, rb = mb - trim;   // + list = port down, + trim = bow down: the tilt left to each edge
                bool portNear = rp <= rs, sternNear = rt <= rb;
                float l = portNear ? mp : ms, t = sternNear ? mt : mb;
                if (l < 89f) { listRed = Math.Max(1f, Math.Min(listRed, l)); remL = Math.Max(0f, portNear ? rp : rs); toL = portNear ? list : -list; }
                if (t < 89f) { trimRed = Math.Max(0.5f, Math.Min(trimRed, t)); remT = Math.Max(0f, sternNear ? rt : rb); toT = sternNear ? -trim : trim; }
            }
            catch { }
        }
        static float RedL(object comp) { float l, t; Red(comp, out l, out t); return l; }
        static float RedT(object comp) { float l, t; Red(comp, out l, out t); return t; }

        /// <summary>The larger of list and trim as a part of the start of their red zones (1 = in the red zone); 0 when unknown or the ship is sinking.</summary>
        public static float HeelRatio(object comp)
        {
            if (!_ok || comp == null) return 0f;
            try
            {
                if ((bool)_sinking.GetValue(comp)) return 0f;
                float l, t, rl, rt, al, at; Red(comp, out l, out t, out rl, out rt, out al, out at);
                float dl = DamageControlUi.ListRed(comp), dt = DamageControlUi.TrimRed(comp);   // the dry limits (from level)
                // the angle against the start of the red zone, or how much of the dry limit the nearest edge has used up - whichever is more
                // (the second sees an edge at the water when the angle is small or points the other way)
                // the angle counts only toward the chosen (nearer) edge: a bow-down trim is no part of the stern's red zone
                float rL = Math.Max(l > 0f ? Math.Max(0f, al) / l : 0f, dl > 0f ? 1f - rl / dl : 0f);
                float rT = Math.Max(t > 0f ? Math.Max(0f, at) / t : 0f, dt > 0f ? 1f - rt / dt : 0f);
                return Math.Max(rL, rT);
            }
            catch { return 0f; }
        }

        static string EdgeText(object comp)
        {
            float lE, tE, lRef, tRef;
            if (!EdgeError(comp, ListOf(comp), TrimOf(comp), out lE, out tE, out lRef, out tRef)) return "";
            var ci = CultureInfo.InvariantCulture;
            return ", deck edges: list-like " + lE.ToString("0.0", ci) + " / " + lRef.ToString("0.0", ci) + ", trim-like " + tE.ToString("0.0", ci) + " / " + tRef.ToString("0.0", ci) + " (+ = port / bow nearer the sea)";
        }

        /// <summary>For the trace: list and trim with the starts of their red zones, and the ratio Auto DC judges the heel by.</summary>
        public static string HeelInfo(object comp)
        {
            try
            {
                return "list " + ListOf(comp).ToString("0.0", CultureInfo.InvariantCulture) + " / red " + RedL(comp).ToString("0.0", CultureInfo.InvariantCulture) +
                       ", trim " + TrimOf(comp).ToString("0.0", CultureInfo.InvariantCulture) + " / red " + RedT(comp).ToString("0.0", CultureInfo.InvariantCulture) +
                       ", ratio " + HeelRatio(comp).ToString("0.00", CultureInfo.InvariantCulture) + " (on at " + HeelOn.ToString("0.0", CultureInfo.InvariantCulture) + ")" + EdgeText(comp);
            }
            catch { return "?"; }
        }

        /// <summary>The ship's heel is dangerous (list or trim in its red zone; wasOn: until it is down to half of it) and water still comes in through holes.</summary>
        public static bool HeelDanger(object comp, bool wasOn, bool leaking)
        {
            if (!leaking || Repairs[TaskCounter] == null) return false;
            return HeelRatio(comp) >= (wasOn ? HeelOff : HeelOn);
        }

        /// <summary>The section where counterflooding (or pumping against the heel) helps most; -1 = none. A section whose teams are at it already counts 25% more.</summary>
        public static int CounterSection(object comp, out float score)
        {
            score = 0f;
            if (!_ok || comp == null || Repairs[TaskCounter] == null) return -1;
            int bestI = -1;
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    float bs = CounterScore(comp, i);
                    if (bs > score) { score = bs; bestI = i; }
                }
            }
            catch { return -1; }
            return bestI;
        }

        /// <summary>What counterflooding in section i would take off the heel now (parts of the red zone; the team at it already: +25%); 0 = nothing that helps.</summary>
        public static float CounterScore(object comp, int i)
        {
            if (!_ok || comp == null || Repairs[TaskCounter] == null || i < 0 || i > 4) return 0f;
            try
            {
                var sc = _scB; var cn = _cnB;
                int cur = CounterAct(comp, i);
                if (!Score(comp, i, cur != ActNone, sc, cn)) return 0f;
                float bs; int ba = Best(sc, cn, out bs);
                bool keep = cur != ActNone && cn[cur] && sc[cur] > ScoreMin;
                if (ba == ActNone && !keep) return 0f;   // nothing that helps can be done here (caps, the LEVEL stop, or it would make the heel worse)
                if (keep) bs = Math.Max(bs, sc[cur]) * 1.25f;
                return bs;
            }
            catch { return 0f; }
        }

        /// <summary>Auto DC's plan for the sections' teams: where the one counterflooding team works (section, -1 = nobody) and how many teams per section repair the priority systems.</summary>
        public static void SetPlan(object comp, bool counter, int counterSection, int[] sysQuota, bool critical = false, int fireAll = 0, int counterN = 1)
        {
            if (comp == null) return;
            var s = Ships.GetOrCreateValue(comp);
            s.AutoCounter = counter && AutoCounterfloodOn;
            s.CounterSec = s.AutoCounter ? counterSection : -1;
            s.CounterCrit = s.AutoCounter && critical;
            for (int i = 0; i < s.SysQuota.Length; i++) s.SysQuota[i] = sysQuota != null && i < sysQuota.Length ? sysQuota[i] : 0;
            s.FireAll = fireAll;
            s.CounterN = Math.Max(1, counterN);
            // a counterflooding action is remembered only where Auto DC plans it now: an old one (the mode went off, or moved to another section) would
            // keep the section "at counterflooding" - held, never a donor - while its teams do other work
            CState st;
            if (CStates.TryGetValue(comp, out st))
                for (int i = 0; i < 5 && i < st.Act.Length; i++) if (i != s.CounterSec) st.Act[i] = ActNone;
        }
        static bool FireAllNow(object comp, int section)
        {
            Ship s;
            return section >= 0 && section < 31 && Ships.TryGetValue(comp, out s) && (s.FireAll & (1 << section)) != 0;
        }
        static bool AutoCounterNow(object comp, int section)
        {
            Ship s;
            return AutoCounterfloodOn && Ships.TryGetValue(comp, out s) && s.AutoCounter && s.CounterSec == section;
        }

        static bool CounterCritNow(object comp)
        {
            Ship s;
            return Ships.TryGetValue(comp, out s) && s.CounterCrit;
        }

        static int SysQuotaOf(object comp, int section)
        {
            Ship s;
            return section >= 0 && section < 5 && Ships.TryGetValue(comp, out s) ? s.SysQuota[section] : 0;
        }

        /// <summary>For the trace: why flooding is (not) possible in section i - the water level of the ship against its danger band, the water in the two sides against the limit.</summary>
        static string CounterWhy(object comp, int i)
        {
            try
            {
                var ports = _port.GetValue(comp) as Array; var stars = _star.GetValue(comp) as Array;
                object fp = ports.GetValue(i), fs = stars.GetValue(i);
                var ci = CultureInfo.InvariantCulture;
                float sum = 0f; int cnt = Math.Min(5, (int)_count.GetValue(comp));
                for (int k = 0; k < cnt; k++) sum += (float)_curFlood.GetValue(ports.GetValue(k)) + (float)_curFlood.GetValue(stars.GetValue(k));
                float sink = DamageControlUi.SinkAt(comp);
                return "gauge " + (100f * LevelNow(comp) / sink).ToString("0", ci) + "%, sides average " + (cnt > 0 ? sum / (2 * cnt) : 0f).ToString("0.0", ci) + ", sinks at " + sink.ToString("0.#", ci) +
                       ", level " + LevelNow(comp).ToString("0.00", ci) + " / danger " + DamageControlUi.LevelDangerAt(comp).ToString("0.0", ci) +
                       ", port " + ((float)_curFlood.GetValue(fp)).ToString("0.00", ci) + "/" + Math.Min(CounterLimit, 100f).ToString("0.0", ci) +
                       ", stbd " + ((float)_curFlood.GetValue(fs)).ToString("0.00", ci) + "/" + Math.Min(CounterLimit, 100f).ToString("0.0", ci);
            }
            catch { return "?"; }
        }

        /// <summary>The counterflooding teams of section i: lets water into a side or pumps one out (CounterRate × the pumping speed for flooding,
        /// the pumping speed for pumping), whichever improves the list / trim. False = nothing to do: the team then works by priority.</summary>
        static bool CounterFlood(object comp, int i)
        {
            if (!_ok || i < 0 || i > 4) return false;
            float dt = Convert.ToSingle(_fixedDt.GetValue(null, null));
            int act = Plan(comp, i, dt);
            Ship sh;
            if (Ships.TryGetValue(comp, out sh) && sh.LastAct[i] != act)
            {
                sh.LastAct[i] = act;
                Trace("Counterflooding in section " + i + ": action " + act + " (0 none, 1 flood port, 2 flood stbd, 3 pump port, 4 pump stbd), " + HeelInfo(comp) + ", " + CounterWhy(comp, i));
            }
            if (act == ActNone) { if (sh != null) { sh.SetAt[i] = -1f; sh.FlDiag[i] = 0f; sh.FlAdd[i] = 0f; sh.FlExt[i] = 0f; } return false; }
            var teams = _teams.GetValue(comp, null) as int[];
            bool port = act == ActFloodPort || act == ActPumpPort;
            var fc = (port ? _port : _star).GetValue(comp) is Array arr ? arr.GetValue(i) : null;
            if (fc == null) return false;
            float cur = (float)_curFlood.GetValue(fc);
            float rate = AdmPlugin.PumpOutMul((float)_repairF.GetValue(_crewF.GetValue(_baseObj.GetValue(comp))));   // as fast as pumping (with PumpOutSpeed)
            if (act == ActFloodPort || act == ActFloodStar)
            {
                float cap = Math.Min(CounterLimit, 100f);
                float nv = Math.Min(cap, cur + teams[i] * rate * dt * CounterRate);
                _curFlood.SetValue(fc, nv);
                if (sh != null)
                {
                    // what happened to the side since our last step (anything else: the game, other code), what we add now
                    if (sh.SetAt[i] >= 0f && sh.SetPort[i] == port) sh.FlExt[i] += cur - sh.SetAt[i];
                    sh.FlAdd[i] += nv - cur; sh.SetAt[i] = nv; sh.SetPort[i] = port;
                    float ft = UnityEngine.Time.fixedTime;
                    if (sh.FlDiag[i] <= 0f) sh.FlDiag[i] = ft + 10f;
                    else if (ft >= sh.FlDiag[i])
                    {
                        var ci = CultureInfo.InvariantCulture;
                        Trace("Counterflooding in section " + i + " (" + (port ? "port" : "stbd") + ", 10 s): the team let in " + sh.FlAdd[i].ToString("0.00", ci) + ", something else changed it by " + sh.FlExt[i].ToString("0.00", ci) +
                              ", now " + nv.ToString("0.00", ci) + " (cap " + cap.ToString("0.#", ci) + ", game _maxFlooding " + ((float)_maxFlood.GetValue(fc)).ToString("0.#", ci) + ", teams here " + teams[i] + ", rate " + rate.ToString("0.000", ci) + ", dt " + dt.ToString("0.000", ci) + ")");
                        sh.FlAdd[i] = 0f; sh.FlExt[i] = 0f; sh.FlDiag[i] = ft + 10f;
                    }
                }
            }
            else
            {
                float d = (float)_floodDelta.GetValue(fc, null);   // water above the minimum (the hull's own limit)
                _curFlood.SetValue(fc, cur - Math.Min(d, teams[i] * rate * dt));
            }
            return true;
        }

        /// <summary>Water pumped out per team per second, % (the crew's skill and PumpOutSpeed): for the tooltips.</summary>
        public static float PumpPerTeam(object comp)
        {
            try { return AdmPlugin.PumpOutMul((float)_repairF.GetValue(_crewF.GetValue(_baseObj.GetValue(comp)))); } catch { return 0f; }
        }

        /// <summary>The trim in degrees: positive = the bow is down (Euler x of the hull, as the game reads it).</summary>
        static float TrimOf(object comp) { var h = HeelNow(comp); return h == null ? 0f : h.Trim; }

        /// <summary>
        /// Pumping out for a team that is ordered to counterflood: only from a side (and a section) where it does not make the list / trim worse.
        /// Taking water out of a side lifts it: that is bad for the list when this is the side that is already up (opposite to the heel), and for the trim when
        /// the end of this section is already up (a forward section with the bow up, an after one with the stern up). The middle section does not touch the trim.
        /// Pumps as the game does (teams × crew skill × PumpOutSpeed × dt, down to the side's minimum) and returns false when nothing may be pumped.
        /// </summary>
        static bool PumpSafe(object comp, int i) { return PumpSafe(comp, i, 1f); }

        /// <summary>Pumping of an Auto DC team: the game's own while the ship is level (the dead zone of the heel), PumpSafe once the list / trim is off it.</summary>
        static bool PumpGuard(object comp, int i)
        {
            long _p0 = Prof.Start();
            try { return PumpGuardCore(comp, i); }
            finally { Prof.Stop(Prof.PumpGuard, _p0); }
        }

        static bool PumpGuardCore(object comp, int i)
        {
            try
            {
                var sc = _scB; var cn = _cnB;
                if (Repairs[TaskCounter] == null || !Score(comp, i, false, sc, cn)) return Repairs[TaskPump](comp, i);
            }
            catch { return Repairs[TaskPump](comp, i); }
            return PumpSafe(comp, i, 1f);
        }

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

        static FieldInfo _sysRepF;
        /// <summary>The ship's systems repair a team a second of game time as the game reads it (Crew._systemRepair, with SystemRepairSpeed); 0 = unknown.</summary>
        public static float SysRepairBase(object comp)
        {
            try
            {
                var crew = _crewF.GetValue(_baseObj.GetValue(comp));
                if (_sysRepF == null) _sysRepF = crew.GetType().GetField("_systemRepair", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return _sysRepF == null ? 0f : AdmPlugin.SysRepairMul(Convert.ToSingle(_sysRepF.GetValue(crew)));
            }
            catch { return 0f; }
        }

        static bool PumpSafe(object comp, int i, float share)
        {
            if (Repairs[TaskCounter] == null) return Repairs[TaskPump](comp, i);   // no way to tell: the game's pumping
            try
            {
                float list = ListOf(comp), trim = TrimOf(comp);
                var ports = _port.GetValue(comp) as Array; var stars = _star.GetValue(comp) as Array;
                object fp = ports.GetValue(i), fs = stars.GetValue(i);
                float wp = (float)_curFlood.GetValue(fp), ws = (float)_curFlood.GetValue(fs);
                float minP = wp - (float)_floodDelta.GetValue(fp, null), minS = ws - (float)_floodDelta.GetValue(fs, null);   // FloodingDelta = water above the minimum
                bool canPort = wp > minP + 1e-4f, canStar = ws > minS + 1e-4f;
                var sc = _scB; var cn = _cnB;
                if (Score(comp, i, false, sc, cn))
                {
                    // list and trim together, weighed by their red zones (as counterflooding does): a side may be pumped when that takes more off the heel
                    // than it adds (an after section with the stern a little up still helps a large list)
                    // >= 0: pumping that does not change the heel (the middle section, the list in its dead zone) is allowed - less water in the ship
                    // lifts it, which is what a ship that sits deep needs; only pumping that makes the list / trim worse is refused
                    canPort &= sc[ActPumpPort] >= -0.05f; canStar &= sc[ActPumpStar] >= -0.05f;   // DcAuto.PumpTolerance: a hair worse is no reason to stop (it lifts the ship)
                }
                else
                {
                    if (i < 2 && trim < -0.2f) return false;    // forward section, bow already up
                    if (i > 2 && trim > 0.2f) return false;     // after section, stern already up
                    canPort &= !(list < -0.3f);   // the port side is the high one when the list is to starboard
                    canStar &= !(list > 0.3f);
                }
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
            // a small dynamic method (object, int) -> ((Compartments)o).M(i): MethodDelegate refuses the object-typed instance argument, and
            // MethodInfo.Invoke (an object[] and boxing per call) was most of the cost of the teams' work every physics step
            try
            {
                if (m.IsStatic || m.ReturnType != typeof(bool) || m.GetParameters().Length != 1 || m.GetParameters()[0].ParameterType != typeof(int)) throw new InvalidOperationException("signature");
                var dm = new System.Reflection.Emit.DynamicMethod("adm_call_" + m.Name, typeof(bool), new[] { typeof(object), typeof(int) }, typeof(DcControl).Module, true);
                var il = dm.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Castclass, m.DeclaringType);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1); il.Emit(System.Reflection.Emit.OpCodes.Call, m); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var f = (Func<object, int, bool>)dm.CreateDelegate(typeof(Func<object, int, bool>));
                bool broken = false;
                return (o, i) =>
                {
                    if (!broken) { try { return f(o, i); } catch (InvalidCastException) { broken = true; } catch (System.Security.VerificationException) { broken = true; } catch (MethodAccessException) { broken = true; } }
                    return (bool)m.Invoke(o, new object[] { i });
                };
            }
            catch (Exception e)
            {
                Trace("DcControl: fast call of " + m.Name + " failed (" + e.Message + "); using reflection");
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
            try { return _gAutoDc != null ? _gAutoDc.Get(comp) : (bool)_autoDc.GetValue(comp, null); } catch { return true; }
        }

        // ------------------------------------------------------------ hooks

        /// <summary>Prefix of Compartments.CheckDamageControl(int i): false = done here.</summary>
        public static bool CheckPrefix(object comp, int i)
        {
            long _p0 = Prof.Start();
            try { return CheckPrefixCore(comp, i); }
            finally { Prof.Stop(Prof.CheckDc, _p0); }
        }

        static bool CheckPrefixCore(object comp, int i)
        {
            if (!_ok || comp == null || i < 0 || i > 4) return true;
            bool ui = IsEnabled();
            if (!ui && !AutoOn()) return true;
            var teams = _gTeams != null ? _gTeams.Get(comp) : _teams.GetValue(comp, null) as int[];
            if (teams == null || i >= teams.Length) return true;
            int n = teams[i];
            if (n <= 0) return false;   // no team here: the game does nothing either (and nothing below is needed - a pending task waits for its team)
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
                    int size = 0; float eff = 0f;
                    for (int m = k; m < n; m++) if (m < objs.Count && objs[m].Task == t && !_done[m]) { _done[m] = true; size++; eff += DcTeams.TeamEff(objs[m]); }
                    teams[i] = size;
                    _grpEff = size > 0 ? eff / size : 1f;
                    bool worked = Repairs[t] != null && Work(t, comp, i, null);
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
                    _grpEff = GroupEff(_rest, 0, nRest);
                    int did = TaskIdle;
                    _safeLeak = true;
                    try
                    {
                        bool held = SectionEffects.LeaksHeldByFire(comp, i);
                        foreach (var t in Orders[mode])
                            if (!(held && t == TaskLeak) && Work(t, comp, i, t == TaskPump ? (Func<object, int, bool>)PumpSafe : null)) { did = t; break; }
                    }
                    finally { _safeLeak = false; }
                    for (int m = 0; m < nRest; m++) _did[_rest[m]] = did;
                }

                // the rest. Auto DC: the first team in a section with a fire fights it (whatever the section has to do besides, DcAuto sends another
                // team for that); then the one counterflooding team of the ship (if it is in this section), then the teams of the priority systems repair
                // (DcAuto's plan); the others take the first task in the order that has work.
                if (nFree > 0)
                {
                    int from = 0;
                    // fire first, then the counterflooding team; counterflooding goes first where it ranks above the fire (Flooding first, a critical heel)
                    bool counterFirst = autoDc && AutoCounterNow(comp, i) && (mode == ModeFlooding || CounterCritNow(comp));
                    for (int step = 0; step < 2; step++)
                    {
                        bool doCounter = (step == 0) == counterFirst;
                        if (doCounter)
                        {
                            if (autoDc && nFree - from >= 1 && AutoCounterNow(comp, i))
                            {
                                Ship cs; int k = Ships.TryGetValue(comp, out cs) ? Math.Min(Math.Max(1, cs.CounterN), nFree - from) : 1;   // free teams that joined counterflood too
                                teams[i] = k;
                                _grpEff = GroupEff(_free, from, k);
                                if (Work(TaskCounter, comp, i, null)) { for (int m = from; m < from + k; m++) _did[_free[m]] = TaskCounter; from += k; }
                            }
                        }
                        else if (autoDc && nFree - from >= 1 && FireIn(comp, i))
                        {
                            teams[i] = 1;
                            _grpEff = GroupEff(_free, from, 1);
                            if (Work(TaskFire, comp, i, null)) { _did[_free[from]] = TaskFire; from++; }
                        }
                    }
                    // the engine's section burns (DcAuto): the fire damages the engine, so every free team there fights it before anything else
                    if (autoDc && nFree - from >= 1 && FireAllNow(comp, i) && FireIn(comp, i))
                    {
                        int fa = nFree - from;
                        teams[i] = fa;
                        _grpEff = GroupEff(_free, from, fa);
                        if (Work(TaskFire, comp, i, null)) { for (int m = from; m < nFree; m++) _did[_free[m]] = TaskFire; from = nFree; }
                    }
                    int sq = autoDc ? Math.Min(SysQuotaOf(comp, i), nFree - from) : 0;
                    if (sq > 0)
                    {
                        teams[i] = sq;
                        _grpEff = GroupEff(_free, from, sq);
                        if (Work(TaskSystems, comp, i, null)) { for (int m = from; m < from + sq; m++) _did[_free[m]] = TaskSystems; from += sq; }
                    }
                    int cnt = nFree - from;
                    teams[i] = cnt;
                    _grpEff = GroupEff(_free, from, cnt);
                    int did = TaskIdle;
                    if (cnt > 0)
                    {
                        // hull repair <-> pumping: not oftener than once a minute while the task they work on still has work
                        int cur = s != null && i < 5 ? s.HpTask[i] : 0;
                        bool young = cur != 0 && UnityEngine.Time.fixedTime - s.HpSince[i] < HullPumpHold;
                        int curL = s != null && i < 5 ? s.LpTask[i] : 0;
                        bool youngL = curL != 0 && UnityEngine.Time.fixedTime - s.LpSince[i] < LeakPumpHold;
                        // a critical heel: nobody pumps out where that makes the list / trim worse (also not the team that counterfloods here when it has
                        // nothing to let in this step - it would pump out the very side it floods)
                        bool safe = autoDc;   // Auto DC: nobody pumps out where that makes the list / trim worse (PumpGuard); at a critical heel the guard is always on
                        Func<object, int, bool> pumpCall = safe ? (Func<object, int, bool>)PumpGuard : null;
                        _safeLeak = safe;
                        try
                        {
                            bool fireHeld = SectionEffects.LeaksHeldByFire(comp, i);   // holes the fire keeps open: the fire first, plugging waits
                        if (fireHeld && curL == TaskLeak) youngL = false;
                        foreach (var t in Orders[mode])
                            {
                                if (fireHeld && t == TaskLeak) continue;
                                if (youngL && ((curL == TaskLeak && t == TaskPump) || (curL == TaskPump && t == TaskLeak)) && Work(curL, comp, i, curL == TaskPump ? pumpCall : null)) { did = curL; break; }
                                if (young && ((cur == TaskHull && t == TaskPump) || (cur == TaskPump && t == TaskHull)) && Work(cur, comp, i, cur == TaskPump ? pumpCall : null)) { did = cur; break; }
                                // the hull holds the water (MinimumFlooding): welding it lets the pumps take more out - before the systems
                                if (t == TaskSystems && autoDc && DcAuto.HullHoldsWater(comp, i) && Work(TaskHull, comp, i, null)) { did = TaskHull; break; }
                                if (Work(t, comp, i, t == TaskPump ? pumpCall : null)) { did = t; break; }
                            }
                        }
                        finally { _safeLeak = false; }
                        if (s != null && i < 5 && (did == TaskLeak || did == TaskPump) && did != curL) { s.LpTask[i] = did; s.LpSince[i] = UnityEngine.Time.fixedTime; }
                        if (s != null && i < 5 && (did == TaskHull || did == TaskPump) && did != cur) { s.HpTask[i] = did; s.HpSince[i] = UnityEngine.Time.fixedTime; }
                    }
                    for (int m = from; m < nFree; m++) _did[_free[m]] = did;
                    returnTeam = from == 0 && did == TaskIdle && nFree == n && autoDc && !StayWhenIdle(comp);   // as the game: nothing to do -> back to the pool
                }
            }
            finally { teams[i] = n; _grpEff = 1f; }   // our temporary counts never stay
            for (int k = 0; k < n && k < objs.Count; k++) objs[k].Doing = _did[k];
            if (returnTeam) _returnTeam.Invoke(comp, new object[] { i });
            return false;
        }
        /// <summary>
        /// One task's repair call for a section. Teams that do not fight the fire in a section where one burns work at FireWorkEff of their effectiveness
        /// (DcTeams.WorkMul, read by the multiplier that DCTeamEffectiveness uses).
        /// </summary>
        const float LeakPumpHold = 30f;   // s: the same for plugging holes <-> pumping
        const float HullPumpHold = 60f;   // s: the teams of a section change from hull repair to pumping (or back) no oftener than this
        static float _grpEff = 1f;   // the mean efficiency (wounds, fatigue) of the teams in the group that works now
        static float GroupEff(int[] idx, int from, int count)
        {
            float sum = 0f; int n = 0;
            for (int k = from; k < from + count; k++) { int m = idx[k]; if (m >= 0 && m < _objs.Count) { sum += DcTeams.TeamEff(_objs[m]); n++; } }
            return n > 0 ? sum / n : 1f;
        }

        static bool Work(int task, object comp, int i, Func<object, int, bool> call)
        {
            long _p0 = Prof.Start();
            try { return WorkCore(task, comp, i, call); }
            finally { Prof.Stop(Prof.Repair, _p0); }
        }

        static bool WorkCore(int task, object comp, int i, Func<object, int, bool> call)
        {
            float mul = 1f;
            if (task != TaskFire && DcTeams.FireWorkEff < 1f && FireIn(comp, i)) mul = DcTeams.FireWorkEff;
            DcTeams.WorkMul = mul * _grpEff;
            try { return call != null ? call(comp, i) : Repairs[task](comp, i); }
            finally { DcTeams.WorkMul = 1f; }
        }

        /// <summary>The fire in section i as a part of its maximum (0..1): wounds the teams there that do not fight it.</summary>
        public static float FireLevel(object comp, int i)
        {
            try
            {
                if (_maxSev == null) return 0f;
                var sc = (_sys.GetValue(comp) as Array).GetValue(i);
                float max = Convert.ToSingle(_maxSev.GetValue(sc)), sev = (float)_severity.GetValue(sc, null);
                return max > 0f ? Math.Max(0f, Math.Min(1f, sev / max)) : 0f;
            }
            catch { return 0f; }
        }

        public static bool FireIn(object comp, int i)
        {
            try
            {
                if (_gSys != null && _gSev != null && _gGrow != null) { var sc0 = _gSys.Get(comp).GetValue(i); return _gSev.Get(sc0) > 0f || _gGrow.Get(sc0) > 0f; }
                var sc = (_sys.GetValue(comp) as Array).GetValue(i);
                return (float)_severity.GetValue(sc, null) > 0f || (float)_growRate.GetValue(sc) > 0f;
            }
            catch { return false; }
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
            long _p0 = Prof.Start();
            try { return AllocPrefixCore(comp); }
            finally { Prof.Stop(Prof.AllocHook, _p0); }
        }

        static bool AllocPrefixCore(object comp)
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
