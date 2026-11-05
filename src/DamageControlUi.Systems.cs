using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Noesis;

namespace AlternativeDamageModel
{
    /// <summary>
    /// The tooltip of a damaged system in a section's list: the damage level, the integrity and how fast the system is repaired
    /// (measured, as the hull's trend: the rate of its integrity over the last seconds of game time), what it is waiting for when nobody repairs it,
    /// and up to what level the game lets it be repaired (one damage level better than the worst it has had).
    /// The row is found under the mouse (the list's MouseMove), the tooltip is made for the row on the first move over it.
    /// </summary>
    static partial class DamageControlUi
    {
        sealed class SysTrack { public double T; public float V; public float Rate; }
        static readonly ConditionalWeakTable<object, SysTrack> SysTracks = new ConditionalWeakTable<object, SysTrack>();
        static readonly ConditionalWeakTable<object, object> SysHooked = new ConditionalWeakTable<object, object>();
        sealed class SysRow { public Border Holder; public object Summary; }   // a system row's tooltip and the system it shows now (a list may give a row to another system)
        static readonly ConditionalWeakTable<object, SysRow> SysTips = new ConditionalWeakTable<object, SysRow>();
        static int _sysTipTick; static object _sysTipFor;

        static PropertyInfo _pSumIsSystem, _pSumRepairing; static FieldInfo _fSumSystem, _fSumComp, _fSysMax, _fSysAlways, _fScList;
        static PropertyInfo _pSysCur, _pSysPct, _pSysType, _pSysBiggest, _pSysRepairable, _pSysName, _pSysInop; static bool _sysInit;

        static void SysInit(object summary, object system)
        {
            _sysInit = true;
            try
            {
                var ts = summary.GetType(); var tb = system.GetType();
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                _pSumIsSystem = ts.GetProperty("IsSystem", all); _pSumRepairing = ts.GetProperty("IsRepairing", all);
                _fSumSystem = ts.GetField("system", all); _fSumComp = ts.GetField("compartment", all);
                _fSysMax = ReserveHits.Field(tb, "_maxIntegrity"); _fSysAlways = ReserveHits.Field(tb, "_alwaysRepairable");
                _pSysCur = Prop(tb, "CurrentIntegrity"); _pSysPct = Prop(tb, "CurrentIntegrityPercent"); _pSysType = Prop(tb, "CurrentDamageType");
                _pSysBiggest = Prop(tb, "BiggestDamageType"); _pSysRepairable = Prop(tb, "Repairable"); _pSysName = Prop(tb, "DisplayName"); _pSysInop = Prop(tb, "Inoperable");
            }
            catch (Exception e) { Trace("SysInit: " + e.Message); }
        }

        static PropertyInfo Prop(Type t, string n)
        {
            for (; t != null; t = t.BaseType) { var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); if (p != null) return p; }
            return null;
        }

        /// <summary>Samples the integrity of the damaged systems of every section (Update): the repair rate, points a second of game time.</summary>
        static void TrackSystems(object comp, double t)
        {
            try
            {
                var sys = _compSys.GetValue(comp) as Array;
                if (sys == null) return;
                for (int i = 0; i < sys.Length; i++)
                {
                    var sc = sys.GetValue(i);
                    if (sc == null) continue;
                    if (_fScList == null) _fScList = ReserveHits.Field(sc.GetType(), "_listOfSystems");
                    var list = _fScList == null ? null : _fScList.GetValue(sc) as IList;
                    if (list == null) continue;
                    foreach (var s in list)
                    {
                        if (s == null) continue;
                        if (!_sysInit) { SysInitFromSystem(s); if (_pSysCur == null) return; }
                        float cur = F(_pSysCur.GetValue(s, null)), max = _fSysMax == null ? 0f : F(_fSysMax.GetValue(s));
                        SysTrack tr;
                        bool has = SysTracks.TryGetValue(s, out tr);
                        if (!has && cur >= max - 0.001f) continue;                   // untouched: nothing to follow
                        if (!has) { tr = new SysTrack { T = t, V = cur }; SysTracks.Add(s, tr); continue; }
                        if (t > tr.T)
                        {
                            float dt = (float)(t - tr.T);
                            if (dt > 20f || cur < tr.V - 0.0005f) tr.Rate = 0f;      // a gap, or a hit: no rate from it
                            else { float a = 1f - (float)Math.Exp(-dt / (TrendSmooth * 3f)); tr.Rate += ((cur - tr.V) / dt - tr.Rate) * a; }
                            tr.T = t; tr.V = cur;
                        }
                    }
                }
            }
            catch (Exception e) { Trace("TrackSystems: " + e.Message); }
        }

