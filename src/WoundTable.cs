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

        class Am { public string Name; public float Power; }

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
                        if (p > 0.05f) _ammo.Add(new Am { Name = name, Power = p });
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

        static TextBlock Cell(string s, Color c, bool bold, TextAlignment al, int row, int col)
        {
            var t = new TextBlock { Text = s, Foreground = new SolidColorBrush(c), FontSize = 12f, TextAlignment = al,
                                    FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, Margin = new Thickness(6f, 1f, 6f, 1f), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(t, row); Grid.SetColumn(t, col);
            return t;
        }

        static string Short(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        public static FrameworkElement Build()
        {
            var root = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8f) };
            Color cText = Color.FromRgb(225, 228, 232), cDim = Color.FromRgb(150, 156, 164);
            try
            {
                var ships = Spread(ShipNames.Sizes(), Ships, k => k.Value);
                var ammo = Spread(LoadAmmo(), Ammo, a => a.Power);
                if (ships.Count == 0 || ammo.Count == 0)
                {
                    root.Children.Add(Cell("No game data yet.", cDim, false, TextAlignment.Left, 0, 0));
                    return root;
                }
                var head = new TextBlock { Text = "Chance that one hit wounds a team, % (Max health and Wound chance as set)", Foreground = new SolidColorBrush(cText), FontSize = 12f, FontWeight = FontWeight.Bold, Margin = new Thickness(6f, 0f, 6f, 4f) };
                root.Children.Add(head);
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                for (int i = 0; i < ships.Count; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                for (int i = 0; i < ammo.Count + 2; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                g.Children.Add(Cell("Warhead power \\ team health", cDim, false, TextAlignment.Left, 0, 0));
                for (int c = 0; c < ships.Count; c++)
                {
                    float hp = DcTeams.MaxHpFor(ships[c].Value);
                    g.Children.Add(Cell(Short(ships[c].Key, 16), cText, true, TextAlignment.Right, 0, c + 1));
                    g.Children.Add(Cell(Fmt(ships[c].Value) + " t, HP " + Fmt(hp), cDim, false, TextAlignment.Right, 1, c + 1));
                }
                for (int r = 0; r < ammo.Count; r++)
                {
                    g.Children.Add(Cell(Short(ammo[r].Name, 24) + "  " + Fmt(ammo[r].Power), cText, false, TextAlignment.Left, r + 2, 0));
                    for (int c = 0; c < ships.Count; c++)
                    {
                        double p = DcTeams.WoundOdds(ammo[r].Power, DcTeams.MaxHpFor(ships[c].Value)) * 100.0;
                        // grey -> yellow -> red with the chance
                        Color col = p < 10.0 ? cDim : p < 40.0 ? Color.FromRgb(232, 200, 90) : Color.FromRgb(240, 120, 70);
                        g.Children.Add(Cell(p.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), col, false, TextAlignment.Right, r + 2, c + 1));
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
