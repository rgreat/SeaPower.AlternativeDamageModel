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
    /// Unit Status -> Damage Control tab (SeapowerUI.DamageControlView), extended in place:
    ///  - section header: "Aft   41% v / 47% v"  - integrity of the port / starboard compartment, one arrow when it changes
    ///    (green up: repaired, red down: burning or hit); nothing at 100% / 100%;
    ///  - "DC Teams: N" -> one icon per team, showing what the compartment's teams are doing now
    ///    (plug leak, fight fire, pump water, repair hull, repair systems, no task);
    ///  - "Flooding 23% ^^ / 5% v" - water per side and its trend; "Fire 64% ^" - fire severity and its trend.
    /// The game's text blocks are found in the tab's visual tree by what they are bound to (the rows by their DamageSummary,
    /// the column by the ItemsControl of that compartment, the team row by the -/+ buttons' commands) and their Inlines are
    /// rewritten; nothing of the game's layout is replaced. If something is not found the game's text stays.
    /// Noesis types are used directly (Noesis.NoesisGUI.dll); the game's own types by reflection.
    /// </summary>
    static partial class DamageControlUi
    {
        // ------------------------------------------------------------ settings (set by AdmPlugin)
        public static float FloodOne = 0.02f, FloodTwo = 0.25f, FireOne = 0.05f, FireTwoUp = 1f, FireTwoDown = 0.25f;
        public static Action<string> Trace = s => { };
        public static Action<string> Warn = s => { };
        public static bool Verbose;
        /// <summary>Alternative Damage Control on (orders to single teams, DC Priority); the view itself works either way.</summary>
        public static Func<bool> ModelOn = () => true;
        static bool Manual(object comp) { return !DcControl.AutoDc(comp); }   // tasks by hand: always; moves only with the damage model
        /// <summary>
        /// True when DamageSummary.DamageIntensity no longer notifies on an unchanged value (AdmPlugin's prefix): the list items
        /// then stay, and the Flooding / Fire rows are drawn into (coloured arrows). False: text mode via Description.
        /// </summary>
        public static bool StableRows;
        public static string DumpFile;      // BepInEx/AlternativeDamageModel.dctab.txt: written once when the tab is not recognised
        static bool _dumpedOnce, _warnedOnce;
        static string _lastStats = "";

        // ------------------------------------------------------------ tasks (DcControl decides; shown here)
        const int TaskNone = DcControl.TaskNone, TaskIdle = DcControl.TaskIdle, TaskLeak = DcControl.TaskLeak, TaskFire = DcControl.TaskFire,
                  TaskPump = DcControl.TaskPump, TaskHull = DcControl.TaskHull, TaskSystems = DcControl.TaskSystems;
        const int IconByPriority = 99;   // menu entry "By priority": a gear in a ring of arrows
        const int IconKnocked = 98;      // (old) knocked out: the crewman greyed out with a red cross
        const int IconTransit = 97, IconInjured = 96, IconDead = 95;   // team on the move (grey), recovering (medical badge), dead (red cross)

        // ------------------------------------------------------------ open views
        static readonly List<WeakReference> Views = new List<WeakReference>();

        public static void RegisterView(object view)
        {
            if (view == null) return;
            lock (Views)
            {
                Views.RemoveAll(w => !w.IsAlive);
                Views.Add(new WeakReference(view));
            }
            var fe = view as FrameworkElement;
            if (fe != null)
            {
                // the tab opened / another unit shown: work at once (the next frame), not at the next quarter-second tick
                try { fe.IsVisibleChanged += OnViewVisible; } catch (Exception e) { Trace("IsVisibleChanged: " + e.Message); }
                try { fe.DataContextChanged += OnViewContext; } catch (Exception e) { Trace("DataContextChanged: " + e.Message); }
            }
            Kick();
            Trace("Damage Control view created");
        }

        static void OnViewVisible(object sender, DependencyPropertyChangedEventArgs e)
        {
            try { if (e.NewValue is bool && (bool)e.NewValue) Kick(); } catch { }
        }

        static void OnViewContext(object sender, DependencyPropertyChangedEventArgs e) { Kick(); }

        /// <summary>
        /// Work on the next frame, and for a second after it every frame while a view is not recognised yet (its rows are made
        /// by the layout pass after the tab opens or the unit changes); failed tries in that second are not counted as failures.
        /// </summary>
        static void Kick() { _kick = true; }

        /// <summary>Every frame between the ticks: whether a shown view now has another unit (another view model, or the same
        /// one with another ship) - only reference checks.</summary>
        static bool UnitChanged()
        {
            lock (Views)
            {
                for (int i = 0; i < Views.Count; i++)
                {
                    var fe = Views[i].Target as FrameworkElement;
                    ViewState st;
                    if (fe == null || !States.TryGetValue(fe, out st) || st.Vm == null) continue;
                    try
                    {
                        if (!fe.IsVisible) continue;
                        var vm = fe.DataContext;
                        if (vm == null || !_vmType.IsInstanceOfType(vm)) continue;   // Process would not take it either: no kick loop
                        var comp = _vmComp.GetValue(vm, null);
                        if (comp == null) continue;
                        if (!ReferenceEquals(vm, st.Vm) || !ReferenceEquals(comp, st.Comp)) return true;
                    }
                    catch { }
                }
            }
            return false;
        }

        static bool _kick, _pending;
        static float _fastUntil = -1f;

        // ------------------------------------------------------------ game members
        static bool _init, _ok;
        static PropertyInfo _vmComp, _vmSums, _vmAssign, _vmReturn, _sumDesc, _fcIntPct, _fcFloodPct, _scSeverity, _compTeams, _gameTime;
        static FieldInfo _compPort, _compStar, _compSys, _compFloodStates, _compCount, _scMaxSev, _scFireState;
        static Type _vmType;

        public static bool Init(Assembly asm)
        {
            if (_init) return _ok;
            _init = true;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                _vmType = asm.GetType("SeapowerUI.ViewModels.DamageControlViewModel");
                var tComp = asm.GetType("SeaPower.Compartments");
                var tFc = asm.GetType("SeaPower.FloodingCompartment");
                var tSc = asm.GetType("SeaPower.SystemCompartment");
                var tSum = asm.GetType("SeapowerUI.ViewModels.DamageSummary");
                var tGt = asm.GetType("SeaPower.GameTime");
                _vmComp = _vmType.GetProperty("Compartments", all);
                _vmSums = _vmType.GetProperty("DamageSummaries", all);
                _vmAssign = _vmType.GetProperty("AssignDamageControlTeam", all);
                _vmReturn = _vmType.GetProperty("ReturnDamageControlTeam", all);
                _sumDesc = tSum.GetProperty("Description", all);
                _fcIntPct = tFc.GetProperty("CurrentIntegrityPercent", all);
                _fcFloodPct = tFc.GetProperty("CurrentFloodingPercent", all);
                _scSeverity = tSc.GetProperty("FireSeverity", all);
                _scMaxSev = tSc.GetField("_maxFireSeverity", all);
                _scFireState = tSc.GetField("fireState", all);
                _compTeams = tComp.GetProperty("DamageControlTeamsNumbers", all);
                _compPort = tComp.GetField("_portCompartments", all);
                _compStar = tComp.GetField("_starboardCompartments", all);
                _compSys = tComp.GetField("_systemCompartments", all);
                _compFloodStates = tComp.GetField("floodStates", all);
                _compCount = tComp.GetField("_compartmentsCount", all);
                _gameTime = tGt.GetProperty("missionElapsedTime", all);
                _ok = _vmComp != null && _vmSums != null && _vmAssign != null && _vmReturn != null && _sumDesc != null && _fcIntPct != null &&
                      _fcFloodPct != null && _scSeverity != null && _scMaxSev != null && _scFireState != null && _compTeams != null &&
                      _compPort != null && _compStar != null && _compSys != null && _compFloodStates != null && _compCount != null && _gameTime != null;
            }
            catch (Exception e) { Trace("DamageControlUi.Init: " + e); _ok = false; }
            if (!_ok) Warn("Damage Control tab: game members not found (game updated?); the detailed view is off.");
            return _ok;
        }

        // ------------------------------------------------------------ per view state
        sealed class Col
        {
            public TextBlock Header, Teams, Flood, Fire;
            public object FloodSum, FireSum;
            public DependencyObject List;                                       // this compartment's ListBox
            public int Column;                                                  // its Grid.Column (display order: left to right)
            public string FloodDesc, FireDesc;                                  // the game's Description of the rows
            public string FloodData, FireData;                                  // what we wrote into Description (text mode), or null
            public string HeaderOrig, TeamsOrig, FloodOrig, FireOrig;          // the game's own text
            public string HeaderSig, TeamsSig, FloodSig, FireSig;              // what we rendered
            public string HeaderShown, TeamsShown, FloodShown, FireShown;      // TextBlock.Text right after rendering
            // trend samples
            public double T = -1;
            public float P, S, F, IP, IS, RP, RS, RF, RIP, RIS;
            public double FireHitT = -1, HullHitP = -1, HullHitS = -1;          // game time of the last jump (a hit)
            public float RealT = -1;                                            // real time of the last sample (a gap: the tab was hidden)
            public int FindMisses; public float NextFind;                       // rows not found: looked for every tick for 2 s, then once a second
        }

        sealed class ViewState
        {
            public float Now;                     // real time of this tick
            public object Vm, Comp;               // the unit shown now: the tab is reused when another unit is selected
            public Border AutoCard; public UIElement AutoContent; public Panel AutoStack;   // AutoStack: our panel it is in
            public Image Picture; public float PictureMaxW, PictureMaxH; public Thickness PictureMargin;   // the ship's profile, made bigger   // the game's Auto DC card and its content, moved into ours
            public bool Ok, Dumped;
            public float NextDiscover;
            public int Fails;
            public Col[] Cols;
            public TextBlock Help;               // "Auto DC prioritizes threats in this order: ..."
            public Border Priority;              // our "DC Priority" card
            public Panel PriorityHost;           // the grid of the summary cards it was added to
            public ToggleButton[] PriorityButtons;
            public Extras X;                     // Fires row and the list / trim panel (DamageControlUi.Panels.cs)
            public string HelpOrig, HelpShown;
            public TextBlock HelpTitle;          // "Auto Damage Control Status" above it: folded into the same line
            public string HelpTitleText;
            public TextBlock TabHeader;          // "Damage Control" on the Unit Status tab button: gets the (i) with the legend
            public FrameworkElement View;        // the Damage Control view itself (the settings window opens over its screen)
            public string TabHeaderShown, TabHeaderText, HelpSig;
        }

        static readonly ConditionalWeakTable<object, ViewState> States = new ConditionalWeakTable<object, ViewState>();
        static float _nextTick;
        static bool _wasActive;

        /// <summary>Called every frame by the plugin; works 4 times a second.</summary>
        /// <summary>A setting of the view changed: the next tick decorates / restores the views and scans the headers at once.</summary>
        public static void RefreshNow() { _nextTick = 0f; _scanNow = true; }

        /// <summary>The detailed view is on: the (i) legend next to the gear (without it only the gear, to switch it on again).</summary>
        static bool _full, _headersOn;

        /// <summary>active: the detailed view; on: the mod (the gear of the settings window stays while the mod is on).</summary>
        static float _tickNow;

        public static void Tick(float now, bool active, bool on)
        {
            if (!_kick && now < _nextTick && _ok && active) _kick = UnitChanged();
            if (_kick) { _kick = false; _fastUntil = now + 1f; _nextTick = 0f; }
            if (now < _nextTick) return;
            _nextTick = now + 0.25f;
            _tickNow = now;
            if (!_ok) return;
            _pending = false;

            List<object> views = new List<object>();
            lock (Views)
            {
                for (int i = Views.Count - 1; i >= 0; i--)
                {
                    var v = Views[i].Target;
                    if (v == null) Views.RemoveAt(i); else views.Add(v);
                }
            }

            _full = active;
            if (!active)
            {
                if (_wasActive)
                {
                    foreach (var v in views) try { Restore(v, true); } catch (Exception e) { Trace("Restore: " + e); }
                    Groups.Clear(); OpenTips.Clear(); OpenMenus.Clear();   // our icons are gone
                }
                _wasActive = false;
                if (on) { try { DecorateStatusViews(now); } catch (Exception e) { Trace("DecorateStatusViews: " + e.Message); } }   // the gear only
                else if (_headersOn) { try { RestoreHeaders(); } catch (Exception e) { Trace("RestoreHeaders: " + e); } }
                _headersOn = on;
                return;
            }
            _wasActive = true; _headersOn = true;
            try { DecorateStatusViews(now); } catch (Exception e) { Trace("DecorateStatusViews: " + e.Message); }

            foreach (var v in views)
            {
                try { Process(v, now); }
                catch (Exception e) { Trace("DamageControlUi.Process: " + e); }
            }
            if (OpenTips.Count > 0) try { UpdateOpenTips(); } catch (Exception e) { Trace("UpdateOpenTips: " + e.Message); }
            PruneGroups();
            if (_pending && now < _fastUntil) _nextTick = 0f;   // a view still being laid out: try again next frame
        }

        /// <summary>Hover groups: icons no longer shown are dropped, empty groups too (they kept the team objects and the last
        /// mission's icons alive).</summary>
        static void PruneGroups()
        {
            if (Groups.Count == 0) return;
            List<object> empty = null;
            foreach (var kv in Groups)
            {
                kv.Value.RemoveAll(f => f == null || Parent(f) == null);
                if (kv.Value.Count == 0) (empty ?? (empty = new List<object>())).Add(kv.Key);
            }
            if (empty != null) foreach (var key in empty) Groups.Remove(key);
        }

        static void Process(object view, float now)
        {
            var fe = view as FrameworkElement;
            if (fe == null || !fe.IsVisible) return;
            var vm = fe.DataContext;
            if (vm == null || !_vmType.IsInstanceOfType(vm)) return;
            var comp = _vmComp.GetValue(vm, null);
            if (comp == null) return;

            var st = States.GetOrCreateValue(view);
            st.View = fe;
            if (!ReferenceEquals(st.Vm, vm))
            {
                // another unit (the game makes a new view model for each): the old one's rows back to the game's own text first -
                // in text mode the Description we wrote belongs to that ship's objects and would stay with them
                if (st.Vm != null && st.Cols != null) try { Restore(view); } catch (Exception e) { Trace("Restore (unit switch): " + e.Message); }
                st.Vm = vm; st.Ok = false; st.Cols = null; st.Fails = 0; st.Dumped = false; st.NextDiscover = 0f;
            }
            if (!ReferenceEquals(st.Comp, comp))
            {
                // another unit in the same tab: its own rows, teams and priority; the parts we added stay and follow st.Comp
                if (st.Comp != null && st.Cols != null) Restore(view);
                st.Comp = comp; st.Ok = false; st.Cols = null; st.NextDiscover = 0f;
            }

            if (!st.Ok)
            {
                bool fast = now < _fastUntil;
                if (now < st.NextDiscover && !fast) return;
                if (fast) try { fe.UpdateLayout(); } catch { }   // the lists' rows made now, not by the next layout pass
                st.Ok = Discover(fe, vm, comp, st);
                if (!st.Ok)
                {
                    if (fast) { _pending = true; return; }   // retried next frame; not a failure yet
                    st.Fails++;
                    st.NextDiscover = now + 1f;
                    if (st.Fails == 3 && !st.Dumped)
                    {
                        st.Dumped = true;
                        if (!_warnedOnce)
                        {
                            _warnedOnce = true;
                            Warn("Damage Control tab: its layout was not recognised (" + _lastStats + "); the game's view stays." +
                                 (DumpFile != null ? " Layout written to " + DumpFile + "." : ""));
                        }
                        if (!_dumpedOnce) { _dumpedOnce = true; Dump(fe, vm, comp); }
                    }
                    return;
                }
            }
            if (!Valid(st)) { st.Ok = false; return; }

            st.Now = now;
            Update(comp, st);
        }

        // ------------------------------------------------------------ finding the elements

        static bool Same(object a, object b) { return a != null && b != null && (ReferenceEquals(a, b) || a.Equals(b)); }

        static DependencyObject Parent(DependencyObject e)
        {
            try { return VisualTreeHelper.GetParent(e); } catch { return null; }
        }

        static bool IsAncestor(DependencyObject a, DependencyObject b)
        {
            for (var p = b == null ? null : Parent(b); p != null; p = Parent(p))
                if (Same(p, a)) return true;
            return false;
        }

        static bool InsideButton(DependencyObject e, DependencyObject stopAt)
        {
            for (var p = Parent(e); p != null && !Same(p, stopAt); p = Parent(p))
                if (p is ButtonBase) return true;
            return false;
        }

        static List<DependencyObject> Flatten(DependencyObject root, int budget)
        {
            var list = new List<DependencyObject>();
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0 && list.Count < budget)
            {
                var e = stack.Pop();
                list.Add(e);
                int n;
                try { n = VisualTreeHelper.GetChildrenCount(e); } catch { n = 0; }
                for (int k = n - 1; k >= 0; k--)
                {
                    DependencyObject c = null;
                    try { c = VisualTreeHelper.GetChild(e, k); } catch { }
                    if (c != null) stack.Push(c);
                }
            }
            return list;
        }

        static int IndexOfRef(IList list, object item)
        {
            if (list == null || item == null) return -1;
            for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], item)) return i;
            return -1;
        }

        static string Desc(object summary)
        {
            try { return _sumDesc.GetValue(summary, null) as string ?? ""; } catch { return ""; }
        }

        /// <summary>Whether the element or one of its parents (up to the view) has this data context.</summary>
        static bool HasContext(DependencyObject e, object context, DependencyObject stopAt)
        {
            if (context == null) return false;
            int n = 0;
            for (var p = e; p != null && n < 30 && !Same(p, stopAt); p = Parent(p), n++)
            {
                var fe = p as FrameworkElement;
                if (fe == null) continue;
                object dc = null;
                try { dc = fe.DataContext; } catch { }
                if (ReferenceEquals(dc, context)) return true;
            }
            return false;
        }

        static bool Discover(FrameworkElement view, object vm, object comp, ViewState st)
        {
            int count = (int)_compCount.GetValue(comp);
            if (count <= 0 || count > 5) return false;
            var sums = _vmSums.GetValue(vm, null) as IList;
            var floodStates = _compFloodStates.GetValue(comp) as Array;
            var sys = _compSys.GetValue(comp) as Array;
            var assign = _vmAssign.GetValue(vm, null);
            var ret = _vmReturn.GetValue(vm, null);
            if (sums == null || floodStates == null || sys == null) return false;

            var all = Flatten(view, 20000);
            var ics = new DependencyObject[5];
            var plus = new List<ButtonBase>();
            var minus = new List<ButtonBase>();
            var blocks = new List<TextBlock>();
            foreach (var e in all)
            {
                var ic = e as ItemsControl;
                if (ic != null)
                {
                    int idx = IndexOfRef(sums, ic.ItemsSource);
                    if (idx >= 0 && idx < 5 && ics[idx] == null) ics[idx] = ic;
                }
                var b = e as ButtonBase;
                if (b != null)
                {
                    var cmd = b.Command;
                    if (cmd != null && ReferenceEquals(cmd, assign)) plus.Add(b);
                    else if (cmd != null && ReferenceEquals(cmd, ret)) minus.Add(b);
                }
                var tb = e as TextBlock;
                if (tb != null) blocks.Add(tb);
            }

            var cols = new Col[count];
            var roots = new List<DependencyObject>();
            int rows = 0;
            for (int i = 0; i < count; i++)
            {
                var c = new Col();
                cols[i] = c;
                c.FloodSum = floodStates.GetValue(i);
                var sc = sys.GetValue(i);
                c.FireSum = sc == null ? null : _scFireState.GetValue(sc);
                c.FloodDesc = Desc(c.FloodSum);
                c.FireDesc = Desc(c.FireSum);
                c.List = ics[i];
                try { if (c.List != null) c.Column = Grid.GetColumn(c.List); } catch { }

                // the Flooding / Fire rows, if their template is in the visual tree (otherwise: text mode, see RenderRows)
                FindRows(c, view);
                if (c.Flood != null && c.Fire != null) rows++;

                var ic = ics[i];
                if (ic == null) continue;
                roots.Add(ic);

                // the section name: a text block next to the list in the same grid column (the game puts the five names,
                // the five team rows and the five lists side by side in one grid)
                var parent = Parent(ic);
                int col = -1;
                try { col = Grid.GetColumn(ic); } catch { }
                if (parent != null)
                {
                    int nch = 0;
                    try { nch = VisualTreeHelper.GetChildrenCount(parent); } catch { }
                    var texts = new List<TextBlock>();
                    for (int k = 0; k < nch; k++)
                    {
                        var tb = VisualTreeHelper.GetChild(parent, k) as TextBlock;
                        if (tb != null && !string.IsNullOrEmpty(tb.Text)) texts.Add(tb);
                    }
                    foreach (var tb in texts)
                    {
                        int tc = -2;
                        try { tc = Grid.GetColumn(tb); } catch { }
                        if (tc == col) { c.Header = tb; break; }
                    }
                    if (c.Header == null && texts.Count == count) c.Header = texts[i]; // same order as the lists
                }

                // "DC Teams: N": the text block next to the + button of this compartment (CommandParameter = its index)
                foreach (var b in plus)
                {
                    object prm = null;
                    try { prm = b.CommandParameter; } catch { }
                    if (prm == null || prm.ToString() != i.ToString(CultureInfo.InvariantCulture)) continue;
                    var holder = Parent(b);
                    if (holder == null) break;
                    int nch = 0;
                    try { nch = VisualTreeHelper.GetChildrenCount(holder); } catch { }
                    for (int k = 0; k < nch; k++)
                    {
                        var tb = VisualTreeHelper.GetChild(holder, k) as TextBlock;
                        if (tb != null) { c.Teams = tb; break; }
                    }
                    break;
                }

                Keep(c.Teams); Keep(c.Header);
                c.TeamsOrig = FirstText(c.Teams);   // as the game had it when we first saw it (not our icons after a re-discovery)
                c.HeaderOrig = FirstText(c.Header);
            }

            int nIcs = 0; foreach (var x in ics) if (x != null) nIcs++;
            int nTeams = 0, nHeaders = 0; foreach (var x in cols) { if (x.Teams != null) nTeams++; if (x.Header != null) nHeaders++; }
            _lastStats = "elements " + all.Count + ", text blocks " + blocks.Count + ", lists " + nIcs + "/" + count + ", +/- buttons " +
                         plus.Count + "/" + minus.Count + ", rows " + rows + "/" + count + ", team rows " + nTeams + ", headers " + nHeaders;
            Trace("Damage Control tab: " + _lastStats);
            int nLists = 0; foreach (var x in ics) if (x != null) nLists++;
            if (nLists < count) return false;   // the rows can work in text mode, the lists are needed
            st.Cols = cols;

            // the help line of the "Auto Damage Control Status" panel: the longest text outside the sections and the buttons
            st.Help = null; st.HelpOrig = null; st.HelpShown = null; st.HelpTitle = null; st.HelpTitleText = null;
            if (roots.Count > 0)
                foreach (var tb in blocks)
                {
                    string text = tb.Text;
                    if (string.IsNullOrEmpty(text) || text.Length < 20 || InsideButton(tb, view)) continue;
                    bool inCol = false;
                    foreach (var r in roots) if (IsAncestor(r, tb)) { inCol = true; break; }
                    if (inCol) continue;
                    if (st.Help == null || text.Length > st.HelpOrig.Length) { st.Help = tb; st.HelpOrig = text; Keep(tb); }
                }
            var sb = new StringBuilder("Damage Control tab recognised:");
            for (int i = 0; i < count; i++)
                sb.Append(" [" + i + ": header " + (cols[i].Header != null ? "'" + cols[i].HeaderOrig + "'" : "-") +
                          ", teams " + (cols[i].Teams != null ? "'" + cols[i].TeamsOrig + "'" : "-") + "]");
            // the title above the help line (same panel): its first text block
            if (st.Help != null)
            {
                var panel = Parent(st.Help);
                int nch = 0;
                try { nch = panel == null ? 0 : VisualTreeHelper.GetChildrenCount(panel); } catch { }
                for (int k = 0; k < nch; k++)
                {
                    var t = VisualTreeHelper.GetChild(panel, k) as TextBlock;
                    if (t != null && !Same(t, st.Help) && !string.IsNullOrEmpty(t.Text)) { st.HelpTitle = t; st.HelpTitleText = t.Text; break; }
                }
            }
            if (st.Priority == null) AddPriorityCard(view, vm, comp, st, all);
            FindTabHeader(view, vm, st);
            sb.Append(" help: " + (st.Help != null ? "'" + st.HelpOrig + "'" : "-") + ", priority card: " + (st.Priority != null ? "added" : "-"));
            Trace(sb.ToString());
            return true;
        }

        /// <summary>The text blocks of the Flooding / Fire rows inside this compartment's list, when its items are in the visual tree.</summary>
        static void FindRows(Col c, DependencyObject view)
        {
            c.FloodOrig = c.FloodDesc;
            c.FireOrig = c.FireDesc;
            if (!StableRows) return;
            var root = c.List ?? view;
            if (root == null) return;
            foreach (var e in Flatten(root, 3000))
            {
                var tb = e as TextBlock;
                if (tb == null) continue;
                string text = tb.Text;
                // a fresh item shows the game's text; one we drew into shows ours - both are this row's
                if (c.Flood == null && HasContext(tb, c.FloodSum, root) && (text == c.FloodDesc || text.StartsWith(c.FloodDesc + "  ", StringComparison.Ordinal)))
                { c.Flood = tb; c.FloodSig = null; Keep(tb); }
                else if (c.Fire == null && HasContext(tb, c.FireSum, root) && (text == c.FireDesc || text.StartsWith(c.FireDesc + "  ", StringComparison.Ordinal)))
                { c.Fire = tb; c.FireSig = null; Keep(tb); }
            }
        }

        static void SetDesc(object summary, string text)
        {
            try { _sumDesc.SetValue(summary, text, null); } catch { }
        }

        static bool Attached(TextBlock tb) { return tb == null || Parent(tb) != null; }

        static bool Valid(ViewState st)
        {
            if (st.Priority != null && (Parent(st.Priority) == null || st.PriorityHost == null || Parent(st.PriorityHost) == null))
            {
                RemoveExtras(st.X);
                PutBackAuto(st);
                st.Priority = null; st.PriorityHost = null; st.PriorityButtons = null; st.X = null;
                return false;   // found again, the card added again
            }
            if (st.Cols == null) return false;
            foreach (var c in st.Cols)
            {
                if (!Attached(c.Teams) || !Attached(c.Header)) return false;
                if (!Attached(c.Flood)) { c.Flood = null; c.FloodSig = null; }
                if (!Attached(c.Fire)) { c.Fire = null; c.FireSig = null; }
                try
                {
                    if (c.Flood != null && !ReferenceEquals(c.Flood.DataContext, c.FloodSum)) return false;
                    if (c.Fire != null && !ReferenceEquals(c.Fire.DataContext, c.FireSum)) return false;
                }
                catch { return false; }
            }
            return true;
        }

        // ------------------------------------------------------------ rendering

        static readonly Color CWhite = Color.FromRgb(214, 218, 222), COrange = Color.FromRgb(236, 160, 82), CRed = Color.FromRgb(222, 82, 62);
        static readonly Color CUp = Color.FromRgb(232, 72, 60), CDown = Color.FromRgb(98, 204, 112);
        static readonly Color CBlue = Color.FromRgb(86, 160, 232), CFire = Color.FromRgb(240, 120, 50), CFireIn = Color.FromRgb(255, 206, 90);
        static readonly Color CGrey = Color.FromRgb(170, 176, 184), CPatch = Color.FromRgb(236, 228, 200), CWood = Color.FromRgb(200, 170, 120);

        static float F(object v) { try { return Convert.ToSingle(v, CultureInfo.InvariantCulture); } catch { return 0f; } }

        static bool _forceRender;

        static void Update(object comp, ViewState st)
        {
            if (_forceRender) { _forceRender = false; foreach (var cc in st.Cols) cc.TeamsSig = null; if (st.X != null) st.X.TeamsSig = null; }
            double t = 0;
            try { t = Convert.ToDouble(_gameTime.GetValue(null, null), CultureInfo.InvariantCulture); } catch { }
            var port = _compPort.GetValue(comp) as Array;
            var star = _compStar.GetValue(comp) as Array;
            var sys = _compSys.GetValue(comp) as Array;
            var teams = _compTeams.GetValue(comp, null) as int[];

            // section names and their order on screen (left to right), and the teams on the move (shown where they are going)
            var sections = new Sections { Names = new string[st.Cols.Length], Order = new int[st.Cols.Length] };
            for (int i = 0; i < st.Cols.Length; i++) { sections.Names[i] = st.Cols[i].HeaderOrig; sections.Order[i] = i; }
            Array.Sort(sections.Order, (a, b) => st.Cols[a].Column.CompareTo(st.Cols[b].Column));
            _order = sections.Order;
            _colsShown = st.Cols; _colsComp = new WeakReference(comp);
            var moving = DcTeams.Snapshot(comp).FindAll(v => v.State == (int)DcTeams.St.Moving);

            for (int i = 0; i < st.Cols.Length; i++)
            {
                var c = st.Cols[i];
                var fp = port.GetValue(i); var fs = star.GetValue(i); var sc = sys.GetValue(i);
                float ip = F(_fcIntPct.GetValue(fp, null)), isb = F(_fcIntPct.GetValue(fs, null));
                float p = F(_fcFloodPct.GetValue(fp, null)), s = F(_fcFloodPct.GetValue(fs, null));
                float max = F(_scMaxSev.GetValue(sc));
                float f = max > 0f ? 100f * F(_scSeverity.GetValue(sc, null)) / max : 0f;

                // trends (game time; paused game keeps the last ones). After a gap (the tab hidden for a while) the samples start
                // again: a change over minutes is no trend and no hit
                if (c.RealT >= 0f && st.Now - c.RealT > 1f) c.T = -1;
                c.RealT = st.Now;
                if (c.T >= 0 && t > c.T)
                {
                    float dt = (float)(t - c.T);
                    // sudden jumps between two samples (a quarter of a second): hits - more than the trend itself brings in that time
                    // (at high time compression a quarter of a second is many game seconds)
                    if (f - c.F > 3f + 2f * Math.Max(0f, c.RF) * dt) c.FireHitT = t;
                    if (c.IP - ip > 1.5f + 2f * Math.Max(0f, -c.RIP) * dt) c.HullHitP = t;
                    if (c.IS - isb > 1.5f + 2f * Math.Max(0f, -c.RIS) * dt) c.HullHitS = t;
                    c.RP = (p - c.P) / dt; c.RS = (s - c.S) / dt; c.RF = (f - c.F) / dt;
                    c.RIP = (ip - c.IP) / dt; c.RIS = (isb - c.IS) / dt;
                }
                if (c.T < 0 || t != c.T) { c.T = t; c.P = p; c.S = s; c.F = f; c.IP = ip; c.IS = isb; }

                RenderHeader(c, ip, isb, comp, i);
                RenderTeams(c, comp, i, teams != null && i < teams.Length ? teams[i] : 0, sections, moving);
                RenderRows(c, p, s, f, comp, i);
            }
            RenderHelp(comp, st);
            RenderTabHeader(st);
            RenderPriority(comp, st);
            FitPicture(st);
            if (st.X != null)
                try
                {
                    var names = new string[st.Cols.Length];
                    for (int k = 0; k < names.Length; k++) names[k] = st.Cols[k].HeaderOrig;
                    RenderExtras(comp, st.Vm, st.X, names);
                } catch (Exception e) { Trace("RenderExtras: " + e.Message); }
        }

        // ------------------------------------------------------------ "DC Priority" card (Auto / Fire / Flooding)

        static readonly string[] PriorityNames = { "Auto", "Fire", "Flooding" };
        static readonly string[] PriorityTips =
        {
            "Auto: the game's order - Flooding Rate > Fire > Flooding > Structure > Systems.",
            "Fire first: Fire > Flooding Rate > Flooding > Structure > Systems. Auto DC sends free teams to the worst fire first.",
            "Flooding first: Flooding Rate > Flooding > Fire > Structure > Systems. Auto DC sends free teams to leaks, then to water.",
        };

        /// <summary>
        /// Adds a card like "Hull Damage / Flooding" to the left of it, in the free column of the summary cards' grid:
        /// "DC Priority" and three buttons. Found through the Auto DC toggle: its card sits in that grid (5 columns).
        /// </summary>
        /// <summary>
        /// Re-arranges the top of the tab:
        ///  - the game's "Auto Damage Control" card is emptied and hidden; its content (label + toggle, with its bindings) goes into
        ///    our combined card together with "DC Priority" - placed left of the ship's profile picture;
        ///  - the List / Trim / Level card goes right of the picture (under "Hull Damage / Flooding / Fires");
        ///  - both side columns of that row get the same width, so the picture stays in the middle; both cards fill the row's height,
        ///    so the gaps above and below them are the same (6 px, as between the summary cards).
        /// </summary>
        static void AddPriorityCard(FrameworkElement view, object vm, object comp, ViewState st, List<DependencyObject> all)
        {
            try
            {
                Grid grid = null; Border card = null;
                foreach (var e in all)
                {
                    var tg = e as ToggleButton;
                    if (tg == null || !tg.IsVisible) continue;
                    DependencyObject child = tg;
                    for (var p = Parent(tg); p != null && !Same(p, view); child = p, p = Parent(p))
                    {
                        var g = p as Grid;
                        int cols = 0;
                        try { cols = g == null ? 0 : g.ColumnDefinitions.Count; } catch { }
                        // the summary row: 5 columns, the toggle's card a Border in it. The sections' grid has 5 columns too - a
                        // toggle inside a section's rows (realised once there is damage) leads there: not it, the next toggle is tried
                        if (cols == 5) { if (child is Border) { grid = g; card = (Border)child; } break; }
                    }
                    if (grid != null && card != null) break;
                    grid = null; card = null;
                }
                if (grid == null || card == null) { Trace("priority card: summary grid not found"); return; }

                // the row with the ship's profile picture: Grid (Auto | * picture | Auto)
                Grid host = null;
                foreach (var e in all)
                {
                    var img = e as Image;
                    if (img == null) continue;
                    var g = Parent(img) as Grid;
                    int cols = 0; try { cols = g == null ? 0 : g.ColumnDefinitions.Count; } catch { }
                    if (cols == 3) { host = g; break; }
                }
                if (host == null) { Trace("priority card: picture row not found"); return; }

                // the ship's profile: bigger (the row starts 30 px into the summary row; the picture keeps that top)
                try
                {
                    int nh = VisualTreeHelper.GetChildrenCount(host);
                    for (int k = 0; k < nh; k++)
                    {
                        var img = VisualTreeHelper.GetChild(host, k) as Image;
                        if (img == null) continue;
                        st.Picture = img; st.PictureMaxW = img.MaxWidth; st.PictureMaxH = img.MaxHeight; st.PictureMargin = img.Margin;
                        img.MaxWidth = PictureMaxWidth;
                        img.MaxHeight = PictureMaxHeight;
                        img.Margin = new Thickness(img.Margin.Left, PictureLift, img.Margin.Right, img.Margin.Bottom);
                        break;
                    }
                }
                catch (Exception e) { Trace("picture: " + e.Message); }

                TextBlock label = null;
                foreach (var e in Flatten(card, 200)) { var t = e as TextBlock; if (t != null && !InsideButton(t, card) && !string.IsNullOrEmpty(t.Text)) { label = t; break; } }

                var box = SideCard(card);
                box.Margin = new Thickness(0f, SideTop, card.Margin.Right, 0f);   // same right gap as the summary cards above it
                box.HorizontalAlignment = HorizontalAlignment.Left;               // its own width: no space wasted
                Grid.SetColumn(box, 0);
                var stack = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };

                // the game's Auto DC content, moved (its bindings come along: same view, same data context)
                var autoContent = card.Child as UIElement;
                if (autoContent != null)
                {
                    card.Child = null;
                    stack.Children.Add(autoContent);
                    card.Visibility = Visibility.Collapsed;
                    st.AutoCard = card; st.AutoContent = autoContent; st.AutoStack = stack;
                }

                var title = new TextBlock { Text = "DC Priority", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0f, 6f, 0f, 4f) };
                if (label != null) { title.Foreground = label.Foreground; title.FontSize = label.FontSize; }
                try { ToolTipService.SetToolTip(title, "Which work damage control teams do first, on the whole ship."); } catch { }
                stack.Children.Add(title);
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                var buttons = new ToggleButton[3];
                for (int m = 0; m < 3; m++)
                {
                    int mode = m;
                    var b = new ToggleButton { Content = PriorityNames[m], Padding = new Thickness(8f, 2f, 8f, 2f), Margin = new Thickness(m == 0 ? 0f : 4f, 0f, 0f, 0f), MinWidth = 52f };
                    try { ToolTipService.SetToolTip(b, PriorityTips[m]); } catch { }
                    b.Click += (sender, e) =>
                    {
                        DcControl.SetMode(st.Comp, mode);   // the unit shown now, not the one the card was made for
                        SyncPriority(buttons, DcControl.GetMode(st.Comp));
                    };
                    buttons[m] = b;
                    row.Children.Add(b);
                }
                stack.Children.Add(row);
                box.Child = stack;
                host.Children.Add(box);
                st.Priority = box; st.PriorityHost = host; st.PriorityButtons = buttons;

                try { st.X = AddExtras(view, grid, host, card, all); } catch (Exception e) { Trace("extras: " + e.Message); }
                SyncPriority(buttons, DcControl.GetMode(comp));
            }
            catch (Exception e) { Trace("priority card: " + e.Message); }
        }

        // the picture row starts 30 px above the summary cards' bottom edge less the summary grid's 6 px bottom margin:
        // a top margin of 30 leaves 6 px - the gap between the summary cards (and below the row, to the section columns)
        const float SideTop = 30f;
        // the game: MaxWidth 330, MaxHeight 256, Margin 5; profile pictures are wide, so the width decides their size
        const float PictureMaxWidth = 600f, PictureMaxHeight = 300f, PictureLift = 0f;   // 0: its top 30 px into the summary row (the row starts there), was -56 (too high)

        /// <summary>
        /// The ship's picture may not make its row taller than the cards beside it need (a short, tall boat picture did):
        /// its MaxHeight follows the cards' content, and the picture (Stretch Uniform) gets narrower instead.
        /// </summary>
        static void FitPicture(ViewState st)
        {
            var img = st.Picture;
            if (img == null) return;
            try
            {
                float need = 0f;
                foreach (var card in new[] { st.Priority, st.X != null ? st.X.ListCard : null })
                {
                    var child = card == null ? null : card.Child as UIElement;
                    if (child == null) continue;
                    float h = child.DesiredSize.Height + card.Padding.Top + card.Padding.Bottom + card.BorderThickness.Top + card.BorderThickness.Bottom;
                    if (h > need) need = h;
                }
                if (need < 20f) return;   // not measured yet
                // the cards start SideTop below the row's top; the picture PictureLift (+ its bottom margin)
                float max = Math.Min(PictureMaxHeight, need + SideTop - PictureLift - img.Margin.Bottom);
                if (Math.Abs(img.MaxHeight - max) > 0.5f) img.MaxHeight = max;
            }
            catch (Exception e) { Trace("FitPicture: " + e.Message); }
        }

        /// <summary>A card in the picture row: the summary cards' look, the row's full height (equal gaps above and below).</summary>
        static Border SideCard(Border style)
        {
            return new Border
            {
                Background = style.Background, BorderBrush = style.BorderBrush, BorderThickness = style.BorderThickness,
                Padding = new Thickness(6f, 4f, 6f, 4f),
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0f, SideTop, 0f, 0f)
            };
        }

        /// <summary>The game's Auto DC content back into its own card.</summary>
        static void PutBackAuto(ViewState st)
        {
            try
            {
                if (st.AutoContent != null)
                {
                    // out of our stack (known), else out of whatever holds it now (logical or visual parent)
                    if (st.AutoStack != null) try { st.AutoStack.Children.Remove(st.AutoContent); } catch { }
                    DependencyObject holder = null;
                    var fe = st.AutoContent as FrameworkElement;
                    try { if (fe != null) holder = fe.Parent; } catch { }
                    if (holder == null) holder = Parent(st.AutoContent);
                    var panel = holder as Panel;
                    if (panel != null) panel.Children.Remove(st.AutoContent);
                    else { var dec = holder as Decorator; if (dec != null && dec != st.AutoCard) dec.Child = null; }
                }
                if (st.AutoCard != null && st.AutoContent != null && st.AutoCard.Child == null) st.AutoCard.Child = st.AutoContent;
                if (st.AutoCard != null) st.AutoCard.Visibility = Visibility.Visible;
            }
            catch (Exception e) { Trace("PutBackAuto: " + e.Message); }
            st.AutoCard = null; st.AutoContent = null; st.AutoStack = null;
            if (st.Picture != null)
            {
                try { st.Picture.MaxWidth = st.PictureMaxW; st.Picture.MaxHeight = st.PictureMaxH; st.Picture.Margin = st.PictureMargin; } catch { }
                st.Picture = null;
            }
        }

        static void SyncPriority(ToggleButton[] buttons, int mode)
        {
            if (buttons == null) return;
            for (int m = 0; m < buttons.Length; m++)
            {
                bool on = m == mode;
                try { if (buttons[m].IsChecked != on) buttons[m].IsChecked = on; } catch { }
            }
        }

        static void RenderPriority(object comp, ViewState st)
        {
            if (st.PriorityButtons != null) SyncPriority(st.PriorityButtons, DcControl.GetMode(comp));
        }

        // ------------------------------------------------------------ the help line and its legend

        static readonly string[] OrderText =
        {
            null,   // Auto: the game's own text
            "DC Priority: Fire - teams take threats in this order: Fire > Flooding Rate > Flooding > Structure > Systems",
            "DC Priority: Flooding - teams take threats in this order: Flooding Rate > Flooding > Fire > Structure > Systems",
        };

        /// <summary>
        /// The "Auto Damage Control Status" panel on one line: its title folded into the help line (the title's own line is hidden),
        /// and the order of the ship's DC Priority when it is not Auto.
        /// </summary>
        static void RenderHelp(object comp, ViewState st)
        {
            var tb = st.Help;
            if (tb == null) return;
            int mode = DcControl.GetMode(comp);
            string body = (mode > 0 && mode < OrderText.Length ? OrderText[mode] : null) ?? st.HelpOrig ?? "";
            string sig = mode + "|" + body;
            if (st.HelpShown != null && tb.Text == st.HelpShown && sig == st.HelpSig) return;   // ours, unchanged
            if (st.HelpShown != null && tb.Text != st.HelpShown) st.HelpOrig = tb.Text;          // the game rewrote it (language)
            if (mode == 0) body = st.HelpOrig ?? "";
            var il = tb.Inlines;
            il.Clear();
            if (st.HelpTitle != null)
            {
                if (!string.IsNullOrEmpty(st.HelpTitle.Text)) st.HelpTitleText = st.HelpTitle.Text;
                var title = new Run((st.HelpTitleText ?? "") + ":  ");
                try { title.Foreground = st.HelpTitle.Foreground; } catch { }
                il.Add(title);
                try { st.HelpTitle.Visibility = Visibility.Collapsed; } catch { }
            }
            il.Add(new Run(body));
            try { ToolTipService.SetToolTip(tb, null); } catch { }
            st.HelpSig = sig;
            st.HelpShown = tb.Text;
        }

        // ------------------------------------------------------------ (i) with the legend on the "Damage Control" tab button

        /// <summary>
        /// The tab buttons of Unit Status are RadioButtons bound to UnitStatusTabViewModel (Header, Content); the one whose Content
        /// is this view's view model is "Damage Control". Its text block (made by the button's ContentPresenter) gets the (i).
        /// </summary>
        static void FindTabHeader(FrameworkElement view, object vm, ViewState st)
        {
            var before = st.TabHeader;
            st.TabHeader = null;
            try
            {
                DependencyObject root = view;
                for (var p = Parent(view); p != null; p = Parent(p)) { root = p; if (p.GetType().Name == "UnitStatusView") break; }
                foreach (var e in Flatten(root, 8000))
                {
                    var rb = e as RadioButton;
                    if (rb == null) continue;
                    object dc = null; try { dc = rb.DataContext; } catch { }
                    if (dc == null || dc.GetType().Name != "UnitStatusTabViewModel") continue;
                    var pc = dc.GetType().GetProperty("Content");
                    if (pc == null || !ReferenceEquals(pc.GetValue(dc, null), vm)) continue;
                    foreach (var x in Flatten(rb, 50)) { var t = x as TextBlock; if (t != null) { st.TabHeader = t; break; } }
                    break;
                }
            }
            catch (Exception e) { Trace("tab header: " + e.Message); }
            if (st.TabHeader == null || !Same(st.TabHeader, before)) { st.TabHeaderShown = null; st.TabHeaderText = null; }  // a new button: draw anew
        }

        static void RenderTabHeader(ViewState st)
        {
            if (st.TabHeader != null) DecorateHeader(st.TabHeader, (UIElement)st.View);
        }

        // ------------------------------------------------------------ the tab button, also before the tab is first opened

        sealed class HeaderState { public string Shown, Text; public UIElement Anchor; public bool Full; }
        static readonly ConditionalWeakTable<TextBlock, HeaderState> Headers = new ConditionalWeakTable<TextBlock, HeaderState>();
        static readonly List<WeakReference> Decorated = new List<WeakReference>();     // TextBlocks with our icons (to restore)
        static readonly List<WeakReference> StatusViews = new List<WeakReference>();   // SeapowerUI.UnitStatusView
        sealed class StatusState { public TextBlock Header; public float NextScan; }
        static readonly ConditionalWeakTable<object, StatusState> StatusStates = new ConditionalWeakTable<object, StatusState>();

        // the "Damage Control" tab view models (UnitStatusTabViewModel, Kind = DamageControl), caught when the game makes them;
        // their content (the tab's view model) is made only when the tab is first opened, so they are told by Kind
        static readonly List<WeakReference> DcTabs = new List<WeakReference>();
        static PropertyInfo _tabKind;

        public static void RegisterTab(object tabVm)
        {
            if (tabVm == null) return;
            try
            {
                if (_tabKind == null) _tabKind = tabVm.GetType().GetProperty("Kind");
                if (_tabKind == null || Convert.ToString(_tabKind.GetValue(tabVm, null)) != "DamageControl") return;
                lock (DcTabs) { DcTabs.RemoveAll(w => !w.IsAlive); DcTabs.Add(new WeakReference(tabVm)); }
                _scanNow = true;
            }
            catch (Exception e) { Trace("RegisterTab: " + e.Message); }
        }

        static bool _scanNow;

        static bool IsDcTab(object dc)
        {
            if (dc.GetType().Name != "UnitStatusTabViewModel") return false;
            lock (DcTabs) foreach (var w in DcTabs) if (ReferenceEquals(w.Target, dc)) return true;
            try
            {
                if (_tabKind == null) _tabKind = dc.GetType().GetProperty("Kind");
                return _tabKind != null && Convert.ToString(_tabKind.GetValue(dc, null)) == "DamageControl";
            }
            catch { return false; }
        }

        /// <summary>A Unit Status window was made: its "Damage Control" tab button gets (i) and the gear at once, not only once the tab is opened.</summary>
        public static void RegisterStatusView(object view)
        {
            if (view == null) return;
            lock (StatusViews) { StatusViews.RemoveAll(w => !w.IsAlive); StatusViews.Add(new WeakReference(view)); }
        }

        static void DecorateStatusViews(float now)
        {
            List<object> views = new List<object>();
            lock (StatusViews) { foreach (var w in StatusViews) { var v = w.Target; if (v != null) views.Add(v); } }
            foreach (var v in views)
            {
                var fe = v as FrameworkElement;
                if (fe == null || !fe.IsVisible) continue;
                var ss = StatusStates.GetOrCreateValue(v);
                if (ss.Header != null && Parent(ss.Header) != null) { DecorateHeader(ss.Header, fe); continue; }
                if (now < ss.NextScan && !_scanNow) continue;
                ss.NextScan = now + 0.5f;
                foreach (var e in Flatten(fe, 8000))
                {
                    var rb = e as RadioButton;
                    if (rb == null) continue;
                    object dc = null; try { dc = rb.DataContext; } catch { }
                    if (dc == null || !IsDcTab(dc)) continue;
                    foreach (var x in Flatten(rb, 50)) { var t = x as TextBlock; if (t != null) { ss.Header = t; break; } }
                    break;
                }
                if (ss.Header != null) DecorateHeader(ss.Header, fe);
            }
            _scanNow = false;
        }

        /// <summary>"Damage Control" + (i) with the legend + the gear (the settings window); kept when the game rewrites the text.</summary>
        static void DecorateHeader(TextBlock tb, UIElement anchor)
        {
            if (tb == null) return;
            var hs = Headers.GetOrCreateValue(tb);
            if (anchor != null) hs.Anchor = anchor;
            bool full = _full;
            if (hs.Shown != null && tb.Text == hs.Shown && hs.Full == full) return;
            string text = tb.Text ?? "";
            if (hs.Text != null && text.StartsWith(hs.Text + "  ", StringComparison.Ordinal))
            {
                if (hs.Full == full) { hs.Shown = text; return; }   // still ours
                text = hs.Text;                                       // ours, with the other set of icons: done again
            }
            var il = tb.Inlines;
            il.Clear();
            il.Add(new Run(text + "  "));
            // (i) and the gear side by side in ONE inline container (two containers overlapped / sat at different heights):
            // a horizontal panel, both icons 14 px and centred in it, 6 px apart; the panel 3 px down (an inline element stands on
            // the baseline: down to the middle of the text)
            var info = HeaderIcon(InfoIcon());
            try
            {
                ToolTipService.SetToolTip(info, Legend());
                ToolTipService.SetPlacement(info, PlacementMode.Top);       // above the tab (flips below when there is no room)
                ToolTipService.SetShowDuration(info, int.MaxValue);         // stays while the mouse is on it
            }
            catch (Exception e) { Trace("Legend: " + e.Message); }
            var gear = HeaderIcon(GearIcon());
            gear.Margin = new Thickness(6f, 0f, 0f, 0f);
            try { gear.Cursor = Cursors.Hand; ToolTipService.SetToolTip(gear, "Alternative Damage Model settings"); } catch { }
            gear.PreviewMouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                Trace("settings gear clicked");
                SettingsWindow.Open(gear, hs.Anchor);   // the gear clicked: its own screen (after a mission restart the old anchor is gone)
            };
            // the negative top margin: in layout the icons are lower than the text's ascent, so they never make the line taller (the
            // line's height did change once the header was measured again - the first hover -, the text jumped up); drawn where they were
            var pair = new StackPanel { Orientation = Orientation.Horizontal, RenderTransform = new TranslateTransform(0f, 3f), Margin = new Thickness(0f, -6f, 0f, 0f) };
            if (full) pair.Children.Add(info); else gear.Margin = new Thickness(0f);   // detailed view off: the gear only
            pair.Children.Add(gear);
            il.Add(new InlineUIContainer(pair));
            hs.Shown = tb.Text;
            hs.Text = text;
            hs.Full = full;
            lock (Decorated) { Decorated.RemoveAll(w => !w.IsAlive); Decorated.Add(new WeakReference(tb)); }
        }

        /// <summary>The mod or its view switched off: every tab button back to its own text.</summary>
        static void RestoreHeaders()
        {
            lock (Decorated)
            {
                foreach (var w in Decorated)
                {
                    var tb = w.Target as TextBlock;
                    HeaderState hs;
                    if (tb == null || !Headers.TryGetValue(tb, out hs) || hs.Text == null) continue;
                    RestoreBlock(tb, hs.Text);
                    hs.Shown = null; hs.Text = null;
                }
                Decorated.Clear();
            }
        }

        /// <summary>A header icon: 14 x 14, centred, a transparent (hit-testable) frame, a little dimmer until the mouse is on it.</summary>
        static Border HeaderIcon(FrameworkElement icon)
        {
            var b = new Border { Child = icon, Width = 14f, Height = 14f, VerticalAlignment = VerticalAlignment.Center,
                                 Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)), Opacity = 0.9f };
            b.MouseEnter += (s, e) => b.Opacity = 1f;
            b.MouseLeave += (s, e) => b.Opacity = 0.9f;
            return b;
        }

        /// <summary>The gear (grey, as the first version): the mod's settings.</summary>
        static FrameworkElement GearIcon()
        {
            return new Path
            {
                Data = new StreamGeometry("F0 M10.3,2 L13.7,2 L14.2,4.6 C14.8,4.8 15.4,5.1 15.9,5.5 L18.4,4.6 L20.1,7.5 L18.1,9.2 C18.2,9.8 18.2,10.4 18.1,11 " +
                                          "L20.1,12.7 L18.4,15.6 L15.9,14.7 C15.4,15.1 14.8,15.4 14.2,15.6 L13.7,18.2 L10.3,18.2 L9.8,15.6 C9.2,15.4 8.6,15.1 8.1,14.7 " +
                                          "L5.6,15.6 L3.9,12.7 L5.9,11 C5.8,10.4 5.8,9.8 5.9,9.2 L3.9,7.5 L5.6,4.6 L8.1,5.5 C8.6,5.1 9.2,4.8 9.8,4.6 Z " +
                                          "M15.4,10.1 A3.4,3.4 0 1 1 8.6,10.1 A3.4,3.4 0 1 1 15.4,10.1 Z"),
                Fill = new SolidColorBrush(CGrey), Stretch = Stretch.Uniform, Width = 14f, Height = 14f
            };
        }

        static FrameworkElement InfoIcon()
        {
            var cv = new Canvas { Width = 14f, Height = 14f };
            cv.Children.Add(Shape("F0 M7,0 A7,7 0 1 1 7,14 A7,7 0 1 1 7,0 Z M6,6 L8,6 L8,11.5 L6,11.5 Z M7,2.5 A1.2,1.2 0 1 1 7,4.9 A1.2,1.2 0 1 1 7,2.5 Z", CBlue));
            return cv;
        }

        /// <summary>White bold text centred on (cx, cy) of the legend picture.</summary>
        static TextBlock Centred(string text, float size, float cx, float cy)
        {
            const float w = 84f;
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeight.Bold, Width = w, TextAlignment = TextAlignment.Center,
                                    Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255)) };
            Canvas.SetLeft(t, cx - w / 2f);
            Canvas.SetTop(t, cy - size * 0.68f);
            return t;
        }

        /// <summary>
        /// The tooltip of the help line: a top view of a ship in a circle split along its centreline - port half red, starboard half
        /// green (the colours of the navigation lights), the labels centred in each half - and what the numbers, arrows and icons mean.
        /// </summary>
        static FrameworkElement Legend()
        {
            var pic = new Canvas { Width = 200f, Height = 200f, Margin = new Thickness(0f, 8f, 16f, 0f) };
            var ch = pic.Children;
            var cInk = Color.FromRgb(24, 26, 30);
            ch.Add(Shape(PicPort, Color.FromRgb(200, 62, 54)));     // left half: port
            ch.Add(Shape(PicStar, Color.FromRgb(46, 160, 86)));     // right half: starboard
            ch.Add(new Path { Data = new StreamGeometry("M100,6 A94,94 0 1 1 99.99,6 Z"), Stroke = new SolidColorBrush(cInk), StrokeThickness = 2f });
            ch.Add(new Path { Data = new StreamGeometry("M100,6 L100,194"), Stroke = new SolidColorBrush(cInk), StrokeThickness = 1f });
            ch.Add(new Path { Data = new StreamGeometry(PicHull), Fill = new SolidColorBrush(Color.FromRgb(222, 225, 230)), Stroke = new SolidColorBrush(cInk), StrokeThickness = 1.4f });
            ch.Add(Shape(PicDeck, Color.FromRgb(96, 104, 116)));
            ch.Add(Centred("Port", 15f, 48f, 100f));        // centred between the circle and the hull
            ch.Add(Centred("Starboard", 12.5f, 153f, 100f));

            var text = new StackPanel { Orientation = Orientation.Vertical, Width = 300f };
            Action<string, Color, float> line = (t, col, size) =>
                text.Children.Add(new TextBlock { Text = t, Foreground = new SolidColorBrush(col), FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0f, 0f, 0f, 5f) });
            line("A section = port + starboard compartment:", CWhite, 15f);
            line("Aft  41% / 47%  - hull, port / starboard", CWhite, 14f);
            line("Flooding  23% / 5%  - water inside", CWhite, 14f);
            line("Fire  64%  - % of its maximum", CWhite, 14f);
            line("Red arrow: worse, green: better, two: fast.", CGrey, 13f);

            line("One icon = one damage control team:", CWhite, 14f);
            var icons = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0f, 2f, 0f, 0f) };
            foreach (var task in new[] { TaskLeak, TaskFire, TaskPump, TaskHull, TaskSystems, TaskIdle, IconTransit, IconInjured, IconDead })
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0f, 0f, 0f, 3f) };
                row.Children.Add(Icon(task));
                row.Children.Add(new TextBlock { Text = "  " + TaskName(task), Foreground = new SolidColorBrush(CGrey), FontSize = 13f, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 270f });
                icons.Children.Add(row);
            }
            text.Children.Add(icons);
            line("Auto DC off: click a team to give it a task (underlined = has one).", CGrey, 13f);
            line("DC Priority: what all teams do first.", CGrey, 13f);
            line("Point at an arrow: why it changes.", CGrey, 13f);
            line("Point at LIST / TRIM / LEVEL: water per compartment.", CGrey, 13f);
            line("Gear next to (i): settings and profiles.", CGrey, 13f);

            var body = new StackPanel { Orientation = Orientation.Horizontal };
            body.Children.Add(pic);
            body.Children.Add(text);
            return new Border
            {
                Child = body, Background = new SolidColorBrush(Color.FromRgb(30, 33, 38)), BorderBrush = new SolidColorBrush(Color.FromRgb(70, 76, 86)),
                BorderThickness = new Thickness(1f), Padding = new Thickness(12f), CornerRadius = new CornerRadius(4f)
            };
        }

        /// <summary>
        /// Flooding / Fire rows. When their text blocks are in the visual tree: coloured arrows in the text block (RenderFlood,
        /// RenderFire). Otherwise (the game's list items do not show their template to VisualTreeHelper) the row's own
        /// DamageSummary.Description is rewritten - the game shows it in its stage colour - with + / - for the trend.
        /// </summary>
        static void RenderRows(Col c, float p, float s, float f, object comp, int i)
        {
            if (StableRows)
            {
                // the game recreates a row's item only when its stage changes: look for it again until found
                if ((c.Flood == null || c.Fire == null) && _tickNow >= c.NextFind)
                {
                    FindRows(c, c.List);
                    if (c.Flood != null && c.Fire != null) c.FindMisses = 0;
                    else if (++c.FindMisses > 8) c.NextFind = _tickNow + 1f;   // not in the tree (not realised / scrolled away): no full search every tick
                }
                if (c.Flood != null) RenderFlood(c, p, s, comp, i);
                if (c.Fire != null) RenderFire(c, f, comp, i);
                return;
            }

            // text mode: the row's own Description (any change of it recreates the item, so it is never mixed with drawing)
            string t = c.FloodDesc;
            if (p > 0.0005f || s > 0.0005f)
                t += "  " + Pct(p) + Marks(Arrows(c.RP, FloodOne, FloodTwo, FloodTwo)) + " / " + Pct(s) + Marks(Arrows(c.RS, FloodOne, FloodTwo, FloodTwo));
            if (t != (c.FloodData ?? c.FloodDesc)) { SetDesc(c.FloodSum, t); c.FloodData = t == c.FloodDesc ? null : t; }

            t = c.FireDesc;
            if (f > 0.0005f) t += "  " + Pct(f) + Marks(Arrows(c.RF, FireOne, FireTwoUp, FireTwoDown));
            if (t != (c.FireData ?? c.FireDesc)) { SetDesc(c.FireSum, t); c.FireData = t == c.FireDesc ? null : t; }
        }

        static string Marks(int a)
        {
            switch (a) { case 2: return " ++"; case 1: return " +"; case -1: return " -"; case -2: return " --"; default: return ""; }
        }

        static int Arrows(float rate, float one, float twoUp, float twoDown)
        {
            if (rate >= twoUp) return 2;
            if (rate >= one) return 1;
            if (rate <= -twoDown) return -2;
            if (rate <= -one) return -1;
            return 0;
        }

        static string Pct(float v)
        {
            if (v <= 0.0005f) return "0%";
            if (v < 1f) return "<1%";
            return ((int)Math.Floor(v)).ToString(CultureInfo.InvariantCulture) + "%";
        }

        static Color Stage(float integrity) { return integrity >= 75f ? CWhite : (integrity >= 25f ? COrange : CRed); }

        /// <summary>Rebuilds a text block when what it shows changed, or when the game rewrote it (a binding update).</summary>
        static bool NeedsRender(TextBlock tb, string sig, string lastSig, string lastShown)
        {
            return tb != null && (sig != lastSig || tb.Text != lastShown);
        }

        static void RenderHeader(Col c, float ip, float isb, object comp, int i)
        {
            var tb = c.Header;
            if (tb == null) return;
            if (c.HeaderSig != null && tb.Text != c.HeaderShown) c.HeaderOrig = tb.Text; // the game changed the name (language)
            int ap = Math.Abs(c.RIP) < 1e-6f ? 0 : (c.RIP > 0 ? 1 : -1), asb = Math.Abs(c.RIS) < 1e-6f ? 0 : (c.RIS > 0 ? 1 : -1);
            bool whole = ip >= 99.95f && isb >= 99.95f;
            string sig = whole ? "whole" : Pct(ip) + ap + "/" + Pct(isb) + asb + Stage(ip) + Stage(isb);
            if (!NeedsRender(tb, sig, c.HeaderSig, c.HeaderShown)) return;
            var il = tb.Inlines;
            il.Clear();
            il.Add(new Run(c.HeaderOrig ?? ""));
            if (!whole)
            {
                il.Add(new Run("   "));
                il.Add(Colored(Pct(ip), Stage(ip)));
                if (ap != 0) il.Add(Arrow(ap > 0, 1, ap > 0 ? CDown : CUp, () => HullCause(comp, c, i, true))); // integrity: up is good (green), down is bad (red)
                il.Add(Colored(" / ", Stage(isb)));
                il.Add(Colored(Pct(isb), Stage(isb)));
                if (asb != 0) il.Add(Arrow(asb > 0, 1, asb > 0 ? CDown : CUp, () => HullCause(comp, c, i, false)));
            }
            c.HeaderSig = sig;
            c.HeaderShown = tb.Text;
        }

        static readonly Color CHover = Color.FromArgb(56, 255, 255, 255), CManual = Color.FromRgb(255, 200, 60);

        /// <summary>
        /// One icon per team, showing what it does. Auto DC off: an icon lights up under the mouse and a click opens a menu to give
        /// that team a task by hand (kept while the team stays in the compartment); a team with a task given by hand is underlined.
        /// </summary>
        sealed class Sections { public string[] Names; public int[] Order; }

        static string SectionName(Sections sec, int i)
        {
            if (i < 0) return "reserve";
            return sec != null && i < sec.Names.Length && !string.IsNullOrEmpty(sec.Names[i]) ? sec.Names[i] : "section " + (i + 1);
        }

        /// <summary>
        /// One icon per team in the section, showing what it does, then (grey) the teams on their way here, with their time.
        /// Auto DC off: an icon lights up under the mouse; a click opens a menu - for a working team its task and "Move to section",
        /// for a team on the way "Cancel Transfer". A team with a task given by hand is underlined.
        /// </summary>
        static void RenderTeams(Col c, object comp, int i, int n, Sections sec, List<DcTeams.View> moving)
        {
            var tb = c.Teams;
            if (tb == null) return;
            int[] forced, actual; object[] refs;
            DcControl.GetTeams(comp, i, n, out forced, out actual, out refs);
            bool manual = Manual(comp);
            var coming = moving.FindAll(v => v.Section == i);
            var sb = new StringBuilder();
            sb.Append(n).Append(manual ? 'm' : 'a');
            for (int k = 0; k < n; k++) sb.Append(':').Append(actual[k]).Append('/').Append(forced[k]);
            foreach (var v in coming) sb.Append('>').Append(v.Ref.GetHashCode());
            string sig = sb.ToString();
            if (c.TeamsSig != null && MenusOpen()) return;   // an open menu would close with its icon
            if (!NeedsRender(tb, sig, c.TeamsSig, c.TeamsShown)) return;
            var il = tb.Inlines;
            il.Clear();
            if (n <= 0 && coming.Count == 0) il.Add(new Run(" ")); // keeps the row's height
            int shown = 0;
            for (int k = 0; k < n && k < 8; k++, shown++)
            {
                int did = actual[k] == TaskNone ? TaskIdle : actual[k];
                var frame = Frame(Icon(did), shown, forced[k] != TaskNone);
                int kk = k;
                if (manual)
                {
                    int fk = forced[k]; object rk = refs[k];
                    if (rk != null) Clickable(frame, () => TeamMenu(comp, i, rk, fk, sec), null);
                }
                LiveTip(frame, () => SlotTip(comp, i, kk, sec));
                il.Add(new InlineUIContainer(frame));
            }
            foreach (var v in coming)
            {
                var frame = Frame(Icon(IconTransit), shown++, false);
                var r = v.Ref;
                Clickable(frame, manual ? (Func<ContextMenu>)(() => CancelMenu(comp, r)) : null, r);
                LiveTip(frame, () => RefTip(comp, r, sec.Names));
                il.Add(new InlineUIContainer(frame));
            }
            try { ToolTipService.SetToolTip(tb, n <= 0 && coming.Count == 0 ? (c.TeamsOrig ?? "") : null); } catch { }
            c.TeamsSig = sig;
            c.TeamsShown = tb.Text;
        }

        // ------------------------------------------------------------ team tooltips: "DC3: fighting the fire", built when they open

        /// <summary>A tooltip made when it opens (times and tasks change while the icon stays).</summary>
        static void LiveTip(FrameworkElement el, Func<string> text)
        {
            try
            {
                // the tooltip is a text block of its own: written when it opens and 4 times a second while it is open
                var tb = new TextBlock { Text = " " };
                ToolTipService.SetToolTip(el, tb);
                ToolTipService.SetShowDuration(el, int.MaxValue);
                var entry = new OpenTip { Tb = tb, Text = text, Owner = el };
                el.ToolTipOpening += (s, e) =>
                {
                    try { tb.Text = text(); } catch { }
                    if (!OpenTips.Contains(entry)) OpenTips.Add(entry);
                };
                el.ToolTipClosing += (s, e) => OpenTips.Remove(entry);
            }
            catch (Exception e) { Trace("LiveTip: " + e.Message); }
        }

        sealed class OpenTip { public TextBlock Tb; public Func<string> Text; public FrameworkElement Owner; }
        static readonly List<OpenTip> OpenTips = new List<OpenTip>();

        /// <summary>The open tooltips follow what they describe (times, rates, teams).</summary>
        static void UpdateOpenTips()
        {
            for (int k = OpenTips.Count - 1; k >= 0; k--)
            {
                var o = OpenTips[k];
                try
                {
                    // its icon no longer shown (rebuilt, the tab hidden or switched off): its tooltip is closed even when no
                    // ToolTipClosing came - dropped, so it is not computed 4 times a second for ever
                    if (o.Owner == null || !o.Owner.IsVisible || Parent(o.Owner) == null) { OpenTips.RemoveAt(k); continue; }
                    string t = o.Text();
                    if (o.Tb.Text != t) o.Tb.Text = t;
                }
                catch { OpenTips.RemoveAt(k); }
            }
        }

        static string Dc(int number) { return number > 0 ? "DC" + number.ToString(CultureInfo.InvariantCulture) : "DC team"; }

        static string SectionName(string[] names, int i)
        {
            if (i < 0) return "reserve";
            return names != null && i < names.Length && !string.IsNullOrEmpty(names[i]) ? names[i] : "section " + (i + 1);
        }

        static string KnockNote(int knocks)
        {
            if (knocks <= 0) return null;
            string s = knocks == 1 ? "Knocked out once" : "Knocked out " + knocks + " times";
            if (DcTeams.KnocksToDie > 0 && knocks + 1 >= DcTeams.KnocksToDie) s += " (next: lost)";
            return s;
        }

        /// <summary>A team working in section i, slot k (its k-th icon).</summary>
        static string SlotTip(object comp, int i, int k, Sections sec)
        {
            try
            {
                var counts = _compTeams.GetValue(comp, null) as int[];
                int n = counts != null && i < counts.Length ? counts[i] : 0;
                int[] forced, actual;
                DcControl.GetTeams(comp, i, n, out forced, out actual);
                if (k >= n || k >= actual.Length) return "DC team";
                int did = actual[k] == TaskNone ? TaskIdle : actual[k];
                var here = DcTeams.InSection(comp, i);
                int number = k < here.Count ? here[k].Number : 0;
                var sb = new StringBuilder(Dc(number) + ": " + TaskName(did));
                if (forced[k] != TaskNone) sb.Append(forced[k] == did ? "\nTask by hand" : "\nBy hand: " + TaskName(forced[k]) + " (nothing to do)");
                if (k < here.Count)
                {
                    if (here[k].Stay < 1e6f && DcTeams.TransfersOn) sb.Append("\nHere " + Clock(here[k].Stay));
                    string kn = KnockNote(here[k].Knocks);
                    if (kn != null) sb.Append("\n" + kn);
                }
                if (Manual(comp)) sb.Append(ModelOn() ? "\nClick: task / move" : "\nClick: task");
                return sb.ToString();
            }
            catch (Exception e) { Trace("SlotTip: " + e.Message); return "DC team"; }
        }

        /// <summary>A team that is not working in a section: on the move, recovering, dead, waiting in the reserve.</summary>
        static string RefTip(object comp, object teamRef, string[] names)
        {
            try
            {
                foreach (var v in DcTeams.Snapshot(comp))
                {
                    if (!ReferenceEquals(v.Ref, teamRef)) continue;
                    string dc = Dc(v.Number) + ": ";
                    bool manual = Manual(comp);
                    string kn = KnockNote(v.Knocks);
                    switch ((DcTeams.St)v.State)
                    {
                        case DcTeams.St.Moving:
                        {
                            bool mustering = !v.Back && v.Total - v.Left < DcTeams.Muster;
                            var sb = new StringBuilder(dc + SectionName(names, v.From) + " → " + (v.Section >= 0 ? SectionName(names, v.Section) : "reserve"));
                            sb.Append("\n" + Clock(v.Left) + " left" + (mustering ? ", getting ready" : ""));
                            if (kn != null) sb.Append("\n" + kn);
                            if (manual) sb.Append("\nClick: cancel");
                            return sb.ToString();
                        }
                        case DcTeams.St.Injured:
                            return dc + "recovering\nBack in " + Clock(v.Left) + (kn != null ? "\n" + kn : "");
                        case DcTeams.St.Dead:
                            return dc + "lost" + (v.From >= 0 ? " (" + SectionName(names, v.From) + ")" : "");
                        case DcTeams.St.Pool:
                            return dc + "free, in reserve" + (kn != null ? "\n" + kn : "");
                        default:
                            return dc + SectionName(names, v.Section);
                    }
                }
            }
            catch (Exception e) { Trace("RefTip: " + e.Message); }
            return "DC team";
        }

        static Border Frame(UIElement icon, int index, bool underline)
        {
            return new Border
            {
                Child = icon, Padding = new Thickness(2f, 1f, 2f, 1f), CornerRadius = new CornerRadius(2f),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),   // transparent but hit-testable
                BorderBrush = new SolidColorBrush(CManual),
                BorderThickness = new Thickness(0f, 0f, 0f, underline ? 2f : 0f),
                Margin = new Thickness(index == 0 ? 0f : 3f, 0f, 0f, 0f)
            };
        }

        /// <summary>
        /// Our menus open now: their icons are not rebuilt meanwhile (the menu would close). The menus themselves are kept, not a
        /// count: a menu whose Closed never came (its view switched off or replaced while it was open) no longer blocks the icons.
        /// </summary>
        static readonly List<ContextMenu> OpenMenus = new List<ContextMenu>();
        static bool MenusOpen()
        {
            OpenMenus.RemoveAll(m =>
            {
                try { return m == null || !m.IsOpen; }
                catch { return true; }
            });
            return OpenMenus.Count > 0;
        }

        // a team on the move is shown twice (where it goes, "In Transfer"): its icons light up together
        static readonly Dictionary<object, List<Border>> Groups = new Dictionary<object, List<Border>>();

        static void SetHover(Border frame, object group, bool on)
        {
            var brush = new SolidColorBrush(on ? CHover : Color.FromArgb(0, 0, 0, 0));
            List<Border> list;
            if (group == null || !Groups.TryGetValue(group, out list)) { try { frame.Background = brush; } catch { } return; }
            list.RemoveAll(f => Parent(f) == null && !ReferenceEquals(f, frame));
            foreach (var f in list) try { f.Background = brush; } catch { }
        }

        /// <summary>
        /// Lights up under the mouse (with the other icons of the same team on the move, 'group'); a left click opens the menu
        /// made by 'menu' (none: no click).
        /// </summary>
        static void Clickable(Border frame, Func<ContextMenu> menu, object group)
        {
            if (group != null)
            {
                List<Border> list;
                if (!Groups.TryGetValue(group, out list)) Groups[group] = list = new List<Border>();
                list.RemoveAll(f => Parent(f) == null);
                list.Add(frame);

            }
            frame.MouseEnter += (s, e) => SetHover(frame, group, true);
            frame.MouseLeave += (s, e) => SetHover(frame, group, false);
            if (menu == null) return;
            try { frame.Cursor = Cursors.Hand; } catch { }
            frame.MouseLeftButtonUp += (s, e) =>
            {
                try
                {
                    var m = menu();
                    m.Opened += (o, a) => { if (!OpenMenus.Contains(m)) OpenMenus.Add(m); };
                    m.Closed += (o, a) => { OpenMenus.Remove(m); _forceRender = true; };
                    m.PlacementTarget = frame;
                    m.Placement = PlacementMode.Bottom;
                    frame.ContextMenu = m;
                    m.IsOpen = true;
                    e.Handled = true;
                }
                catch (Exception ex) { Trace("team menu: " + ex.Message); }
            };
        }

        /// <summary>The menu of one team. It acts on the team itself (not on its place in the row): others may come and go while it is open.</summary>
        static ContextMenu TeamMenu(object comp, int i, object team, int forced, Sections sec)
        {
            var menu = new ContextMenu();
            foreach (var t in new[] { TaskLeak, TaskFire, TaskPump, TaskHull, TaskSystems })
                menu.Items.Add(MenuEntry(Cap(TaskName(t)), Icon(t), t == forced, comp, team, t));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("By priority (as the other teams)", Icon(IconByPriority), forced == TaskNone, comp, team, TaskNone));
            if (!ModelOn()) return menu;   // moves between sections: only with Alternative Damage Control
            menu.Items.Add(new Separator());
            var move = new MenuItem { Header = "Move to section" };
            foreach (int j in sec.Order)
            {
                int to = j;
                var item = new MenuItem { Header = SectionName(sec, j) + (j == i ? "  (here)" : ""), IsEnabled = j != i };
                if (j != i)
                    item.Click += (s, e) =>
                    {
                        int from, k = DcTeams.SlotOf(comp, team, out from);
                        if (k < 0 || from == to || !DcTeams.MoveToSection(comp, from, to, false, k)) Trace("move to section failed");
                        _forceRender = true;
                    };
                move.Items.Add(item);
            }
            move.Items.Add(new Separator());
            var home = new MenuItem { Header = "To Reserve" };
            home.Click += (s, e) =>
            {
                int from, k = DcTeams.SlotOf(comp, team, out from);
                if (k < 0 || !DcTeams.ToReserve(comp, from, k)) Trace("to reserve failed");
                _forceRender = true;
            };
            move.Items.Add(home);
            menu.Items.Add(move);
            return menu;
        }

        static ContextMenu CancelMenu(object comp, object teamRef)
        {
            var menu = new ContextMenu();
            var item = new MenuItem { Header = "Cancel Transfer" };
            item.Click += (s, e) => { DcTeams.CancelTransfer(comp, teamRef); _forceRender = true; };
            menu.Items.Add(item);
            return menu;
        }

        static MenuItem MenuEntry(string text, FrameworkElement icon, bool current, object comp, object team, int task)
        {
            var item = new MenuItem { Header = text, IsChecked = current };
            if (icon != null) item.Icon = icon;
            item.Click += (s, e) =>
            {
                DcControl.SetForced(comp, team, task);
                _forceRender = true;
            };
            return item;
        }

        static string Cap(string s) { return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1); }

        static void RenderFlood(Col c, float p, float s, object comp, int i)
        {
            var tb = c.Flood;
            if (tb == null) return;
            bool any = p > 0.0005f || s > 0.0005f;
            int ap = Arrows(c.RP, FloodOne, FloodTwo, FloodTwo), asb = Arrows(c.RS, FloodOne, FloodTwo, FloodTwo);
            // colour: yellow / red while damage control can still get water out (or a leak is open), grey once it cannot
            int sp = any ? FloodState(comp, i, true) : 0, ss = any ? FloodState(comp, i, false) : 0;
            string sig = any ? Pct(p) + ap + "/" + Pct(s) + asb + "|" + sp + ss : "none";
            if (!NeedsRender(tb, sig, c.FloodSig, c.FloodShown)) return;
            var il = tb.Inlines;
            il.Clear();
            var label = new Run(c.FloodOrig ?? "");
            if (any) label.Foreground = new SolidColorBrush(FloodColor(Math.Max(sp, ss)));
            il.Add(label);
            if (any)
            {
                il.Add(Colored("  " + Pct(p), FloodColor(sp)));
                if (ap != 0) il.Add(Arrow(ap > 0, Math.Abs(ap), ap > 0 ? CUp : CDown, () => FloodCause(comp, c, i, true)));
                il.Add(Colored(" / ", FloodColor(Math.Max(sp, ss))));
                il.Add(Colored(Pct(s), FloodColor(ss)));
                if (asb != 0) il.Add(Arrow(asb > 0, Math.Abs(asb), asb > 0 ? CUp : CDown, () => FloodCause(comp, c, i, false)));
            }
            c.FloodSig = sig;
            c.FloodShown = tb.Text;
        }

        static readonly Color CFixLow = Color.FromRgb(236, 200, 80), CFixHigh = Color.FromRgb(222, 82, 62), CNoFix = Color.FromRgb(150, 156, 162);

        /// <summary>0 = grey (nothing more damage control can take out), 1 = yellow (can, under 25%), 2 = red (can, 25% and more).</summary>
        static Color FloodColor(int state) { return state >= 2 ? CFixHigh : state == 1 ? CFixLow : CNoFix; }

        /// <summary>
        /// Whether damage control can still lower the water of one compartment: a leak is open, or there is more water than will
        /// stay once the hull is welded as far as it can be (MinimumFlooding falls as the hull is repaired, up to MaxAllowedIntegrity).
        /// 0 = no (or no water), 1 = yes and under 25%, 2 = yes and 25% or more.
        /// </summary>
        static int FloodState(object comp, int i, bool port)
        {
            try
            {
                var fc = ((port ? _compPort : _compStar).GetValue(comp) as Array).GetValue(i);
                var sc = (_compSys.GetValue(comp) as Array).GetValue(i);
                InitCauses(comp, fc, sc);
                float water = F(_fcFloodPct.GetValue(fc, null));
                if (water <= 0.0005f) return 0;
                bool leak = GetF(_fcRate, fc) > 0f;
                float integ = F(_fcIntPct.GetValue(fc, null));
                float maxInt = GetF(_fcMaxInt, fc);
                float allowed = maxInt > 0f ? 100f * GetP(_fcMaxAllowed, fc) / maxInt : 100f;
                float minNow = GetP(_fcMinFlood, fc);
                // MinimumFlooding = (100 - integrity%) / 2 (submarines / 3): what stays after welding up to the allowed integrity
                float minAfter = integ < 99.99f ? minNow * Math.Max(0f, 100f - allowed) / (100f - integ) : 0f;
                bool fixable = leak || water > minAfter + 0.5f;
                if (!fixable) return 0;
                return water >= 25f ? 2 : 1;
            }
            catch { return 0; }
        }

        static void RenderFire(Col c, float f, object comp, int i)
        {
            var tb = c.Fire;
            if (tb == null) return;
            bool any = f > 0.0005f;
            int a = Arrows(c.RF, FireOne, FireTwoUp, FireTwoDown);
            string sig = any ? Pct(f) + a : "none";
            if (!NeedsRender(tb, sig, c.FireSig, c.FireShown)) return;
            var il = tb.Inlines;
            il.Clear();
            il.Add(new Run(c.FireOrig ?? ""));
            if (any)
            {
                il.Add(new Run("  " + Pct(f)));
                if (a != 0) il.Add(Arrow(a > 0, Math.Abs(a), a > 0 ? CUp : CDown, () => FireCause(comp, c, i)));
            }
            c.FireSig = sig;
            c.FireShown = tb.Text;
        }

        static Run Colored(string text, Color color)
        {
            var r = new Run(text);
            r.Foreground = new SolidColorBrush(color);
            return r;
        }

        static Inline Arrow(bool up, int n, Color color, Func<string> why = null)
        {
            string g = up ? "M0,7 L4,0 L8,7 Z" : "M0,0 L8,0 L4,7 Z";
            if (n > 1) g += up ? " M9,7 L13,0 L17,7 Z" : " M9,0 L17,0 L13,7 Z";
            try
            {
                var path = new Path { Data = new StreamGeometry(g), Fill = new SolidColorBrush(color), Width = n > 1 ? 17f : 8f, Height = 7f };
                if (why == null)
                {
                    path.Margin = new Thickness(4f, 0f, 1f, 1f);
                    return new InlineUIContainer(path);
                }
                // a bigger hover area around the small arrow; its tooltip says why (made when it opens)
                var hit = new Border { Child = path, Padding = new Thickness(3f, 3f, 2f, 3f), Margin = new Thickness(1f, -3f, -1f, -2f),
                                       Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) };
                LiveTip(hit, why);
                try { ToolTipService.SetShowDuration(hit, int.MaxValue); } catch { }
                return new InlineUIContainer(hit);
            }
            catch
            {
                // no vector shapes: plain + / -
                return Colored(" " + new string(up ? '+' : '-', n), color);
            }
        }

        // ------------------------------------------------------------ team icons (16 x 16)

        static Path Shape(string data, Color fill)
        {
            return new Path { Data = new StreamGeometry(data), Fill = new SolidColorBrush(fill) };
        }

        // ---- generated from the approved drawings (16 x 16): {path data, r, g, b} per layer, back to front
        static readonly Dictionary<int, object[][]> IconLayers = new Dictionary<int, object[][]>
        {
            { TaskLeak, new[] {
                new object[] { "F0 M8.0,0.6 L12.44,7.03 L12.7,7.45 L12.92,7.88 L13.1,8.34 L13.24,8.81 L13.34,9.29 L13.39,9.77 L13.4,10.26 L13.36,10.75 L13.28,11.23 L13.16,11.71 L12.99,12.17 L12.78,12.61 L12.54,13.03 L12.25,13.43 L11.93,13.8 L11.58,14.14 L11.2,14.45 L10.79,14.72 L10.36,14.95 L9.92,15.15 L9.45,15.3 L8.97,15.41 L8.49,15.48 L8.0,15.5 L7.51,15.48 L7.03,15.41 L6.55,15.3 L6.08,15.15 L5.64,14.95 L5.21,14.72 L4.8,14.45 L4.42,14.14 L4.07,13.8 L3.75,13.43 L3.46,13.03 L3.22,12.61 L3.01,12.17 L2.84,11.71 L2.72,11.23 L2.64,10.75 L2.6,10.26 L2.61,9.77 L2.66,9.29 L2.76,8.81 L2.9,8.34 L3.08,7.88 L3.3,7.45 L3.56,7.03 Z", 74, 150, 226 },
                new object[] { "F0 M6.92,10.73 L7.0,10.42 L7.06,10.11 L7.09,9.81 L7.09,9.52 L7.07,9.25 L7.02,9.01 L6.95,8.79 L6.86,8.6 L6.74,8.45 L6.61,8.33 L6.46,8.25 L6.29,8.21 L6.11,8.22 L5.92,8.26 L5.73,8.34 L5.53,8.47 L5.34,8.62 L5.15,8.82 L4.97,9.04 L4.8,9.29 L4.64,9.56 L4.5,9.85 L4.38,10.16 L4.28,10.47 L4.2,10.78 L4.14,11.09 L4.11,11.39 L4.11,11.68 L4.13,11.95 L4.18,12.19 L4.25,12.41 L4.34,12.6 L4.46,12.75 L4.59,12.87 L4.74,12.95 L4.91,12.99 L5.09,12.98 L5.28,12.94 L5.47,12.86 L5.67,12.73 L5.86,12.58 L6.05,12.38 L6.23,12.16 L6.4,11.91 L6.56,11.64 L6.7,11.35 L6.82,11.04 Z", 160, 208, 250 },
            } },
            { TaskFire, new[] {
                new object[] { "M8,0.5 C10.2,3.4 13.5,5.6 13.5,10 C13.5,13.2 11.2,15.5 8,15.5 C4.8,15.5 2.5,13.2 2.5,10 C2.5,7.2 4.2,5.6 5.2,2.8 C6.4,4.6 6.9,5.6 7.7,6.8 C8,4.6 8,2.6 8,0.5 Z", 240, 120, 50 },
                new object[] { "M8,8 C9.7,9.6 10.8,10.8 10.8,12.4 C10.8,14.4 9.6,15.3 8,15.3 C6.4,15.3 5.2,14.4 5.2,12.4 C5.2,11 6.3,10 8,8 Z", 255, 206, 90 },
            } },
            { TaskPump, new[] {
                new object[] { "F0 M15.07,-0.22 L10.53,2.4 L9.4,2.4 L9.4,2.8 L6.6,2.8 L6.2,4.6 L6.8,4.6 L6.8,6.6 L4.3,6.6 L2.8,6.6 L2.8,8.2 L2.8,9.8 L4.3,9.8 L4.3,8.2 L6.8,8.2 L6.8,14.0 L5.2,14.0 L5.2,15.6 L12.0,15.6 L12.0,14.0 L10.4,14.0 L10.4,4.6 L11.0,4.6 L10.8,3.7 L10.8,3.63 L15.67,0.82 Z", 112, 130, 154 },
                new object[] { "F0 M8.1,5.0 L8.1,13.8 L7.4,13.8 L7.4,5.0 Z", 162, 180, 202 },
                new object[] { "F0 M3.55,10.6 L4.73,13.15 L4.77,13.26 L4.8,13.36 L4.83,13.46 L4.84,13.57 L4.85,13.68 L4.85,13.79 L4.84,13.9 L4.81,14.0 L4.78,14.11 L4.75,14.21 L4.7,14.31 L4.65,14.4 L4.58,14.49 L4.51,14.57 L4.44,14.65 L4.36,14.72 L4.27,14.78 L4.17,14.84 L4.08,14.89 L3.98,14.93 L3.87,14.96 L3.77,14.98 L3.66,15.0 L3.55,15.0 L3.44,15.0 L3.33,14.98 L3.23,14.96 L3.12,14.93 L3.02,14.89 L2.93,14.84 L2.83,14.78 L2.74,14.72 L2.66,14.65 L2.59,14.57 L2.52,14.49 L2.45,14.4 L2.4,14.31 L2.35,14.21 L2.32,14.11 L2.29,14.0 L2.26,13.9 L2.25,13.79 L2.25,13.68 L2.26,13.57 L2.27,13.46 L2.3,13.36 L2.33,13.26 L2.37,13.15 Z", 74, 150, 226 },
            } },
            { TaskHull, new[] {
                new object[] { "F0 M7.5,11.4 L7.5,15.6 L0.4,15.6 L0.4,11.4 Z M15.6,11.4 L15.6,15.6 L8.5,15.6 L8.5,11.4 Z", 170, 176, 184 },
                new object[] { "F0 M8.5,11.4 L8.5,15.6 L7.5,15.6 L7.5,11.4 Z", 250, 150, 60 },
                new object[] { "F0 M8.5,11.4 L8.5,12.8 L7.5,12.8 L7.5,11.4 Z", 255, 226, 120 },
                new object[] { "F0 M15.08,1.97 L11.06,6.43 L9.72,5.23 L13.74,0.77 Z", 162, 180, 202 },
                new object[] { "F0 M11.41,5.57 L10.74,4.96 L7.79,8.23 L7.96,8.39 L7.6,8.94 L8.27,9.54 L9.83,8.26 L9.36,7.84 Z", 170, 176, 184 },
                new object[] { "F0 M11.92,11.18 L8.91,11.01 L10.22,13.73 L8.21,11.48 L7.22,14.32 L7.39,11.31 L4.67,12.62 L6.92,10.61 L4.08,9.62 L7.09,9.79 L5.78,7.07 L7.79,9.32 L8.78,6.48 L8.61,9.49 L11.33,8.18 L9.08,10.19 Z", 255, 226, 120 },
                new object[] { "F0 M4.9,6.71 L5.59,6.23 L4.33,4.43 L3.64,4.91 Z M11.41,6.99 L11.95,7.64 L13.48,6.35 L12.94,5.71 Z M3.65,8.84 L4.01,8.08 L1.47,6.89 L1.12,7.65 Z M2.62,9.79 L0.22,9.71 L0.19,10.55 L2.59,10.63 Z M3.53,11.58 L1.09,12.47 L1.38,13.26 L3.82,12.37 Z", 250, 150, 60 },
            } },
            { TaskSystems, new[] {
                new object[] { "F0 M12.1,8.42 L14.65,5.88 L14.65,3.05 L12.38,5.31 L10.69,3.62 L12.95,1.35 L10.12,1.35 L7.58,3.9 L8.07,6.37 L1.85,12.6 L3.4,14.15 L9.63,7.93 Z", 170, 176, 184 },
                new object[] { "F0 M8.04,8.81 L8.81,8.04 L3.72,2.94 L2.7,2.27 L2.27,2.7 L2.94,3.72 Z", 206, 212, 220 },   // screwdriver shaft: light (was 110,118,130 - hard to see)
                new object[] { "F0 M7.43,9.13 L9.13,7.43 L10.12,7.86 L14.08,11.82 L14.08,12.67 L12.67,14.08 L11.82,14.08 L7.86,10.12 Z", 74, 150, 226 },
                new object[] { "F0 M9.48,9.06 L12.88,12.45 L12.45,12.88 L9.06,9.48 Z", 160, 208, 250 },
            } },
            { TaskIdle, new[] {
                new object[] { "F0 M1.6,15.6 L1.6,12.4 L2.6,10.6 L5.4,9.4 L10.6,9.4 L13.4,10.6 L14.4,12.4 L14.4,15.6 Z", 52, 72, 118 },
                new object[] { "F0 M6.2,12.4 L6.2,13.2 L3.4,13.2 L3.4,12.4 Z", 86, 110, 160 },
                new object[] { "F0 M12.0,12.4 L12.0,13.2 L9.6,13.2 L9.6,12.4 Z", 236, 240, 244 },
                new object[] { "F0 M11.3,8.0 L11.1,5.0 L4.9,5.0 L4.7,8.0 L5.3,9.2 L4.4,9.2 L5.4,10.8 L8.0,11.8 L10.6,10.8 L11.6,9.2 L10.7,9.2 Z", 212, 184, 128 },
                new object[] { "F0 M6.3,5.0 L9.7,5.0 L9.5,7.2 L8.7,8.1 L7.3,8.1 L6.5,7.2 Z", 238, 196, 156 },
                new object[] { "F0 M11.2,2.2 L9.6,0.8 L6.4,0.8 L4.8,2.2 L4.47,4.0 L3.2,4.0 L3.2,5.0 L12.8,5.0 L12.8,4.0 L11.53,4.0 Z", 204, 44, 38 },
                new object[] { "F0 M11.47,3.7 L11.33,2.9 L4.67,2.9 L4.53,3.7 Z", 255, 200, 60 },
            } },
            { IconByPriority, new[] {
                new object[] { "F0 M11.75,1.5 L11.45,1.34 L11.14,1.19 L10.82,1.05 L10.5,0.93 L10.18,0.82 L9.85,0.73 L9.51,0.65 L9.17,0.59 L8.83,0.55 L8.49,0.52 L8.15,0.5 L7.8,0.5 L7.46,0.52 L7.12,0.55 L6.78,0.6 L6.44,0.66 L6.11,0.74 L5.78,0.84 L5.45,0.95 L5.13,1.07 L4.82,1.21 L4.51,1.36 L4.21,1.53 L3.92,1.71 L3.63,1.9 L3.36,2.11 L3.09,2.33 L2.84,2.56 L2.59,2.8 L2.36,3.05 L2.14,3.32 L1.93,3.59 L1.74,3.87 L1.55,4.17 L1.39,4.46 L1.23,4.77 L1.09,5.08 L0.96,5.4 L0.85,5.73 L0.76,6.06 L2.11,6.42 L2.19,6.15 L2.28,5.89 L2.38,5.63 L2.49,5.37 L2.62,5.12 L2.76,4.88 L2.91,4.64 L3.06,4.41 L3.23,4.19 L3.41,3.98 L3.6,3.77 L3.8,3.58 L4.01,3.39 L4.22,3.21 L4.45,3.04 L4.68,2.88 L4.92,2.74 L5.16,2.6 L5.41,2.48 L5.67,2.36 L5.93,2.26 L6.19,2.17 L6.46,2.1 L6.73,2.03 L7.01,1.98 L7.28,1.94 L7.56,1.92 L7.84,1.9 L8.12,1.9 L8.4,1.91 L8.68,1.94 L8.95,1.98 L9.23,2.03 L9.5,2.09 L9.77,2.16 L10.04,2.25 L10.3,2.35 L10.55,2.46 L10.8,2.58 L11.05,2.72 L10.3,4.02 L13.31,3.21 L12.5,0.21 Z M4.55,14.66 L4.86,14.81 L5.18,14.95 L5.5,15.07 L5.82,15.18 L6.15,15.27 L6.49,15.35 L6.83,15.41 L7.17,15.45 L7.51,15.48 L7.85,15.5 L8.2,15.5 L8.54,15.48 L8.88,15.45 L9.22,15.4 L9.56,15.34 L9.89,15.26 L10.22,15.16 L10.55,15.05 L10.87,14.93 L11.18,14.79 L11.49,14.64 L11.79,14.47 L12.08,14.29 L12.37,14.1 L12.64,13.89 L12.91,13.67 L13.16,13.44 L13.41,13.2 L13.64,12.95 L13.86,12.68 L14.07,12.41 L14.26,12.13 L14.45,11.83 L14.61,11.54 L14.77,11.23 L14.91,10.92 L15.04,10.6 L15.15,10.27 L15.24,9.94 L13.89,9.58 L13.81,9.85 L13.72,10.11 L13.62,10.37 L13.51,10.63 L13.38,10.88 L13.24,11.12 L13.09,11.36 L12.94,11.59 L12.77,11.81 L12.59,12.02 L12.4,12.23 L12.2,12.42 L11.99,12.61 L11.78,12.79 L11.55,12.96 L11.32,13.12 L11.08,13.26 L10.84,13.4 L10.59,13.52 L10.33,13.64 L10.07,13.74 L9.81,13.83 L9.54,13.9 L9.27,13.97 L8.99,14.02 L8.72,14.06 L8.44,14.08 L8.16,14.1 L7.88,14.1 L7.6,14.09 L7.32,14.06 L7.05,14.02 L6.77,13.97 L6.5,13.91 L6.23,13.84 L5.96,13.75 L5.7,13.65 L5.45,13.54 L5.2,13.42 L4.95,13.28 L5.7,11.98 L2.69,12.79 L3.5,15.79 L4.6,13.89 L4.25,14.5 Z", 74, 150, 226 },
                new object[] { "F0 M9.81,5.13 L9.6,5.0 L9.3,4.86 L8.99,4.75 L8.75,4.69 L8.75,2.9 L7.25,2.9 L7.25,4.69 L7.01,4.75 L6.7,4.86 L6.4,5.0 L6.19,5.13 L4.92,3.86 L3.86,4.92 L5.13,6.19 L5.0,6.4 L4.86,6.7 L4.75,7.01 L4.69,7.25 L2.9,7.25 L2.9,8.75 L4.69,8.75 L4.75,8.99 L4.86,9.3 L5.0,9.6 L5.13,9.81 L3.86,11.08 L4.92,12.14 L6.19,10.87 L6.4,11.0 L6.7,11.14 L7.01,11.25 L7.25,11.31 L7.25,13.1 L8.75,13.1 L8.75,11.31 L8.99,11.25 L9.3,11.14 L9.6,11.0 L9.81,10.87 L11.08,12.14 L12.14,11.08 L10.87,9.81 L11.0,9.6 L11.14,9.3 L11.25,8.99 L11.31,8.75 L13.1,8.75 L13.1,7.25 L11.31,7.25 L11.25,7.01 L11.14,6.7 L11.0,6.4 L10.87,6.19 L12.14,4.92 L11.08,3.86 Z M9.34,8.13 L9.32,8.26 L9.29,8.39 L9.25,8.52 L9.19,8.64 L9.12,8.75 L9.04,8.86 L8.95,8.95 L8.86,9.04 L8.75,9.12 L8.64,9.19 L8.52,9.25 L8.39,9.29 L8.26,9.32 L8.13,9.34 L8.0,9.35 L7.87,9.34 L7.74,9.32 L7.61,9.29 L7.48,9.25 L7.36,9.19 L7.25,9.12 L7.14,9.04 L7.05,8.95 L6.96,8.86 L6.88,8.75 L6.81,8.64 L6.75,8.52 L6.71,8.39 L6.68,8.26 L6.66,8.13 L6.65,8.0 L6.66,7.87 L6.68,7.74 L6.71,7.61 L6.75,7.48 L6.81,7.36 L6.88,7.25 L6.96,7.14 L7.05,7.05 L7.14,6.96 L7.25,6.88 L7.36,6.81 L7.48,6.75 L7.61,6.71 L7.74,6.68 L7.87,6.66 L8.0,6.65 L8.13,6.66 L8.26,6.68 L8.39,6.71 L8.52,6.75 L8.64,6.81 L8.75,6.88 L8.86,6.96 L8.95,7.05 L9.04,7.14 L9.12,7.25 L9.19,7.36 L9.25,7.48 L9.29,7.61 L9.32,7.74 L9.34,7.87 L9.35,8.0 Z", 170, 176, 184 },
            } },
            { IconKnocked, new[] {
                new object[] { "F0 M1.6,15.6 L1.6,12.4 L2.6,10.6 L5.4,9.4 L10.6,9.4 L13.4,10.6 L14.4,12.4 L14.4,15.6 Z", 84, 90, 100 },
                new object[] { "F0 M6.2,12.4 L6.2,13.2 L3.4,13.2 L3.4,12.4 Z", 110, 116, 126 },
                new object[] { "F0 M12.0,12.4 L12.0,13.2 L9.6,13.2 L9.6,12.4 Z", 110, 116, 126 },
                new object[] { "F0 M11.3,8.0 L11.1,5.0 L4.9,5.0 L4.7,8.0 L5.3,9.2 L4.4,9.2 L5.4,10.8 L8.0,11.8 L10.6,10.8 L11.6,9.2 L10.7,9.2 Z", 128, 124, 116 },
                new object[] { "F0 M6.3,5.0 L9.7,5.0 L9.5,7.2 L8.7,8.1 L7.3,8.1 L6.5,7.2 Z", 128, 124, 116 },
                new object[] { "F0 M11.2,2.2 L9.6,0.8 L6.4,0.8 L4.8,2.2 L4.47,4.0 L3.2,4.0 L3.2,5.0 L12.8,5.0 L12.8,4.0 L11.53,4.0 Z", 120, 74, 70 },
                new object[] { "F0 M11.47,3.7 L11.33,2.9 L4.67,2.9 L4.53,3.7 Z", 128, 124, 116 },
                new object[] { "F0 M10.76,6.97 L8.0,9.73 L5.24,6.97 L3.97,8.24 L6.73,11.0 L3.97,13.76 L5.24,15.03 L8.0,12.27 L10.76,15.03 L12.03,13.76 L9.27,11.0 L12.03,8.24 Z", 222, 60, 48 },
            } },
            { IconTransit, new[] {
                new object[] { "F0 M1.6,15.6 L1.6,12.4 L2.6,10.6 L5.4,9.4 L10.6,9.4 L13.4,10.6 L14.4,12.4 L14.4,15.6 Z", 84, 90, 100 },
                new object[] { "F0 M6.2,12.4 L6.2,13.2 L3.4,13.2 L3.4,12.4 Z", 110, 116, 126 },
                new object[] { "F0 M12.0,12.4 L12.0,13.2 L9.6,13.2 L9.6,12.4 Z", 110, 116, 126 },
                new object[] { "F0 M11.3,8.0 L11.1,5.0 L4.9,5.0 L4.7,8.0 L5.3,9.2 L4.4,9.2 L5.4,10.8 L8.0,11.8 L10.6,10.8 L11.6,9.2 L10.7,9.2 Z", 128, 124, 116 },
                new object[] { "F0 M6.3,5.0 L9.7,5.0 L9.5,7.2 L8.7,8.1 L7.3,8.1 L6.5,7.2 Z", 128, 124, 116 },
                new object[] { "F0 M11.2,2.2 L9.6,0.8 L6.4,0.8 L4.8,2.2 L4.47,4.0 L3.2,4.0 L3.2,5.0 L12.8,5.0 L12.8,4.0 L11.53,4.0 Z", 120, 74, 70 },
                new object[] { "F0 M11.47,3.7 L11.33,2.9 L4.67,2.9 L4.53,3.7 Z", 128, 124, 116 },
            } },
            { IconInjured, new[] {
                new object[] { "F0 M1.6,15.6 L1.6,12.4 L2.6,10.6 L5.4,9.4 L10.6,9.4 L13.4,10.6 L14.4,12.4 L14.4,15.6 Z", 84, 90, 100 },
                new object[] { "F0 M6.2,12.4 L6.2,13.2 L3.4,13.2 L3.4,12.4 Z", 110, 116, 126 },
                new object[] { "F0 M12.0,12.4 L12.0,13.2 L9.6,13.2 L9.6,12.4 Z", 110, 116, 126 },
                new object[] { "F0 M11.3,8.0 L11.1,5.0 L4.9,5.0 L4.7,8.0 L5.3,9.2 L4.4,9.2 L5.4,10.8 L8.0,11.8 L10.6,10.8 L11.6,9.2 L10.7,9.2 Z", 128, 124, 116 },
                new object[] { "F0 M6.3,5.0 L9.7,5.0 L9.5,7.2 L8.7,8.1 L7.3,8.1 L6.5,7.2 Z", 128, 124, 116 },
                new object[] { "F0 M11.2,2.2 L9.6,0.8 L6.4,0.8 L4.8,2.2 L4.47,4.0 L3.2,4.0 L3.2,5.0 L12.8,5.0 L12.8,4.0 L11.53,4.0 Z", 120, 74, 70 },
                new object[] { "F0 M11.47,3.7 L11.33,2.9 L4.67,2.9 L4.53,3.7 Z", 128, 124, 116 },
                new object[] { "F0 M15.6,9.6 L15.6,15.6 L9.6,15.6 L9.6,9.6 Z", 236, 240, 244 },
                new object[] { "F0 M13.35,10.4 L11.85,10.4 L11.85,11.85 L10.4,11.85 L10.4,13.35 L11.85,13.35 L11.85,14.8 L13.35,14.8 L13.35,13.35 L14.8,13.35 L14.8,11.85 L13.35,11.85 Z", 222, 60, 48 },
            } },
            { IconDead, new[] {
                new object[] { "F0 M1.6,15.6 L1.6,12.4 L2.6,10.6 L5.4,9.4 L10.6,9.4 L13.4,10.6 L14.4,12.4 L14.4,15.6 Z", 84, 90, 100 },
                new object[] { "F0 M6.2,12.4 L6.2,13.2 L3.4,13.2 L3.4,12.4 Z", 110, 116, 126 },
                new object[] { "F0 M12.0,12.4 L12.0,13.2 L9.6,13.2 L9.6,12.4 Z", 110, 116, 126 },
                new object[] { "F0 M11.3,8.0 L11.1,5.0 L4.9,5.0 L4.7,8.0 L5.3,9.2 L4.4,9.2 L5.4,10.8 L8.0,11.8 L10.6,10.8 L11.6,9.2 L10.7,9.2 Z", 128, 124, 116 },
                new object[] { "F0 M6.3,5.0 L9.7,5.0 L9.5,7.2 L8.7,8.1 L7.3,8.1 L6.5,7.2 Z", 128, 124, 116 },
                new object[] { "F0 M11.2,2.2 L9.6,0.8 L6.4,0.8 L4.8,2.2 L4.47,4.0 L3.2,4.0 L3.2,5.0 L12.8,5.0 L12.8,4.0 L11.53,4.0 Z", 120, 74, 70 },
                new object[] { "F0 M11.47,3.7 L11.33,2.9 L4.67,2.9 L4.53,3.7 Z", 128, 124, 116 },
                new object[] { "F0 M10.76,6.97 L8.0,9.73 L5.24,6.97 L3.97,8.24 L6.73,11.0 L3.97,13.76 L5.24,15.03 L8.0,12.27 L10.76,15.03 L12.03,13.76 L9.27,11.0 L12.03,8.24 Z", 222, 60, 48 },
            } },
        };

        const string PicPort = "M100,6 A94,94 0 0 0 100,194 Z";
        const string PicStar = "M100,6 A94,94 0 0 1 100,194 Z";
        const string PicHull = "M100,14 C107,28 113,50 114,80 L114,168 C114,177 110,183 104,186 L96,186 C90,183 86,177 86,168 L86,80 C87,50 93,28 100,14 Z";
        const string PicDeck = "M100,50 A6,6 0 1 1 99.99,50 Z M93,72 L107,72 L107,98 L93,98 Z M100,104 A4.5,7.5 0 1 1 99.99,104 Z M94,124 L106,124 L106,146 L94,146 Z M100,156 A6,6 0 1 1 99.99,156 Z";

        static FrameworkElement Icon(int task)
        {
            var cv = new Canvas { Width = 16f, Height = 16f };
            object[][] layers;
            if (!IconLayers.TryGetValue(task, out layers)) IconLayers.TryGetValue(TaskIdle, out layers);
            if (layers != null)
                foreach (var l in layers)
                    cv.Children.Add(Shape((string)l[0], Color.FromRgb((byte)(int)l[1], (byte)(int)l[2], (byte)(int)l[3])));
            // no tooltip of its own: the frame around it has the team's (an inner one would hide it)
            return cv;
        }

        static string TaskName(int task)
        {
            switch (task)
            {
                case TaskLeak: return "plugging a leak";
                case TaskFire: return "fighting the fire";
                case TaskPump: return "pumping water out";
                case TaskHull: return "welding the hull";
                case TaskSystems: return "repairing systems";
                case IconKnocked: case IconDead:
                    return DcTeams.KnocksToDie > 1 ? "lost (knocked out " + DcTeams.KnocksToDie.ToString(CultureInfo.InvariantCulture) + " times)" :
                           DcTeams.KnocksToDie == 1 ? "lost (knocked out)" : "lost";
                case IconTransit: return "on the move (In Transfer)";
                case IconInjured: return "recovering (In Transfer)";
                default: return "no task";
            }
        }

        // ------------------------------------------------------------ switching off

        static void Restore(object view) { Restore(view, false); }

        static void Restore(object view, bool removeCard)
        {
            ViewState st;
            if (view == null || !States.TryGetValue(view, out st)) return;
            int[] counts = null;
            try { if (st.Comp != null) counts = _compTeams.GetValue(st.Comp, null) as int[]; } catch { }
            if (st.Cols != null)
            for (int ci = 0; ci < st.Cols.Length; ci++)
            {
                var c = st.Cols[ci];
                RestoreBlock(c.Header, c.HeaderOrig); c.HeaderSig = null;
                // "DC Teams: N" with the number of now, not of when the view was decorated (the game rewrites it only when it changes)
                if (!RestoreBlock(c.Teams, c.TeamsOrig) && c.Teams != null && counts != null && ci < counts.Length)
                {
                    // not bound (does not update itself): with the number of now
                    string want = WithCount(c.TeamsOrig, counts[ci]);
                    if (!string.IsNullOrEmpty(want) && c.Teams.Text != want) try { c.Teams.Inlines.Clear(); c.Teams.Inlines.Add(new Run(want)); } catch { }
                }
                c.TeamsSig = null;
                RestoreBlock(c.Flood, c.FloodOrig); c.FloodSig = null;
                RestoreBlock(c.Fire, c.FireOrig); c.FireSig = null;
                if (c.FloodData != null) { SetDesc(c.FloodSum, c.FloodDesc); c.FloodData = null; }
                if (c.FireData != null) { SetDesc(c.FireSum, c.FireDesc); c.FireData = null; }
                if (c.Teams != null) try { ToolTipService.SetToolTip(c.Teams, null); } catch { }
            }
            if (removeCard)
            {
                PutBackAuto(st);   // first: it takes the game's Auto DC content out of our card
                if (st.Priority != null && st.PriorityHost != null) try { st.PriorityHost.Children.Remove(st.Priority); } catch { }
                st.Priority = null; st.PriorityHost = null; st.PriorityButtons = null;
                try { RemoveExtras(st.X); } catch (Exception e) { Trace("RemoveExtras: " + e.Message); }
                st.X = null;
            }
            if (st.Help != null && st.HelpOrig != null)
            {
                RestoreBlock(st.Help, st.HelpOrig);
                try { ToolTipService.SetToolTip(st.Help, null); } catch { }
                st.HelpShown = null; st.HelpSig = null;
            }
            if (st.HelpTitle != null) { try { st.HelpTitle.Visibility = Visibility.Visible; } catch { } }
            if (removeCard) RestoreHeaders();
            st.Ok = false;   // found again (and the priority card added again) when switched back on
            if (removeCard) Trace("Damage Control tab restored");
        }

        /// <summary>The game's "DC Teams: N" text with another N (its last number); as it is when it has none.</summary>
        static string WithCount(string text, int n)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int end = text.Length - 1;
            while (end >= 0 && !char.IsDigit(text[end])) end--;
            if (end < 0) return text;
            int start = end;
            while (start > 0 && char.IsDigit(text[start - 1])) start--;
            return text.Substring(0, start) + Math.Max(0, n).ToString(CultureInfo.InvariantCulture) + text.Substring(end + 1);
        }

        /// <summary>
        /// What the game had in a text block before we first wrote into it: its Text binding (if any), its inlines (the objects
        /// themselves - Runs with their own bindings keep them) and its text. Kept once per text block, at the first sight.
        /// </summary>
        sealed class Orig { public BindingBase Binding; public List<Inline> Inlines = new List<Inline>(); public string Text; }
        static readonly ConditionalWeakTable<TextBlock, Orig> Origs = new ConditionalWeakTable<TextBlock, Orig>();

        static void Keep(TextBlock tb)
        {
            if (tb == null) return;
            Orig o;
            if (Origs.TryGetValue(tb, out o)) return;
            o = new Orig();
            try { o.Binding = BindingOperations.GetBindingBase(tb, TextBlock.TextProperty); } catch { }
            try { foreach (var i in tb.Inlines) o.Inlines.Add(i); } catch { }
            try { o.Text = tb.Text; } catch { }
            Origs.Add(tb, o);
        }

        static string FirstText(TextBlock tb)
        {
            if (tb == null) return null;
            Keep(tb);
            Orig o;
            return Origs.TryGetValue(tb, out o) && o.Text != null ? o.Text : tb.Text;
        }

        /// <summary>
        /// The game's text back: its own inlines and its binding again (the live value - e.g. "DC Teams: N" of now); without
        /// either, 'text'. True when the game's binding is back (it updates itself).
        /// </summary>
        static bool RestoreBlock(TextBlock tb, string text)
        {
            if (tb == null) return false;
            Orig o;
            bool have = Origs.TryGetValue(tb, out o);
            try
            {
                tb.Inlines.Clear();
                if (have && o.Inlines.Count > 0) foreach (var i in o.Inlines) tb.Inlines.Add(i);
                else if (text != null) tb.Inlines.Add(new Run(text));
            }
            catch (Exception ex) { Trace("RestoreBlock: " + ex.Message); if (text != null) try { tb.Inlines.Clear(); tb.Inlines.Add(new Run(text)); } catch { } }
            if (!have || o.Binding == null) return false;
            try
            {
                var e = BindingOperations.GetBindingExpressionBase(tb, TextBlock.TextProperty);
                if (e != null) e.UpdateTarget();
                else BindingOperations.SetBinding(tb, TextBlock.TextProperty, o.Binding);
                return true;
            }
            catch (Exception ex) { Trace("RestoreBlock binding: " + ex.Message); return false; }
        }

        // ------------------------------------------------------------ diagnostics

        static void Dump(FrameworkElement view, object vm, object comp)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("Alternative Damage Model - Damage Control tab layout (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ")\n");
                sb.Append("Found: " + _lastStats + "\n");
                // what we look for
                try
                {
                    var floodStates = _compFloodStates.GetValue(comp) as Array;
                    var sys = _compSys.GetValue(comp) as Array;
                    var sums = _vmSums.GetValue(vm, null) as IList;
                    for (int i = 0; floodStates != null && i < floodStates.Length; i++)
                    {
                        var fsum = floodStates.GetValue(i);
                        var sc = sys == null ? null : sys.GetValue(i);
                        var firesum = sc == null ? null : _scFireState.GetValue(sc);
                        sb.Append("compartment " + i + ": flooding row '" + Desc(fsum) + "' #" + Id(fsum) + ", fire row '" + Desc(firesum) + "' #" + Id(firesum) +
                                  ", list #" + (sums != null && i < sums.Count ? Id(sums[i]) : "?") + "\n");
                    }
                    sb.Append("commands: + #" + Id(_vmAssign.GetValue(vm, null)) + ", - #" + Id(_vmReturn.GetValue(vm, null)) + ", view model #" + Id(vm) + "\n\n");
                }
                catch (Exception e) { sb.Append("(targets: " + e.Message + ")\n"); }

                int lines = 0;
                Action<DependencyObject, int> walk = null;
                walk = (e, depth) =>
                {
                    if (lines++ > 6000) return;
                    sb.Append(new string(' ', Math.Min(depth, 60) * 2)).Append(e.GetType().Name);
                    var fe = e as FrameworkElement;
                    if (fe != null)
                    {
                        if (!string.IsNullOrEmpty(fe.Name)) sb.Append(" name=").Append(fe.Name);
                        object dc = null; try { dc = fe.DataContext; } catch { }
                        if (dc != null) sb.Append(" dc=").Append(dc.GetType().Name).Append('#').Append(Id(dc));
                        if (!fe.IsVisible) sb.Append(" hidden");
                    }
                    var tb = e as TextBlock; if (tb != null) sb.Append(" text='").Append(tb.Text).Append("'");
                    var cc = e as ContentControl; if (cc != null && cc.Content != null) sb.Append(" content=").Append(cc.Content is string ? "'" + cc.Content + "'" : cc.Content.GetType().Name + "#" + Id(cc.Content));
                    var ic = e as ItemsControl; if (ic != null && ic.ItemsSource != null) sb.Append(" items=").Append(ic.ItemsSource.GetType().Name).Append('#').Append(Id(ic.ItemsSource));
                    var b = e as ButtonBase; if (b != null && b.Command != null) sb.Append(" cmd=#").Append(Id(b.Command)).Append(" param=").Append(b.CommandParameter);
                    sb.Append('\n');
                    int n; try { n = VisualTreeHelper.GetChildrenCount(e); } catch { n = 0; }
                    for (int k = 0; k < n; k++) { DependencyObject c = null; try { c = VisualTreeHelper.GetChild(e, k); } catch { } if (c != null) walk(c, depth + 1); }
                };
                walk(view, 0);
                if (DumpFile != null) System.IO.File.WriteAllText(DumpFile, sb.ToString(), new UTF8Encoding(false));
                Trace(sb.ToString());
            }
            catch (Exception e) { Trace("Dump: " + e.Message); }
        }

        // ------------------------------------------------------------ XAML export ([General] DumpXaml)

        static readonly HashSet<string> Exported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Writes the XAML of every NoesisXaml asset the game has loaded so far (uri + content) under dir, keeping the
        /// asset's path. Returns how many new files were written. Assets load when their UI is first shown, so this is run again
        /// when a Damage Control tab is created.
        /// </summary>
        public static int ExportXaml(string dir)
        {
            int n = 0;
            foreach (var x in UnityEngine.Resources.FindObjectsOfTypeAll<NoesisXaml>())
            {
                if (x == null || x.content == null || x.content.Length == 0) continue;
                string uri = string.IsNullOrEmpty(x.uri) ? x.name + ".xaml" : x.uri;
                if (!Exported.Add(uri)) continue;
                string rel = uri.Replace('\\', '/');
                int colon = rel.IndexOf(":/", StringComparison.Ordinal);   // pack-style uris
                if (colon >= 0) rel = rel.Substring(colon + 2);
                rel = rel.TrimStart('/');
                foreach (var bad in System.IO.Path.GetInvalidPathChars()) rel = rel.Replace(bad, '_');
                string path = System.IO.Path.Combine(dir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    System.IO.File.WriteAllBytes(path, x.content);
                    n++;
                }
                catch (Exception e) { Trace("ExportXaml " + uri + ": " + e.Message); }
            }
            return n;
        }

        static readonly ConditionalWeakTable<object, object> Ids = new ConditionalWeakTable<object, object>();
        static int _nextId;

        /// <summary>A short id per object instance, to see in the dump which elements point to the same object.</summary>
        static string Id(object o)
        {
            if (o == null) return "null";
            if (o is string || o.GetType().IsValueType) return "v";
            object id;
            if (!Ids.TryGetValue(o, out id)) { id = ++_nextId; Ids.Add(o, id); }
            return id.ToString();
        }
    }
}
