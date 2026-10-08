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
    ///  - knocked out: the team is out for HealTime + the walk from its section to the middle; knocked out a second time it is dead
    ///    and does not come back;
    ///  - the Auto DC "recall all teams every minute" is skipped while transfers are on (it would keep the teams walking).
    /// The game itself keeps only counts per section (DamageControlTeamsNumbers[0..4]) and the reserve ([5]); teams on the move,
    /// recovering or dead are kept here and are in none of those counts. The game's own knock-out timers are taken over.
    /// </summary>
    static class DcTeams
    {
        public static Func<bool> IsEnabled = () => false;
        public static Action<string> Trace = s => { };

        // settings (AdmPlugin)
        public static float Speed = 50f, Muster = 60f, Heal = 90f, HealSpread = 0f, Effectiveness = 0.5f;
        public static int KnocksToDie = 2;   // 0 = never
        static readonly System.Random Rnd = new System.Random();

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
            public int Knocks;
            public double Arrived;       // InSection: when it got there (Auto DC does not pull a team that has just arrived)
        }

        sealed class Ship { public readonly List<Team> Teams = new List<Team>(); }
        static readonly ConditionalWeakTable<object, Ship> Ships = new ConditionalWeakTable<object, Ship>();

        // game members
        static bool _ok, _bypass;
        static PropertyInfo _teams, _defaults, _gameTime, _length;
        static MethodInfo _set, _send;
        static FieldInfo _timers, _sinking, _count, _obp;

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
            if (!_ok || iniOverride > 0) return;   // NumberOfDCTeams in the ship's ini: the game's value stays
            int n = Lookup(isSub ? SubTable : isCiv ? CivTable : WarTable, displacement);
            try
            {
                _defaults.SetValue(comp, n, null);
                _set.Invoke(comp, new object[] { 5, n });
            }
            catch (Exception e) { Trace("DcTeams.SetCount: " + e.Message); }
        }

        /// <summary>Multiplier of every damage control work rate (Repair* methods, after the crew's rate is read).</summary>
        public static float Eff(float v) { return IsEnabled() ? v * Effectiveness : v; }

        // ------------------------------------------------------------ helpers

        static double Now()
        {
            try { return Convert.ToDouble(_gameTime.GetValue(null, null), CultureInfo.InvariantCulture); } catch { return 0; }
        }

        static int[] Counts(object comp) { return _teams.GetValue(comp, null) as int[]; }

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
            // teams stay where they are as far as the counts allow (their numbers stay with them); the rest are moved
            var need = new int[6];
            for (int loc = 0; loc < 6 && loc < counts.Length; loc++) need[loc] = Math.Max(0, counts[loc]);
            var spare = new List<Team>();
            foreach (var t in s.Teams)
            {
                if (t.State != St.Pool && t.State != St.InSection) continue;
                int loc = t.State == St.Pool ? 5 : t.Section;
                if (loc >= 0 && loc < 6 && need[loc] > 0) need[loc]--; else spare.Add(t);
            }
            double now = Now();
            int k = 0;
            for (int loc = 5; loc >= 0; loc--)
                for (; need[loc] > 0; need[loc]--)
                {
                    Team t;
                    if (k < spare.Count) t = spare[k++];
                    else { t = new Team(); s.Teams.Add(t); }
                    t.State = loc == 5 ? St.Pool : St.InSection;
                    t.Section = loc == 5 ? -1 : loc;
                    if (loc < 5) t.Arrived = now;
                }
            for (; k < spare.Count; k++) s.Teams.Remove(spare[k]);
        }

        static bool InSync(object comp, Ship s)
        {
            var counts = Counts(comp);
            if (counts == null) return true;
            var mine = new int[counts.Length];
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
            t.Total = walked; t.Until = now + walked;
            Trace("DC transfer cancelled: back to " + back + " in " + walked.ToString("0", CultureInfo.InvariantCulture) + " s");
            return true;
        }

        /// <summary>Prefix of RecallAllTeams: skipped while transfers take time.</summary>
        public static bool RecallPrefix() { return !(_ok && IsEnabled() && TransfersOn); }

        /// <summary>Prefix of Compartments.OnFixedUpdate: knock-outs, arrivals, recoveries.</summary>
        public static void Tick(object comp)
        {
            if (!_ok || comp == null || !IsEnabled()) return;
            try
            {
                if ((bool)_sinking.GetValue(comp)) return;
                var s = Get(comp);
                double now = Now();
                var counts = Counts(comp);
                if (counts == null) return;

                // knocked out by a hit (Blastzone): the game took one from a section and queued a return timer - taken over
                var timers = _timers.GetValue(comp) as List<double>;
                if (timers != null && timers.Count > 0)
                {
                    for (int n = 0; n < timers.Count; n++)
                    {
                        Team hit = null;
                        for (int i = 0; i < 5 && hit == null; i++)
                        {
                            int mine = 0;
                            foreach (var t in s.Teams) if (t.State == St.InSection && t.Section == i) mine++;
                            if (i < counts.Length && mine > counts[i]) hit = s.Teams.FindLast(x => x.State == St.InSection && x.Section == i);
                        }
                        if (hit == null) break;
                        int from = hit.Section;
                        hit.Knocks++;
                        if (KnocksToDie > 0 && hit.Knocks >= KnocksToDie) { hit.State = St.Dead; Trace("DC team dead (section " + from + ")"); }
                        else
                        {
                            hit.State = St.Injured;
                            float heal = Heal + (HealSpread > 0f ? (float)(Rnd.NextDouble() * 2.0 - 1.0) * HealSpread : 0f);
                            hit.Total = Math.Max(0f, heal) + TransferTime(comp, from);
                            hit.Until = now + hit.Total;
                            Trace("DC team knocked out (section " + from + "), back in " + hit.Total + " s");
                        }
                        hit.Section = -1; hit.From = from;   // From: where it was hit (shown in its tooltip)
                    }
                    timers.Clear();
                }

                // arrivals and recoveries
                foreach (var t in s.Teams)
                {
                    if (t.State == St.Moving && t.Until <= now)
                    {
                        counts = Counts(comp);
                        if (t.Section >= 0)
                        {
                            // into the section through the game's own send (it also picks the side to drain)
                            Set(comp, 5, counts[5] + 1);
                            _bypass = true;
                            try { _send.Invoke(comp, new object[] { t.Section }); } finally { _bypass = false; }
                            t.State = St.InSection; t.Arrived = now;
                        }
                        else { Set(comp, 5, counts[5] + 1); t.State = St.Pool; }
                    }
                    else if (t.State == St.Injured && t.Until <= now)
                    {
                        counts = Counts(comp);
                        Set(comp, 5, counts[5] + 1);
                        t.State = St.Pool; t.Section = -1;
                    }
                }

                if (!InSync(comp, s)) Reconcile(comp, s);   // the game changed the counts itself (reset, load)
            }
            catch (Exception e) { Trace("DcTeams.Tick: " + e.Message); }
        }

        // ------------------------------------------------------------ saved games

        /// <summary>"state/section/left/total/knocks/from|..." per team (no ';': the game's ini reader takes it for a comment).</summary>
        public static string Save(object comp)
        {
            Ship s;
            if (!_ok || comp == null || !Ships.TryGetValue(comp, out s) || s.Teams.Count == 0) return null;
            double now = Now();
            var parts = new List<string>();
            foreach (var t in s.Teams)
                parts.Add((int)t.State + "/" + t.Section + "/" + Math.Max(0, t.Until - now).ToString("0.#", CultureInfo.InvariantCulture) + "/" +
                          t.Total.ToString("0.#", CultureInfo.InvariantCulture) + "/" + t.Knocks + "/" + t.From);
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
                    list.Add(new Team { State = (St)Math.Max(0, Math.Min(4, st)), Section = sec, From = from, Until = now + left, Total = total, Knocks = kn });
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
                Trace("DC teams loaded: " + text);
            }
            catch (Exception e) { Trace("DcTeams.Load: " + e.Message); }
        }

        // ------------------------------------------------------------ read by the UI

        public struct View { public int State; public int Section; public int From; public float Left; public float Total; public float Stay; public int Number; public int Knocks; public object Ref; }

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
                                    Stay = (float)(now - t.Arrived), Number = k + 1, Knocks = t.Knocks, Ref = t });
            }
            return list;
        }

        /// <summary>The teams working in a section, in the order of the section's icons (slot k = k-th of them).</summary>
        public static List<View> InSection(object comp, int section)
        {
            return Snapshot(comp).FindAll(v => v.State == (int)St.InSection && v.Section == section);
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
