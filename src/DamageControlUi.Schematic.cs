using System;
using System.Collections.Generic;
using System.Globalization;
using Noesis;

namespace AlternativeDamageModel
{
    /// <summary>
    /// The tooltip of the List / Trim / Level card (opens below it): the ship from above like the game's F10 damage panel, bow on
    /// the right, port side on top. Every compartment (5 sections × port / starboard) is a cell filled from its bottom edge to its
    /// water level, with the same trend arrows as the section's Flooding row and its water % under them (white, one colour). The cells are clipped by the hull's outline,
    /// so the bow cells come out pointed. Built once per card, updated in place 4 times a second while the tab is shown.
    /// </summary>
    static partial class DamageControlUi
    {
        sealed class Schematic
        {
            public Border Root;
            public int Count;
            public TextBlock Title, Limits; public CheckBox Pin;
            public Border[] Fill;          // [column * 2 + side]: port 0 (top), starboard 1 (bottom)
            public StackPanel[] Arrows;
            public TextBlock[] Value;
            public string[] Sig;
        }

        const float SchW = 236f, SchH = 76f, SchL = 3f, SchT = 3f, SchR = 233f, SchB = 73f;   // half the first size
        const float SchMid = (SchT + SchB) / 2f;
        static int[] _order = new int[0];               // sections left to right on the tab (set by Update)
        static Col[] _colsShown; static WeakReference _colsComp = new WeakReference(null);   // their trends (set by Update)

        static string HullTop()
        {
            // stern on the left (square, rounded corners), bow on the right (pointed)
            float bowStart = SchR - 46f;
            return "M" + N(SchL + 5f) + "," + N(SchT) + " L" + N(bowStart) + "," + N(SchT) +
                   " C" + N(bowStart + 26f) + "," + N(SchT) + " " + N(SchR - 5f) + "," + N(SchMid - 17f) + " " + N(SchR) + "," + N(SchMid) +
                   " C" + N(SchR - 5f) + "," + N(SchMid + 17f) + " " + N(bowStart + 26f) + "," + N(SchB) + " " + N(bowStart) + "," + N(SchB) +
                   " L" + N(SchL + 5f) + "," + N(SchB) + " Q" + N(SchL) + "," + N(SchB) + " " + N(SchL) + "," + N(SchB - 5f) +
                   " L" + N(SchL) + "," + N(SchT + 5f) + " Q" + N(SchL) + "," + N(SchT) + " " + N(SchL + 5f) + "," + N(SchT) + " Z";
        }

        static Schematic BuildSchematic(int count)
        {
            var s = new Schematic { Count = count, Fill = new Border[count * 2], Arrows = new StackPanel[count * 2], Value = new TextBlock[count * 2], Sig = new string[count * 2] };
            var cv = new Canvas { Width = SchW, Height = SchH };
            string hull = HullTop();

            // the cells, clipped by the hull
            var cells = new Canvas { Width = SchW, Height = SchH, Clip = new StreamGeometry(hull) };
            cells.Children.Add(At(new Border { Width = SchW, Height = SchH, Background = new SolidColorBrush(Color.FromRgb(26, 29, 34)) }, 0f, 0f));
            float cw = (SchR - SchL) / count, ch = (SchB - SchT) / 2f;
            for (int k = 0; k < count; k++)
                for (int side = 0; side < 2; side++)
                {
                    var fill = new Border { Width = cw, Height = 0f, Background = new SolidColorBrush(Color.FromArgb(200, 46, 110, 190)) };
                    cells.Children.Add(At(fill, SchL + k * cw, SchT + (side + 1) * ch));
                    s.Fill[k * 2 + side] = fill;
                }
            cv.Children.Add(cells);

            // bulkheads, the centre line, the hull on top
            var lines = new System.Text.StringBuilder("M" + N(SchL) + "," + N(SchMid) + " L" + N(SchR) + "," + N(SchMid));
            for (int k = 1; k < count; k++) lines.Append(" M" + N(SchL + k * cw) + "," + N(SchT) + " L" + N(SchL + k * cw) + "," + N(SchB));
            var grid = new Path { Data = new StreamGeometry(lines.ToString()), Stroke = new SolidColorBrush(Color.FromRgb(200, 204, 210)), StrokeThickness = 1f, Clip = new StreamGeometry(hull) };
            cv.Children.Add(grid);
            cv.Children.Add(new Path { Data = new StreamGeometry(hull), Stroke = new SolidColorBrush(Color.FromRgb(236, 238, 240)), StrokeThickness = 1.5f });

            // arrows and % in the middle of each cell (the bow cells: a bit to the left, where the cell is wide enough)
            for (int k = 0; k < count; k++)
                for (int side = 0; side < 2; side++)
                {
                    // a box the size of the cell (the bow cell: its wide part), the arrows and % centred in it
                    float bw = k == count - 1 ? cw * 0.7f : cw;
                    var box = new Grid { Width = bw, Height = ch };
                    // the % in the very middle, the arrows just above it (the number does not move when they come and go)
                    var arrows = new StackPanel { Orientation = Orientation.Horizontal, Height = 5f, HorizontalAlignment = HorizontalAlignment.Center,
                                                  VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0f, 0f, 0f, 21f) };
                    var val = new TextBlock { FontSize = 10f, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(CWhite), HorizontalAlignment = HorizontalAlignment.Center,
                                              VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Text = "0%" };
                    box.Children.Add(arrows);
                    box.Children.Add(val);
                    // the top row sat high: 6 px down, towards the centre line
                    if (side == 0) box.RenderTransform = new TranslateTransform(0f, 6f);
                    cv.Children.Add(At(box, SchL + k * cw, SchT + side * ch));
                    s.Arrows[k * 2 + side] = arrows; s.Value[k * 2 + side] = val;
                }