        static void SysInitFromSystem(object system)
        {
            _sysInit = true;
            var tb = system.GetType();
            _fSysMax = ReserveHits.Field(tb, "_maxIntegrity"); _fSysAlways = ReserveHits.Field(tb, "_alwaysRepairable");
            _pSysCur = Prop(tb, "CurrentIntegrity"); _pSysPct = Prop(tb, "CurrentIntegrityPercent"); _pSysType = Prop(tb, "CurrentDamageType");
            _pSysBiggest = Prop(tb, "BiggestDamageType"); _pSysRepairable = Prop(tb, "Repairable"); _pSysName = Prop(tb, "DisplayName"); _pSysInop = Prop(tb, "Inoperable");
        }

        /// <summary>Listens to the mouse over a section's list: the system row under it gets the tooltip.</summary>
        static void HookSystemTips(DependencyObject list)
        {
            var fe = list as FrameworkElement;
            if (fe == null) return;
            object o;
            if (SysHooked.TryGetValue(fe, out o)) return;
            SysHooked.Add(fe, fe);
            fe.MouseMove += (s, e) =>
            {
                try
                {
                    var hit = VisualTreeHelper.HitTest(fe, e.GetPosition(fe));
                    var src = hit.VisualHit as DependencyObject;
                    FrameworkElement row = null; object summary = null;
                    for (var p = src; p != null && !Same(p, fe); p = Parent(p))
                    {
                        var f = p as FrameworkElement; if (f == null) continue;
                        object dc = null; try { dc = f.DataContext; } catch { }
                        if (dc == null || dc.GetType().Name != "DamageSummary") continue;
                        if (summary != null && !ReferenceEquals(dc, summary)) break;
                        summary = dc; row = f;
                    }
                    if (row == null || summary == null) return;
                    if (_pSumIsSystem == null) { var sy0 = summary.GetType().GetField("system", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); var sv = sy0 == null ? null : sy0.GetValue(summary); if (sv != null) SysInit(summary, sv); }
                    if (_pSumIsSystem == null || !(bool)_pSumIsSystem.GetValue(summary, null)) return;
                    SysRow sr;
                    if (!SysTips.TryGetValue(row, out sr))
                    {
                        sr = new SysRow { Holder = new Border() /* no frame of its own: the tooltip has the game's */ };
                        SysTips.Add(row, sr);
                        try { ToolTipService.SetToolTip(row, sr.Holder); ToolTipService.SetPlacement(row, PlacementMode.Bottom); ToolTipService.SetShowDuration(row, int.MaxValue); } catch { }
                        // as the other live tooltips: made when it opens, and with the view's tick while it is open (OpenTips)
                        var r0 = sr;
                        LiveTip(row, sr.Holder, () => SystemTip(r0.Summary), () => SysDiag(r0.Summary));
                        _sysTipFor = null;
                    }
                    sr.Summary = summary;
                    var holder = sr.Holder;
                    int now = Environment.TickCount;
                    if (!ReferenceEquals(_sysTipFor, row) || unchecked(now - _sysTipTick) > 500)
                    {
                        _sysTipFor = row; _sysTipTick = now;
                        holder.Child = SystemTip(summary);
                    }
                }
                catch (Exception ex) { Trace("system tip: " + ex.Message); }
            };
        }

