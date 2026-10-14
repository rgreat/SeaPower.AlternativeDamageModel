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
            public TextBlock Title, Limits;
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
        static void RenderSchematic(object comp, Extras x, string[] names)
        {
            if (x.ListCard == null) return;
            int count = 0;
            try { count = (int)_compCount.GetValue(comp); } catch { }
            if (count <= 0 || count > 5) return;
            if (x.Schem == null || x.Schem.Count != count)
            {
                x.Schem = BuildSchematic(count);
                try
                {
                    ToolTipService.SetToolTip(x.ListCard, x.Schem.Root);
                    ToolTipService.SetPlacement(x.ListCard, PlacementMode.Bottom);
                    ToolTipService.SetShowDuration(x.ListCard, int.MaxValue);
                }
                catch (Exception e) { Trace("schematic tooltip: " + e.Message); }
            }
            var s = x.Schem;
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
                lim = "Deck edge touches the sea at list " + tl.ToString("0.#", CultureInfo.InvariantCulture) + "° · trim " + tt.ToString("0.0", CultureInfo.InvariantCulture) + "° (red zones)";
            }
            else if (ModelOn())
                lim = "List limit " + Limits.ListFull.ToString("0.#", CultureInfo.InvariantCulture) + "° · Trim limit " +
                      Limits.Trim(L).ToString("0.0", CultureInfo.InvariantCulture) + "°" + (L > 0f ? " (" + L.ToString("0", CultureInfo.InvariantCulture) + " m)" : "") +
                      " · floods faster from the red zones";
            else lim = "List 45° · Trim 8° (the game's)";
            if (s.Limits.Text != lim) s.Limits.Text = lim;
        }
    }
}
