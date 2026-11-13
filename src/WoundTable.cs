using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Noesis;

namespace AlternativeDamageModel
{
    /// <summary>
    /// The tooltip of the (i) after "Wound chance": a table calculated from the game's own data (mods included) - a spread of its
    /// ammunition by warhead power (rows) against a spread of its ships by displacement (columns); a cell is the chance that one
    /// hit wounds a team (percent), with the current Max health and Wound chance values. Built when the mouse comes to the icon.
    /// </summary>
    static class WoundTable
    {
        class Am { public string Ini, Name; public float Power; }

        static MethodInfo _shared; static FieldInfo _wp; static IEnumerable _ammoData;

        /// <summary>An ammunition by its ini name: the power from the game's own parameters, the name as the game shows it.</summary>
        static Am FindAmmo(string ini)
        {
            try
            {
                if (_shared == null)
                {
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (a.GetName().Name != "Seapower-Scripts") continue;
                        foreach (var t in a.GetTypes()) if (t.Name == "AmmunitionParameters") { _shared = t.GetMethod("GetShared", BindingFlags.Static | BindingFlags.Public); _wp = t.GetField("_warheadPower"); break; }
                    }
                    var prov = ShipNames.Provider();
                    if (prov != null) _ammoData = prov.GetType().GetProperty("AmmunitionData").GetValue(prov, null) as IEnumerable;
                }
                if (_shared == null || _wp == null) return null;
                var ap = _shared.Invoke(null, new object[] { ini });
                if (ap == null) return null;
                float p = Convert.ToSingle(_wp.GetValue(ap));
                string name = null;
                if (_ammoData != null)
                    foreach (var kv in _ammoData)
                    {
                        var kt = kv.GetType();
                        string k = kt.GetProperty("Key").GetValue(kv, null) as string;
                        if (k != null && k.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) k = k.Substring(0, k.Length - 4);
                        if (!string.Equals(k, ini, StringComparison.OrdinalIgnoreCase)) continue;
                        var val = kt.GetProperty("Value").GetValue(kv, null);
                        if (val != null) name = val.GetType().GetProperty("DisplayedName").GetValue(val, null) as string;
                        break;
                    }
                return new Am { Ini = ini, Name = string.IsNullOrEmpty(name) ? ini : name.Trim(), Power = p };
            }
            catch (Exception e) { SettingsWindow.Trace("WoundTable ammo " + ini + ": " + e.Message); return null; }
        }

        // what the table shows first (the ship's / ammunition's ini name or a part of the shown name); mods' ships are found by name,
        // gaps are filled from the game's data by size / power
        static readonly string[] WantShips = { "wp_pb_zhuk", "knm_fs_sleipner", "wp_skr_krivak1", "usn_cg_ticonderoga", "wp_rkr_kirov", "usn_cvn_nimitz", "ijn_bb_kongo", "ijn_bb_yamato", "usn_dd_fletcher" };   // the last three: ships of a test battle (mods)
        static readonly string[] WantAmmo = { "wp_cal_37mm", "wp_cal_57mm", "usn_cal_76mm", "wp_cal_100mm", "usn_cal_127mm", "usn_rim-66b", "usn_mk-82", "usn_rgm-84c", "wp_53-65K", "wp_ss-n-19",
                                                  "rm_cal_90mm", "rm_cal_152mmhe", "rm_cal_152mm", "ijn_cal_356mm", "rm_cal_381mmsap" };   // the last five: shells of a test battle (mods)

        static Border Cell(string s, Color c, bool bold, TextAlignment al, int row, int col, Color? bg = null, float fs = 12f)
        {
            var t = new TextBlock { Text = s, Foreground = new SolidColorBrush(c), FontSize = fs, TextAlignment = al, VerticalAlignment = VerticalAlignment.Center,
                                    FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, Margin = new Thickness(8f, 3f, 8f, 3f) };
            var b = new Border { Child = t, Background = new SolidColorBrush(bg ?? Color.FromArgb(0, 0, 0, 0)) };
            Grid.SetRow(b, row); Grid.SetColumn(b, col);
            return b;
        }

