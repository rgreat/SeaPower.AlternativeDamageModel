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
        static readonly string[] WantShips = { "wp_pb_zhuk", "knm_fs_sleipner", "wp_skr_krivak1", "usn_cg_ticonderoga", "wp_rkr_kirov", "usn_cvn_nimitz" };
        static readonly string[] WantAmmo = { "usn_cal_35mm", "wp_cal_57mm", "usn_cal_76mm", "wp_cal_100mm", "usn_cal_127mm", "usn_mk-82", "usn_rgm-84c", "wp_53-65K", "wp_ss-n-19" };

        static Border Cell(string s, Color c, bool bold, TextAlignment al, int row, int col, Color? bg = null, float fs = 12f)
        {
            var t = new TextBlock { Text = s, Foreground = new SolidColorBrush(c), FontSize = fs, TextAlignment = al, VerticalAlignment = VerticalAlignment.Center,
                                    FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, Margin = new Thickness(8f, 3f, 8f, 3f) };
            var b = new Border { Child = t, Background = new SolidColorBrush(bg ?? Color.FromArgb(0, 0, 0, 0)) };
            Grid.SetRow(b, row); Grid.SetColumn(b, col);
            return b;
        }

        public static FrameworkElement Build()
        {
            var root = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10f) };
            Color cText = Color.FromRgb(225, 228, 232), cDim = Color.FromRgb(140, 148, 158);
            try
            {
                var all = ShipNames.Sizes();
                var ships = new List<ShipNames.ShipSize>();
                foreach (var w in WantShips)
                {
                    var f = all.Find(x => string.Equals(x.Ini, w, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Ini, w + ".ini", StringComparison.OrdinalIgnoreCase));
                    if (f != null) ships.Add(f); else SettingsWindow.Trace("WoundTable: ship not found: " + w);
                }
                var ammo = new List<Am>();
                foreach (var w in WantAmmo) { var f = FindAmmo(w); if (f != null) ammo.Add(f); else SettingsWindow.Trace("WoundTable: ammo not found: " + w); }
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
