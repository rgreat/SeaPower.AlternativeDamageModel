using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Noesis;

namespace AlternativeDamageModel
{
    /// <summary>
    /// The Unit Status window for a ship that is not yours: the game keeps it empty (no unit you command). With [UI] ShowEnemyDamage:
    /// the window gets one tab, Damage State, and over its content a heavily simplified panel of the mod: what the player can tell
    /// about the contact by its sensors (ContactIntel: the same four icons, words instead of figures, the sinking forecast).
    /// The game's view model is touched only after UnitStatusViewModel.SwitchUnit when it has no unit: the window shows the tab,
    /// the name; the panel (an overlay in the content area) is ours. Own unit selected again: the game's states are not touched.
    /// </summary>
    static class EnemyStatus
    {
        public static Action<string> Trace = s => { };
        public static Func<bool> IsEnabled = () => false;

        static bool _ok;
        static FieldInfo _fUnit, _gMain;
        static PropertyInfo _pHeader, _pHasUnit, _pUnitName, _pEngagement, _pDcTab, _pSelObj, _pAvail, _pSelected, _pValue;
        static MethodInfo _mGetVeh;
        static FieldInfo _gPlayerTf;
        static PropertyInfo _oName, _vIdent, _vId;
        static Type _tVehicle;

        static string _origHeader;                 // the tab's own title, put back when a unit of the player is selected
        public static bool Active;                 // the window shows a contact now
        static object _vehicle;                    // the contact (Vehicle)