        /// <summary>
        /// Diagnostics (on opening a system's tooltip): the game's repair icon goes off when the integrity did not grow over its last 10 frames. The line
        /// gives what one physics step adds (the formula) against the float step at this integrity: a step below it is lost (no growth, the icon off).
        /// </summary>
        static void SysDiag(object summary)
        {
            try
            {
                var sysObj = _fSumSystem == null ? null : _fSumSystem.GetValue(summary);
                object rc; int ri;
                if (sysObj == null || !SysSection(summary, out rc, out ri)) return;
                float cur = F(_pSysCur.GetValue(sysObj, null)), max = F(_fSysMax.GetValue(sysObj));
                int w; float e; int b; SysTeams(rc, ri, out w, out e, out b);
                var sc = (_compSys.GetValue(rc) as Array).GetValue(ri);
                var target = SysRepairFix.Target(sc);
                float rate = w * DcControl.SysRepairBase(rc) * (DcTeams.IsEnabled() ? DcTeams.Effectiveness * e : 1f);
                float dt = UnityEngine.Time.fixedDeltaTime;
                int bits = BitConverter.ToInt32(BitConverter.GetBytes(cur), 0);
                float ulp = BitConverter.ToSingle(BitConverter.GetBytes(bits + 1), 0) - cur;
                bool flag = _pSumRepairing != null && (bool)_pSumRepairing.GetValue(summary, null);
                string tn = "none";
                try { if (target != null) { var dn = _pSysName.GetValue(target, null); var pv = dn == null ? null : dn.GetType().GetProperty("Value"); tn = pv == null ? Convert.ToString(dn) : Convert.ToString(pv.GetValue(dn, null)); } } catch { }
                Trace("System tip: section " + ri + ", integrity " + cur.ToString("0.####", CultureInfo.InvariantCulture) + " / " + max.ToString("0.##", CultureInfo.InvariantCulture) +
                      ", teams on systems " + w + ", repaired now: '" + tn + "'" + (ReferenceEquals(target, sysObj) ? " (this one)" : "") + ", game flag " + flag +
                      ", rate " + rate.ToString("0.#####", CultureInfo.InvariantCulture) + "/s, fixed dt " + dt.ToString("0.####", CultureInfo.InvariantCulture) +
                      " (time scale " + UnityEngine.Time.timeScale.ToString("0.##", CultureInfo.InvariantCulture) + "), float step at this integrity " + ulp.ToString("0.########", CultureInfo.InvariantCulture));
            }
            catch (Exception ex) { Trace("SysDiag: " + ex.Message); }
        }

        static string Level(object type)
        {
            switch (type == null ? "" : type.ToString())
            {
                case "No": return Loc.T("none");
                case "Light": return Loc.T("light");
                case "Medium": return Loc.T("medium");
                case "Severe": return Loc.T("severe");
                case "Destroyed": return Loc.T("destroyed");
                default: return type == null ? "?" : type.ToString().ToLowerInvariant();
            }
        }

        // the damage levels by the integrity (BaseSystem.SetCurrentDamageType): over 90% none, over 60 light, over 20 medium, over 1 severe
        static float LevelTop(string biggest)
        {
            switch (biggest) { case "Light": return 90f; case "Medium": return 60f; case "Severe": return 20f; default: return -1f; }
        }

