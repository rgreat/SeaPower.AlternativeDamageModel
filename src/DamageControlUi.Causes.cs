using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Why a value on the Damage Control tab changes - the tooltips of its arrows (made when they open, from the state now):
    ///  - Flooding (per side): holes (leak rate), who plugs / pumps (by team number), water over the deck edge from heavy list
    ///    or from trim past the limit (TrimFlooding.FloodsNow), the leak growing by itself (hull below 19% and no team), sinking;
    ///  - Fire: its own growth, hits, who fights it, the stage and what it does (spreading, systems, hull);
    ///  - Hull (per side): fire in the section, hits, who welds, how far it can be welded.
    /// The game's rules behind it (Compartments.CheckDamageControl / Repair*, FloodingCompartment.OnFixedUpdate,
    /// SystemCompartment.OnFixedUpdate) - see DEVNOTES.
    /// </summary>
    static partial class DamageControlUi
    {
        static bool _causeInit;
        static FieldInfo _fcRate, _fcCurInt, _fcMaxInt, _fcFaster, _scGrow, _compSinking, _compDrainPort;
        static PropertyInfo _fcMinFlood, _fcMaxAllowed, _fcFloodDelta;

        static void InitCauses(object comp, object fc, object sc)
        {
            if (_causeInit) return;
            _causeInit = true;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var tF = fc.GetType(); var tS = sc.GetType(); var tC = comp.GetType();
                _fcRate = tF.GetField("_floodingRate", all); _fcCurInt = tF.GetField("_currentIntegrity", all); _fcMaxInt = tF.GetField("_maxIntegrity", all);
                _fcFaster = tF.GetField("_activateFasterFloodingForSinking", all);
                _fcMinFlood = tF.GetProperty("MinimumFlooding", all); _fcMaxAllowed = tF.GetProperty("MaxAllowedIntegrity", all); _fcFloodDelta = tF.GetProperty("FloodingDelta", all);
                _scGrow = tS.GetField("_fireGrowRate", all);
                _compSinking = tC.GetField("_isSinking", all); _compDrainPort = tC.GetField("_drainPortSide", all);
            }
            catch (Exception e) { Trace("InitCauses: " + e.Message); }
        }

        static float GetF(FieldInfo f, object o) { try { return f == null || o == null ? 0f : Convert.ToSingle(f.GetValue(o), CultureInfo.InvariantCulture); } catch { return 0f; } }
        static float GetP(PropertyInfo p, object o) { try { return p == null || o == null ? 0f : Convert.ToSingle(p.GetValue(o, null), CultureInfo.InvariantCulture); } catch { return 0f; } }
        static bool GetB(FieldInfo f, object o) { try { return f != null && o != null && (bool)f.GetValue(o); } catch { return false; } }

        static string R(float v) { return Math.Abs(v).ToString(Math.Abs(v) < 0.1f ? "0.00" : "0.0", CultureInfo.InvariantCulture); }
        static string Pc(float v) { return v.ToString(v < 10f ? "0.#" : "0", CultureInfo.InvariantCulture) + "%"; }

        static double Now()
        {
            try { return Convert.ToDouble(_gameTime.GetValue(null, null), CultureInfo.InvariantCulture); } catch { return 0; }
        }

        /// <summary>The numbers of the teams in section i doing 'task' now ("DC1, DC3"), and how many teams are in the section.</summary>
        static List<string> Doing(object comp, int i, int task, out int inSection)
        {
            var list = new List<string>();
            inSection = 0;
            try
            {
                var counts = _compTeams.GetValue(comp, null) as int[];
                int n = counts != null && i < counts.Length ? counts[i] : 0;
                inSection = n;
                int[] forced, actual;
                DcControl.GetTeams(comp, i, n, out forced, out actual);
                var here = DcTeams.InSection(comp, i);
                for (int k = 0; k < n && k < actual.Length; k++)
                    if (actual[k] == task) list.Add(k < here.Count ? Dc(here[k].Number) : "a team");
            }
            catch (Exception e) { Trace("Doing: " + e.Message); }
            return list;
        }

        static string Who(List<string> teams) { return string.Join(", ", teams.ToArray()); }

        static int Coming(object comp, int i)
        {
            int n = 0;
            foreach (var v in DcTeams.Snapshot(comp)) if (v.State == (int)DcTeams.St.Moving && v.Section == i) n++;
            return n;
        }

        static void Line(StringBuilder sb, string s) { if (!string.IsNullOrEmpty(s)) sb.Append("\n• ").Append(s); }

        static string Rate(float r) { return (r >= 0 ? "+" : "−") + R(r) + "%/s"; }

        /// <summary>Who is (not) on it: "DC1 plugging" / "busy: DC2 fire" / "team coming" / "no team".</summary>
        static string OnIt(object comp, int i, List<string> doing, string verb, int inSec, List<string> busy, string busyWith)
        {
            if (doing.Count > 0) return Who(doing) + " " + verb;
            if (busy != null && busy.Count > 0) return "Busy: " + Who(busy) + " " + busyWith + " first";
            if (inSec > 0) return "Team busy with other work";
            return Coming(comp, i) > 0 ? "Team on its way" : "No team here";
        }

        // ------------------------------------------------------------ flooding

        static string FloodCause(object comp, Col c, int i, bool port)
        {
            try
            {
                var fc = ((port ? _compPort : _compStar).GetValue(comp) as Array).GetValue(i);
                var other = ((port ? _compStar : _compPort).GetValue(comp) as Array).GetValue(i);
                var sc = (_compSys.GetValue(comp) as Array).GetValue(i);
                InitCauses(comp, fc, sc);
                float water = F(_fcFloodPct.GetValue(fc, null)), rate = port ? c.RP : c.RS;
                float leak = GetF(_fcRate, fc), leakOther = GetF(_fcRate, other);
                float integ = F(_fcIntPct.GetValue(fc, null));
                bool sinking = GetB(_compSinking, comp);
                int inSec;
                var plugging = Doing(comp, i, TaskLeak, out inSec);
                var pumping = Doing(comp, i, TaskPump, out inSec);
                var counter = Doing(comp, i, TaskCounter, out inSec);
                var fighting = Doing(comp, i, TaskFire, out inSec);

                var sb = new StringBuilder((port ? "Port" : "Starboard") + ": " + Pc(water) + ", " + Rate(rate));
                if (counter.Count > 0 && DcControl.CounterSide(comp, i) == (port ? 1 : -1)) Line(sb, Who(counter) + " counterflooding");
                if (rate >= 0)
                {
                    if (leak > 0f) { Line(sb, "Leak " + R(leak)); Line(sb, OnIt(comp, i, plugging, "plugging", inSec, fighting, "fire")); }
                    else if (leakOther > 0f && plugging.Count > 0) Line(sb, Who(plugging) + " plugging other side");
                    if (_shownListComp.Target == comp)
                    {
                        float list = _shownList;
                        if (!IsSub(comp) && !SubmergedFlooding.Covers(comp) && (port ? list > 0f : list < 0f) && Math.Abs(list) > (ModelOn() ? Limits.ListFlood : 45f))
                            Line(sb, "List " + Math.Abs(list).ToString("0", CultureInfo.InvariantCulture) + "°: water over deck");
                    }
                    float pitch;
                    if (TrimFlooding.FloodsNow(comp, i, port, out pitch))
                        Line(sb, "Trim " + Math.Abs(pitch).ToString("0.0", CultureInfo.InvariantCulture) + "° " + (pitch > 0f ? "bow" : "stern") + " down: water over deck");
                    foreach (var why in SubmergedFlooding.Reasons(comp, i, port, _sectionNames)) Line(sb, why);
                    if (integ < 19f && inSec == 0 && leak > 0f) Line(sb, "Leak grows: hull " + Pc(integ) + ", no team");
                    if (sinking) Line(sb, "Sinking");
                }
                else if (pumping.Count > 0) Line(sb, Who(pumping) + " pumping");
                return sb.ToString();
            }
            catch (Exception e) { Trace("FloodCause: " + e.Message); return "Flooding"; }
        }

        // ------------------------------------------------------------ fire

        static string FireCause(object comp, Col c, int i)
        {
            try
            {
                var sc = (_compSys.GetValue(comp) as Array).GetValue(i);
                var fc = (_compPort.GetValue(comp) as Array).GetValue(i);
                InitCauses(comp, fc, sc);
                float max = F(_scMaxSev.GetValue(sc));
                float fire = max > 0f ? 100f * F(_scSeverity.GetValue(sc, null)) / max : 0f;
                float grow = max > 0f ? 100f * GetF(_scGrow, sc) / max : 0f;
                float rate = c.RF;
                string stage = fire > 49.8f ? "Severe" : fire > 24.9f ? "Moderate" : "Minor";
                int inSec;
                var fighting = Doing(comp, i, TaskFire, out inSec);
                var plugging = Doing(comp, i, TaskLeak, out inSec);

                var sb = new StringBuilder("Fire " + Pc(fire) + " (" + stage + "), " + Rate(rate));
                double ago = c.FireHitT >= 0 ? Now() - c.FireHitT : -1;
                if (grow > 0f) Line(sb, "Grows " + R(grow) + "%/s by itself");
                if (rate >= 0 && ago >= 0 && ago < 60) Line(sb, "Hit " + ((int)ago).ToString(CultureInfo.InvariantCulture) + " s ago");
                Line(sb, OnIt(comp, i, fighting, rate >= 0 ? "fighting" : "putting out", inSec, plugging, "leak"));
                if (fighting.Count == 0) Line(sb, "Only DC puts it out");
                double un = AdmPlugin.UnattendedFor(comp, i);
                if (un >= 0) Line(sb, "Unattended " + ((int)un).ToString(CultureInfo.InvariantCulture) + " s: grows faster");
                // the game: the hull burns at every stage (in proportion to the fire); systems take damage from time to time -
                // a little while minor, more when moderate, most when severe; only a severe fire spreads
                if (fire > 49.8f) Line(sb, "Spreads, hurts systems, hull");
                else if (fire > 24.9f) Line(sb, "Hurts systems, hull");
                else if (fire > 0f) Line(sb, "Hurts hull, systems a little");
                return sb.ToString();
            }
            catch (Exception e) { Trace("FireCause: " + e.Message); return "Fire"; }
        }

        // ------------------------------------------------------------ hull

        static string HullCause(object comp, Col c, int i, bool port)
        {
            try
            {
                var fc = ((port ? _compPort : _compStar).GetValue(comp) as Array).GetValue(i);
                var sc = (_compSys.GetValue(comp) as Array).GetValue(i);
                InitCauses(comp, fc, sc);
                float integ = F(_fcIntPct.GetValue(fc, null)), rate = port ? c.RIP : c.RIS;
                float maxInt = GetF(_fcMaxInt, fc);
                float allowed = maxInt > 0f ? 100f * GetP(_fcMaxAllowed, fc) / maxInt : 100f;
                float smax = F(_scMaxSev.GetValue(sc));
                float fire = smax > 0f ? 100f * F(_scSeverity.GetValue(sc, null)) / smax : 0f;
                int inSec;
                var welding = Doing(comp, i, TaskHull, out inSec);
                bool drainPort = true;
                try { var arr = _compDrainPort == null ? null : _compDrainPort.GetValue(comp) as bool[]; if (arr != null && i < arr.Length) drainPort = arr[i]; } catch { }
                double hit = port ? c.HullHitP : c.HullHitS;
                double ago = hit >= 0 ? Now() - hit : -1;

                var sb = new StringBuilder((port ? "Port" : "Starboard") + " hull " + Pc(integ) + (rate >= 0 ? ", repairing" : ", falling"));
                if (rate < 0)
                {
                    if (fire > 0f) Line(sb, "Fire " + Pc(fire) + " burns it");
                    if (ago >= 0 && ago < 60) Line(sb, "Hit " + ((int)ago).ToString(CultureInfo.InvariantCulture) + " s ago");
                    else if (fire <= 0f) Line(sb, "Hits");
                }
                else if (welding.Count > 0) Line(sb, Who(welding) + " welding" + (drainPort == port ? "" : " (other side first)"));
                if (allowed < 99.95f) Line(sb, "Weldable up to " + Pc(allowed));
                return sb.ToString();
            }
            catch (Exception e) { Trace("HullCause: " + e.Message); return "Hull"; }
        }
    }
}