        public static bool Init(Harmony harmony, Assembly asm)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var tVm = ByName(asm, "UnitStatusViewModel");
                var tMain = ByName(asm, "MainGameViewModel");
                var tGlobals = ByName(asm, "Globals");
                _tVehicle = asm.GetType("SeaPower.Vehicle");
                var tObj = asm.GetType("SeaPower.ObjectBase");
                if (tVm == null || tMain == null || tGlobals == null || _tVehicle == null || tObj == null) { Trace("EnemyStatus: types not found"); return false; }
                _fUnit = tVm.GetField("_unit", all);
                _pHasUnit = tVm.GetProperty("HasUnit", all); _pUnitName = tVm.GetProperty("UnitName", all);
                _pEngagement = tVm.GetProperty("EngagementTab", all); _pDcTab = tVm.GetProperty("DamageControlTab", all);
                var tTab = ByName(asm, "UnitStatusTabViewModel");
                _pHeader = tTab.GetProperty("Header", all); _pAvail = tTab.GetProperty("IsAvailable", all); _pSelected = tTab.GetProperty("IsSelected", all);
                _gMain = tGlobals.GetField("_mainGameViewModel", all); _gPlayerTf = tGlobals.GetField("_playerTaskforce", all);
                _pSelObj = tMain.GetProperty("SelectedObject", all);
                _mGetVeh = tObj.GetMethod("GetMapVehicle", all);
                _oName = tObj.GetProperty("DisplayNameShort", all);
                _vIdent = _tVehicle.GetProperty("Identified", all);
                _vId = _tVehicle.GetProperty("Id", all);
                _ok = _fUnit != null && _pHasUnit != null && _pUnitName != null && _pEngagement != null && _pDcTab != null && _pAvail != null && _pSelected != null &&
                      _gMain != null && _pSelObj != null && _mGetVeh != null && _gPlayerTf != null;
                if (!_ok) { Trace("EnemyStatus: members not found"); return false; }
                var sw = tVm.GetMethod("SwitchUnit", all);
                if (sw == null) { Trace("EnemyStatus: SwitchUnit not found"); return false; }
                harmony.Patch(sw, postfix: new HarmonyMethod(typeof(EnemyStatus).GetMethod(nameof(SwitchPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                return true;
            }
            catch (Exception e) { Trace("EnemyStatus.Init: " + e); _ok = false; return false; }
        }

        static Type ByName(Assembly asm, string name)
        {
            try { foreach (var t in asm.GetTypes()) if (t.Name == name) return t; } catch { }
            return null;
        }

        static object Val(object rp)
        {
            if (rp == null) return null;
            if (_pValue == null || _pValue.DeclaringType != rp.GetType()) _pValue = rp.GetType().GetProperty("Value");
            return _pValue == null ? null : _pValue.GetValue(rp, null);
        }

        /// <summary>The selected contact (a Vehicle of the player's plotting table) or null.</summary>
        static object SelectedContact()
        {
            var main = _gMain.GetValue(null);
            if (main == null) return null;
            var sel = Val(_pSelObj.GetValue(main, null));
            if (sel == null) return null;
            if (_tVehicle.IsInstanceOfType(sel)) return sel;
            return _mGetVeh.Invoke(sel, new object[] { _gPlayerTf.GetValue(null) });
        }

        static void SwitchPostfix(object __instance)
        {
            try
            {
                if (!_ok) return;
                bool want = false;
                if (IsEnabled() && _fUnit.GetValue(__instance) == null)
                {
                    var v = SelectedContact();
                    if (v != null && ContactIntel.HasCompartments(v)) { _vehicle = v; want = true; }
                }
                if (want)
                {
                    var eng = _pEngagement.GetValue(__instance, null); var dc = _pDcTab.GetValue(__instance, null);
                    _pHasUnit.GetSetMethod(true).Invoke(__instance, new object[] { true });
                    _pUnitName.GetSetMethod(true).Invoke(__instance, new object[] { NameOf(_vehicle) });
                    if (_pHeader != null)
                    {
                        if (_origHeader == null) _origHeader = Convert.ToString(_pHeader.GetValue(dc, null));
                        if (_origHeader != "Damage State") _pHeader.SetValue(dc, "Damage State", null);
                    }
                    _pAvail.SetValue(eng, false, null);
                    _pAvail.SetValue(dc, true, null);
                    _pSelected.SetValue(dc, true, null);
                    Active = true;
                }
                else if (Active)
                {
                    Active = false; _vehicle = null;
                    if (_pHeader != null && _origHeader != null) _pHeader.SetValue(_pDcTab.GetValue(__instance, null), _origHeader, null);
                    if (_fUnit.GetValue(__instance) == null)
                    {
                        // nothing of the player's selected: the window as the game leaves it
                        var eng = _pEngagement.GetValue(__instance, null); var dc = _pDcTab.GetValue(__instance, null);
                        _pHasUnit.GetSetMethod(true).Invoke(__instance, new object[] { false });
                        _pUnitName.GetSetMethod(true).Invoke(__instance, new object[] { "" });
                        _pSelected.SetValue(dc, false, null);
                        _pAvail.SetValue(dc, false, null);
                        _pAvail.SetValue(eng, true, null);
                        _pSelected.SetValue(eng, true, null);
                    }
                }
            }
            catch (Exception e) { Trace("EnemyStatus.SwitchPostfix: " + e.Message); }
        }

        static string NameOf(object vehicle)
        {
            try
            {
                string id = _vId == null ? "" : Convert.ToString(_vId.GetValue(vehicle, null));
                bool ident = _vIdent != null && ContactIntel.Flag(_vIdent.GetValue(vehicle, null));
                var obj = ContactIntel.ObjectOf(vehicle);
                string name = ident && obj != null && _oName != null ? Convert.ToString(_oName.GetValue(obj, null)) : null;
                return string.IsNullOrEmpty(name) ? "Contact " + id : name + " (" + id + ")";
            }
            catch { return "Contact"; }
        }

        // ------------------------------------------------------------ the panel over the tab's content

        sealed class Card
        {
            public Grid Root; public TextBlock Title, Empty, Forecast, Via;
            public Noesis.Path[] Icons = new Noesis.Path[4]; public TextBlock[] Texts = new TextBlock[4]; public StackPanel[] Rows = new StackPanel[4];
            public StackPanel Body; public float Next;
        }
        static readonly ConditionalWeakTable<object, Card> Panels = new ConditionalWeakTable<object, Card>();

        /// <summary>Called every frame; works twice a second.</summary>
        public static void Tick(float now)
        {
            if (!_ok) return;
            foreach (var view in DamageControlUi.StatusViewElements())
            {
                Card p;
                if (!Panels.TryGetValue(view, out p))
                {
                    if (!Active) continue;
                    p = Make(view);
                    if (p == null) continue;
                    Panels.Add(view, p);
                }
                if (p.Root == null) continue;
                bool show = Active && IsEnabled() && _vehicle != null;
                var vis = show ? Visibility.Visible : Visibility.Collapsed;
                if (p.Root.Visibility != vis) p.Root.Visibility = vis;
                if (!show || now < p.Next) continue;
                p.Next = now + 0.5f;
                try { Fill(p, view); } catch (Exception e) { Trace("EnemyStatus.Fill: " + e.Message); }
            }
        }

        /// <summary>The overlay goes next to the game's DamageControlView, in the grid that hides everything while there is no unit.</summary>
        static Card Make(FrameworkElement view)
        {
            FrameworkElement dcView = null;
            foreach (var e in DamageControlUi.TreeOf(view, 8000)) { var fe = e as FrameworkElement; if (fe != null && fe.GetType().Name == "DamageControlView") { dcView = fe; break; } }
            if (dcView == null) return null;
            // up to the grid that holds all the tabs' views (the one bound to HasUnit)
            var wrap = DamageControlUi.ParentOf(dcView) as Grid;           // the tab's own grid
            var host = wrap == null ? null : DamageControlUi.ParentOf(wrap) as Grid;
            if (host == null) return null;
            var p = new Card();
            var bg = DamageControlUi.ResBrush(view, "Brush.Window.Background", Color.FromRgb(30, 34, 40));
            var text = DamageControlUi.ResBrush(view, "Brush.Foreground.Emphasis", Color.FromRgb(214, 218, 222));
            p.Root = new Grid { Background = bg, Visibility = Visibility.Collapsed };
            Panel_SetZ(p.Root);
            var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(16f, 12f, 16f, 12f), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            p.Title = new TextBlock { FontSize = 16f, Foreground = text, Margin = new Thickness(0f, 0f, 0f, 8f) };
            stack.Children.Add(p.Title);
            p.Forecast = new TextBlock { FontSize = 18f, FontWeight = FontWeight.Bold, Margin = new Thickness(0f, 0f, 0f, 10f) };
            stack.Children.Add(p.Forecast);
            p.Empty = new TextBlock { FontSize = 14f, Foreground = text };
            stack.Children.Add(p.Empty);
            p.Body = new StackPanel { Orientation = Orientation.Vertical };
            for (int k = 0; k < 4; k++)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0f, 3f, 0f, 3f) };
                var icon = ContactIntel.NewIcon(k, k == 3 ? 24f : 20f, 0f);
                var holder = new Border { Width = 28f, Child = icon, HorizontalAlignment = HorizontalAlignment.Left };
                row.Children.Add(holder);
                var t = new TextBlock { FontSize = 14f, Foreground = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8f, 0f, 0f, 0f) };
                row.Children.Add(t);
                p.Icons[k] = icon; p.Texts[k] = t; p.Rows[k] = row;
                p.Body.Children.Add(row);
            }
            stack.Children.Add(p.Body);
            p.Via = new TextBlock { FontSize = 12f, Foreground = DamageControlUi.ResBrush(view, "Brush.Foreground.Highlight", Color.FromRgb(160, 166, 174)), Margin = new Thickness(0f, 12f, 0f, 0f) };
            stack.Children.Add(p.Via);
            p.Root.Children.Add(stack);
            host.Children.Add(p.Root);
            return p;
        }

        static void Panel_SetZ(UIElement e) { try { Noesis.Panel.SetZIndex(e, 100); } catch { } }

        static void SetText(TextBlock tb, string s) { if (tb.Text != s) tb.Text = s; }

        static readonly string[] FireWords = { "", "minor", "moderate", "severe" };
        static readonly string[] WaterWords = { "", "slight", "moderate", "heavy", "critical" };
        static readonly string[] HullWords = { "", "slightly damaged", "damaged", "heavily damaged", "breaking up" };

        static void Fill(Card p, FrameworkElement view)
        {
            var i = ContactIntel.Of(_vehicle);
            SetText(p.Title, "Damage State — " + NameOf(_vehicle));
            string fc = ""; int sev = 0;
            if (i.Sinking) { fc = "Sinking"; sev = 2; }
            else if (i.Seen && i.Ident && i.EtaMin > 0) { int m = (i.EtaMin + 4) / 5 * 5; fc = "Sinks in ~" + m + " min" + (string.IsNullOrEmpty(i.Cause) ? "" : " (" + i.Cause + ")"); sev = i.EtaMin <= 20 ? 2 : 1; }
            else if (i.Seen && !i.Ident && i.EtaMin > 0) { fc = "Sinking soon"; sev = 1; }
            SetText(p.Forecast, fc);
            p.Forecast.Visibility = fc.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            p.Forecast.Foreground = ContactIntelBrush(sev);
            SetText(p.Via, string.IsNullOrEmpty(i.Via) ? "" : "Source: " + i.Via);
            string att = i.Attitude ?? "";
            bool any = i.Any || att.Length > 0;
            SetText(p.Empty, any ? "" : i.Seen ? "No damage visible" : "No data: not in direct sight");
            p.Empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            int[] lv = { i.Fire, i.Flood, i.Hull, i.Engine };
            if (lv[1] == 0 && att.Length > 0) lv[1] = 1;   // only the attitude is seen: the water icon, yellow
            string[] txt =
            {
                lv[0] > 0 ? "Fire: " + FireWords[Math.Min(lv[0], 3)] : "",
                i.Flood > 0 ? "Flooding: " + WaterWords[Math.Min(i.Flood, 4)] + (att.Length > 0 ? ", " + att : "") : att.Length > 0 ? "Attitude: " + att : "",
                lv[2] > 0 ? "Hull: " + HullWords[Math.Min(lv[2], 4)] : "",
                lv[3] > 0 ? (lv[3] >= 4 ? "Engine: dead in the water" : "Engine: part of propulsion lost") + (i.Guess ? " ?" : "") : ""
            };
            if (lv[3] > 0 && i.Guess) txt[3] = (lv[3] >= 4 ? "Engine: almost stopped" : "Engine: slow") + " ?";
            for (int k = 0; k < 4; k++)
            {
                p.Rows[k].Visibility = lv[k] > 0 ? Visibility.Visible : Visibility.Collapsed;
                ContactIntel.SetLevel(p.Icons[k], lv[k]);
                SetText(p.Texts[k], txt[k]);
            }
        }

        static Brush ContactIntelBrush(int sev)
        {
            return sev >= 2 ? (Brush)ContactIntel.RedBrush : sev == 1 ? ContactIntel.OrangeBrush : ContactIntel.TextBrush;
        }
    }
}