        static string Clock2(float seconds)
        {
            int s = (int)Math.Max(0f, seconds + 0.5f);
            return (s / 60).ToString("0", CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        static FrameworkElement SystemTip(object summary)
        {
            var root = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10f) };
            Func<string, Color, bool, TextBlock> line = (t, c, bold) => new TextBlock { Text = t, FontSize = 12f, Foreground = new SolidColorBrush(c), FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, Margin = new Thickness(0f, 1f, 0f, 1f) };
            var text = Color.FromRgb(225, 228, 232); var dim = Color.FromRgb(150, 156, 164);
            try
            {
                var sysObj = _fSumSystem == null ? null : _fSumSystem.GetValue(summary);
                if (sysObj == null) { root.Children.Add(line(Loc.T("System"), text, true)); return root; }
                float cur = F(_pSysCur.GetValue(sysObj, null)), max = F(_fSysMax.GetValue(sysObj)), pct = F(_pSysPct.GetValue(sysObj, null));
                string type = _pSysType.GetValue(sysObj, null).ToString(), biggest = _pSysBiggest.GetValue(sysObj, null).ToString();
                bool repairable = _pSysRepairable == null || (bool)_pSysRepairable.GetValue(sysObj, null);
                bool always = _fSysAlways != null && (bool)_fSysAlways.GetValue(sysObj);
                string name = null;
                try { var dn = _pSysName == null ? null : _pSysName.GetValue(sysObj, null); if (dn != null) { var pv = dn.GetType().GetProperty("Value"); name = pv == null ? dn as string : pv.GetValue(dn, null) as string; } } catch { }
                bool inop = false;
                try { var io = _pSysInop == null ? null : _pSysInop.GetValue(sysObj, null); if (io != null) inop = (bool)io.GetType().GetProperty("Value").GetValue(io, null); } catch { }
                bool repairing = _pSumRepairing != null && (bool)_pSumRepairing.GetValue(summary, null);
                // the repair as the mod knows it (not the game's flag, which goes off whenever the integrity did not grow over its last 10 frames):
                // a team of the section works on systems and this is the system RepairSystems takes; the rate from the game's formula
                // (teams x Crew._systemRepair x SystemRepairSpeed x DCTeamEffectiveness x their health / fatigue x the fire penalty)
                // a system may stand in the lists of several sections (an engine, a long magazine): each section whose teams work on systems and
                // take it first repairs it - the rate is the sum, the teams are counted over those sections
                float rate = 0f; int wSys = 0; float effSys = 1f; bool known = false, elsewhere = false;
                {
                    object rc0; int ri0;
                    if (SysRepairFix.Ready && SysSection(summary, out rc0, out ri0))
                    {
                        known = true;
                        var arr0 = _compSys.GetValue(rc0) as Array;
                        float effSum = 0f;
                        for (int j = 0; arr0 != null && j < arr0.Length; j++)
                        {
                            var scj = arr0.GetValue(j);
                            if (scj == null) continue;
                            if (j != ri0)
                            {
                                if (_fScList == null) _fScList = ReserveHits.Field(scj.GetType(), "_listOfSystems");
                                var lj = _fScList == null ? null : _fScList.GetValue(scj) as IList;
                                if (lj == null || !lj.Contains(sysObj)) continue;
                            }
                            int wj; float ej; int bj; SysTeams(rc0, j, out wj, out ej, out bj);
                            if (wj <= 0 || !ReferenceEquals(SysRepairFix.Target(scj), sysObj)) continue;
                            float fireMul = DcTeams.FireWorkEff < 1f && DcControl.FireIn(rc0, j) ? DcTeams.FireWorkEff : 1f;
                            rate += wj * DcControl.SysRepairBase(rc0) * (DcTeams.IsEnabled() ? DcTeams.Effectiveness * ej * fireMul : 1f);
                            wSys += wj; effSum += ej * wj;
                            if (j != ri0) elsewhere = true;
                        }
                        repairing = wSys > 0;
                        if (wSys > 0) effSys = effSum / wSys;
                    }
                }

                root.Children.Add(line(string.IsNullOrEmpty(name) ? Loc.T("System") : name, text, true));
                root.Children.Add(line(Loc.F("Damage: {0}, integrity {1} / {2} ({3})", Level(type), cur.ToString("0.#", CultureInfo.InvariantCulture), max.ToString("0.#", CultureInfo.InvariantCulture), Pc(pct)) + (inop ? ", " + Loc.T("out of order") : ""), text, false));

                // how it stands with the repair: the rate is the measured one (points of integrity a second of game time)
                if (!known) { SysTrack tr; SysTracks.TryGetValue(sysObj, out tr); rate = tr == null ? 0f : tr.Rate; }   // members not found: the measured rate
                float top = LevelTop(biggest);
                bool limitReached = biggest != type && Rank(type) < Rank(biggest);    // already one level better than the worst it has had: no more repair
                if (type == "Destroyed" && !always) root.Children.Add(line(Loc.T("Destroyed: cannot be repaired"), dim, false));
                else if (!repairable) root.Children.Add(line(Loc.T("Cannot be repaired"), dim, false));
                else if (limitReached) root.Children.Add(line(Loc.T("Repaired as far as it can be"), dim, false));
                else
                {
                    if (repairing && rate > 0.0001f && max > 0f)
                    {
                        root.Children.Add(line(Loc.F("Being repaired: +{0} per minute (+{1} of its integrity)", (rate * 60f).ToString("0.0", CultureInfo.InvariantCulture), Pc(100f * rate * 60f / max)), text, false));
                        if (top > 0f && pct < top) root.Children.Add(line(Loc.F("Level {0} in {1}", Level(NextBetter(type)), Clock2((top + 0.01f - pct) * max / 100f / rate)), text, false));
                    }
                    else if (repairing) root.Children.Add(line(Loc.T("Being repaired"), text, false));
                    if (repairing && wSys > 0)
                        root.Children.Add(line(Loc.F(elsewhere ? "Teams repairing it (from other sections too): {0}, efficiency {1}%" : "Teams repairing systems here: {0}, efficiency {1}%",
                                                     wSys.ToString(CultureInfo.InvariantCulture), Math.Round(effSys * 100f).ToString("0", CultureInfo.InvariantCulture)), dim, false));
                    else
                    {
                        string why = Loc.T("Waiting for a team");
                        try
                        {
                            var sc = _fSumComp == null ? null : _fSumComp.GetValue(summary);
                            var comp = _colsComp.Target; var arr = _compSys.GetValue(comp) as Array;
                            int i = arr == null ? -1 : Array.IndexOf(arr, sc);
                            var counts = _compTeams.GetValue(comp, null) as int[];
                            if (i >= 0 && counts != null && counts[i] <= 0) why = Loc.T("No damage control team in the section");
                            else if (i >= 0 && SectionEffects.Locked(comp, i)) why = Loc.T("The section is flooded: no repair");
                            else if (i >= 0)
                            {
                                int w; float e; int busy; SysTeams(comp, i, out w, out e, out busy);
                                if (w > 0) why = Loc.T("The team here repairs another system of the section first");
                                else if (busy != TaskNone) why = Loc.F("The team here is busy: {0}", TaskName(busy));
                            }
                        }
                        catch { }
                        root.Children.Add(line(why, dim, false));
                    }
                    if (top > 0f) root.Children.Add(line(Loc.F("The game lets it be repaired up to {0} (one level better than the worst it had)", Pc(top)), dim, false));
                }
            }
            catch (Exception e) { Trace("SystemTip: " + e.Message); root.Children.Add(line(Loc.T("System"), text, true)); }
            return root;
        }