            var root = new StackPanel { Orientation = Orientation.Vertical };
            s.Title = new TextBlock { FontSize = 10f, Foreground = new SolidColorBrush(CDim), Margin = new Thickness(0f, 0f, 0f, 3f) };
            root.Children.Add(cv);
            s.Limits = new TextBlock { FontSize = 10f, Foreground = new SolidColorBrush(CDim), Margin = new Thickness(0f, 4f, 0f, 0f) };
            root.Children.Add(s.Limits);
            // the deck points of this ship in 3D, all the time (clicked: the panel is shown over the screen, not as a tooltip - tooltips take no clicks)
            s.Pin = new CheckBox { Content = "Show deck levels in 3D", FontSize = 11f, Foreground = new SolidColorBrush(Color.FromRgb(230, 232, 235)), Margin = new Thickness(0f, 5f, 0f, 0f) };
            root.Children.Add(s.Pin);
            s.Root = new Border { Child = root, Padding = new Thickness(6f, 4f, 6f, 6f), Background = new SolidColorBrush(Color.FromRgb(30, 33, 38)),
                                  BorderBrush = new SolidColorBrush(Color.FromRgb(70, 76, 86)), BorderThickness = new Thickness(1f), CornerRadius = new CornerRadius(4f) };
            return s;
        }

