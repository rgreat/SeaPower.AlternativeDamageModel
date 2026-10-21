using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Noesis;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Damage Control tab, extra panels:
    ///  - the "Hull Damage / Flooding" card gets a third row "Fires:" - the worst fire stage on board and how many sections burn;
    ///  - a "List / Trim" card under "Damage Control Teams" / "Teams Knocked Out": two dials (list: port / starboard, trim:
    ///    bow / stern) and a water level bar (flooding of the whole ship). The angles are the ones the game shows in its F10 debug
    ///    panel (Damage: "Listing" = the hull's local roll, transform.localEulerAngles.z; trim = .x), smoothed over a few seconds so
    ///    that the dial shows the list rather than the rolling in the waves.
    /// </summary>
    static partial class DamageControlUi
    {
        sealed class Extras
        {
            public Grid HullInner; public RowDefinition FireRowDef; public Grid FireRow; public TextBlock FireValue;
            public Panel ListHost; public Border ListCard; public object PinComp; public Panel SchemPanel; public bool SchemShown; public int SchemOff;
            public float[] Lim; public object LimComp; public Action<float, float> ListRed, TrimRed; public RotateTransform ListRot, TrimRot; public TextBlock ListVal, TrimVal;
            public Border LevelFill; public float LevelHeight; public TextBlock LevelVal;
            public FrameworkElement Res;                // where the game's brushes are looked up
            public double T = -1; public float List, Trim;
            public string FireSig;
            public Border HullCard;                      // the card of Hull Damage / Flooding / Fires: its tooltip is the list of hits
            public TextBlock HullVal, FloodVal; public bool HullColored;   // the game's "Hull Damage" / "Flooding" values: we add (NN%)
            // "Damage Control Teams" / "Teams Knocked Out": the game's value (and the first card's "Available / Total") hidden, ours shown
            public List<UIElement> Hidden = new List<UIElement>();
            public List<KeyValuePair<Panel, UIElement>> Added = new List<KeyValuePair<Panel, UIElement>>();
            public WrapPanel FreeIcons, MovingIcons;
            public TextBlock FreeLabel;
            public string TeamsSig;
            public List<KeyValuePair<object, TextBlock>> Clocks = new List<KeyValuePair<object, TextBlock>>();   // time under an icon, updated in place
            public object Comp;                         // the unit shown: another one -> values at once, no smoothing across units
            public Schematic Schem;
            public UIElement[] GameCard2, OurCard2;     // "Teams Knocked Out" (the game's) / "In Transfer" (ours)
            public int Card2Mode = -1;                     // the card's tooltip: the sections and their water (DamageControlUi.Schematic.cs)
        }

        const int HitLogLines = 20;

        /// <summary>The hits on the ship: up to 20 as lines (time, ammunition, section and side); more - the count by ammunition ("100mm: x 31").</summary>
        static FrameworkElement HitLogTip(object comp)
        {
            var root = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10f) };
            Func<string, Color, bool, TextBlock> line = (t, c, bold) => new TextBlock { Text = t, FontSize = 12f, Foreground = new SolidColorBrush(c), FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, Margin = new Thickness(0f, 1f, 0f, 1f) };
            var text = Color.FromRgb(225, 228, 232); var dim = Color.FromRgb(150, 156, 164);
            var recs = HitLog.Records(comp);
            root.Children.Add(line("Hits on the ship: " + recs.Count, text, true));
            if (recs.Count == 0) { root.Children.Add(line("None yet", dim, false)); return root; }
            if (recs.Count > HitLogLines)
            {
                var count = new Dictionary<string, int>(); var order = new List<string>();
                foreach (var r in recs) { int n; count.TryGetValue(r.Ammo, out n); if (n == 0) order.Add(r.Ammo); count[r.Ammo] = n + 1; }
                order.Sort((a, b) => count[b].CompareTo(count[a]));
                foreach (var a in order) root.Children.Add(line(a + ": x " + count[a], text, false));
                return root;
            }
            foreach (var r in recs)
            {
                var ts = TimeSpan.FromSeconds(Math.Max(0.0, r.Time));
                string when = ((int)ts.TotalHours).ToString("0", CultureInfo.InvariantCulture) + ":" + ts.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + ts.Seconds.ToString("00", CultureInfo.InvariantCulture);
                string where = SectionName(_sectionNames, r.Section) + ", " + (r.Port ? "port" : "starboard");
                root.Children.Add(line(when + "   " + r.Ammo + "   " + where + (r.Armor ? "   (stopped by armour)" : "") + (r.Blast ? "   (explosion)" : ""), r.Armor ? dim : text, false));
            }
            return root;
        }

        // dials: half a circle, the needle at the top for 0
        const float DialW = 104f, DialH = 56f, DialCx = 52f, DialCy = 52f, DialR = 46f;
        // full scale = the point where the mod lets the game switch to faster flooding (Limits); red: the last 25% of each scale
        static float ListRange { get { return Limits.ListFull; } }
        static float TrimRange { get { return Limits.TrimFull; } }
        static float LevelFull { get { return Limits.LevelFull; } }
        static float ListDanger { get { return 0.75f * Limits.ListFull; } }
        static float TrimDanger { get { return 0.75f * Limits.TrimFull; } }
        static float LevelDanger { get { return 0.8f * Limits.LevelFull; } }   // the bar's red band: top 20%

        // ---- the scales that apply to this unit now
        // the mod's (Limits) with Alternative Damage Control on, the game's otherwise: list - water over the deck at 45°, trim - a
        // flooded ship sinks at 8°: as the mod's dials, those points are where the red zone starts (75% of the scale);
        // submarines: list and trim flood nothing (the game's rules are for surface ships only), they sink at 50% water - always
        static Type _tSub; static FieldInfo _compBase2;
        static SolidColorBrush _bDanger, _bDim;   // made on first use (static colours of the other part files may not be set up yet)
        static SolidColorBrush BDanger { get { return _bDanger ?? (_bDanger = new SolidColorBrush(CDanger)); } }
        static SolidColorBrush BDimText { get { return _bDim ?? (_bDim = new SolidColorBrush(CDim)); } }
        static SolidColorBrush _bWhite, _bPastMuster;
        static SolidColorBrush BPastMuster { get { return _bPastMuster ?? (_bPastMuster = new SolidColorBrush(CPastMuster)); } }
        static SolidColorBrush BWhite { get { return _bWhite ?? (_bWhite = new SolidColorBrush(CWhite)); } }

        static bool IsSub(object comp)
        {
            try
            {
                if (_compBase2 == null) _compBase2 = comp.GetType().GetField("_baseObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var bo = _compBase2 == null ? null : _compBase2.GetValue(comp);
                if (bo == null) return false;
                if (_tSub == null) _tSub = bo.GetType().Assembly.GetType("SeaPower.Submarine");
                return _tSub != null && _tSub.IsInstanceOfType(bo);
            }
            catch { return false; }
        }

        /// <summary>List: the dial's full scale; its red zone (faster flooding) from 75%.</summary>
        static float ListScale(object comp)
        {
            float l, t;   // ships with exact deck points: the red zone starts where a deck point touches the flat sea
            if (ModelOn() && SubmergedFlooding.TouchAngles(comp, out l, out t)) return l / Limits.FloodFrom;
            return ModelOn() ? Limits.ListFull : 45f / Limits.FloodFrom;
        }
        static float TrimScale(object comp)
        {
            float l, t;
            if (ModelOn() && SubmergedFlooding.TouchAngles(comp, out l, out t)) return t / Limits.FloodFrom;
            return ModelOn() ? Limits.Trim(DcTeams.ShipLength(comp)) : 8f / Limits.FloodFrom;
        }
        /// <summary>Water in the ship (% of the hull) at which it sinks.</summary>
        public static float SinkAt(object comp) { return IsSub(comp) ? 50f : ModelOn() ? Limits.LevelFull : 40f; }

        static PropertyInfo _compFloodPct, _compIntPct, _scFireStateProp;
        static MethodInfo _hullConv, _floodConv; static object _hullConvObj, _floodConvObj;
        static float _shownList; static WeakReference _shownListComp = new WeakReference(null);   // the list on the dial (smoothed): for the tooltips
        static FieldInfo _compBaseObj;

        static string N(float v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        static string P(float r, float phiDeg)
        {
            double a = phiDeg * Math.PI / 180.0;
            return N(DialCx + r * (float)Math.Sin(a)) + "," + N(DialCy - r * (float)Math.Cos(a));
        }

        /// <summary>A ring segment between radii r0..r1 from dial angle a0 to a1 (degrees from straight up, clockwise).</summary>
        static string Band(float r0, float r1, float a0, float a1)
        {
            string large = Math.Abs(a1 - a0) > 180f ? "1" : "0";
            return "M" + P(r1, a0) + " A" + N(r1) + "," + N(r1) + " 0 " + large + " 1 " + P(r1, a1) +
                   " L" + P(r0, a1) + " A" + N(r0) + "," + N(r0) + " 0 " + large + " 0 " + P(r0, a0) + " Z";
        }

        static Brush Res(FrameworkElement where, string key, Color fallback)
        {
            try { var b = where == null ? null : where.TryFindResource(key) as Brush; if (b != null) return b; } catch { }
            return new SolidColorBrush(fallback);
        }

        static Brush StageBrush(FrameworkElement where, int stage)
        {
            switch (stage)
            {
                case 1: return Res(where, "Brush.TextBox.ForegroundFocus", Color.FromRgb(214, 218, 222));
                case 2: return Res(where, "Brush.TextBox.ForegroundEmphasis", COrange);
                case 3: return Res(where, "Brush.TextBox.ForegroundStrongEmphasis", CRed);
                case 4: return Res(where, "Brush.TextBox.ForegroundReallyStrongEmphasis", CRed);
                default: return Res(where, "Brush.TextBox.Foreground", CWhite);
            }
        }

        static ColumnDefinition ColDef(float w, GridUnitType t) { return new ColumnDefinition { Width = new GridLength(w, t) }; }

        // ------------------------------------------------------------ building

        static Extras AddExtras(FrameworkElement view, Grid summary, Grid host, Border styleCard, List<DependencyObject> all)
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (_compFloodPct == null)
            {
                var asm = _vmType.Assembly;
                var tComp = asm.GetType("SeaPower.Compartments");
                _compFloodPct = tComp.GetProperty("FloodingPercentage", inst);
                _compBaseObj = tComp.GetField("_baseObject", inst);
                _compIntPct = tComp.GetProperty("IntegrityPercentage", inst);
                FindConv(asm, "IntegrityToDamageControlConverter", out _hullConv, out _hullConvObj);
                FindConv(asm, "FloodingPercentageToDamageControlStringConverter", out _floodConv, out _floodConvObj);
                _scFireStateProp = asm.GetType("SeaPower.SystemCompartment").GetProperty("FireState", inst);
            }
            var x = new Extras { Res = view };

            // ---- "Fires:" under "Hull Damage:" / "Flooding:" (the card in the last column of the summary grid)
            Border hullCard = null;
            int n = VisualTreeHelper.GetChildrenCount(summary);
            for (int k = 0; k < n; k++)
            {
                var b = VisualTreeHelper.GetChild(summary, k) as Border;
                if (b != null && Grid.GetColumn(b) == 4) { hullCard = b; break; }
            }
            var inner = hullCard == null ? null : hullCard.Child as Grid;
            if (inner != null && inner.RowDefinitions.Count == 2)
            {
                Grid tmpl = null;
                int m = VisualTreeHelper.GetChildrenCount(inner);
                for (int k = 0; k < m; k++) { var g = VisualTreeHelper.GetChild(inner, k) as Grid; if (g != null && Grid.GetRow(g) == 1) tmpl = g; }
                TextBlock tLabel = null, tValue = null;
                if (tmpl != null)
                {
                    int c = VisualTreeHelper.GetChildrenCount(tmpl);
                    for (int k = 0; k < c; k++)
                    {
                        var t = VisualTreeHelper.GetChild(tmpl, k) as TextBlock;
                        if (t == null) continue;
                        if (Grid.GetColumn(t) == 0) tLabel = t; else if (Grid.GetColumn(t) == 2) tValue = t;
                    }
                }
                x.FireRowDef = new RowDefinition { Height = new GridLength(1f, GridUnitType.Star) };
                inner.RowDefinitions.Add(x.FireRowDef);
                var row = new Grid { Margin = tmpl != null ? tmpl.Margin : new Thickness(0f, 4f, 0f, 0f) };
                row.ColumnDefinitions.Add(ColDef(0f, GridUnitType.Auto));
                row.ColumnDefinitions.Add(ColDef(10f, GridUnitType.Pixel));
                row.ColumnDefinitions.Add(ColDef(1f, GridUnitType.Star));
                Grid.SetRow(row, 2);
                var label = new TextBlock { Text = "Fires:", VerticalAlignment = VerticalAlignment.Center };
                if (tLabel != null) { label.FontSize = tLabel.FontSize; }
                var value = new TextBlock { Text = "None", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                if (tValue != null) { value.Padding = tValue.Padding; value.Background = tValue.Background; value.FontSize = tValue.FontSize; }
                Grid.SetColumn(value, 2);
                row.Children.Add(label);
                row.Children.Add(value);
                inner.Children.Add(row);
                x.HullInner = inner; x.FireRow = row; x.FireValue = value;
                for (int k = 0; k < m; k++)
                {
                    var g = VisualTreeHelper.GetChild(inner, k) as Grid;
                    if (g == null || g == row) continue;
                    int r = Grid.GetRow(g), cc = VisualTreeHelper.GetChildrenCount(g);
                    for (int q = 0; q < cc; q++)
                    {
                        var t = VisualTreeHelper.GetChild(g, q) as TextBlock;
                        if (t == null || Grid.GetColumn(t) != 2) continue;
                        if (r == 0) x.HullVal = t; else if (r == 1) x.FloodVal = t;
                    }
                }
            }
            else Trace("extras: Hull Damage card not found");
            if (hullCard != null)
            {
                // the tooltip of the card: every hit on the ship (HitLog), made when the mouse comes
                x.HullCard = hullCard;
                var holder = new Border { Background = new SolidColorBrush(Color.FromRgb(30, 34, 40)), BorderBrush = new SolidColorBrush(Color.FromRgb(90, 98, 110)), BorderThickness = new Thickness(1f) };
                try
                {
                    ToolTipService.SetToolTip(hullCard, holder);
                    ToolTipService.SetPlacement(hullCard, PlacementMode.Left);
                    ToolTipService.SetShowDuration(hullCard, int.MaxValue);
                }
                catch (Exception e) { Trace("hit log tip: " + e.Message); }
                hullCard.MouseEnter += (s2, e2) => { try { holder.Child = HitLogTip(x.Comp); } catch (Exception ex) { Trace("hit log: " + ex.Message); } };
            }

            // ---- team cards: free teams and knocked-out teams as icons (each team is shown once on the tab:
            //      free ones here, the others in their sections)
            try { AddTeamCards(summary, x); } catch (Exception e) { Trace("team cards: " + e.Message); }

            // ---- list / trim card: right of the ship's profile picture (under "Hull Damage / Flooding / Fires")
            if (host != null)
            {
                var card = SideCard(styleCard);
                card.HorizontalAlignment = HorizontalAlignment.Right;   // its own width
                Grid.SetColumn(card, 2);
                var rowp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                rowp.Children.Add(Dial("LIST", "PORT", "STBD", ListRange, ListDanger, out x.ListRot, out x.ListVal, out x.ListRed));
                rowp.Children.Add(new Border { Width = 6f });
                var trimDial = Dial("TRIM", "STERN", "BOW", TrimRange, TrimDanger, out x.TrimRot, out x.TrimVal, out x.TrimRed);
                // no tooltip of its own: the card's flooding plan shows over all three gauges (with the list / trim limits)
                rowp.Children.Add(trimDial);
                rowp.Children.Add(new Border { Width = 8f });
                rowp.Children.Add(LevelBar(x));
                card.Child = rowp;
                // its tooltip: the flooding schematic (RenderSchematic)
                host.Children.Add(card);
                x.ListHost = host; x.ListCard = card;
            }
            else Trace("extras: profile picture grid not found");
            return x;
        }

        /// <summary>
        /// The first two summary cards. "Damage Control Teams": the game's value and "Available / Total" hidden; ours - the free teams
        /// and the dead ones. "Teams Knocked Out" becomes "In Transfer": the teams on the move and the recovering ones,
        /// each with its time left (its title and the "Available In" line replaced, the value hidden).
        /// </summary>
        static void AddTeamCards(Grid summary, Extras x)
        {
            int n = VisualTreeHelper.GetChildrenCount(summary);
            for (int k = 0; k < n; k++)
            {
                var b = VisualTreeHelper.GetChild(summary, k) as Border;
                if (b == null) continue;
                int col = Grid.GetColumn(b);
                if (col != 0 && col != 1) continue;
                var inner = b.Child as Grid;
                if (inner == null) continue;
                TextBlock title = null, value = null, label = null;
                int m = VisualTreeHelper.GetChildrenCount(inner);
                for (int j = 0; j < m; j++)
                {
                    var t = VisualTreeHelper.GetChild(inner, j) as TextBlock;
                    if (t == null) continue;
                    int r = Grid.GetRow(t);
                    if (r == 0) title = t; else if (r == 1) value = t; else if (r == 2) label = t;
                }
                if (value == null) continue;
                var icons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = value.Margin, MinHeight = 24f };
                Grid.SetRow(icons, 1);
                Hide(x, value);
                Add(x, inner, icons);
                if (col == 0)
                {
                    x.FreeIcons = icons;
                    if (label != null)
                    {
                        Hide(x, label);
                        x.FreeLabel = Copy(label, "", 2);
                        Add(x, inner, x.FreeLabel);
                    }
                }
                else
                {
                    x.MovingIcons = icons;
                    TextBlock ours = null;
                    if (title != null) { Hide(x, title); ours = Copy(title, "In Transfer", 0); Add(x, inner, ours); }
                    if (label != null) Hide(x, label);
                    // with Alternative Damage Control off the card is the game's own again ("Teams Knocked Out")
                    x.GameCard2 = new UIElement[] { title, value, label };
                    x.OurCard2 = new UIElement[] { ours, icons };
                }
            }
        }

        static void Hide(Extras x, UIElement e) { e.Visibility = Visibility.Collapsed; x.Hidden.Add(e); }
        static void Add(Extras x, Panel p, UIElement e) { p.Children.Add(e); x.Added.Add(new KeyValuePair<Panel, UIElement>(p, e)); }

        /// <summary>A text block in the place and look of a game's one.</summary>
        static TextBlock Copy(TextBlock like, string text, int row)
        {
            var t = new TextBlock { Text = text, HorizontalAlignment = like.HorizontalAlignment, TextAlignment = like.TextAlignment, Margin = like.Margin,
                                    FontSize = like.FontSize, Foreground = like.Foreground };
            Grid.SetRow(t, row);
            return t;
        }

        static TextBlock _unused;
        static FrameworkElement BigIcon(int task, string tip, string under) { TextBlock t; return BigIcon(task, tip, under, out t, null); }

        static FrameworkElement BigIcon(int task, string tip, string under, out TextBlock time, object team = null)
        {
            var vb = new Viewbox { Width = 22f, Height = 22f, Child = Icon(task, team) };
            FrameworkElement e = vb;
            time = null;
            if (under != null)
            {
                var st = new StackPanel { Orientation = Orientation.Vertical };
                st.Children.Add(vb);
                time = new TextBlock { Text = under, FontSize = 10f, Foreground = new SolidColorBrush(CDim), HorizontalAlignment = HorizontalAlignment.Center };
                st.Children.Add(time);
                e = st;
            }
            e.Margin = new Thickness(2f, 0f, 2f, 0f);
            if (tip != null) try { ToolTipService.SetToolTip(e, tip); } catch { }
            return e;
        }

        static readonly Color CPastMuster = Color.FromRgb(236, 212, 110);   // the team is walking already: cancelling takes time

        static string Clock(float sec)
        {
            int s = (int)Math.Ceiling(sec);
            return (s / 60) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The two team cards. Rebuilt only when the teams change (not every second: an open menu would close with its icon);
        /// the times under the icons are updated in place - yellowish in "In Transfer" once the muster time is over (a cancel then
        /// takes time).
        /// </summary>
        static void RenderTeamCards(object comp, object vm, Extras x, string[] sectionNames)
        {
            if (x.FreeIcons == null && x.MovingIcons == null) return;
            var teams = DcTeams.Snapshot(comp);
            int[] counts = _compTeams.GetValue(comp, null) as int[];
            int free = counts != null && counts.Length > 5 ? counts[5] : 0;
            bool manual = Manual(comp);
            var sb = new System.Text.StringBuilder();
            foreach (var t in teams) sb.Append(t.State).Append(':').Append(t.Section).Append(':').Append(t.Ref.GetHashCode()).Append(':').Append((int)(t.Health * 10f)).Append(DcTeams.FatigueLevel(t.Fatigue)).Append(' ');
            sb.Append('|').Append(free).Append(manual ? 'm' : 'a').Append(DcTeams.FatigueMin > 0f ? 'f' : 'n');
            string sig = sb.ToString();

            if (sig != x.TeamsSig && !(x.TeamsSig != null && MenusOpen()))
            {
                x.TeamsSig = sig;
                x.Clocks.Clear();
                Func<int, string> name = i => i < 0 ? "the reserve" : (sectionNames != null && i < sectionNames.Length && !string.IsNullOrEmpty(sectionNames[i]) ? sectionNames[i] : "section " + (i + 1));

                // reserve: free teams and dead ones only - teams on their way back and recovering ones are shown in In Transfer only
                if (x.FreeIcons != null)
                {
                    var ch = x.FreeIcons.Children;
                    ch.Clear();
                    int alive = 0, all = teams.Count;
                    var pool = teams.FindAll(t => t.State == (int)DcTeams.St.Pool);
                    for (int k = 0; k < free; k++)
                    {
                        var e = BigIcon(TaskIdle, null, null, out _unused, k < pool.Count ? pool[k].Ref : null);
                        if (k < pool.Count) { var r = pool[k].Ref; LiveTip(e, () => RefTip(comp, r, sectionNames)); }
                        else try { ToolTipService.SetToolTip(e, "A free damage control team, waiting in the reserve"); } catch { }
                        ch.Add(e);
                    }
                    foreach (var t in teams)
                    {
                        if (t.State == (int)DcTeams.St.Dead) { var r = t.Ref; var e = BigIcon(IconDead, null, null); LiveTip(e, () => RefTip(comp, r, sectionNames)); ch.Add(e); }
                        if (t.State != (int)DcTeams.St.Dead) alive++;
                    }
                    if (x.FreeLabel != null) x.FreeLabel.Text = DcTeams.FatigueMin > 0f ? "Resting" : "In reserve";
                }

                // on the move and recovering, with the time left
                if (x.MovingIcons != null)
                {
                    var ch = x.MovingIcons.Children;
                    ch.Clear();
                    foreach (var t in teams)
                    {
                        if (t.State != (int)DcTeams.St.Moving) continue;
                        TextBlock clock;
                        var e = BigIcon(IconTransit, null, Clock(t.Left), out clock, t.Ref);
                        var frame = new Border { Child = e, CornerRadius = new CornerRadius(2f), Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) };
                        var r = t.Ref;
                        LiveTip(frame, () => RefTip(comp, r, sectionNames));
                        Clickable(frame, manual ? (Func<ContextMenu>)(() => CancelMenu(comp, r)) : null, r);
                        ch.Add(frame);
                        x.Clocks.Add(new KeyValuePair<object, TextBlock>(t.Ref, clock));
                    }
                    foreach (var t in teams)
                    {
                        if (t.State != (int)DcTeams.St.Injured) continue;
                        TextBlock clock;
                        var e = BigIcon(IconInjured, null, Clock(t.Left), out clock, t.Ref);
                        var r = t.Ref;
                        LiveTip(e, () => RefTip(comp, r, sectionNames));
                        ch.Add(e);
                        x.Clocks.Add(new KeyValuePair<object, TextBlock>(t.Ref, clock));
                    }
                }
            }

            // times, in place
            foreach (var kv in x.Clocks)
            {
                foreach (var t in teams)
                {
                    if (!ReferenceEquals(t.Ref, kv.Key)) continue;
                    string text = Clock(t.Left);
                    if (kv.Value.Text != text) kv.Value.Text = text;
                    bool walking = t.State == (int)DcTeams.St.Moving && (t.Back || t.Total - t.Left > DcTeams.Muster);
                    SetBrush(kv.Value, walking ? BPastMuster : BDimText);
                    break;
                }
            }
        }

        static readonly Color CDial = Color.FromRgb(36, 40, 46), CScale = Color.FromRgb(214, 218, 222), CDanger = Color.FromRgb(200, 62, 54),
                              CNeedle = Color.FromRgb(250, 250, 250), CDim = Color.FromRgb(150, 156, 162);

        static StackPanel Dial(string title, string left, string right, float range, float danger, out RotateTransform rot, out TextBlock val, out Action<float, float> setRed)
        {
            var stack = new StackPanel { Orientation = Orientation.Vertical };
            stack.Children.Add(new TextBlock { Text = title, FontSize = 11f, Foreground = new SolidColorBrush(CDim), HorizontalAlignment = HorizontalAlignment.Center });
            var cv = new Canvas { Width = DialW, Height = DialH };
            var ch = cv.Children;
            float dz = danger / range * 90f;
            ch.Add(Shape("M" + P(DialR, -90f) + " A" + N(DialR) + "," + N(DialR) + " 0 0 1 " + P(DialR, 90f) + " Z", CDial));   // dial face
            var bandL = Shape("M0,0", CDanger); var bandM = Shape("M0,0", CBand); var bandR = Shape("M0,0", CDanger);   // a bit darker than the needle
            var greyP = new Path { Stroke = new SolidColorBrush(CScale), StrokeThickness = 1.2f };
            var redP = new Path { Stroke = new SolidColorBrush(CDanger), StrokeThickness = 1.2f };
            ch.Add(bandL); ch.Add(bandM); ch.Add(bandR); ch.Add(greyP); ch.Add(redP);
            // the red zones start at dzL (left) / dzR (right), degrees of the dial from the top; ticks every 11.25 deg and on both edges
            float lastL = -1f, lastR = -1f;
            setRed = (dzL, dzR) =>
            {
                dzL = Math.Max(0f, Math.Min(90f, dzL)); dzR = Math.Max(0f, Math.Min(90f, dzR));
                if (Math.Abs(dzL - lastL) < 0.3f && Math.Abs(dzR - lastR) < 0.3f) return;
                lastL = dzL; lastR = dzR;
                bandL.Data = new StreamGeometry(Band(DialR - 9f, DialR - 2f, -90f, -dzL));
                bandM.Data = new StreamGeometry(Band(DialR - 9f, DialR - 2f, -dzL, dzR));
                bandR.Data = new StreamGeometry(Band(DialR - 9f, DialR - 2f, dzR, 90f));
                var grey = new System.Text.StringBuilder(); var red = new System.Text.StringBuilder();
                var angles = new List<float>();
                for (int k = -8; k <= 8; k++) angles.Add(k * 11.25f);
                angles.Add(-dzL); angles.Add(dzR);
                foreach (float a in angles)
                {
                    bool isRed = a <= -dzL + 0.01f || a >= dzR - 0.01f;
                    bool major = Math.Abs(a % 22.5f) < 0.01f || Math.Abs(Math.Abs(a) - dzL) < 0.01f || Math.Abs(a - dzR) < 0.01f;
                    (isRed ? red : grey).Append("M" + P(DialR - (major ? 15f : 13f), a) + " L" + P(DialR - 9f, a) + " ");
                }
                greyP.Data = grey.Length > 0 ? new StreamGeometry(grey.ToString()) : null;
                redP.Data = red.Length > 0 ? new StreamGeometry(red.ToString()) : null;
            };
            setRed(dz, dz);
            var needle = new Path { Data = new StreamGeometry("M" + N(DialCx) + "," + N(DialCy) + " L" + P(DialR - 4f, 0f)), Stroke = new SolidColorBrush(CNeedle), StrokeThickness = 2f };
            rot = new RotateTransform(0f) { CenterX = DialCx, CenterY = DialCy };
            needle.RenderTransform = rot;
            ch.Add(needle);
            // the axis: two half circles (an almost-closed single arc put its centre 2.5 px off)
            ch.Add(Shape("M" + N(DialCx - 3.5f) + "," + N(DialCy) + " A3.5,3.5 0 1 1 " + N(DialCx + 3.5f) + "," + N(DialCy) +
                         " A3.5,3.5 0 1 1 " + N(DialCx - 3.5f) + "," + N(DialCy) + " Z", CNeedle));
            stack.Children.Add(cv);

            var row = new Grid { Margin = new Thickness(0f, 2f, 0f, 0f) };
            row.ColumnDefinitions.Add(ColDef(1f, GridUnitType.Star));
            row.ColumnDefinitions.Add(ColDef(0f, GridUnitType.Auto));
            row.ColumnDefinitions.Add(ColDef(1f, GridUnitType.Star));
            var l = new TextBlock { Text = left, FontSize = 10f, Foreground = new SolidColorBrush(CDim), VerticalAlignment = VerticalAlignment.Center };
            var r = new TextBlock { Text = right, FontSize = 10f, Foreground = new SolidColorBrush(CDim), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            val = new TextBlock { Text = "0°", FontSize = 12f, MinWidth = 30f, TextAlignment = TextAlignment.Center };
            var box = new Border { Child = val, Background = new SolidColorBrush(CDial), Padding = new Thickness(4f, 0f, 4f, 0f), Margin = new Thickness(4f, 0f, 4f, 0f) };
            Grid.SetColumn(box, 1); Grid.SetColumn(r, 2);
            row.Children.Add(l); row.Children.Add(box); row.Children.Add(r);
            stack.Children.Add(row);
            return stack;
        }

        /// <summary>
        /// Water in the whole ship: a 16 x 56 bar. Layers, back to front: dark background, the water (blue, from the bottom),
        /// scale marks on both sides every 1/8 of the height (red bands in the top 20%), the frame.
        /// </summary>
        static FrameworkElement LevelBar(Extras x)
        {
            const float w = 16f, h = 56f, inner = h - 2f;
            var stack = new StackPanel { Orientation = Orientation.Vertical };
            stack.Children.Add(new TextBlock { Text = "LEVEL", FontSize = 11f, Foreground = new SolidColorBrush(CDim), HorizontalAlignment = HorizontalAlignment.Center });
            var bar = new Grid { Width = w, Height = h, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0f, 2f, 0f, 0f) };  // 2 px lower top edge
            bar.Children.Add(new Border { Background = new SolidColorBrush(CDial) });
            x.LevelHeight = inner;
            x.LevelFill = new Border { Height = 0f, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1f), Background = new SolidColorBrush(CBlueWater) };
            bar.Children.Add(x.LevelFill);

            var marks = new Canvas { Width = w, Height = h };
            float red = 1f + inner * 0.2f;   // the top 20% (y grows downwards)
            var grey = new System.Text.StringBuilder();
            for (int k = 1; k < 8; k++)
            {
                float y = 1f + inner * k / 8f;
                if (y <= red) continue;     // inside the red band
                grey.Append("M1," + N(y) + " L3.5," + N(y) + " M12.5," + N(y) + " L15," + N(y) + " ");
            }
            marks.Children.Add(new Path { Data = new StreamGeometry(grey.ToString()), Stroke = new SolidColorBrush(CScale), StrokeThickness = 1f });
            marks.Children.Add(Shape("M1,1 L4,1 L4," + N(red) + " L1," + N(red) + " Z M12,1 L15,1 L15," + N(red) + " L12," + N(red) + " Z", CDanger));
            bar.Children.Add(marks);

            bar.Children.Add(new Border { BorderBrush = new SolidColorBrush(CDim), BorderThickness = new Thickness(1f) });   // frame on top
            stack.Children.Add(bar);
            x.LevelVal = new TextBlock { Text = "0%", FontSize = 10f, Foreground = new SolidColorBrush(CDim), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0f, 2f, 0f, 0f) };
            stack.Children.Add(x.LevelVal);
            return stack;
        }

        // ------------------------------------------------------------ updating

        static float Angle(float a) { a %= 360f; if (a > 180f) a -= 360f; else if (a < -180f) a += 360f; return a; }

        static void RenderExtras(object comp, object vm, Extras x, string[] sectionNames)
        {
            try { RenderTeamCards(comp, vm, x, sectionNames); } catch (Exception e) { Trace("RenderTeamCards: " + e.Message); }
            int mode = ModelOn() ? 1 : 0;
            if (mode != x.Card2Mode && x.GameCard2 != null)
            {
                x.Card2Mode = mode;
                foreach (var e in x.GameCard2) if (e != null) e.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
                foreach (var e in x.OurCard2) if (e != null) e.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
            }
            // Fires: the worst stage on board, and how many sections burn
            if (x.FireValue != null)
            {
                var sys = _compSys.GetValue(comp) as Array;
                int count = (int)_compCount.GetValue(comp), worst = 0, burning = 0;
                for (int i = 0; sys != null && i < count && i < sys.Length; i++)
                {
                    int st = 0;
                    try { st = Convert.ToInt32(_scFireStateProp.GetValue(sys.GetValue(i), null)); } catch { }
                    if (st > 0) burning++;
                }
                // the fires' strength is what counts (the sum over the sections); their number is only added in brackets
                worst = ContactIntel.FireStage(ContactIntel.FireTotal(comp));
                string text = worst == 0 ? "None" : (worst == 1 ? "Minor" : worst == 2 ? "Moderate" : "Severe") + (burning > 1 ? " (" + burning + ")" : "");
                string sig = text + worst;
                if (sig != x.FireSig)
                {
                    x.FireValue.Text = text;
                    x.FireValue.Foreground = StageBrush(x.Res, worst == 0 ? 0 : worst == 1 ? 1 : worst == 2 ? 2 : 3);
                    x.FireSig = sig;
                }
            }

            try { RenderHullFlood(comp, x); } catch (Exception e) { Trace("RenderHullFlood: " + e.Message); }

            // list / trim from the hull's attitude, smoothed (game time; a paused game keeps them)
            if (x.ListRot != null)
            {
                var bo = _compBaseObj == null ? null : _compBaseObj.GetValue(comp) as UnityEngine.Component;
                if (bo != null)
                {
                    var e = bo.transform.localEulerAngles;
                    float roll = Angle(e.z), pitch = Angle(e.x);
                    double t = 0;
                    try { t = Convert.ToDouble(_gameTime.GetValue(null, null), CultureInfo.InvariantCulture); } catch { }
                    if (!ReferenceEquals(x.Comp, comp)) { x.Comp = comp; x.T = -1; x.FireSig = null; }   // another unit: at once
                    if (x.T < 0) { x.List = roll; x.Trim = pitch; }
                    else if (t > x.T)
                    {
                        float k = 1f - (float)Math.Exp(-(t - x.T) / 0.5);   // settles within 1-2 s: takes out the rolling, not the list
                        x.List += (roll - x.List) * k; x.Trim += (pitch - x.Trim) * k;
                    }
                    if (t != x.T) x.T = t;
                    _shownList = x.List; _shownListComp = new WeakReference(comp);
                    // +z = port side down (needle to the left); trim: +x turns the needle to the right (checked in game)
                    float listRange = ListScale(comp), trimRange = TrimScale(comp);   // trim: this ship's limit (by its length)
                    bool sub = IsSub(comp);                                            // nothing floods from list / trim
                    // the limits: the list / trim at which a deck point touches the flat sea (ships with exact points; SubmergedFlooding.Margins),
                    // smoothed; the scales and the red zones follow from them (the larger limit of a gauge at 75% of its scale), else 75% of the scale
                    float redLP = Limits.FloodFrom * listRange, redLS = redLP, redTS = Limits.FloodFrom * trimRange, redTB = redTS;
                    if (ModelOn() && !sub)
                    {
                        float mp, ms, mt, mb;
                        if (SubmergedFlooding.Margins(comp, x.List, x.Trim, out mp, out ms, out mt, out mb))
                        {
                            var nv = new[] { mp, ms, mt, mb };
                            if (x.Lim == null || !ReferenceEquals(x.LimComp, comp)) { x.Lim = nv; x.LimComp = comp; }
                            else for (int q = 0; q < 4; q++) x.Lim[q] += (nv[q] - x.Lim[q]) * 0.2f;
                            redLP = x.Lim[0]; redLS = x.Lim[1]; redTS = x.Lim[2]; redTB = x.Lim[3];
                            listRange = Math.Max(Math.Max(redLP, redLS), 3f) / Limits.FloodFrom;
                            trimRange = Math.Max(Math.Max(redTS, redTB), 0.5f) / Limits.FloodFrom;
                        }
                    }
                    x.ListRed(Math.Min(1f, redLP / listRange) * 90f, Math.Min(1f, redLS / listRange) * 90f);
                    x.TrimRed(Math.Min(1f, redTS / trimRange) * 90f, Math.Min(1f, redTB / trimRange) * 90f);
                    x.ListRot.Angle = -Math.Max(-1f, Math.Min(1f, x.List / listRange)) * 90f;
                    x.TrimRot.Angle = Math.Max(-1f, Math.Min(1f, x.Trim / trimRange)) * 90f;
                    SetText(x.ListVal, Math.Abs(x.List).ToString(Math.Abs(x.List) < 10f ? "0.0" : "0", CultureInfo.InvariantCulture) + "°");
                    SetText(x.TrimVal, Math.Abs(x.Trim).ToString("0.0", CultureInfo.InvariantCulture) + "°");
                    SetBrush(x.ListVal, !sub && (x.List >= 0f ? x.List >= redLP : -x.List >= redLS) ? BDanger : StageBrush(x.Res, 0));
                    SetBrush(x.TrimVal, !sub && (x.Trim >= 0f ? x.Trim >= redTB : -x.Trim >= redTS) ? BDanger : StageBrush(x.Res, 0));
                }
            }

            try { RenderSchematic(comp, x, sectionNames); } catch (Exception e) { Trace("RenderSchematic: " + e.Message); }

            // water in the whole ship
            if (x.LevelFill != null)
            {
                float fp = 0f;
                try { fp = Convert.ToSingle(_compFloodPct.GetValue(comp, null), CultureInfo.InvariantCulture); } catch { }
                float sinkAt = SinkAt(comp);
                x.LevelFill.Height = Math.Max(0f, Math.Min(1f, fp / sinkAt)) * x.LevelHeight;
                SetText(x.LevelVal, Pct(Math.Min(100f, 100f * fp / sinkAt)));   // 0..100% of the scale: 100% = the top, where the ship starts to sink
                SetBrush(x.LevelVal, fp >= 0.8f * sinkAt ? BDanger : BDimText);
            }
        }

        static void FindConv(System.Reflection.Assembly asm, string name, out MethodInfo conv, out object obj)
        {
            conv = null; obj = null;
            try
            {
                Type t = null;
                foreach (var ty in asm.GetTypes()) if (ty.Name == name) { t = ty; break; }
                if (t == null) return;
                obj = Activator.CreateInstance(t, true);
                conv = t.GetMethod("Convert");
            }
            catch (Exception e) { Trace("FindConv " + name + ": " + e.Message); }
        }

        static string GameText(MethodInfo conv, object obj, float v)
        {
            try { return conv == null ? null : conv.Invoke(obj, new object[] { v, typeof(string), null, CultureInfo.InvariantCulture }) as string; }
            catch { return null; }
        }

        static string StripPct(string t)
        {
            int i = t == null ? -1 : t.LastIndexOf(" (", StringComparison.Ordinal);
            return i > 0 && t.EndsWith("%)", StringComparison.Ordinal) ? t.Substring(0, i) : t;
        }

        /// <summary>
        /// The top-right "Hull Damage" and "Flooding" values with the overall percentage: "Moderate (15%)".
        /// Hull: damage = 100 - integrity. With Alternative Damage Control the words change at 25 / 50 / 75% of damage (the game's own
        /// words: None, Slight, Moderate, Heavy and the game's last one above 75%). Flooding: the game's word and the water in the
        /// whole ship on the scale of the LEVEL gauge (0..100%).
        /// </summary>
        static readonly Brush BHeavy = new SolidColorBrush(COrange), BCritical = new SolidColorBrush(Color.FromRgb(236, 70, 56));

        /// <summary>The card's colours by class: the game's for 0-2, orange for Heavy, red for Structure Destroyed / Flooding Uncontrolled
        /// (the game's own last colour is a dark one that is hard to read).</summary>
        static Brush CardBrush(FrameworkElement where, int stage)
        {
            return stage >= 4 ? BCritical : stage == 3 ? BHeavy : StageBrush(where, stage);
        }

        static void RenderHullFlood(object comp, Extras x)
        {
            if (x.HullVal != null && _compIntPct != null)
            {
                float integ = 100f;
                try { integ = Convert.ToSingle(_compIntPct.GetValue(comp, null), CultureInfo.InvariantCulture); } catch { }
                float dmg = Math.Max(0f, Math.Min(100f, 100f - integ));
                int pct = (int)Math.Round(dmg);
                // the word is made here every tick (setting Text by hand replaces the game's binding, so it would stay as it was)
                string word = GameText(_hullConv, _hullConvObj, integ) ?? StripPct(x.HullVal.Text);
                bool model = ModelOn();
                if (model)
                {
                    // the game's words through its own converter: a typical integrity of each class
                    int stage = dmg < 1f ? 0 : dmg <= 25f ? 1 : dmg <= 50f ? 2 : dmg <= 75f ? 3 : 4;
                    float[] typical = { 100f, 85f, 50f, 10f, 0f };   // integrity inside each of the game's classes
                    string g = GameText(_hullConv, _hullConvObj, typical[stage]);
                    if (!string.IsNullOrEmpty(g)) word = g;
                    SetBrush(x.HullVal, CardBrush(x.Res, stage));
                }
                else
                {
                    // the game's classes (by integrity): 99 / 75 / 25 / 1
                    int stage = integ >= 99f ? 0 : integ >= 75f ? 1 : integ >= 25f ? 2 : integ >= 1f ? 3 : 4;
                    SetBrush(x.HullVal, CardBrush(x.Res, stage));
                }
                x.HullColored = true;
                SetText(x.HullVal, pct == 0 ? word : word + " (" + pct.ToString(CultureInfo.InvariantCulture) + "%)");
            }
            if (x.FloodVal != null && _compFloodPct != null)
            {
                float fp = 0f;
                try { fp = Convert.ToSingle(_compFloodPct.GetValue(comp, null), CultureInfo.InvariantCulture); } catch { }
                int pct = (int)Math.Round(Math.Min(100f, 100f * fp / SinkAt(comp)));   // the LEVEL gauge's scale
                // the game's classes (by its value): 1 / 10 / 20 / 30
                SetBrush(x.FloodVal, CardBrush(x.Res, fp >= 30f ? 4 : fp >= 20f ? 3 : fp >= 10f ? 2 : fp >= 1f ? 1 : 0));
                string fw = GameText(_floodConv, _floodConvObj, fp) ?? StripPct(x.FloodVal.Text);   // the game's word for the game's value
                SetText(x.FloodVal, pct == 0 ? fw : fw + " (" + pct.ToString(CultureInfo.InvariantCulture) + "%)");
            }
        }

        /// <summary>Text and brush only when they change (no new brush and no re-render every tick).</summary>
        static void SetText(TextBlock tb, string text) { if (tb != null && tb.Text != text) tb.Text = text; }
        static void SetBrush(TextBlock tb, Brush b) { if (tb != null && !ReferenceEquals(tb.Foreground, b)) tb.Foreground = b; }

        static readonly Color CBand = Color.FromRgb(160, 166, 174);   // dial scale band: grey, so the white needle stands out
        static readonly Color CBlueWater = Color.FromRgb(74, 150, 226);

        static void RemoveExtras(Extras x)
        {
            if (x == null) return;
            try { if (x.HullVal != null) { x.HullVal.Text = StripPct(x.HullVal.Text); if (x.HullColored) x.HullVal.ClearValue(TextBlock.ForegroundProperty); } } catch { }
            try { if (x.FloodVal != null) { x.FloodVal.Text = StripPct(x.FloodVal.Text); x.FloodVal.ClearValue(TextBlock.ForegroundProperty); } } catch { }
            try { if (x.HullInner != null && x.FireRow != null) x.HullInner.Children.Remove(x.FireRow); } catch { }
            try { if (x.HullInner != null && x.FireRowDef != null) x.HullInner.RowDefinitions.Remove(x.FireRowDef); } catch { }
            HideSchem(x);
            try { if (x.ListHost != null && x.ListCard != null) x.ListHost.Children.Remove(x.ListCard); } catch { }
            foreach (var kv in x.Added) try { kv.Key.Children.Remove(kv.Value); } catch { }
            foreach (var h in x.Hidden) try { h.Visibility = Visibility.Visible; } catch { }
            x.Added.Clear(); x.Hidden.Clear();
        }
    }
}