        /// <summary>A cell with two lines: the chance (as before) and under it, small, the average wound.</summary>
        static Border Cell2(string top, string below, Color c, Color cBelow, bool bold, int row, int col, Color bg)
        {
            var sp = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8f, 3f, 8f, 3f), VerticalAlignment = VerticalAlignment.Center };
            var row1 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            row1.Children.Add(new TextBlock { Text = top, Foreground = new SolidColorBrush(c), FontSize = 12f, TextAlignment = TextAlignment.Center, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center });
            // a white crosshair after the chance to wound (drawn too)
            row1.Children.Add(new Path { Data = Geometry.Parse("M5,1 A4,4 0 1 1 4.99,1 Z M5,0 L5,3 M5,7 L5,10 M0,5 L3,5 M7,5 L10,5"), Stroke = new SolidColorBrush(Color.FromRgb(255, 255, 255)), StrokeThickness = 0.6f, Opacity = 0.5f,
                                         Stretch = Stretch.Uniform, Width = 9f, Height = 9f, Margin = new Thickness(3f, 0f, 0f, 0f), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(row1);
            var row2 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            row2.Children.Add(new TextBlock { Text = below, Foreground = new SolidColorBrush(cBelow), FontSize = 10f, VerticalAlignment = VerticalAlignment.Center });
            // a drop of blood after the wound strength (drawn, not a font symbol: the game's font has no emoji)
            row2.Children.Add(new Path { Data = Geometry.Parse("M5,0 C5,0 0,6 0,9 C0,12 2.2,14 5,14 C7.8,14 10,12 10,9 C10,6 5,0 5,0 Z"), Fill = new SolidColorBrush(Color.FromRgb(200, 40, 40)), Opacity = 0.5f,
                                         Stretch = Stretch.Uniform, Width = 6f, Height = 9f, Margin = new Thickness(2f, 0f, 0f, 0f), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(row2);
            var b = new Border { Child = sp, Background = new SolidColorBrush(bg) };
            Grid.SetRow(b, row); Grid.SetColumn(b, col);
            return b;
        }

        public static FrameworkElement Build()
        {
            var root = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10f) };
            Color cText = Color.FromRgb(225, 228, 232), cDim = Color.FromRgb(140, 148, 158);
            try
            {
                // the values as they stand in the window now (not applied yet)
                float hmul = SettingsWindow.WorkNumber("DamageControlTeams/TeamHealth", DcTeams.HealthMul), wc = SettingsWindow.WorkNumber("DamageControlTeams/WoundChance", (float)DcTeams.WoundChance), hw = SettingsWindow.WorkNumber("DamageControlTeams/HitWound", DcTeams.HitWound);
                var all = ShipNames.Sizes();
                var ships = new List<ShipNames.ShipSize>();
                foreach (var w in WantShips)
                {
                    var f = all.Find(x => string.Equals(x.Ini, w, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Ini, w + ".ini", StringComparison.OrdinalIgnoreCase));
                    if (f == null)
                    {   // a mod may name the ship's ini differently: look for the last word of the wanted name in the shown name
                        string part = w.Substring(w.LastIndexOf('_') + 1);
                        f = all.Find(x => x.Name != null && x.Name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                    if (f != null && !ships.Contains(f)) ships.Add(f); else if (f == null) SettingsWindow.Trace("WoundTable: ship not found: " + w);   // not in the game's data: no column
                }
                ships.Sort((x, y) => x.Tons.CompareTo(y.Tons));   // by displacement, whatever the order of the list
                var ammo = new List<Am>();
                foreach (var w in WantAmmo) { var f = FindAmmo(w); if (f != null) ammo.Add(f); else SettingsWindow.Trace("WoundTable: ammo not found: " + w); }
                ammo.Sort((x, y) => x.Power.CompareTo(y.Power));   // by power, whatever the order of the list
                if (ships.Count == 0 || ammo.Count == 0)
                {
                    root.Children.Add(Cell(Loc.T("No game data yet."), cDim, false, TextAlignment.Left, 0, 0));
                    return root;
                }
                root.Children.Add(new TextBlock { Text = Loc.T("Chance that one hit wounds a team"), Foreground = new SolidColorBrush(cText), FontSize = 14f, FontWeight = FontWeight.Bold });
                root.Children.Add(new TextBlock { Text = Loc.T("% per hit, with Max health, Wound chance and Wound strength now set (the chance is limited to 100). Under it: the wound a team gets, on average, in % of its health"),
                                                  Foreground = new SolidColorBrush(cDim), FontSize = 11f, Margin = new Thickness(0f, 2f, 0f, 8f) });
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                for (int i = 0; i < ships.Count; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                for (int i = 0; i < ammo.Count + 4; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var lt = Cell(Loc.T("Displacement, t"), cDim, false, TextAlignment.Left, 2, 0, null, 11f); Grid.SetColumnSpan(lt, 2); g.Children.Add(lt);
                var lh = Cell(Loc.T("Team health, HP"), cDim, false, TextAlignment.Left, 3, 0, null, 11f); Grid.SetColumnSpan(lh, 2); g.Children.Add(lh);
                // the column titles sit in the row of the ship names (no empty row of their own)
                var ca = Cell(Loc.T("Ammunition"), cDim, false, TextAlignment.Left, 1, 0, null, 11f); ca.VerticalAlignment = VerticalAlignment.Bottom; g.Children.Add(ca);
                var cp = Cell(Loc.T("Power"), cDim, false, TextAlignment.Center, 1, 1, null, 11f); cp.VerticalAlignment = VerticalAlignment.Bottom; g.Children.Add(cp);
                for (int c = 0; c < ships.Count; c++)
                {
                    float hp = DcTeams.MaxHpFor(ships[c].Tons, hmul);
                    var img = Profile(ships[c].Ini);
                    if (img != null) { Grid.SetRow(img, 0); Grid.SetColumn(img, c + 2); g.Children.Add(img); }
                    var nm = new TextBlock { Text = ShortName(ships[c].Name), Foreground = new SolidColorBrush(cText), FontSize = 12f, FontWeight = FontWeight.Bold, TextAlignment = TextAlignment.Center,
                                             TextWrapping = TextWrapping.Wrap, MaxWidth = 110f, Margin = new Thickness(8f, 2f, 8f, 2f), VerticalAlignment = VerticalAlignment.Bottom };
                    Grid.SetRow(nm, 1); Grid.SetColumn(nm, c + 2); g.Children.Add(nm);
                    g.Children.Add(Cell(Fmt(ships[c].Tons), cDim, false, TextAlignment.Center, 2, c + 2, null, 11f));
                    g.Children.Add(Cell(Fmt(hp), cDim, false, TextAlignment.Center, 3, c + 2, null, 11f));
                }
                for (int r = 0; r < ammo.Count; r++)
                {
                    Color zebra = r % 2 == 0 ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(0, 0, 0, 0);
                    g.Children.Add(Cell(ammo[r].Name, cText, false, TextAlignment.Left, r + 4, 0, zebra));
                    g.Children.Add(Cell(Fmt(ammo[r].Power), cDim, false, TextAlignment.Center, r + 4, 1, zebra));
                    for (int c = 0; c < ships.Count; c++)
                    {
                        double p = Math.Min(100.0, DcTeams.WoundOdds(ammo[r].Power, DcTeams.MaxHpFor(ships[c].Tons, hmul), wc) * 100.0);   // the multiplier first, then the limit of 100%
                        // heat background: up to 30% a log-scale red that gets denser; above 30% the colour itself moves red -> orange -> amber, so 40..100% stay distinguishable; the text stays white
                        double u = Math.Max(0.0, Math.Min(1.0, (p - 30.0) / 70.0));
                        int a = (int)Math.Round(Math.Log(1.0 + Math.Min(p, 30.0)) / Math.Log(31.0) * 150.0 + 40.0 * u);
                        byte cr = (byte)Math.Round(220.0 + 10.0 * u), cg = (byte)Math.Round(80.0 + 60.0 * u), cb = (byte)Math.Round(50.0 - 30.0 * u);
                        double w = DcTeams.WoundMean(ammo[r].Power, DcTeams.MaxHpFor(ships[c].Tons, hmul), hw) * 100.0;
                        double v = Math.Min(1.0, u * 2.0);   // the small line gets lighter on the strong (amber) cells: grey does not read on them
                        Color cBelow = Color.FromRgb((byte)Math.Round(140 + 95 * v), (byte)Math.Round(148 + 90 * v), (byte)Math.Round(158 + 84 * v));
                        g.Children.Add(Cell2(p.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%", w.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%", p < 3.0 ? cDim : cText, cBelow, p >= 50.0, r + 4, c + 2, Color.FromArgb((byte)a, cr, cg, cb)));
                    }
                }
                root.Children.Add(g);
            }
            catch (Exception e) { SettingsWindow.Trace("WoundTable: " + e.Message); }
            return root;
        }

        /// <summary>The ship's profile picture of the game's encyclopedia (ui/profiles/&lt;ini&gt;.png), small; null when there is none.</summary>
        static Image Profile(string ini)
        {
            try
            {
                Type t = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name != "Seapower-Scripts") continue;
                    foreach (var x in a.GetTypes()) if (x.Name == "IniToProfileConverter") { t = x; break; }
                }
                if (t == null) return null;
                const BindingFlags bf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var cache = t.GetProperty("Profiles", bf).GetValue(null, null);
                var load = t.GetMethod("LoadUnitArt", bf);
                string key = (ini ?? "").ToLowerInvariant().Replace("/", "_");
                if (key.EndsWith(".ini", StringComparison.Ordinal)) key = key.Substring(0, key.Length - 4);
                var tex = load.Invoke(null, new object[] { cache, "ui/profiles", key, (int?)0 }) as ImageSource;
                if (tex == null) return null;
                return new Image { Source = tex, Height = 30f, MaxWidth = 110f, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8f, 4f, 8f, 0f) };
            }
            catch (Exception e) { SettingsWindow.Trace("WoundTable image " + ini + ": " + e.Message); return null; }
        }

        /// <summary>The ship's name without a trailing "(... -class)" part: the column is narrow and the class is redundant there.</summary>
        static string ShortName(string n)
        {
            if (string.IsNullOrEmpty(n)) return n;
            int i = n.LastIndexOf('(');
            if (i > 0 && n.TrimEnd().EndsWith(")", StringComparison.Ordinal)) { string r = n.Substring(0, i).Trim(); if (r.Length > 0) return r; }
            return n.Trim();
        }

        static string Fmt(float v)
        {
            return (v >= 100f ? v.ToString("0") : v >= 10f ? v.ToString("0.#") : v.ToString("0.##")).Replace(',', '.');
        }
    }
}