        static UIElement At(UIElement e, float x, float y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); return e; }

        static Path ArrowShape(bool up, int n, Color color)
        {
            string g = up ? "M0,7 L4,0 L8,7 Z" : "M0,0 L8,0 L4,7 Z";
            if (n > 1) g += up ? " M9,7 L13,0 L17,7 Z" : " M9,0 L17,0 L13,7 Z";
            return new Path { Data = new StreamGeometry(g), Fill = new SolidColorBrush(color), Stretch = Stretch.Fill, Width = n > 1 ? 12f : 6f, Height = 5f };
        }

        /// <summary>Puts the schematic on the card (once per card and number of sections) and brings its levels up to date.</summary>
        static bool _schemOpen;   // the schematic tooltip is open

        const float SchGap = 5f;   // between the plan and the list / trim card

        static Panel TopPanel(DependencyObject from)
        {
            Panel panel = null;
            for (DependencyObject p = from; p != null; p = VisualTreeHelper.GetParent(p))
            {
                var pn = p as Panel;
                bool itemsHost = false;
                try { itemsHost = pn != null && pn.IsItemsHost; } catch { }
                if (pn != null && !itemsHost && (pn is Grid || pn is Canvas)) panel = pn;
            }
            return panel;
        }

        /// <summary>The flooding plan under the list / trim card, in the screen's top panel (above everything), until the mouse leaves both.</summary>
        static void ShowSchem(Extras x)
        {
            try
            {
                if (x.SchemShown || x.Schem == null || x.SchemPanel == null || x.ListCard == null) return;
                var root = x.Schem.Root;
                var old = VisualTreeHelper.GetParent(root) as Panel;
                if (old != null) old.Children.Remove(root);
                float w = root.ActualWidth, h = root.ActualHeight;
                if (w < 10f || h < 10f) { try { root.Measure(new Size(10000f, 10000f)); w = root.DesiredSize.Width; h = root.DesiredSize.Height; } catch { } }
                if (w < 10f) w = 252f;
                if (h < 10f) h = 150f;
                var at = x.ListCard.TranslatePoint(new Point(0f, 0f), x.SchemPanel);
                // above the card, 5 px clear of it, centred on it
                float left = Math.Max(0f, at.X + x.ListCard.ActualWidth / 2f - w / 2f), top = Math.Max(0f, at.Y - SchGap - h);
                var grid = x.SchemPanel as Grid;
                if (grid != null)
                {
                    // anchored by its bottom edge: the height need not be known (a measured one came out too big)
                    float panelH = grid.ActualHeight;
                    root.HorizontalAlignment = HorizontalAlignment.Left;
                    if (panelH > 10f)
                    {
                        root.VerticalAlignment = VerticalAlignment.Bottom;
                        root.Margin = new Thickness(left, 0f, 0f, Math.Max(0f, panelH - at.Y + SchGap));
                    }
                    else
                    {
                        root.VerticalAlignment = VerticalAlignment.Top;
                        root.Margin = new Thickness(left, top, 0f, 0f);
                    }
                    Grid.SetRow(root, 0); Grid.SetColumn(root, 0);
                    Grid.SetRowSpan(root, Math.Max(1, grid.RowDefinitions.Count)); Grid.SetColumnSpan(root, Math.Max(1, grid.ColumnDefinitions.Count));
                }
                else { Canvas.SetLeft(root, left); Canvas.SetTop(root, top); }
                Panel.SetZIndex(root, 30000);
                x.SchemPanel.Children.Add(root);
                x.SchemShown = true; x.SchemOff = 0; _schemOpen = true;
            }
            catch (Exception e) { Trace("ShowSchem: " + e.Message); }
        }

        static void HideSchem(Extras x)
        {
            try
            {
                if (x == null) return;
                if (x.Schem != null) { var pp = VisualTreeHelper.GetParent(x.Schem.Root) as Panel; if (pp != null) pp.Children.Remove(x.Schem.Root); }
            }
            catch (Exception e) { Trace("HideSchem: " + e.Message); }
            x.SchemShown = false; x.SchemOff = 0; _schemOpen = false;
        }

        static void RenderSchematic(object comp, Extras x, string[] names)
        {
            if (x.ListCard == null) return;
            int count = 0;
            try { count = (int)_compCount.GetValue(comp); } catch { }
            if (count <= 0 || count > 5) return;
            // the flooding schematic (the card's tooltip) is open: the deck points of the ship are shown with it (whatever ShowDeckCorners says)
            if (_schemOpen) DeckCorners.WantedAt = UnityEngine.Time.unscaledTime;   // it is on the screen now (a tooltip of the card): the points follow it
            if (x.Schem == null || x.Schem.Count != count)
            {
                HideSchem(x); x.Schem = BuildSchematic(count); _schemOpen = false;
                var sch = x.Schem;
                sch.Pin.Click += (o, e) => { DeckCorners.Pin(x.PinComp, sch.Pin.IsChecked == true); };
                try
                {
                    bool hooked = x.SchemPanel != null;
                    if (!hooked) x.SchemPanel = TopPanel(x.ListCard);
                    if (x.SchemPanel != null)
                    {
                        if (!hooked) {
                        x.ListCard.MouseEnter += (o, e) => ShowSchem(x);
                        x.ListCard.IsVisibleChanged += (o, e) => { if (!x.ListCard.IsVisible) HideSchem(x); };
                        }
                    }
                    else   // no panel to put it in: the old tooltip (no clicks in it)
                    {
                        ToolTipService.SetToolTip(x.ListCard, x.Schem.Root);
                        ToolTipService.SetPlacement(x.ListCard, PlacementMode.Bottom);
                        ToolTipService.SetShowDuration(x.ListCard, int.MaxValue);
                        x.ListCard.ToolTipOpening += (o, e) => _schemOpen = true;
                        x.ListCard.ToolTipClosing += (o, e) => _schemOpen = false;
                    }
                }
                catch (Exception e) { Trace("schematic panel: " + e.Message); }
            }
            var s = x.Schem;
            x.PinComp = comp;
            try { bool pinned = DeckCorners.IsPinned(comp); if (s.Pin.IsChecked != pinned) s.Pin.IsChecked = pinned; } catch { }
            if (x.SchemShown)
            {
                // closed once the mouse has been off both the card and the panel for two checks in a row (4 a second)
                bool over = false;
                try { over = x.ListCard.IsMouseOver || s.Root.IsMouseOver; } catch { }
                x.SchemOff = over ? 0 : x.SchemOff + 1;
                if (x.SchemOff >= 2) HideSchem(x);
            }
            var port = _compPort.GetValue(comp) as Array; var star = _compStar.GetValue(comp) as Array;
            if (port == null || star == null) return;
            Col[] cols = ReferenceEquals(_colsComp.Target, comp) ? _colsShown : null;

            // left to right as on the tab (bow on the right); without the tab's order: aft first
            bool okOrder = _order != null && _order.Length == count;
            float cellH = (SchB - SchT) / 2f;
            for (int k = 0; k < count; k++)
            {
                int i = okOrder ? _order[k] : count - 1 - k;
                for (int side = 0; side < 2; side++)
                {
                    var fc = (side == 0 ? port : star).GetValue(i);
                    float water = Math.Max(0f, Math.Min(100f, F(_fcFloodPct.GetValue(fc, null))));
                    int j = k * 2 + side;
                    float h = cellH * water / 100f;
                    s.Fill[j].Height = h;
                    Canvas.SetTop(s.Fill[j], SchT + (side + 1) * cellH - h);

                    float rate = cols != null && i < cols.Length ? (side == 0 ? cols[i].RP : cols[i].RS) : 0f;
                    int a = Arrows(rate, FloodOne, FloodTwo, FloodTwo);
                    string sig = Pct(water) + "|" + a;
                    if (sig == s.Sig[j]) continue;
                    s.Sig[j] = sig;
                    s.Value[j].Text = Pct(water);
                    if (!ReferenceEquals(s.Value[j].Foreground, BWhite)) s.Value[j].Foreground = BWhite;
                    s.Arrows[j].Children.Clear();
                    if (a != 0) s.Arrows[j].Children.Add(ArrowShape(a > 0, Math.Abs(a), a > 0 ? CUp : CDown));
                }
            }
            float total = 0f;
            try { total = Convert.ToSingle(_compFloodPct.GetValue(comp, null), CultureInfo.InvariantCulture); } catch { }
            string title = "Water " + Pct(total) + " · sinks at " + SinkAt(comp).ToString("0.#", CultureInfo.InvariantCulture) + "%";
            if (s.Title.Text != title) s.Title.Text = title;
            // the list / trim limits of this ship (the gauges' full scales; faster flooding from their red zones)
            string lim;
            float L = DcTeams.ShipLength(comp);
            if (IsSub(comp)) lim = "Submarine: list and trim flood nothing";
            else if (ModelOn() && SubmergedFlooding.Covers(comp))
            {
                float tl, tt; SubmergedFlooding.TouchAngles(comp, out tl, out tt);
                lim = SubmergedFlooding.MarginText(comp) ?? ("Deck edge touches the sea at list " + tl.ToString("0.#", CultureInfo.InvariantCulture) + "° · trim " + tt.ToString("0.0", CultureInfo.InvariantCulture) + "° (red zones)");
            }
            else if (ModelOn())
                lim = "List limit " + Limits.ListFull.ToString("0.#", CultureInfo.InvariantCulture) + "° · Trim limit " +
                      Limits.Trim(L).ToString("0.0", CultureInfo.InvariantCulture) + "°" + (L > 0f ? " (" + L.ToString("0", CultureInfo.InvariantCulture) + " m)" : "") +
                      " · floods faster from the red zones";
            else lim = "List 45° · Trim 8° (the game's)";
            // two lines (it was too wide): at the first " · "
            int dot = lim.IndexOf(" · ", StringComparison.Ordinal);
            if (dot > 0) lim = lim.Substring(0, dot) + "\n" + lim.Substring(dot + 3);
            if (s.Limits.Text != lim) s.Limits.Text = lim;
        }
    }
}