        /// <summary>The section of a system's summary (and the ship): false when unknown.</summary>
        static bool SysSection(object summary, out object comp, out int i)
        {
            comp = null; i = -1;
            try
            {
                var sc = _fSumComp == null ? null : _fSumComp.GetValue(summary);
                comp = _colsComp.Target; var arr = comp == null ? null : _compSys.GetValue(comp) as Array;
                i = arr == null ? -1 : Array.IndexOf(arr, sc);
                return i >= 0;
            }
            catch { return false; }
        }

        /// <summary>The teams of section i that repair systems now: their number and mean efficiency (health, fatigue; the engine penalty does not apply to systems);
        /// busy = what the first other team does (TaskNone when none works at anything else).</summary>
        static void SysTeams(object comp, int i, out int working, out float eff, out int busy)
        {
            working = 0; eff = 1f; busy = TaskNone;
            try
            {
                var counts = _compTeams.GetValue(comp, null) as int[];
                int n = counts != null && i < counts.Length ? counts[i] : 0;
                if (n <= 0) return;
                int[] forced, actual;
                DcControl.GetTeams(comp, i, n, out forced, out actual);
                var here = DcTeams.InSection(comp, i);
                float sum = 0f;
                for (int k = 0; k < n && k < actual.Length; k++)
                {
                    int did = actual[k];
                    if (did == TaskSystems) { working++; sum += k < here.Count ? DcTeams.EffOf(here[k].Health, here[k].Fatigue) : 1f; }
                    else if (busy == TaskNone && did != TaskNone && did != TaskIdle) busy = did;
                }
                if (working > 0) eff = sum / working;
            }
            catch { }
        }

        static int Rank(string type)
        {
            switch (type) { case "No": return 0; case "Light": return 1; case "Medium": return 2; case "Severe": return 3; case "Destroyed": return 4; default: return 0; }
        }

        static string NextBetter(string type)
        {
            switch (type) { case "Destroyed": return "Severe"; case "Severe": return "Medium"; case "Medium": return "Light"; default: return "No"; }
        }
    }
}
