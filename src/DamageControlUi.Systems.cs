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
        static readonly ConditionalWeakTable<object, Border> SysTips = new ConditionalWeakTable<object, Border>();
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
                    Border holder;
                    if (!SysTips.TryGetValue(row, out holder))
                    {
                        holder = new Border()   /* no frame of its own: the tooltip has the game's */;
                        SysTips.Add(row, holder);
                        try { ToolTipService.SetToolTip(row, holder); ToolTipService.SetPlacement(row, PlacementMode.Bottom); ToolTipService.SetShowDuration(row, int.MaxValue); } catch { }
                        _sysTipFor = null;
                    }
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

                root.Children.Add(line(string.IsNullOrEmpty(name) ? Loc.T("System") : name, text, true));
                root.Children.Add(line(Loc.F("Damage: {0}, integrity {1} / {2} ({3})", Level(type), cur.ToString("0.#", CultureInfo.InvariantCulture), max.ToString("0.#", CultureInfo.InvariantCulture), Pc(pct)) + (inop ? ", " + Loc.T("out of order") : ""), text, false));

                // how it stands with the repair: the rate is the measured one (points of integrity a second of game time)
                SysTrack tr; SysTracks.TryGetValue(sysObj, out tr);
                float rate = tr == null ? 0f : tr.Rate;
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
