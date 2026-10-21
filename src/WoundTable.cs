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
        const int Ships = 6, Ammo = 9;

        class Am { public string Ini, Name; public float Power; }

        static List<Am> _ammo;      // all warhead ammunition, by power; read once

        static List<Am> LoadAmmo()
        {
            if (_ammo != null && _ammo.Count > 0) return _ammo;
            _ammo = new List<Am>();
            try
            {
                var prov = ShipNames.Provider();
                if (prov == null) return _ammo;
                var data = prov.GetType().GetProperty("AmmunitionData").GetValue(prov, null) as IEnumerable;
                if (data == null) return _ammo;
                Type tAp = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "Seapower-Scripts") { tAp = a.GetType("AmmunitionParameters"); if (tAp == null) foreach (var t in a.GetTypes()) if (t.Name == "AmmunitionParameters") { tAp = t; break; } }
                var shared = tAp == null ? null : tAp.GetMethod("GetShared", BindingFlags.Static | BindingFlags.Public);
                var fp = tAp == null ? null : tAp.GetField("_warheadPower");
                if (shared == null || fp == null) return _ammo;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in data)
                {
                    try
                    {
                        var kt = kv.GetType();
                        string ini = kt.GetProperty("Key").GetValue(kv, null) as string;
                        var val = kt.GetProperty("Value").GetValue(kv, null);
                        if (string.IsNullOrEmpty(ini) || val == null) continue;
                        string name = val.GetType().GetProperty("DisplayedName").GetValue(val, null) as string;
                        if (string.IsNullOrEmpty(name)) name = ini;
                        name = name.Trim();
                        if (!seen.Add(name)) continue;
                        var ap = shared.Invoke(null, new object[] { ini });
                        float p = ap == null ? 0f : Convert.ToSingle(fp.GetValue(ap));
                        if (p > 0.05f) _ammo.Add(new Am { Ini = ini, Name = name, Power = p });
                    }
                    catch { }
                }
                _ammo.Sort((a, b) => a.Power.CompareTo(b.Power));
            }
            catch (Exception e) { SettingsWindow.Trace("WoundTable ammo: " + e.Message); }
            return _ammo;
        }

        /// <summary>'n' items spread evenly along the log of the value, from the first to the last (distinct ones).</summary>
        static List<T> Spread<T>(List<T> sorted, int n, Func<T, double> val)
        {
            var r = new List<T>();
            if (sorted.Count == 0) return r;
            if (sorted.Count <= n) return new List<T>(sorted);
            double lo = Math.Log(Math.Max(1e-3, val(sorted[0]))), hi = Math.Log(Math.Max(1e-3, val(sorted[sorted.Count - 1])));
            int last = -1;
            for (int i = 0; i < n; i++)
            {
                double target = lo + (hi - lo) * i / (n - 1);
                int best = 0; double bd = double.MaxValue;
                for (int j = 0; j < sorted.Count; j++) { double d = Math.Abs(Math.Log(Math.Max(1e-3, val(sorted[j]))) - target); if (d < bd) { bd = d; best = j; } }
                if (best <= last) best = last + 1;
                if (best >= sorted.Count) break;
                r.Add(sorted[best]); last = best;
            }
            return r;
        }

        // what the table shows first (the ship's / ammunition's ini name or a part of the shown name); mods' ships are found by name,
        // gaps are filled from the game's data by size / power
        static readonly string[] WantShips = { "libelle", "sleipnir", "krivak1", "minegumo" };
        static readonly string[] WantAmmo = { "wp_cal_23mm", "wp_cal_57mm", "wp_cal_76mm", "wp_cal_100mm", "usn_mk-82", "usn_agm-84d", "wp_53-65K", "wp_ss-n-19", "wp_fab-9000_m54" };

        static Border Cell(string s, Color c, bool bold, TextAlignment al, int row, int col, Color? bg = null, float fs = 12f)
        {
            var t = new TextBlock { Text = s, Foreground = new SolidColorBrush(c), FontSize = fs, TextAlignment = al, VerticalAlignment = VerticalAlignment.Center,
                                    FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, Margin = new Thickness(8f, 3f, 8f, 3f) };
            var b = new Border { Child = t, Background = new SolidColorBrush(bg ?? Color.FromArgb(0, 0, 0, 0)) };
            Grid.SetRow(b, row); Grid.SetColumn(b, col);
            return b;
        }

        static List<T> Pick<T>(List<T> all, string[] want, Func<T, string> id, Func<T, double> val, int n)
        {
            var r = new List<T>();
            foreach (var w in want)
            {
                T f = default(T); bool found = false;
                foreach (var x in all) if (string.Equals(id(x), w, StringComparison.OrdinalIgnoreCase)) { f = x; found = true; break; }
                if (!found) foreach (var x in all) if (id(x).IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) { f = x; found = true; break; }
                if (found && !r.Contains(f)) r.Add(f);
            }
            if (r.Count < n)
            {
                foreach (var x in Spread(all, n, val)) { if (r.Count >= n) break; if (!r.Contains(x)) r.Add(x); }
            }
            r.Sort((x, y) => val(x).CompareTo(val(y)));
            return r;
        }

        public static FrameworkElement Build()
        {
            var root = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10f) };
            Color cText = Color.FromRgb(225, 228, 232), cDim = Color.FromRgb(140, 148, 158);
            try
            {
                var all = ShipNames.Sizes();
                var ships = Pick(all, WantShips, x => x.Ini + "|" + x.Name, x => x.Tons, Ships);
                var am = LoadAmmo();
                var ammo = Pick(am, WantAmmo, x => x.Ini + "|" + x.Name, x => x.Power, Ammo);
                if (ships.Count == 0 || ammo.Count == 0)
                {
                    root.Children.Add(Cell("No game data yet.", cDim, false, TextAlignment.Left, 0, 0));
                    return root;
                }
                root.Children.Add(new TextBlock { Text = "Chance that one hit wounds a team", Foreground = new SolidColorBrush(cText), FontSize = 14f, FontWeight = FontWeight.Bold });
                root.Children.Add(new TextBlock { Text = "% per hit, with the Max health and Wound chance now set; rows: ammunition (warhead power), columns: ships (displacement, team health)",
                                                  Foreground = new SolidColorBrush(cDim), FontSize = 11f, Margin = new Thickness(0f, 2f, 0f, 8f) });
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                for (int i = 0; i < ships.Count; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                for (int i = 0; i < ammo.Count + 1; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                g.Children.Add(Cell("Ammunition", cDim, false, TextAlignment.Left, 0, 0, null, 11f));
                g.Children.Add(Cell("Power", cDim, false, TextAlignment.Right, 0, 1, null, 11f));
                for (int c = 0; c < ships.Count; c++)
                {
                    float hp = DcTeams.MaxHpFor(ships[c].Tons);
                    var h = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8f, 3f, 8f, 3f), MinWidth = 70f };
                    h.Children.Add(new TextBlock { Text = ships[c].Name, Foreground = new SolidColorBrush(cText), FontSize = 12f, FontWeight = FontWeight.Bold, TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap, MaxWidth = 110f });
                    h.Children.Add(new TextBlock { Text = Fmt(ships[c].Tons) + " t · HP " + Fmt(hp), Foreground = new SolidColorBrush(cDim), FontSize = 11f, TextAlignment = TextAlignment.Right });
                    Grid.SetRow(h, 0); Grid.SetColumn(h, c + 2);
                    g.Children.Add(h);
                }
                for (int r = 0; r < ammo.Count; r++)
                {
                    Color zebra = r % 2 == 0 ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(0, 0, 0, 0);
                    g.Children.Add(Cell(ammo[r].Name, cText, false, TextAlignment.Left, r + 1, 0, zebra));
                    g.Children.Add(Cell(Fmt(ammo[r].Power), cDim, false, TextAlignment.Right, r + 1, 1, zebra));
                    for (int c = 0; c < ships.Count; c++)
                    {
                        double p = DcTeams.WoundOdds(ammo[r].Power, DcTeams.MaxHpFor(ships[c].Tons)) * 100.0;
                        // a heat background: the more chance the stronger the red; the text stays white
                        int a = (int)Math.Round(Math.Min(1.0, p / 100.0) * 170.0);
                        g.Children.Add(Cell(p.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), p < 3.0 ? cDim : cText, p >= 50.0, TextAlignment.Right, r + 1, c + 2, Color.FromArgb((byte)a, 220, 80, 50)));
                    }
                }
                root.Children.Add(g);
            }
            catch (Exception e) { SettingsWindow.Trace("WoundTable: " + e.Message); }
            return root;
        }

        static string Fmt(float v)
        {
            return (v >= 100f ? v.ToString("0") : v >= 10f ? v.ToString("0.#") : v.ToString("0.##")).Replace(',', '.');
        }
    }
}
