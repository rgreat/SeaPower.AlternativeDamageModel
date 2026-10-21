using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Damage control teams as people (game logic, no UI):
    ///  - how many a ship has: by displacement, from the [DamageControlTeams] tables (NumberOfDCTeams in the ship's ini still wins);
    ///  - transfers: a team sent to a section, or sent back to the reserve, walks there - muster time + the distance between the
    ///    section (its place along the hull) and the middle of the ship at TransferSpeed; rounded to 10 s. A walking team is in no
    ///    section and cannot be given orders;
    ///  - health: every team has health (a part of its maximum, which grows with the ship's displacement). A hit that knocks a team
    ///    out of its section wounds it and it walks to the reserve; a fire in the section it does not fight wounds it too; in the
    ///    reserve it recovers. At 0 it is dead. A wounded team works slower;
    ///  - fatigue: working builds it up, the reserve takes it away; a tired team works at half speed;
    ///  - the Auto DC "recall all teams every minute" is skipped while transfers are on (it would keep the teams walking).
    /// The game itself keeps only counts per section (DamageControlTeamsNumbers[0..4]) and the reserve ([5]); teams on the move,
    /// recovering or dead are kept here and are in none of those counts. The game's own knock-out timers are taken over.
    /// </summary>
    static class DcTeams
    {
        public static Func<bool> IsEnabled = () => false;     // Alternative Damage Control on: transfers, knock-outs, our counts
        public static Func<bool> IsTracking = () => false;    // the mod on: the team objects follow the game's counts (numbers for the UI)
        public static Action<string> Trace = s => { };
        public static Action<string> Info = s => { };

        // settings (AdmPlugin)
        public static float Speed = 50f, Muster = 60f, Effectiveness = 0.5f;
        public static float HealthMul = 1f;       // [DamageControlTeams] TeamHealth: x the maximum health; 0 = no wounds
        public static float RegenRate = 0.5f;     // % of the full health a second in the reserve
        public static float FireWound = 1f;       // % of the full health a second at fire 100% (a team that does not fight it)
        public static float HitWound = 1f;        // x the wound of one knock-out
        public static float FatigueRecover = 100f; // % of the build-up speed the reserve takes fatigue away at
        public static float FatigueMin = 0f;      // minutes of work until tired; 0 = off
        public const float TiredEff = 0.5f;       // a tired team works at this part of its speed
        public const float MinWoundEff = 0.25f;   // a wounded team works at its health, but not below this
        const float HitHp = 50f;                  // one knock-out takes this much of the 100 of a 1000 t ship (two knock-outs killed a team before)

        /// <summary>Transfers take time (TransferSpeed or MusterTime not 0): walking teams, no recall, the optimised Auto DC.</summary>
        public static bool TransfersOn { get { return Speed > 0f || Muster > 0f; } }
        public static int[][] WarTable = Parse("0=1, 500=2, 1500=3, 5000=4, 20000=5");
        public static int[][] SubTable = Parse("0=1, 1000=2, 5000=3, 8000=4");
        public static int[][] CivTable = Parse("0=1, 20000=2");

        public enum St { Pool, Moving, InSection, Injured, Dead }

        public sealed class Team
        {
            public St State;
            public int Section = -1;     // InSection: where; Moving: where to (-1 = the reserve)
            public int From = -1;        // Moving: where from (-1 = the reserve)
            public double Until, Total;  // Moving / Injured: when it is over, and how long it takes in all
            public float Health = 1f;    // 0..1 of the maximum; 0 = dead
            public double Fatigue;       // seconds of work in a row (at most twice the limit); the reserve takes it away at FatigueRecover % of that speed
            public double Arrived;       // InSection: when it got there (Auto DC does not pull a team that has just arrived)
            public int Task, Doing;      // InSection: the task given by hand (DcControl, 0 = by priority) and what it did last tick
            public bool Back;            // Moving: turned back (Cancel Transfer) - no muster in its time
        }

        sealed class Ship { public readonly List<Team> Teams = new List<Team>(); public int Timers; public double LastT = -1; }
        static readonly ConditionalWeakTable<object, StrongBox<float>> Disp = new ConditionalWeakTable<object, StrongBox<float>>();
        static readonly ConditionalWeakTable<object, Ship> Ships = new ConditionalWeakTable<object, Ship>();

        // game members
        static bool _ok, _bypass;
        static PropertyInfo _teams, _defaults, _gameTime, _length;
        static MethodInfo _set, _send;
        static FieldInfo _timers, _sinking, _count, _obp, _lastRecall, _base;
        static PropertyInfo _destroyed;

        public static bool Init(Assembly asm)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var t = asm.GetType("SeaPower.Compartments");
                _teams = t.GetProperty("DamageControlTeamsNumbers", all);
                _defaults = t.GetProperty("DamageControlTeamDefaultsNumber", all);
                _set = t.GetMethod("SetDamageControlTeamsNumbers", all);
                _send = t.GetMethod("SendDCTeamToCompartment", all);
                _timers = t.GetField("_damageControlTeamsDisabledTimers", all);
                _sinking = t.GetField("_isSinking", all);
                _count = t.GetField("_compartmentsCount", all);
                _obp = t.GetField("_obp", all);
                _lastRecall = t.GetField("_dcTeamsLastRecall", all);
                _base = t.GetField("_baseObject", all);
                var tBo = asm.GetType("SeaPower.BaseObject");
                _destroyed = tBo == null ? null : tBo.GetProperty("IsDestroyed", all);
                _gameTime = asm.GetType("SeaPower.GameTime").GetProperty("missionElapsedTime", all);
                _length = asm.GetType("SeaPower.ObjectBaseParameters").GetProperty("_length", all);
                _ok = _teams != null && _defaults != null && _set != null && _send != null && _timers != null && _sinking != null && _count != null && _gameTime != null;
            }
            catch (Exception e) { Trace("DcTeams.Init: " + e); _ok = false; }
            return _ok;
        }

        // ------------------------------------------------------------ tables "tons=teams, ..."

        public static int[][] Parse(string text)
        {
            var list = new List<int[]>();
            foreach (var part in (text ?? "").Split(','))
            {
                var kv = part.Split('=');
                int tons, n;
                if (kv.Length == 2 && int.TryParse(kv[0].Trim(), out tons) && int.TryParse(kv[1].Trim(), out n) && n > 0) list.Add(new[] { tons, n });
            }
            list.Sort((a, b) => a[0].CompareTo(b[0]));
            if (list.Count == 0) list.Add(new[] { 0, 1 });
            return list.ToArray();
        }

        static int Lookup(int[][] table, float tons)
        {
            int n = table[0][1];
            foreach (var row in table) if (tons >= row[0]) n = row[1];
            return n;
        }

        /// <summary>Called from Compartments.Init (AdmPlugin.ApplyHull): the ship's number of teams, all in the reserve.</summary>
        public static void SetCount(object comp, float displacement, bool isSub, bool isCiv, int iniOverride)
        {
            try { Disp.GetOrCreateValue(comp).Value = displacement; } catch { }
            if (!_ok || iniOverride > 0) return;   // NumberOfDCTeams in the ship's ini: the game's value stays
            int n = Lookup(isSub ? SubTable : isCiv ? CivTable : WarTable, displacement);
            try
            {
                _defaults.SetValue(comp, n, null);
                _set.Invoke(comp, new object[] { 5, n });
            }
            catch (Exception e) { Trace("DcTeams.SetCount: " + e.Message); }
        }

        /// <summary>The mod's number of teams for a ship (tables; NumberOfDCTeams in its ini wins); -1 = leave it.</summary>
        public static int ModCount(float displacement, bool isSub, bool isCiv, int iniOverride)
        {
            if (iniOverride > 0) return iniOverride;
            return Lookup(isSub ? SubTable : isCiv ? CivTable : WarTable, displacement);
        }

        /// <summary>The game's number of teams (Compartments.Init): civilian 1; submarine 1, over 5000 t 2; others 1, over 7000 t 2, over 24000 t 3.</summary>
        public static int GameCount(float displacement, bool isSub, bool isCiv, int iniOverride)
        {
            if (iniOverride > 0) return iniOverride;
            if (isCiv) return 1;
            if (isSub) return displacement > 5000f ? 2 : 1;
            return displacement > 24000f ? 3 : displacement > 7000f ? 2 : 1;
        }

        /// <summary>
        /// A ship at sea gets another number of teams (settings changed, Alternative Damage Control switched): teams added to the
        /// reserve, or taken from the reserve first, then from the sections (last section first).
        /// </summary>
        public static void Retarget(object comp, int total, string who = null)
        {
            if (!_ok || comp == null || total < 1) return;
            int cur = Convert.ToInt32(_defaults.GetValue(comp, null));
            if (cur == total) return;
            var counts = Counts(comp);
            if (counts == null || counts.Length < 6) return;
            int diff = total - cur;
            if (diff > 0) Set(comp, 5, counts[5] + diff);
            else
            {
                int take = -diff;
                int fromPool = Math.Min(take, counts[5]);
                if (fromPool > 0) { Set(comp, 5, counts[5] - fromPool); take -= fromPool; }
                for (int i = 4; i >= 0 && take > 0; i--)
                {
                    counts = Counts(comp);
                    int t = Math.Min(take, counts[i]);
                    if (t > 0) { Set(comp, i, counts[i] - t); take -= t; }
                }
            }
            _defaults.SetValue(comp, total, null);
            Ship s;
            if (Ships.TryGetValue(comp, out s)) Reconcile(comp, s);
            Info("DC teams" + (who != null ? " of " + who : "") + ": " + cur + " -> " + total + (IsEnabled() ? " (the mod's table)" : " (the game's rule)"));
        }

        /// <summary>Alternative Damage Control switched off: teams on the move arrive, knocked-out and lost ones are back in the reserve.</summary>
        public static void ReleaseAll(object comp)
        {
            Ship s;
            if (!_ok || comp == null || !Ships.TryGetValue(comp, out s)) return;
            foreach (var t in s.Teams)
            {
                var counts = Counts(comp);
                if (counts == null) return;
                if (t.State == St.Moving && t.Section >= 0 && t.Section < 5) { Set(comp, t.Section, counts[t.Section] + 1); t.State = St.InSection; t.Arrived = Now(); }
                else if (t.State == St.Moving || t.State == St.Injured || t.State == St.Dead) { Set(comp, 5, counts[5] + 1); t.State = St.Pool; t.Section = -1; }
                t.Health = 1f; t.Fatigue = 0;
            }
        }

        /// <summary>Multiplier of every damage control work rate (Repair* methods, after the crew's rate is read).</summary>
        public static float WorkMul = 1f;       // set around one section's repair calls (DcControl): teams working amid a fire they do not fight
        public static float FireWorkEff = 1f;   // [FireAndFlooding] FireWorkEfficiency / 100 (1 = off)
        public static float Eff(float v) { return IsEnabled() ? v * Effectiveness * WorkMul : v; }

        // ------------------------------------------------------------ health and fatigue

        public static bool HealthOn { get { return HealthMul > 0f; } }
        public static float FatigueLimit { get { return FatigueMin * 60f; } }

        /// <summary>The ship's team health in points: 100 at 1000 t, growing with the square root of the displacement, x TeamHealth.</summary>
        public static float MaxHp(object comp)
        {
            StrongBox<float> d;
            float tons = comp != null && Disp.TryGetValue(comp, out d) && d.Value > 0f ? d.Value : 1000f;
            return Math.Max(60f, 100f * (float)Math.Sqrt(tons / 1000f)) * HealthMul;
        }

        public static bool Wounded(Team t) { return Wounded(t.Health); }
        public static bool Tired(Team t) { return Tired(t.Fatigue); }
        public static bool Wounded(float health) { return IsEnabled() && HealthOn && health < 0.995f; }
        public static bool Tired(double fatigue) { return IsEnabled() && FatigueMin > 0f && fatigue >= FatigueLimit; }

        /// <summary>How well a team works now: its health (not below MinWoundEff) and, when tired, half of that.</summary>
        public static float TeamEff(Team t)
        {
            if (t == null || !IsEnabled()) return 1f;
            float e = Wounded(t) ? Math.Max(MinWoundEff, t.Health) : 1f;
            return Tired(t) ? e * TiredEff : e;
        }

        /// <summary>" (Wounded)" / " (Tired)" / " (Wounded, Tired)" for the teams that work slower because of it; "" when none.</summary>
        public static string Status(Team t)
        {
            bool w = Wounded(t), f = Tired(t);
            return w && f ? "Wounded, Tired" : w ? "Wounded" : f ? "Tired" : "";
        }

        /// <summary>Team health and fatigue, every tick: fire wounds, reserve recovery, work builds fatigue.</summary>
        static void Condition(object comp, Ship s, double dt)
        {
            float lim = FatigueLimit;
            foreach (var t in s.Teams)
            {
                if (t.State == St.Pool)
                {
                    if (HealthOn && t.Health < 1f) t.Health = Math.Min(1f, t.Health + RegenRate / 100f * (float)dt);
                    if (t.Fatigue > 0) t.Fatigue = Math.Max(0, t.Fatigue - dt * FatigueRecover / 100.0);
                }
                else if (t.State == St.InSection)
                {
                    if (lim > 0f && t.Doing != DcControl.TaskNone && t.Doing != DcControl.TaskIdle) t.Fatigue = Math.Min(2.0 * lim, t.Fatigue + dt);
                    if (HealthOn && FireWound > 0f && t.Doing != DcControl.TaskFire && t.Section >= 0 && t.Section < 5)
                    {
                        float fire = DcControl.FireLevel(comp, t.Section);
                        if (fire > 0f)
                        {
                            t.Health -= FireWound / 100f * fire * (float)dt;
                            if (t.Health <= 0f)
                            {
                                var counts = Counts(comp);
                                if (counts != null && t.Section < counts.Length && counts[t.Section] > 0) Set(comp, t.Section, counts[t.Section] - 1);
                                int sec = t.Section;
                                Leave(t);
                                t.State = St.Dead; t.Health = 0f; t.From = sec; t.Section = -1;
                                Trace("DC team burnt (section " + sec + ")");
                            }
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------ helpers

        static double Now()
        {
            try { return Convert.ToDouble(_gameTime.GetValue(null, null), CultureInfo.InvariantCulture); } catch { return 0; }
        }

        static int[] Counts(object comp) { return _teams.GetValue(comp, null) as int[]; }

        /// <summary>The team leaves its section: a task given by hand stays with the section it was given for.</summary>
        static void Leave(Team t) { t.Task = 0; t.Doing = 0; t.Back = false; }

        /// <summary>Sinking or destroyed: nothing to do for the teams any more (the game stops damage control too).</summary>
        public static bool Out(object comp)
        {
            try
            {
                if ((bool)_sinking.GetValue(comp)) return true;
                var bo = _base == null ? null : _base.GetValue(comp);
                return bo != null && _destroyed != null && (bool)_destroyed.GetValue(bo, null);
            }
            catch { return false; }
        }

        static void Set(object comp, int i, int v) { _set.Invoke(comp, new object[] { i, v }); }

        /// <summary>The ship's length, metres (ObjectBaseParameters._length); 0 when unknown.</summary>
        public static float ShipLength(object comp)
        {
            try { var obp = _obp == null ? null : _obp.GetValue(comp); if (obp != null && _length != null) return Math.Max(0f, Convert.ToSingle(_length.GetValue(obp, null))); } catch { }
            return 0f;
        }

        /// <summary>Walk between section i and the middle of the ship, with the muster time; rounded to 10 s.</summary>
        public static float TransferTime(object comp, int i)
        {
            float len = 100f;
            try { var obp = _obp.GetValue(comp); if (obp != null && _length != null) len = Convert.ToSingle(_length.GetValue(obp, null)); } catch { }
            if (len <= 0f) len = 100f;
            int count = 5;
            try { count = Math.Max(1, (int)_count.GetValue(comp)); } catch { }
            float pos = (i + 0.5f) / count;   // place of the section along the hull, 0..1
            float dist = Math.Abs(pos - 0.5f) * len;
            float t = Muster + (Speed > 0f ? dist / Speed * 60f : 0f);
            return t <= 0f ? 0f : Math.Max(10f, (float)Math.Round(t / 10f) * 10f);
        }

        /// <summary>Walk between two places (-1 = the reserve, in the middle of the ship), with the muster time; rounded to 10 s.</summary>
        public static float TransferTime(object comp, int a, int b)
        {
            float len = 100f;
            try { var obp = _obp.GetValue(comp); if (obp != null && _length != null) len = Convert.ToSingle(_length.GetValue(obp, null)); } catch { }
            if (len <= 0f) len = 100f;
            int count = 5;
            try { count = Math.Max(1, (int)_count.GetValue(comp)); } catch { }
            Func<int, float> pos = i => i < 0 ? 0.5f : (i + 0.5f) / count;
            float dist = Math.Abs(pos(a) - pos(b)) * len;
            float t = Muster + (Speed > 0f ? dist / Speed * 60f : 0f);
            return t <= 0f ? 0f : Math.Max(10f, (float)Math.Round(t / 10f) * 10f);
        }

        static Ship Get(object comp)
        {
            Ship s;
            if (Ships.TryGetValue(comp, out s)) return s;
            s = Ships.GetOrCreateValue(comp);
            Reconcile(comp, s);
            return s;
        }

        /// <summary>
        /// Makes the teams in the reserve and in the sections match the game's counts (start of a mission, a loaded game, the
        /// game's own resets). Teams on the move, recovering or dead are kept; a team missing is added, a team too many removed.
        /// </summary>
        static void Reconcile(object comp, Ship s)
        {
            var counts = Counts(comp);
            if (counts == null) return;
            int total = Total(comp);
            // teams stay where they are as far as the counts allow (their numbers stay with them); the rest are moved
            var need = new int[6];
            int counted = 0;
            for (int loc = 0; loc < 6 && loc < counts.Length; loc++) { need[loc] = Math.Max(0, counts[loc]); counted += need[loc]; }
            var spare = new List<Team>();
            var other = new List<Team>();   // on the move, recovering, lost: in no count of the game
            foreach (var t in s.Teams)
            {
                if (t.State != St.Pool && t.State != St.InSection) { other.Add(t); continue; }
                int loc = t.State == St.Pool ? 5 : t.Section;
                if (loc >= 0 && loc < 6 && need[loc] > 0) need[loc]--; else spare.Add(t);
            }
            // strict accounting: the ship has 'total' teams. The game may put them all back into its counts itself (RepairAllDamage:
            // all in the reserve) - teams on the move / recovering / lost beyond what is left are those teams: they take the
            // places first (lost ones first), so no team is ever counted twice
            int allowed = total > 0 ? Math.Max(0, total - counted) : other.Count;
            if (other.Count > allowed)
            {
                other.Sort((a, b) => Rank(a).CompareTo(Rank(b)));
                for (int k2 = 0; k2 < other.Count - allowed; k2++) spare.Add(other[k2]);
            }
            double now = Now();
            int k = 0, made = 0;
            for (int loc = 5; loc >= 0; loc--)
                for (; need[loc] > 0; need[loc]--)
                {
                    Team t;
                    if (k < spare.Count) t = spare[k++];
                    else { t = new Team(); s.Teams.Add(t); made++; }
                    if (t.State != (loc == 5 ? St.Pool : St.InSection) || t.Section != (loc == 5 ? -1 : loc)) Leave(t);
                    t.State = loc == 5 ? St.Pool : St.InSection;
                    t.Section = loc == 5 ? -1 : loc;
                    if (other.Contains(t)) { t.Health = 1f; t.Fatigue = 0; }   // a lost / recovering team the game put back: whole again
                    if (loc < 5) t.Arrived = now;
                }
            int removed = spare.Count - k;
            for (; k < spare.Count; k++) s.Teams.Remove(spare[k]);
            if (made > 0 || removed > 0) Trace("DC teams reconciled: +" + made + " / -" + removed + ", now " + s.Teams.Count + " of " + total);
            if (total > 0 && s.Teams.Count > total)
                Info("DC teams: " + s.Teams.Count + " team objects for " + total + " teams (game counts " + counted + ")");
        }

        /// <summary>Which teams outside the game's counts give way first: lost, then recovering, then those on the move.</summary>
        static int Rank(Team t) { return t.State == St.Dead ? 0 : t.State == St.Injured ? 1 : 2; }

        /// <summary>The ship's number of teams (Compartments.DamageControlTeamDefaultsNumber); 0 = unknown.</summary>
        static int Total(object comp)
        {
            try { return Math.Max(0, Convert.ToInt32(_defaults.GetValue(comp, null))); } catch { return 0; }
        }

        static int[] _mine = new int[8];   // InSync's buffer (main thread; no array every physics tick)

        static bool InSync(object comp, Ship s)
        {
            var counts = Counts(comp);
            if (counts == null) return true;
            if (_mine.Length < counts.Length) _mine = new int[counts.Length];
            var mine = _mine;
            Array.Clear(mine, 0, mine.Length);
            foreach (var t in s.Teams)
            {
                if (t.State == St.Pool && counts.Length > 5) mine[5]++;
                else if (t.State == St.InSection && t.Section >= 0 && t.Section < mine.Length) mine[t.Section]++;
            }
            for (int i = 0; i < counts.Length; i++) if (mine[i] != counts[i]) return false;
            return true;
        }

        // ------------------------------------------------------------ hooks

        /// <summary>Prefix of SendDCTeamToCompartment(index): the team leaves the reserve and walks to the section.</summary>
        public static bool SendPrefix(object comp, int index)
        {
            if (!_ok || _bypass || comp == null || !IsEnabled() || !TransfersOn) return true;
            var counts = Counts(comp);
            if (counts == null || counts.Length < 6 || counts[5] < 1 || index < 0 || index > 4) return true;
            var s = Get(comp);
            var t = s.Teams.Find(x => x.State == St.Pool);
            if (t == null) { Reconcile(comp, s); t = s.Teams.Find(x => x.State == St.Pool); }
            if (t == null) return true;
            Set(comp, 5, counts[5] - 1);
            t.State = St.Moving; t.Section = index; t.From = -1; t.Total = TransferTime(comp, index); t.Until = Now() + t.Total;
            return false;
        }

        /// <summary>Prefix of ReturnDCTeamToPool(index): the team leaves the section and walks back to the reserve.</summary>
        public static bool ReturnPrefix(object comp, int index)
        {
            if (!_ok || _bypass || comp == null || !IsEnabled() || !TransfersOn) return true;
            var counts = Counts(comp);
            if (counts == null || index < 0 || index > 4 || counts[index] < 1) return true;
            var s = Get(comp);
            var t = s.Teams.FindLast(x => x.State == St.InSection && x.Section == index);
            if (t == null) { Reconcile(comp, s); t = s.Teams.FindLast(x => x.State == St.InSection && x.Section == index); }
            if (t == null) return true;
            Set(comp, index, counts[index] - 1);
            Leave(t);
            t.State = St.Moving; t.Section = -1; t.From = index; t.Total = TransferTime(comp, index); t.Until = Now() + t.Total;
            return false;
        }

        /// <summary>A team working in section 'from' goes straight to section 'to' (Auto DC off; the UI's "Move to section").</summary>
        public static bool MoveToSection(object comp, int from, int to, bool auto = false, int slot = -1)
        {
            if (!_ok || comp == null || !IsEnabled() || from == to || from < 0 || from > 4 || to < 0 || to > 4) return false;
            var counts = Counts(comp);
            if (counts == null || counts[from] < 1) return false;
            var s = Get(comp);
            Team t = null;
            if (slot >= 0)
            {
                var here = s.Teams.FindAll(x => x.State == St.InSection && x.Section == from);
                if (slot < here.Count) t = here[slot];
            }
            if (t == null) t = s.Teams.FindLast(x => x.State == St.InSection && x.Section == from);
            if (t == null) return false;
            Set(comp, from, counts[from] - 1);
            Leave(t);
            t.State = St.Moving; t.From = from; t.Section = to; t.Total = TransferTime(comp, from, to); t.Until = Now() + t.Total;
            Trace("DC team moved: section " + from + " -> " + to + ", " + t.Total + " s");
            return true;
        }

        /// <summary>
        /// The UI's "Move to section -> To Reserve": the team in slot 'slot' of section 'from' goes back to the reserve - walking
        /// while transfers take time, at once otherwise (as the game's ReturnDCTeamToPool).
        /// </summary>
        public static bool ToReserve(object comp, int from, int slot)
        {
            if (!_ok || comp == null || !IsEnabled() || from < 0 || from > 4) return false;
            var counts = Counts(comp);
            if (counts == null || counts.Length < 6 || counts[from] < 1) return false;
            var s = Get(comp);
            var here = s.Teams.FindAll(x => x.State == St.InSection && x.Section == from);
            Team t = slot >= 0 && slot < here.Count ? here[slot] : (here.Count > 0 ? here[here.Count - 1] : null);
            if (t == null) return false;
            Set(comp, from, counts[from] - 1);
            Leave(t);
            if (TransfersOn)
            {
                t.State = St.Moving; t.Section = -1; t.From = from; t.Total = TransferTime(comp, from); t.Until = Now() + t.Total;
            }
            else
            {
                Set(comp, 5, counts[5] + 1);
                t.State = St.Pool; t.Section = -1;
            }
            Trace("DC team to the reserve from section " + from);
            return true;
        }

        /// <summary>Sends a team from the reserve through the game's own (patched) method: it walks there.</summary>
        public static void Send(object comp, int index)
        {
            try { _send.Invoke(comp, new object[] { index }); } catch (Exception e) { Trace("DcTeams.Send: " + e.Message); }
        }

        /// <summary>
        /// "Cancel Transfer": the team turns back. It needs as long as it has walked so far - the time spent so far less the
        /// muster time (no new muster); a team still mustering is back at once.
        /// </summary>
        public static bool CancelTransfer(object comp, object teamRef)
        {
            var t = teamRef as Team;
            if (!_ok || comp == null || t == null || t.State != St.Moving) return false;
            double now = Now();
            double spent = t.Total - Math.Max(0, t.Until - now);
            double walked = Math.Max(0, spent - Muster);
            int back = t.From;
            t.From = t.Section; t.Section = back;
            t.Total = walked; t.Until = now + walked; t.Back = true;
            Trace("DC transfer cancelled: back to " + back + " in " + walked.ToString("0", CultureInfo.InvariantCulture) + " s");
            return true;
        }

        /// <summary>Prefix of RecallAllTeams: skipped while transfers take time.</summary>
        /// <summary>Prefix of RecallAllTeams: skipped while transfers take time (the time of the recall is still set, as the game
        /// does first thing - else Compartments.OnFixedUpdate would call it again every tick).</summary>
        public static bool RecallPrefix(object comp)
        {
            if (!(_ok && IsEnabled() && TransfersOn)) return true;
            try { if (_lastRecall != null && comp != null) _lastRecall.SetValue(comp, Now()); } catch { }
            return false;
        }

        /// <summary>Prefix of Compartments.OnFixedUpdate: knock-outs, arrivals, recoveries.</summary>
        public static void Tick(object comp)
        {
            if (!_ok || comp == null) return;
            if (!IsEnabled())
            {
                // the game's own rules: only keep the team objects in step with its counts (the tab numbers them)
                if (!IsTracking()) return;
                try
                {
                    Ship sh;
                    if (Ships.TryGetValue(comp, out sh))
                    {
                        if (!InSync(comp, sh) || (Total(comp) > 0 && sh.Teams.Count > Total(comp))) Reconcile(comp, sh);
                        var tl = _timers.GetValue(comp) as List<double>;
                        sh.Timers = tl == null ? 0 : tl.Count;   // the game's own timers: not ours to take over when switched on
                    }
                }
                catch { }
                return;
            }
            try
            {
                if (Out(comp)) return;
                var s = Get(comp);
                double now = Now();
                var counts = Counts(comp);
                if (counts == null) return;

                // knocked out by a hit (Blastzone): the game took one from a section and queued a return timer (appended at the
                // end) - taken over. Only timers added since the last tick count, and only as far as a team of ours is missing in a
                // section; the others (queued before, e.g. while ADC was off) the game keeps and returns the team itself
                var timers = _timers.GetValue(comp) as List<double>;
                int nTimers = timers == null ? 0 : timers.Count;
                int fresh = nTimers - s.Timers;
                if (fresh > 0)
                {
                    int taken = 0;
                    for (int n = 0; n < fresh; n++)
                    {
                        Team hit = null;
                        for (int i = 0; i < 5 && hit == null; i++)
                        {
                            int mine = 0;
                            foreach (var t in s.Teams) if (t.State == St.InSection && t.Section == i) mine++;
                            if (i < counts.Length && mine > counts[i]) hit = s.Teams.FindLast(x => x.State == St.InSection && x.Section == i);
                        }
                        if (hit == null) break;   // no team of ours missing
                        taken++;
                        int from = hit.Section;
                        Leave(hit);
                        float wound = HealthOn ? HitHp * HitWound / MaxHp(comp) : 0f;
                        hit.Health -= wound;
                        if (HealthOn && hit.Health <= 0f) { hit.State = St.Dead; hit.Health = 0f; Trace("DC team dead (section " + from + ")"); }
                        else
                        {
                            hit.State = St.Injured;   // wounded: walks to the reserve
                            hit.Total = TransferTime(comp, from);
                            hit.Until = now + hit.Total;
                            Trace("DC team wounded (section " + from + "), health " + hit.Health.ToString("0.00", CultureInfo.InvariantCulture) + ", back in " + hit.Total + " s");
                        }
                        hit.Section = -1; hit.From = from;   // From: where it was hit (shown in its tooltip)
                    }
                    if (taken > 0) timers.RemoveRange(timers.Count - taken, taken);   // the newest ones: those just added
                }
                s.Timers = timers == null ? 0 : timers.Count;

                // arrivals and recoveries
                foreach (var t in s.Teams)
                {
                    if (t.State == St.Moving && t.Until <= now)
                    {
                        counts = Counts(comp);
                        t.Back = false;
                        if (t.Section >= 0 && t.Section <= 4)
                        {
                            // into the section through the game's own send (it also picks the side to drain)
                            Set(comp, 5, counts[5] + 1);
                            _bypass = true;
                            try { _send.Invoke(comp, new object[] { t.Section }); }
                            catch { Set(comp, 5, Counts(comp)[5] - 1); throw; }
                            finally { _bypass = false; }
                            t.State = St.InSection; t.Arrived = now;
                        }
                        else { Set(comp, 5, counts[5] + 1); t.State = St.Pool; t.Section = -1; }
                    }
                    else if (t.State == St.Injured && t.Until <= now)
                    {
                        counts = Counts(comp);
                        Set(comp, 5, counts[5] + 1);
                        t.State = St.Pool; t.Section = -1;
                    }
                }

                double dt = s.LastT < 0 ? 0 : Math.Min(5.0, Math.Max(0.0, now - s.LastT));
                s.LastT = now;
                if (dt > 0) Condition(comp, s, dt);

                int total = Total(comp);
                if (!InSync(comp, s) || (total > 0 && s.Teams.Count > total)) Reconcile(comp, s);   // the game changed the counts itself (reset, repair, load)
            }
            catch (Exception e) { Trace("DcTeams.Tick: " + e.Message); }
        }

        // ------------------------------------------------------------ saved games

        /// <summary>"state/section/left/total/0/from/stay/health/fatigue|..." per team (no ';': the game's ini reader takes it for a comment).</summary>
        public static string Save(object comp)
        {
            Ship s;
            if (!_ok || comp == null || !Ships.TryGetValue(comp, out s) || s.Teams.Count == 0) return null;
            double now = Now();
            var parts = new List<string>();
            foreach (var t in s.Teams)
                parts.Add((int)t.State + "/" + t.Section + "/" + Math.Max(0, t.Until - now).ToString("0.#", CultureInfo.InvariantCulture) + "/" +
                          t.Total.ToString("0.#", CultureInfo.InvariantCulture) + "/0/" + t.From + "/" +
                          (t.State == St.InSection ? Math.Max(0, now - t.Arrived) : 0).ToString("0", CultureInfo.InvariantCulture) + "/" +
                          t.Health.ToString("0.###", CultureInfo.InvariantCulture) + "/" + t.Fatigue.ToString("0", CultureInfo.InvariantCulture));
            return string.Join("|", parts.ToArray());
        }

        /// <summary>Puts the teams back as they were saved, and the game's counts with them (the game does not save them).</summary>
        public static void Load(object comp, string text)
        {
            if (!_ok || comp == null || string.IsNullOrEmpty(text)) return;
            try
            {
                double now = Now();
                var list = new List<Team>();
                foreach (var p in text.Split('|'))
                {
                    var f = p.Split('/');
                    if (f.Length < 5) continue;
                    int st, sec, kn; double left, total;
                    if (!int.TryParse(f[0], out st) || !int.TryParse(f[1], out sec) || !double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out left) ||
                        !double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out total) || !int.TryParse(f[4], out kn)) continue;
                    int from = -1;
                    if (f.Length > 5) int.TryParse(f[5], out from);
                    double stay = 0;
                    if (f.Length > 6) double.TryParse(f[6], NumberStyles.Float, CultureInfo.InvariantCulture, out stay);
                    double hp = 1, fat = 0;
                    if (f.Length > 7) double.TryParse(f[7], NumberStyles.Float, CultureInfo.InvariantCulture, out hp);
                    if (f.Length > 8) double.TryParse(f[8], NumberStyles.Float, CultureInfo.InvariantCulture, out fat);
                    // a broken / hand-edited save: places out of range are the reserve
                    if (sec < -1 || sec > 4) sec = -1;
                    if (from < -1 || from > 4) from = -1;
                    var state = (St)Math.Max(0, Math.Min(4, st));
                    if (state == St.InSection && sec < 0) state = St.Pool;
                    if (state == St.Pool) sec = -1;
                    list.Add(new Team { State = state, Section = sec, From = from, Until = now + Math.Max(0, left), Total = Math.Max(0, total),
                                        Health = (float)Math.Max(0, Math.Min(1, hp)), Fatigue = Math.Max(0, fat), Arrived = now - Math.Max(0, stay) });
                }
                if (list.Count == 0) return;
                var s = Ships.GetOrCreateValue(comp);
                s.Teams.Clear();
                s.Teams.AddRange(list);
                var counts = Counts(comp);
                if (counts == null) return;
                for (int i = 0; i < counts.Length; i++)
                {
                    int n = 0;
                    foreach (var t in list)
                        if ((i == 5 && t.State == St.Pool) || (i < 5 && t.State == St.InSection && t.Section == i)) n++;
                    Set(comp, i, n);
                }
                try { _defaults.SetValue(comp, list.Count, null); } catch { }   // the saved teams are all the ship's teams
                Trace("DC teams loaded: " + text);
            }
            catch (Exception e) { Trace("DcTeams.Load: " + e.Message); }
        }

        // ------------------------------------------------------------ read by the UI

        public struct View { public int State; public int Section; public int From; public float Left; public float Total; public float Stay; public int Number; public float Health; public float Fatigue; public bool Back; public object Ref; }

        /// <summary>Teams that are in no section: moving, recovering, dead (and the reserve as State Pool).</summary>
        public static List<View> Snapshot(object comp)
        {
            var list = new List<View>();
            if (!_ok || comp == null) return list;
            Ship s;
            if (!Ships.TryGetValue(comp, out s)) return list;
            double now = Now();
            for (int k = 0; k < s.Teams.Count; k++)
            {
                var t = s.Teams[k];
                list.Add(new View { State = (int)t.State, Section = t.Section, From = t.From, Left = (float)Math.Max(0, t.Until - now), Total = (float)t.Total,
                                    Stay = (float)(now - t.Arrived), Number = k + 1, Health = t.Health, Fatigue = (float)t.Fatigue, Back = t.Back, Ref = t });
            }
            return list;
        }

        /// <summary>The teams working in a section, in the order of the section's icons (slot k = k-th of them).</summary>
        public static List<View> InSection(object comp, int section)
        {
            return Snapshot(comp).FindAll(v => v.State == (int)St.InSection && v.Section == section);
        }

        /// <summary>The team objects working in a section, in the order of the section's icons (DcControl: tasks given by hand).</summary>
        public static List<Team> SectionTeams(object comp, int section)
        {
            var list = new List<Team>();
            return SectionTeams(comp, section, list) ? list : null;
        }

        /// <summary>The same into 'into' (cleared first): no new list - for the physics tick.</summary>
        public static bool SectionTeams(object comp, int section, List<Team> into)
        {
            into.Clear();
            if (!_ok || comp == null) return false;
            var s = Get(comp);
            foreach (var t in s.Teams) if (t.State == St.InSection && t.Section == section) into.Add(t);
            return true;
        }

        /// <summary>Where a team is now: its section and its slot there (-1 when it is in no section).</summary>
        public static int SlotOf(object comp, object teamRef, out int section)
        {
            section = -1;
            var t = teamRef as Team;
            Ship s;
            if (t == null || comp == null || !Ships.TryGetValue(comp, out s) || t.State != St.InSection) return -1;
            section = t.Section;
            int k = 0;
            foreach (var x in s.Teams) { if (x == t) return k; if (x.State == St.InSection && x.Section == section) k++; }
            return -1;
        }

        /// <summary>The teams waiting in the reserve.</summary>
        public static List<View> InPool(object comp)
        {
            return Snapshot(comp).FindAll(v => v.State == (int)St.Pool);
        }

        public static bool Active(object comp)
        {
            Ship s;
            return _ok && comp != null && IsEnabled() && Ships.TryGetValue(comp, out s);
        }
    }
}
