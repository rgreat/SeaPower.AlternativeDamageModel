using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using Noesis;

namespace AlternativeDamageModel
{
    /// <summary>
    /// The mod's settings window, opened by the gear next to (i) on the "Damage Control" tab button: profile list
    /// (Base Game and Advanced built in, the player's own), Save as / Rename / Delete, the settings on five tabs, a help pane
    /// with the setting under the mouse, Reset to profile / Cancel / Apply / OK.
    ///  - Works on a copy: nothing changes until Apply or OK (AdmPlugin.Commit).
    ///  - Editing a built-in profile switches the list to "Custom" (saved as Custom on Apply). A profile of the player's own
    ///    is saved under its own name.
    ///  - A yellow dot marks a value that differs from the profile it came from; ↺ puts that value back.
    ///  - The Display tab (UI and logging) is not part of a profile.
    /// Noesis elements are built in code; buttons, combo box, text and check boxes get the game's own styles.
    /// While a text box has the keyboard, the game's hot keys are off (InputHandler.typingActive, as the game's own text boxes do).
    /// </summary>
    static class SettingsWindow
    {
        public static Action<string> Trace = s => { };

        // ------------------------------------------------------------ what is shown
        enum Kind { Number, Integer, Table, Bool, Choice }

        sealed class Field
        {
            public string Tab, Group, Key, Label, Unit;
            public Kind Kind;
            public float Step, Min, Max;
            public bool Global;
            public string[] Options;   // Kind.Choice
        }

        const string TUi = "User Interface", TGen = "Damage Model", TDbg = "Debug";
        const string TFx = "Effects";
        static readonly string[] Tabs = { TUi, TFx, TGen, TDbg, "Hull", "Systems", "Fire", "Flooding", "DC Teams" };
        const string AdmKey = "General/AlternativeDamageControl", UiKey = "UI/DetailedView", ArrowsGroup = "Trend arrows";
        /// <summary>Settings applied at once (not on Apply): the window shows / hides tabs or groups with them.</summary>
        static bool LiveKey(string key) { return key == AdmKey || key == UiKey; }
        const string UnattendedKey = "FireAndFlooding/UnattendedFireGrowth", SmokeKey = "Effects/ExtraSmokeEmitters", DensityKey = "Effects/SmokeBySeverity";
        /// <summary>Rows shown only while a switch of the same tab is on: the switch's key, or null.</summary>
        static string ShownWhen(Field f)
        {
            if (f.Group == ArrowsGroup) return UiKey;
            if (f.Key == "FireAndFlooding/UnattendedFireTime" || f.Key == "FireAndFlooding/UnattendedFireGrowthRate" || f.Key == "FireAndFlooding/UnattendedFireMaxGrowth") return UnattendedKey;
            if (f.Key == "Effects/ExtraSmokeMinLength") return SmokeKey;
            if (f.Key == "Effects/SmokeStartOpacity" || f.Key == "Effects/SmokeEndOpacity" ||f.Key == "Effects/SmokeModerateFrom" || f.Key == "Effects/SmokeSevereFrom") return DensityKey;
            return null;
        }
        static bool IsOn(string key)
        {
            string v;
            if (key == UiKey) return UiOn();
            if (_globals.TryGetValue(key, out v)) return v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            if (_work == null || !_work.Values.TryGetValue(key, out v)) return false;
            return v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        /// <summary>Tabs of the damage model: hidden while Alternative Damage Control is off.</summary>
        static bool ModelTab(string tab) { return tab != TUi && tab != TGen && tab != TFx && tab != TDbg; }

        static readonly List<Field> Fields = new List<Field>();

        static void Add(string tab, string group, string key, string label, string unit, Kind kind, float step, float min, float max, bool global = false)
        {
            Fields.Add(new Field { Tab = tab, Group = group, Key = key, Label = label, Unit = unit, Kind = kind, Step = step, Min = min, Max = max, Global = global });
        }

        static SettingsWindow()
        {
            const string H = "Hull", FF = "Fire", DC = "DC Teams", S = "Flooding", D = TUi, G = TGen;
            Add(D, "Damage Control tab", UiKey, "Detailed view", "applies at once", Kind.Bool, 0, 0, 0, true);
            Add(D, "Bottom panel", "UI/ColorWeaponStatus", "Weapon status colours", "yellow: part inoperable, red: all", Kind.Bool, 0, 0, 0, true);
            Add(D, ArrowsGroup, "UI/FloodingOneArrowAt", "Water: one arrow from", "% a second", Kind.Number, 0.01f, 0f, 10f, true);
            Add(D, ArrowsGroup, "UI/FloodingTwoArrowsAt", "Water: two arrows from", "% a second", Kind.Number, 0.05f, 0f, 10f, true);
            Add(D, ArrowsGroup, "UI/FireOneArrowAt", "Fire: one arrow from", "% a second", Kind.Number, 0.01f, 0f, 10f, true);
            Add(D, ArrowsGroup, "UI/FireTwoArrowsUpAt", "Fire: two arrows up from", "% a second", Kind.Number, 0.1f, 0f, 20f, true);
            Add(D, ArrowsGroup, "UI/FireTwoArrowsDownAt", "Fire: two arrows down from", "% a second", Kind.Number, 0.05f, 0f, 20f, true);

            Add(TFx, "Fire smoke", SmokeKey, "Extra smoke points", "5 instead of 3, one per section", Kind.Bool, 0, 0, 0, true);
            Add(TFx, "Fire smoke", "Effects/ExtraSmokeMinLength", "On ships from", "m long", Kind.Number, 10f, 0f, 500f, true);
            Add(TFx, "Fire smoke", DensityKey, "Tune visuals", "instead of the game's stages", Kind.Bool, 0, 0, 0, true);
            Add(TFx, "Fire smoke", "Effects/SmokeStartOpacity", "Small fire: opacity at start", "% of the game's, fire at 0%", Kind.Number, 5f, 0f, 100f, true);
            Add(TFx, "Fire smoke", "Effects/SmokeEndOpacity", "Small fire: opacity at end", "% of the game's, at moderate", Kind.Number, 5f, 0f, 100f, true);
            Add(TFx, "Fire smoke", "Effects/SmokeModerateFrom", "Moderate fire look from", "% of max fire (game 25)", Kind.Number, 5f, 1f, 100f, true);
            Add(TFx, "Fire smoke", "Effects/SmokeSevereFrom", "Severe fire look from", "% of max fire (game 50)", Kind.Number, 5f, 1f, 100f, true);

            Add(G, "Damage model", AdmKey, "Alternative Damage Control", "applies at once", Kind.Bool, 0, 0, 0, true);
            Add(G, "Damage model", "General/FixHighTimeCompressionTrimError", "Fix trim compute error", "above 10x time compression", Kind.Bool, 0, 0, 0, true);

            Add(TDbg, "Logging (BepInEx log)", "General/LogShipIntegrity", "Hull of every ship", "", Kind.Bool, 0, 0, 0, true);
            Add(TDbg, "Logging (BepInEx log)", "General/LogFires", "Fires", "", Kind.Bool, 0, 0, 0, true);
            Add(TDbg, "Logging (BepInEx log)", "General/LogHeel", "List, trim and buoyancy", "", Kind.Bool, 0, 0, 0, true);
            Add(TDbg, "Logging (BepInEx log)", "General/Debug", "Debug trace", "", Kind.Bool, 0, 0, 0, true);
            Add(TDbg, "Visualisation (selected ship)", "General/ShowDeckCorners", "Deck point heights", "wave to hull-only deck, 10 points", Kind.Bool, 0, 0, 0, true);
            Add(TDbg, "Visualisation (selected ship)", "General/ShowHullCollider", "Hull collider (translucent)", "coll_hull* only", Kind.Bool, 0, 0, 0, true);
            Add(TDbg, "Visualisation (selected ship)", "General/ShipModelView", "Ship's 3D model", "for the two above", Kind.Choice, 0, 0, 0, true);
            Fields[Fields.Count - 1].Options = new[] { "Show", "Translucent", "Hide" };

            Add(H, "Hull integrity", "HullIntegrity/Warships", "Warships", "× game", Kind.Number, 0.5f, 0.1f, 20f);
            Add(H, "Hull integrity", "HullIntegrity/CivilianShips", "Civilian ships", "× game", Kind.Number, 0.5f, 0.1f, 20f);
            Add(H, "Hull integrity", "HullIntegrity/Submarines", "Submarines", "× game", Kind.Number, 0.5f, 0.1f, 20f);
            Add(H, "Vulnerability of the stronger hull", "SideEffects/HullShellVulnerability", "Gun shells", "", Kind.Number, 0.1f, 0.1f, 10f);
            Add(H, "Vulnerability of the stronger hull", "SideEffects/HullFireVulnerability", "Fires", "", Kind.Number, 0.1f, 0.1f, 10f);
            Add(H, "Vulnerability of the stronger hull", "SideEffects/HullFloodingVulnerability", "Flooding", "", Kind.Number, 0.1f, 0.1f, 10f);

            Add("Systems", "Systems", "Systems/Integrity", "Integrity", "× game", Kind.Number, 0.1f, 0.1f, 20f);
            Add(FF, "Fire", "FireAndFlooding/FlameUpSpeed", "Fire growth", "× game", Kind.Number, 0.1f, 0f, 10f);
            Add(FF, "Fire", "FireAndFlooding/FireHullDamageSpeed", "Fire damage to the hull", "× game", Kind.Number, 0.1f, 0f, 10f);
            Add(FF, "Fire", "FireAndFlooding/FireSpreadPercent", "Fire spread chance", "% per fire check (10 s)", Kind.Number, 0.005f, 0f, 100f);
            Add(FF, "Unattended fire", UnattendedKey, "Grows when nobody fights it", "", Kind.Bool, 0, 0, 0);
            Add(FF, "Unattended fire", "FireAndFlooding/UnattendedFireTime", "Unattended after", "s without a team", Kind.Number, 10f, 0f, 1200f);
            Add(FF, "Unattended fire", "FireAndFlooding/UnattendedFireGrowthRate", "Growth added", "% of max growth per fire check (10 s)", Kind.Number, 0.05f, 0f, 100f);
            Add(FF, "Unattended fire", "FireAndFlooding/UnattendedFireMaxGrowth", "Maximum growth rate", "% of max fire a second", Kind.Number, 0.1f, 0f, 100f);

            Add(DC, "Number of teams - from tons: teams", "DamageControlTeams/WarshipTeams", "Warships", "", Kind.Table, 0, 0, 0);
            Add(DC, "Number of teams - from tons: teams", "DamageControlTeams/SubmarineTeams", "Submarines", "", Kind.Table, 0, 0, 0);
            Add(DC, "Number of teams - from tons: teams", "DamageControlTeams/CivilianTeams", "Civilian ships", "", Kind.Table, 0, 0, 0);
            Add(DC, "Work and movement", "DamageControlTeams/DCTeamEffectiveness", "Team effectiveness", "× game", Kind.Number, 0.1f, 0.05f, 5f);
            Add(DC, "Work and movement", "FireAndFlooding/ExtinguishSpeed", "Extinguishing", "× game", Kind.Number, 0.1f, 0f, 10f);
            Add(DC, "Work and movement", "FireAndFlooding/PumpOutSpeed", "Pumping out", "× game", Kind.Number, 0.1f, 0f, 10f);
            Add(DC, "Work and movement", "DamageControlTeams/TransferSpeed", "Transfer speed", "m/min · 0 = instant", Kind.Number, 5f, 0f, 1000f);
            Add(DC, "Work and movement", "DamageControlTeams/MusterTime", "Muster time", "s", Kind.Number, 10f, 0f, 600f);
            Add(DC, "Knock-outs", "DamageControlTeams/HealTime", "Recovery time", "s + transfer back", Kind.Number, 10f, 0f, 1200f);
            Add(DC, "Knock-outs", "DamageControlTeams/HealTimeSpread", "Recovery spread", "± s", Kind.Number, 10f, 0f, 600f);
            Add(DC, "Knock-outs", "DamageControlTeams/KnockoutsToDeath", "Knock-outs to death", "0 = never", Kind.Integer, 1f, 0f, 9f);

            Add(S, "Flooding rate", "FireAndFlooding/FloodingSpeed", "Water coming in", "× game", Kind.Number, 0.1f, 0f, 10f);
            Add(S, "Flooding rate", "Sinking/DeckFloodingSpeed", "Water over the deck", "× · 1 = 0.1% per s per m under water", Kind.Number, 0.1f, 0f, 10f);
            Add(S, "Flooding rate", "Sinking/DeckFloodingDelay", "Time under water", "s before water over the deck starts", Kind.Number, 0.5f, 0f, 60f);
            Add(S, "Flooding rate", "Sinking/CapsizeFactor", "Capsizing", "× · centre of mass rises with the water; 0 = off", Kind.Number, 0.1f, 0f, 5f);
            Add(S, "Gauge scales (faster flooding from their red zone, 75%)", "Sinking/ListLimit", "List", "° · water over the deck edge from 75%", Kind.Number, 0.5f, 5f, 69f);
            Add(S, "Gauge scales (faster flooding from their red zone, 75%)", "Sinking/TrimLimit", "Trim", "° at the reference length", Kind.Number, 0.1f, 1f, 30f);
            Add(S, "Gauge scales (faster flooding from their red zone, 75%)", "Sinking/TrimReferenceLength", "Trim: reference length", "m · 0 = same for all", Kind.Number, 10f, 0f, 500f);
            Add(S, "Sinking from", "Sinking/FloodLimit", "Water in the ship", "% of the hull", Kind.Number, 1f, 5f, 100f);
        }

        // ------------------------------------------------------------ colours (the game's dark panels)
        static readonly Color CBg = Color.FromRgb(30, 33, 38), CPanel = Color.FromRgb(38, 42, 48), CCard = Color.FromRgb(45, 50, 57),
                              CLine = Color.FromRgb(70, 76, 86), CText = Color.FromRgb(230, 232, 235), CDim = Color.FromRgb(150, 156, 162),
                              CWarn = Color.FromRgb(236, 212, 110), CTitle = Color.FromRgb(43, 48, 56), CField = Color.FromRgb(21, 24, 28),
                              CErr = Color.FromRgb(232, 96, 80), CHl = Color.FromRgb(52, 58, 66);

        static SolidColorBrush B(Color c) { return new SolidColorBrush(c); }

        /// <summary>The game's value next to a list row: as the other rows' (grey, 11 px); a type the game scales with tonnage in light blue.</summary>
        static readonly Color CGame = Color.FromRgb(132, 186, 238);
        static TextBlock GameLabel(string text, bool accent)
        {
            return new TextBlock { Text = text, Foreground = B(accent ? CGame : CDim), FontSize = 11f, TextAlignment = TextAlignment.Right,
                                   VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        }

        // ------------------------------------------------------------ state
        static Popup _popup;
        static UIElement _top;             // the screen the window is centred on (mouse positions for dragging)
        static Grid _root;                 // window content + the overlay of the small dialogs
        static Border _overlay;
        static ComboBox _combo;
        static TextBlock _note, _help, _tag, _status;
        static Button _rename, _delete, _reset;
        static StackPanel _body, _profRow;
        static readonly List<Border> _tabButtons = new List<Border>();
        static readonly List<Ellipse> _tabDots = new List<Ellipse>();
        static int _tab;
        static bool _syncing;

        static string _selected;           // the profile shown (Custom once a built-in one is edited)
        static Profile _from;              // the stored values of the profile the work came from (dots, ↺, Reset)
        static Profile _work;              // the values being edited
        static Profile _game;              // Base Game: the "game:" column
        static readonly Dictionary<string, string> _globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> _globalsFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // debug visualisation toggles: flipping them should show up in the game at once, not wait for Apply/OK -
        // written straight to their own ConfigEntry as soon as the checkbox changes (Set), same as Commit would do
        // for every global on Apply, just for these three right away. Cfg.SettingChanged then does the rest
        // (AdmPlugin.ReadSettings), same path as any other live settings change.
        static readonly HashSet<string> _liveGlobals = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "General/ShowDeckCorners", "General/ShowHullCollider", "General/ShipModelView",
        };

        static AdmPlugin P { get { return AdmPlugin.Instance; } }

        static ConfigEntryBase Entry(string key)
        {
            if (P == null) return null;
            foreach (var e in P.ProfileEntries) if (string.Equals(e.Definition.Section + "/" + e.Definition.Key, key, StringComparison.OrdinalIgnoreCase)) return e;
            foreach (var e in P.GlobalEntries) if (string.Equals(e.Definition.Section + "/" + e.Definition.Key, key, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        public static bool IsOpen { get { return (_host != null && _root != null && SafeParent(_root) != null) || (_popup != null && _popup.IsOpen); } }

        static Panel _host;                // the top panel of the screen the window was put into (null: shown in a Popup)
        static TranslateTransform _move;   // dragging

        public static Action<string> Warn = s => { };
        public static Action<string> Info = s => { };
        static bool _saidOpen;

        // ------------------------------------------------------------ open / close

        /// <summary>
        /// Opens the window in the middle of the screen that holds 'anchor' (the Damage Control view). It is put into the top
        /// panel of that screen, above everything (a Popup created in code alone did not show); a Popup only when there is no
        /// such panel.
        /// </summary>
        public static void Open(UIElement anchor) { Open(anchor, null); }

        /// <summary>Opens the window on the screen of 'anchor' (the gear clicked); 'other' when the gear's own screen has no panel for it.</summary>
        public static void Open(UIElement anchor, UIElement other)
        {
            if (anchor == null) { anchor = other; other = null; }
            if (P == null || anchor == null) { Warn("Settings window: not ready."); return; }
            try
            {
                if (IsOpen)
                {
                    bool shown = false;
                    try { shown = _root != null && _root.IsVisible; } catch { }
                    if (shown) return;   // open already
                    Trace("settings window: left over from an earlier screen, opened again");
                }
                Close();
                // the screen's root, and the topmost panel on the way up to it
                DependencyObject top = anchor; Panel panel = null;
                for (DependencyObject p = anchor; p != null; p = SafeParent(p))
                {
                    top = p;
                    var pn = p as Panel;
                    bool itemsHost = false;
                    try { itemsHost = pn != null && pn.IsItemsHost; } catch { }
                    if (pn != null && !itemsHost && (pn is Grid || pn is Canvas)) panel = pn;
                }
                if (panel == null && other != null)
                    for (DependencyObject p = other; p != null; p = SafeParent(p))
                    {
                        top = p;
                        var pn = p as Panel;
                        bool itemsHost = false;
                        try { itemsHost = pn != null && pn.IsItemsHost; } catch { }
                        if (pn != null && !itemsHost && (pn is Grid || pn is Canvas)) panel = pn;
                    }
                _top = top as UIElement ?? anchor;
                LoadFrom(P.ActiveProfileName);
                BuildWindow();
                _move = new TranslateTransform(0f, 0f);
                _place = 0;
                _root.LayoutUpdated += OnLayout;   // size in screen pixels and centring on the screen: once laid out (below)
                _root.RenderTransform = _move;
                if (panel != null)
                {
                    var grid = panel as Grid;
                    if (grid != null)
                    {
                        _root.HorizontalAlignment = HorizontalAlignment.Center;
                        _root.VerticalAlignment = VerticalAlignment.Center;
                        Grid.SetRow(_root, 0); Grid.SetColumn(_root, 0);
                        Grid.SetRowSpan(_root, Math.Max(1, grid.RowDefinitions.Count));
                        Grid.SetColumnSpan(_root, Math.Max(1, grid.ColumnDefinitions.Count));
                    }
                    else
                    {
                        float w = panel.ActualWidth > 0 ? panel.ActualWidth : 1280f, h = panel.ActualHeight > 0 ? panel.ActualHeight : 720f;
                        Canvas.SetLeft(_root, Math.Max(0f, (w - _root.Width) / 2f));
                        Canvas.SetTop(_root, Math.Max(0f, (h - _root.Height) / 2f));
                    }
                    Panel.SetZIndex(_root, 30000);
                    panel.Children.Add(_root);
                    _host = panel;
                    Trace("settings window: in " + panel.GetType().Name + " (" + panel.ActualWidth + " x " + panel.ActualHeight + ")");
                }
                else
                {
                    _popup = new Popup
                    {
                        Child = _root, StaysOpen = true, AllowsTransparency = true,
                        Placement = PlacementMode.Center, PlacementTarget = _top
                    };
                    _popup.IsOpen = true;
                    Trace("settings window: popup");
                }
                ShowTab(_tab);
                if (!_saidOpen) { _saidOpen = true; Info("Settings window opened (" + (_host != null ? "in " + _host.GetType().Name : "popup") + ")."); }
            }
            catch (Exception e) { Warn("Settings window could not be opened: " + e.Message); Trace("SettingsWindow.Open: " + e); }
        }

        static DependencyObject SafeParent(DependencyObject e) { try { return VisualTreeHelper.GetParent(e); } catch { return null; } }

        static void Close()
        {
            Typing(false);
            try { if (_host != null && _root != null) _host.Children.Remove(_root); } catch { }
            try { var pp = _root == null ? null : SafeParent(_root) as Panel; if (pp != null) pp.Children.Remove(_root); } catch { }   // wherever it is now
            try { if (_popup != null) { _popup.IsOpen = false; _popup.Child = null; } } catch { }
            _host = null; _popup = null;
        }

        /// <summary>The .cfg was changed outside the window: the window shows it, unless there are unsaved edits.</summary>
        public static void Refresh()
        {
            if (!IsOpen || P == null) return;
            if (Dirty()) return;
            LoadFrom(P.ActiveProfileName);
            RebuildProfiles();
            ShowTab(_tab);
        }

        static void LoadFrom(string name)
        {
            _game = P.LoadProfile(AdmPlugin.BaseGameName);
            var cur = P.CurrentValues();
            _selected = name;
            _from = P.LoadProfile(name);
            if (string.Equals(name, P.ActiveProfileName, StringComparison.OrdinalIgnoreCase)) _work = cur.Copy(name);
            else _work = _from != null ? _from.Copy(name) : cur.Copy(name);
            if (_from == null) _from = _work.Copy(name);
            _globals.Clear(); _globalsFrom.Clear();
            foreach (var e in P.GlobalEntries)
            {
                string k = e.Definition.Section + "/" + e.Definition.Key;
                _globals[k] = _globalsFrom[k] = e.GetSerializedValue();
            }
        }

        static bool Dirty()
        {
            if (_work == null || _from == null) return false;
            if (!string.Equals(_selected, P.ActiveProfileName, StringComparison.OrdinalIgnoreCase)) return true;
            if (!_work.SameAs(_from)) return true;
            foreach (var kv in _globals) { string v; if (!_globalsFrom.TryGetValue(kv.Key, out v) || !AdmPlugin.SameValue(v, kv.Value)) return true; }
            return false;
        }

        // ------------------------------------------------------------ the game's keyboard while typing

        static PropertyInfo _ihInstance; static FieldInfo _ihTyping; static bool _ihTried;

        static void Typing(bool on)
        {
            try
            {
                if (!_ihTried)
                {
                    _ihTried = true;
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (a.GetName().Name != "Seapower-Scripts") continue;
                        var ih = a.GetType("SeaPower.InputHandler"); var sg = a.GetType("SeaPower.Singleton`1");
                        if (ih != null && sg != null)
                        {
                            _ihInstance = sg.MakeGenericType(ih).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
                            _ihTyping = ih.GetField("typingActive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        }
                    }
                }
                if (_ihInstance == null || _ihTyping == null) return;
                var inst = _ihInstance.GetValue(null, null);
                if (inst != null) _ihTyping.SetValue(inst, on);
            }
            catch (Exception e) { Trace("typing: " + e.Message); }
        }

        static TextBox Edit(string text, float width)
        {
            var t = new TextBox { Text = text ?? "", Width = width, Height = 24f, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                                  Background = B(CField), Foreground = B(CText), BorderBrush = B(CLine), Padding = new Thickness(2f, 0f, 2f, 0f) };
            t.GotFocus += (s, e) => { Typing(true); try { t.SelectAll(); } catch { } };
            t.LostFocus += (s, e) => Typing(false);
            return t;
        }

        // ------------------------------------------------------------ building

        static TextBlock Text(string s, Color c, float size = 13f)
        {
            return new TextBlock { Text = s, Foreground = B(c), FontSize = size, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
        }

        static Button Btn(string s, Action click, float minW = 0f)
        {
            var b = new Button { Content = s, Padding = new Thickness(10f, 2f, 10f, 2f), Margin = new Thickness(0f, 0f, 6f, 0f), MinHeight = 26f, MinWidth = minW };
            b.Click += (o, e) => { try { click(); } catch (Exception ex) { Trace("button " + s + ": " + ex); } };
            return b;
        }

        static void BuildWindow()
        {
            _root = new Grid { Width = _winW, Height = _winH };
            var win = new Border { Background = B(CBg), BorderBrush = B(CLine), BorderThickness = new Thickness(1f), CornerRadius = new CornerRadius(4f) };
            _root.Children.Add(win);
            var main = new StackPanel { Orientation = Orientation.Vertical };   // filled top to bottom, then moved into 'layout' below

            // title bar: drag to move, ✕ to close
            var title = new Grid { Background = B(CTitle), Height = 32f };
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1f, GridUnitType.Star) });
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tt = Text("Alternative Damage Model - Settings", CText, 14f); tt.Margin = new Thickness(10f, 0f, 0f, 0f);
            title.Children.Add(tt);
            var x = new Button { Content = Cross(10f), Width = 28f, Height = 24f, Padding = new Thickness(0f), Margin = new Thickness(0f, 0f, 4f, 0f),
                                 HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            x.Click += (o, e) => Close();
            Grid.SetColumn(x, 1);
            title.Children.Add(x);
            Draggable(title);
            main.Children.Add(title);

            // profile row
            var prof = _profRow = new StackPanel { Orientation = Orientation.Horizontal, Background = B(CPanel), Margin = new Thickness(0f, 1f, 0f, 0f) };
            var pin = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12f, 6f, 12f, 6f) };
            prof.Children.Add(pin);
            var pl = Text("Profile", CDim); pl.Margin = new Thickness(0f, 0f, 8f, 0f);
            pin.Children.Add(pl);
            _combo = new ComboBox { Width = 180f, MinHeight = 26f, Margin = new Thickness(0f, 0f, 6f, 0f), VerticalContentAlignment = VerticalAlignment.Center };
            _combo.SelectionChanged += (o, e) => OnPick();
            pin.Children.Add(_combo);
            _tag = Text("", CWarn, 11f); _tag.Margin = new Thickness(0f, 0f, 8f, 0f);
            pin.Children.Add(_tag);
            pin.Children.Add(Btn("Save as…", () => Ask("Save profile as", Suggest(), SaveAs)));
            _rename = Btn("Rename…", () => Ask("Rename profile", _selected, Rename));
            pin.Children.Add(_rename);
            _delete = Btn("Delete", () => Confirm("Delete profile \"" + _selected + "\"?", Delete));
            pin.Children.Add(_delete);
            _note = Text("", CDim, 11f);
            pin.Children.Add(_note);
            // the profile row is shown on the General tab (ShowTab), not above the tabs

            // tabs
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12f, 10f, 12f, 0f) };
            _tabButtons.Clear(); _tabDots.Clear();
            for (int i = 0; i < Tabs.Length; i++)
            {
                int idx = i;
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(Text(Tabs[i], CDim));
                var dot = new Ellipse { Width = 6f, Height = 6f, Fill = B(CWarn), Margin = new Thickness(5f, 0f, 0f, 0f), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
                row.Children.Add(dot);
                var tb = new Border { Child = row, Padding = new Thickness(12f, 6f, 12f, 6f), Margin = new Thickness(0f, 0f, 2f, 0f), BorderBrush = B(CLine),
                                      BorderThickness = new Thickness(1f, 1f, 1f, 0f), CornerRadius = new CornerRadius(3f, 3f, 0f, 0f), Background = B(CPanel) };
                try { tb.Cursor = Cursors.Hand; } catch { }
                tb.MouseLeftButtonUp += (o, e) => { ShowTab(idx); e.Handled = true; };
                _tabButtons.Add(tb); _tabDots.Add(dot);
                tabs.Children.Add(tb);
            }
            main.Children.Add(tabs);

            _body = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0f, 6f, 0f, 6f) };
            var scroll = _scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var settingsArea = new Border { Child = scroll, Background = B(CCard), BorderBrush = B(CLine), BorderThickness = new Thickness(1f), Margin = new Thickness(12f, 0f, 12f, 0f) };
            main.Children.Add(settingsArea);

            _help = new TextBlock { Foreground = B(Color.FromRgb(194, 199, 204)), FontSize = 12f, TextWrapping = TextWrapping.Wrap, Text = "Point at a setting to see what it does." };
            main.Children.Add(new Border { Child = _help, Background = B(Color.FromRgb(34, 38, 44)), BorderBrush = B(CLine), BorderThickness = new Thickness(1f),
                                           Padding = new Thickness(10f, 8f, 10f, 8f), Margin = new Thickness(12f, 8f, 12f, 0f), Height = 108f, CornerRadius = new CornerRadius(3f) });   // fixed: 6 lines - the window does not jump as the text changes

            // footer
            var foot = new Grid { Margin = new Thickness(12f, 10f, 12f, 12f) };
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1f, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _reset = Btn("Reset to profile", ResetAll);
            foot.Children.Add(_reset);
            _status = Text("", CDim, 11f); _status.HorizontalAlignment = HorizontalAlignment.Right; _status.Margin = new Thickness(0f, 0f, 10f, 0f);
            Grid.SetColumn(_status, 1); foot.Children.Add(_status);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(Btn("Cancel", Close, 70f));
            right.Children.Add(Btn("Apply", () => Commit(false), 70f));
            var ok = Btn("OK", () => Commit(true), 70f); ok.Margin = new Thickness(0f);
            right.Children.Add(ok);
            Grid.SetColumn(right, 2); foot.Children.Add(right);
            main.Children.Add(foot);

            // the rows: all as tall as they need, the settings area (4th) takes the rest of the window's height
            var layout = new Grid();
            var parts = new List<UIElement>();
            foreach (UIElement e in main.Children) parts.Add(e);
            main.Children.Clear();
            for (int r = 0; r < parts.Count; r++)
            {
                layout.RowDefinitions.Add(new RowDefinition { Height = ReferenceEquals(parts[r], settingsArea) ? new GridLength(1f, GridUnitType.Star) : GridLength.Auto });
                Grid.SetRow(parts[r], r);
                layout.Children.Add(parts[r]);
            }
            win.Child = layout;

            AddResizeHandles();

            _overlay = new Border { Background = B(Color.FromArgb(150, 0, 0, 0)), CornerRadius = new CornerRadius(4f), Visibility = Visibility.Collapsed };
            _root.Children.Add(_overlay);

            RebuildProfiles();
        }

        /// <summary>A drawn ✕ for a button: the font's glyph sat low in the button, cut at the bottom and off centre.</summary>
        static Path Cross(float size)
        {
            return new Path { Data = new StreamGeometry("M0,0 L10,10 M10,0 L0,10"), Stroke = B(CText), StrokeThickness = 1.5f, Stretch = Stretch.Uniform,
                              Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        // drag the window by its title bar
        static void Draggable(FrameworkElement handle)
        {
            bool down = false; Point start = new Point(); float h0 = 0f, v0 = 0f;
            handle.MouseLeftButtonDown += (o, e) =>
            {
                if (_move == null) return;
                down = true; start = e.GetPosition(_top); h0 = _move.X; v0 = _move.Y;
                handle.CaptureMouse(); e.Handled = true;
            };
            handle.MouseMove += (o, e) =>
            {
                if (!down || _move == null) return;
                var p = e.GetPosition(_top);
                _move.X = h0 + (p.X - start.X);
                _move.Y = v0 + (p.Y - start.Y);
            };
            handle.MouseLeftButtonUp += (o, e) => { if (!down) return; down = false; handle.ReleaseMouseCapture(); e.Handled = true; };
        }

        // ------------------------------------------------------------ placing on the screen
        // The window lives in the panel of the Unit Status window (its own UI root), and the game scales its UI: sizes are set in
        // screen pixels (1040 x 990 by default, at most the screen less 40) and the window is centred on the screen, not in that panel.
        static int _place;
        static bool _sizedOnce;

        static void OnLayout(object sender, Noesis.EventArgs e)
        {
            if (_root == null || _place >= 2) return;
            try
            {
                if (_root.ActualWidth <= 0f) return;
                var a = _root.PointToScreen(new Point(0f, 0f)); var b = _root.PointToScreen(new Point(100f, 0f));
                float k = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)) / 100f;   // screen pixels per UI unit
                if (k <= 0.01f || float.IsNaN(k)) { _place = 2; return; }
                float sw = UnityEngine.Screen.width, sh = UnityEngine.Screen.height;
                if (_place == 0)
                {
                    if (!_sizedOnce) { _winW = PixW / k; _winH = PixH / k; _sizedOnce = true; }   // the first time: 1040 x 990 screen pixels
                    _root.Width = Math.Min(_winW, (sw - 40f) / k);
                    _root.Height = Math.Min(_winH, (sh - 40f) / k);
                    _place = 1;
                    Trace("settings window: " + k.ToString("0.##") + " px per unit, " + _root.Width.ToString("0") + " x " + _root.Height.ToString("0") + " units");
                    return;   // laid out again with the new size
                }
                // centre on the screen
                float w = _root.Width * k, h = _root.Height * k;
                _move.X += ((sw - w) / 2f - a.X) / k;
                _move.Y += ((sh - h) / 2f - a.Y) / k;
                _place = 2;
                _root.LayoutUpdated -= OnLayout;
            }
            catch (Exception ex) { _place = 2; Trace("settings window placing: " + ex.Message); }
        }

        const float PixW = 1050f, PixH = 1000f;   // +80: the Effects tab; +10: the Debug tab

        // ------------------------------------------------------------ size (kept while the game runs)
        static float _winW = 1050f, _winH = 1000f;   // UI units; set from screen pixels at the first opening
        static ScrollViewer _scroll;

        /// <summary>
        /// Resizing by the window's edges (5 px strips) and corners (16 px; the bottom right one shows the grip lines):
        /// 560..1600 x 420..1400, the settings area takes the change. Dragging the left or top side keeps the opposite side in place.
        /// The size is kept while the game runs.
        /// </summary>
        static void AddResizeHandles()
        {
            for (int sy = -1; sy <= 1; sy++)
                for (int sx = -1; sx <= 1; sx++)
                    if (sx != 0 || sy != 0) _root.Children.Add(ResizeHandle(sx, sy));
        }

        /// <param name="sx">-1 left, 1 right, 0 neither</param>
        /// <param name="sy">-1 top, 1 bottom, 0 neither</param>
        static FrameworkElement ResizeHandle(int sx, int sy)
        {
            const float Edge = 5f, Corner = 16f;
            bool corner = sx != 0 && sy != 0;
            var h = new Border { Background = B(Color.FromArgb(0, 0, 0, 0)),
                                 HorizontalAlignment = sx < 0 ? HorizontalAlignment.Left : sx > 0 ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
                                 VerticalAlignment = sy < 0 ? VerticalAlignment.Top : sy > 0 ? VerticalAlignment.Bottom : VerticalAlignment.Stretch };
            if (sx != 0) h.Width = corner ? Corner : Edge;
            if (sy != 0) h.Height = corner ? Corner : Edge;
            if (sx == 0) h.Margin = new Thickness(Corner, 0f, Corner, 0f);        // the corners belong to the corner handles
            else if (sy == 0) h.Margin = new Thickness(0f, Corner, 0f, Corner);
            if (sx > 0 && sy > 0)
            {
                h.Margin = new Thickness(0f, 0f, 1f, 1f);
                h.Child = new Path { Data = new StreamGeometry("M15,4 L4,15 M15,8 L8,15 M15,12 L12,15"), Stroke = B(CDim), StrokeThickness = 1f };
                try { ToolTipService.SetToolTip(h, "Drag to resize"); } catch { }
            }
            try { h.Cursor = corner ? (sx == sy ? Cursors.SizeNWSE : Cursors.SizeNESW) : sx != 0 ? Cursors.SizeWE : Cursors.SizeNS; } catch { }
            bool down = false; Point start = new Point(); float w0 = 0f, h0 = 0f;
            h.MouseLeftButtonDown += (o, e) =>
            {
                down = true; start = e.GetPosition(_top); w0 = _winW; h0 = _winH;
                h.CaptureMouse(); e.Handled = true;
            };
            h.MouseMove += (o, e) =>
            {
                if (!down || _root == null) return;
                var p = e.GetPosition(_top);
                float w = sx == 0 ? _winW : Math.Max(560f, Math.Min(1600f, w0 + sx * (p.X - start.X)));
                float hh = sy == 0 ? _winH : Math.Max(420f, Math.Min(1400f, h0 + sy * (p.Y - start.Y)));
                float dw = w - _winW, dh = hh - _winH;
                if (Math.Abs(dw) < 0.5f && Math.Abs(dh) < 0.5f) return;
                _winW = w; _winH = hh;
                _root.Width = w; _root.Height = hh;
                if (_move == null) return;
                // a window centred in a Grid grows both ways; one placed by its top left corner grows right / down:
                // move it so that only the dragged side moves
                bool centred = _host is Grid;
                if (sx != 0) _move.X += centred ? sx * dw / 2f : (sx < 0 ? -dw : 0f);
                if (sy != 0) _move.Y += centred ? sy * dh / 2f : (sy < 0 ? -dh : 0f);
            };
            h.MouseLeftButtonUp += (o, e) => { if (!down) return; down = false; h.ReleaseMouseCapture(); e.Handled = true; };
            return h;
        }

        static void RebuildProfiles()
        {
            if (_combo == null) return;
            _syncing = true;
            try
            {
                _combo.Items.Clear();
                var names = AdmPlugin.ProfileNames();
                bool has = false;
                foreach (var n in names) if (string.Equals(n, _selected, StringComparison.OrdinalIgnoreCase)) has = true;
                if (!has) names.Add(_selected);
                int sel = 0;
                for (int i = 0; i < names.Count; i++)
                {
                    _combo.Items.Add(names[i] + (AdmPlugin.IsBuiltIn(names[i]) ? "   (built-in)" : ""));
                    if (string.Equals(names[i], _selected, StringComparison.OrdinalIgnoreCase)) sel = i;
                }
                _comboNames = names;
                _combo.SelectedIndex = sel;
            }
            finally { _syncing = false; }
            UpdateHeader();
        }

        static List<string> _comboNames = new List<string>();

        static void OnPick()
        {
            if (_syncing || _combo == null) return;
            int i = _combo.SelectedIndex;
            if (i < 0 || i >= _comboNames.Count) return;
            string name = _comboNames[i];
            if (string.Equals(name, _selected, StringComparison.OrdinalIgnoreCase)) return;
            var p = P.LoadProfile(name);
            if (p == null) return;
            _selected = p.Name; _from = p; _work = p.Copy(p.Name);
            RebuildProfiles();
            ShowTab(_tab);
        }

        static void UpdateHeader()
        {
            if (_tag == null) return;
            bool builtIn = AdmPlugin.IsBuiltIn(_selected);
            bool edited = _work != null && !_work.SameAs(_from);
            bool active = string.Equals(_selected, P.ActiveProfileName, StringComparison.OrdinalIgnoreCase);
            _tag.Text = edited ? "edited" : (active ? "in use" : "");
            _tag.Foreground = B(edited ? CWarn : CDim);
            bool saved = !builtIn && Array.Exists(AdmPlugin.ProfileNames().ToArray(), n => string.Equals(n, _selected, StringComparison.OrdinalIgnoreCase));
            _rename.IsEnabled = !builtIn;
            _delete.IsEnabled = saved;
            _reset.IsEnabled = edited;
            _note.Text = _from != null && !string.Equals(_from.Name, _selected, StringComparison.OrdinalIgnoreCase) ? "based on " + _from.Name : "";
            if (_status != null) _status.Text = edited ? "● differs from " + (_from != null ? _from.Name : "the profile") + "   ↺ = back to it" : "";
            for (int t = 0; t < Tabs.Length && t < _tabDots.Count; t++)
            {
                bool any = false;
                foreach (var f in Fields) if (f.Tab == Tabs[t] && Differs(f)) { any = true; break; }
                if (Tabs[t] == "Hull" && _work != null && !MapSame(p => p.Ships)) any = true;
                if (Tabs[t] == "Systems" && _work != null && !MapSame(p => p.Modules)) any = true;
                _tabDots[t].Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ------------------------------------------------------------ values

        static string Get(Field f)
        {
            string v;
            if (f.Global) return _globals.TryGetValue(f.Key, out v) ? v : "";
            return _work != null && _work.Values.TryGetValue(f.Key, out v) ? v : "";
        }

        static string FromValue(Field f)
        {
            string v;
            if (f.Global) return _globalsFrom.TryGetValue(f.Key, out v) ? v : "";
            return _from != null && _from.Values.TryGetValue(f.Key, out v) ? v : "";
        }

        static bool AdmOn()
        {
            string v;
            return !_globals.TryGetValue(AdmKey, out v) || !v.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        static bool UiOn()
        {
            string v;
            return !_globals.TryGetValue(UiKey, out v) || !v.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        static bool Differs(Field f) { return !AdmPlugin.SameValue(Get(f), FromValue(f)); }

        static bool MapSame(Func<Profile, SortedDictionary<string, string>> mapOf)
        {
            if (_from == null || _work == null) return true;
            var a = mapOf(_from); var b = mapOf(_work);
            if (a.Count != b.Count) return false;
            foreach (var kv in b) { string v; if (!a.TryGetValue(kv.Key, out v) || !AdmPlugin.SameValue(v, kv.Value)) return false; }
            return true;
        }

        /// <summary>A value changed in the window. A built-in profile becomes Custom.</summary>
        static void Set(Field f, string v)
        {
            if (f.Global)
            {
                _globals[f.Key] = v;
                if (_liveGlobals.Contains(f.Key))
                {
                    var e = Entry(f.Key);
                    if (e != null) try { e.SetSerializedValue(v); } catch (Exception ex) { Warn(f.Key + ": " + ex.Message); }
                }
            }
            else
            {
                _work.Values[f.Key] = v;
                BecomeOwn();
            }
            UpdateHeader();
        }

        static void BecomeOwn()
        {
            if (!AdmPlugin.IsBuiltIn(_selected) || _work.SameAs(_from)) return;
            _selected = AdmPlugin.CustomName;
            _work.Name = _selected;
            RebuildProfiles();
        }

        static string Fmt(Field f, float v)
        {
            if (f.Kind == Kind.Integer) return ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            return new Factor(v).ToString();
        }

        static bool TryNum(string s, out float v)
        {
            return float.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !float.IsNaN(v) && !float.IsInfinity(v);
        }

        static float Num(string s)
        {
            float v;
            return float.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0f;
        }

        static string GameText(Field f)
        {
            if (f.Global)
            {
                var e = Entry(f.Key);
                return e == null ? "" : "default: " + TomlTypeConverter.ConvertToString(e.DefaultValue, e.SettingType).Replace("True", "on").Replace("False", "off").Replace("true", "on").Replace("false", "off");
            }
            if (f.Key.StartsWith("FireAndFlooding/Unattended", StringComparison.Ordinal))
            {   // not in the game: the mod's own default
                var ue = Entry(f.Key);
                return ue == null ? "" : "default: " + TomlTypeConverter.ConvertToString(ue.DefaultValue, ue.SettingType).Replace("True", "on").Replace("False", "off").Replace("true", "on").Replace("false", "off");
            }
            string v;
            if (_game == null || !_game.Values.TryGetValue(f.Key, out v)) return "";
            if (f.Kind == Kind.Bool) return "game: " + (v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ? "on" : "off");
            if (f.Kind == Kind.Table)
            {
                var t = DcTeams.Parse(v);
                if (t == null || t.Length == 0) return "game: " + v;
                var teams = new List<string>(); var tons = new List<string>();
                foreach (var r in t) { teams.Add(r[1].ToString(CultureInfo.InvariantCulture)); if (r[0] > 0) tons.Add(r[0].ToString(CultureInfo.InvariantCulture)); }
                return "game: " + string.Join("/", teams.ToArray()) + (tons.Count > 0 ? "\nfrom " + string.Join("/", tons.ToArray()) + " t" : "");
            }
            if (f.Key == "DamageControlTeams/HealTime") return "game: 60-120";
            if (f.Key == "Sinking/TrimReferenceLength" && Num(v) == 0f) return "game: off";
            if (f.Key == "DamageControlTeams/KnockoutsToDeath" && Num(v) == 0f) return "game: never";
            if ((f.Key == "DamageControlTeams/TransferSpeed" || f.Key == "DamageControlTeams/MusterTime") && Num(v) == 0f) return "game: instant";
            return "game: " + v;
        }

        // ------------------------------------------------------------ tabs and rows

        static void ShowTab(int t)
        {
            _tab = Math.Max(0, Math.Min(Tabs.Length - 1, t));
            bool adm = AdmOn();
            if (!adm && ModelTab(Tabs[_tab])) _tab = Array.IndexOf(Tabs, TGen);
            if (_profRow != null)
            {
                var host = _profRow.Parent as Panel;   // taken out of the previous tab's body
                if (host != null) host.Children.Remove(_profRow);
            }
            for (int i = 0; i < _tabButtons.Count; i++)
            {
                _tabButtons[i].Visibility = !adm && ModelTab(Tabs[i]) ? Visibility.Collapsed : Visibility.Visible;
                bool on = i == _tab;
                _tabButtons[i].Background = B(on ? CCard : CPanel);
                var row = _tabButtons[i].Child as StackPanel;
                if (row != null && row.Children.Count > 0) ((TextBlock)row.Children[0]).Foreground = B(on ? CText : CDim);
            }
            if (_body == null) return;
            _body.Children.Clear();
            string group = null;
            bool ui = UiOn();
            foreach (var f in Fields)
            {
                if (f.Tab != Tabs[_tab]) continue;
                string when = ShownWhen(f);
                if (when != null && !IsOn(when)) continue;   // only while its switch is on
                try
                {
                    if (f.Group != group)
                    {
                        // General: the settings profile (of the damage model, hidden while it is off) between the switch and Logging
                        if (Tabs[_tab] == TGen && group == "Damage model" && adm && _profRow != null)
                        {
                            _body.Children.Add(GroupTitle("Settings profile"));
                            _body.Children.Add(_profRow);
                        }
                        group = f.Group; _body.Children.Add(GroupTitle(group));
                    }
                    _body.Children.Add(Row(f));
                }
                // one bad row must not blank out every row after it - this is why: Trace shows which key and why
                catch (Exception e) { Trace("Settings row '" + f.Key + "' failed: " + e); }
            }
            // same insertion as inside the loop, for when "Damage model" is the last (now: the only) group on the tab -
            // the loop's own check only fires on a group CHANGE, so it never ran once Logging/Visualisation moved to TDbg
            if (Tabs[_tab] == TGen && group == "Damage model" && adm && _profRow != null)
            {
                _body.Children.Add(GroupTitle("Settings profile"));
                _body.Children.Add(_profRow);
            }
            if (Tabs[_tab] == "Hull") ShipRows();
            if (Tabs[_tab] == "Systems") ModuleRows();
            UpdateHeader();
        }

        static TextBlock GroupTitle(string s)
        {
            return new TextBlock { Text = s.ToUpperInvariant(), Foreground = B(CDim), FontSize = 11f, Margin = new Thickness(12f, 10f, 12f, 2f) };
        }

        static Grid RowGrid()
        {
            var g = new Grid { Margin = new Thickness(0f), Background = B(Color.FromArgb(0, 0, 0, 0)) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200f) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1f, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124f) });   // "game: 2 by tonnage" on one line
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30f) });
            return g;
        }

        static FrameworkElement Row(Field f)
        {
            var g = RowGrid();
            var outer = new Border { Child = g, Padding = new Thickness(12f, 4f, 6f, 4f), Background = B(Color.FromArgb(0, 0, 0, 0)) };

            // label with the "differs" dot
            var lab = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var dot = new Ellipse { Width = 6f, Height = 6f, Fill = B(CWarn), Margin = new Thickness(0f, 0f, 6f, 0f), VerticalAlignment = VerticalAlignment.Center };
            lab.Children.Add(dot);
            lab.Children.Add(Text(f.Label, CText));
            g.Children.Add(lab);

            Action mark = () => { dot.Visibility = Differs(f) ? Visibility.Visible : Visibility.Hidden; };
            FrameworkElement editor;
            Action refresh;
            switch (f.Kind)
            {
                case Kind.Bool: editor = BoolEditor(f, mark, out refresh); break;
                case Kind.Choice: editor = ChoiceEditor(f, mark, out refresh); break;
                case Kind.Table: editor = TableEditor(f, mark, out refresh); break;
                default: editor = NumberEditor(f, mark, out refresh); break;
            }
            Grid.SetColumn(editor, 1);
            g.Children.Add(editor);

            var game = new TextBlock { Text = GameText(f), Foreground = B(CDim), FontSize = 11f, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(game, 2);
            g.Children.Add(game);

            var rst = new Button { Content = "↺", Width = 24f, Height = 22f, Padding = new Thickness(0f), HorizontalAlignment = HorizontalAlignment.Right };
            try { ToolTipService.SetToolTip(rst, "Back to the value of the profile"); } catch { }
            rst.Click += (o, e) => { Set(f, FromValue(f)); refresh(); mark(); };
            Grid.SetColumn(rst, 3);
            g.Children.Add(rst);

            mark();
            Hover(outer, f.Label, f.Key);
            return outer;
        }

        static void Hover(Border row, string label, string key, string extra = null)
        {
            row.MouseEnter += (o, e) =>
            {
                row.Background = B(CHl);
                var en = key == null || key.StartsWith("#") ? null : Entry(key);
                string d = en != null && en.Description != null ? en.Description.Description : "";
                if (key == null || key == "#ships") d = "Ships with their own hull multiplier, chosen from the game's ships (or by ini file name): how many times stronger than in the game; replaces the class value, is not multiplied by it.";
                else if (key == "#modules") d = "Module types (modules.ini) with their own Integrity, set explicitly in place of modules.ini's (the game's value is put in when a type is picked); the Integrity multiplier above does not apply to them. The game's own adjustments keep their proportion: types marked \"× tonnage\" (amber) grow with the ship's displacement - point at one for its final values -, and a ship's own value for a system is changed in the same ratio.";
                _help.Inlines.Clear();
                var r = new Run(label); r.FontWeight = FontWeight.Bold; r.Foreground = B(CText);
                _help.Inlines.Add(r);
                _help.Inlines.Add(new Run(" - " + d));
                if (!string.IsNullOrEmpty(extra)) { var x = new Run("  " + extra); x.Foreground = B(CWarn); _help.Inlines.Add(x); }
            };
            row.MouseLeave += (o, e) => row.Background = B(Color.FromArgb(0, 0, 0, 0));
        }

        static FrameworkElement NumberEditor(Field f, Action mark, out Action refresh)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var box = Edit(Get(f), 66f);
            Action<float> put = v =>
            {
                v = Math.Max(f.Min, Math.Min(f.Max, v));
                string s = Fmt(f, v);
                box.Text = s;
                Set(f, s);
                mark();
            };
            var minus = new Button { Content = "−", Width = 24f, Height = 24f, Padding = new Thickness(0f) };
            var plus = new Button { Content = "+", Width = 24f, Height = 24f, Padding = new Thickness(0f) };
            minus.Click += (o, e) => put((float)Math.Round((Num(box.Text) - f.Step) / f.Step) * f.Step);
            plus.Click += (o, e) => put((float)Math.Round((Num(box.Text) + f.Step) / f.Step) * f.Step);
            // typed in: a number is taken (within the limits); anything else puts the value back as it was
            Action typed = () => { float v; if (TryNum(box.Text, out v)) put(v); else box.Text = Get(f); };
            box.LostFocus += (o, e) => typed();
            box.KeyDown += (o, e) => { if (e.Key == Key.Return) { typed(); e.Handled = true; } };
            sp.Children.Add(minus); sp.Children.Add(box); sp.Children.Add(plus);
            if (!string.IsNullOrEmpty(f.Unit)) { var u = Text(f.Unit, CDim, 12f); u.Margin = new Thickness(8f, 0f, 0f, 0f); sp.Children.Add(u); }
            refresh = () => box.Text = Get(f);
            return sp;
        }

        static FrameworkElement ChoiceEditor(Field f, Action mark, out Action refresh)
        {
            var cb = new ComboBox { Width = 150f, MinHeight = 24f, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            foreach (var o in f.Options) cb.Items.Add(o);
            Func<int> index = () =>
            {
                string cur = Get(f).Trim();
                for (int i = 0; i < f.Options.Length; i++) if (string.Equals(f.Options[i], cur, StringComparison.OrdinalIgnoreCase)) return i;
                return 0;
            };
            cb.SelectedIndex = index();
            cb.SelectionChanged += (o, e) =>
            {
                int i = cb.SelectedIndex;
                if (i < 0 || i >= f.Options.Length || string.Equals(f.Options[i], Get(f).Trim(), StringComparison.OrdinalIgnoreCase)) return;
                Set(f, f.Options[i]); mark();
            };
            refresh = () => cb.SelectedIndex = index();
            return cb;
        }

        static FrameworkElement BoolEditor(Field f, Action mark, out Action refresh)
        {
            var cb = new CheckBox { IsChecked = Get(f).Trim().Equals("true", StringComparison.OrdinalIgnoreCase), VerticalAlignment = VerticalAlignment.Center };
            cb.Click += (o, e) =>
            {
                string v = cb.IsChecked == true ? "true" : "false";
                if (LiveKey(f.Key))
                {
                    // applies at once, not on Apply: ships at sea / the Damage Control tab follow, tabs and groups of the window show / hide
                    try { var en = Entry(f.Key); if (en != null) en.SetSerializedValue(v); } catch (Exception ex) { Trace(f.Key + ": " + ex.Message); }
                    _globals[f.Key] = v; _globalsFrom[f.Key] = v;
                    DamageControlUi.RefreshNow();
                    ShowTab(_tab);
                    return;
                }
                Set(f, v); mark();
                foreach (var other in Fields) if (ShownWhen(other) == f.Key) { ShowTab(_tab); break; }   // rows that show / hide with it
            };
            refresh = () => cb.IsChecked = Get(f).Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            return cb;
        }

        /// <summary>"tons=teams" pairs as a two-row table: tons on top, teams below; + adds a column, − removes the last.</summary>
        static FrameworkElement TableEditor(Field f, Action mark, out Action refresh)
        {
            var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Action build = null;
            build = () =>
            {
                host.Children.Clear();
                var rows = DcTeams.Parse(Get(f));
                var list = new List<int[]>();
                if (rows != null) foreach (var r in rows) list.Add(new[] { r[0], r[1] });
                if (list.Count == 0) list.Add(new[] { 0, 1 });
                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var l1 = Text("tons", CDim, 12f); l1.Margin = new Thickness(0f, 0f, 6f, 0f);
                var l2 = Text("teams", CDim, 12f); l2.Margin = new Thickness(0f, 0f, 6f, 0f); Grid.SetRow(l2, 1);
                grid.Children.Add(l1); grid.Children.Add(l2);
                Action save = () =>
                {
                    var parts = new List<string>();
                    foreach (var r in list) parts.Add(r[0].ToString(CultureInfo.InvariantCulture) + "=" + r[1].ToString(CultureInfo.InvariantCulture));
                    Set(f, string.Join(", ", parts.ToArray()));
                    mark();
                };
                for (int i = 0; i < list.Count; i++)
                {
                    int k = i;
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var tons = Edit(list[k][0].ToString(CultureInfo.InvariantCulture), 52f);
                    var teams = Edit(list[k][1].ToString(CultureInfo.InvariantCulture), 52f);
                    tons.Margin = teams.Margin = new Thickness(0f, 1f, 2f, 1f);
                    Grid.SetColumn(tons, k + 1); Grid.SetColumn(teams, k + 1); Grid.SetRow(teams, 1);
                    Action commit = () =>
                    {
                        int a, b;
                        if (!int.TryParse(tons.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out a) || a < 0) a = list[k][0];
                        if (!int.TryParse(teams.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out b) || b < 1) b = list[k][1];
                        b = Math.Min(b, 9);
                        tons.Text = a.ToString(CultureInfo.InvariantCulture); teams.Text = b.ToString(CultureInfo.InvariantCulture);
                        if (a == list[k][0] && b == list[k][1]) return;
                        list[k][0] = a; list[k][1] = b;
                        save();
                    };
                    tons.LostFocus += (o, e) => commit(); teams.LostFocus += (o, e) => commit();
                    tons.KeyDown += (o, e) => { if (e.Key == Key.Return) { commit(); e.Handled = true; } };
                    teams.KeyDown += (o, e) => { if (e.Key == Key.Return) { commit(); e.Handled = true; } };
                    grid.Children.Add(tons); grid.Children.Add(teams);
                }
                host.Children.Add(grid);
                var pm = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(4f, 0f, 0f, 0f) };
                var add = new Button { Content = "+", Width = 24f, Height = 24f, Padding = new Thickness(0f), Margin = new Thickness(0f, 1f, 0f, 1f) };
                var del = new Button { Content = "−", Width = 24f, Height = 24f, Padding = new Thickness(0f), Margin = new Thickness(0f, 1f, 0f, 1f), IsEnabled = list.Count > 1 };
                try { ToolTipService.SetToolTip(add, "One more step: more teams from more tons"); ToolTipService.SetToolTip(del, "Remove the last step"); } catch { }
                add.Click += (o, e) =>
                {
                    var last = list[list.Count - 1];
                    list.Add(new[] { Math.Max(last[0] * 2, last[0] + 1000), Math.Min(9, last[1] + 1) });
                    save(); build();
                };
                del.Click += (o, e) => { if (list.Count > 1) { list.RemoveAt(list.Count - 1); save(); build(); } };
                pm.Children.Add(add); pm.Children.Add(del);
                host.Children.Add(pm);
            };
            build();
            refresh = build;
            return host;
        }

        // ------------------------------------------------------------ [HullIntegrityPerShip]

        static void ShipRows()
        {
            MapRows("Single ships - × game, instead of the class value", p => p.Ships, ShipNames.Display, ShipNames.Filter, ShipNames.ToIni,
                    "+ Add ship", "a ship from the list, or its ini file name", "Remove: the ship takes the class value again", "#ships",
                    n => "", n => 2f, 0.5f, 0.1f, 20f);
        }

        static void ModuleRows()
        {
            MapRows("Single system types - Integrity, instead of the multiplier above", p => p.Modules, n => n, ModuleNames.Filter, ModuleNames.ToKey,
                    "+ Add type", "a module type from the list (modules.ini)", "Remove: the type takes the multiplier above again", "#modules",
                    ModuleGame, n => ModuleNames.GameIntegrity(n), 5f, 0.1f, 10000f, ModuleTip, ModuleNames.ScalesWithDisplacement, ModuleHelp);
        }

        static string ModuleGame(string n)
        {
            float gi = ModuleNames.GameIntegrity(n);
            if (gi <= 0f) return "unknown type";
            // a type the game scales with the ship's displacement has no one value: the examples are in the tooltip
            string v = gi.ToString("0.##", CultureInfo.InvariantCulture);
            return ModuleNames.ScalesWithDisplacement(n) ? "game: " + v + "\ntonnage dependent" : "game: " + v;
        }

        /// <summary>
        /// The final Integrity of a type the game scales with the ship's displacement (modules.ini ScalesWithDisplacement):
        /// value × displacement^0.25 (civilian ships: half of that, at least × 1), at least 10 - the game's LoadModuleParameters.
        /// </summary>
        public static float ScaledIntegrity(float v, float tons, bool civilian)
        {
            float k = tons > 1f ? (float)Math.Pow(tons, 0.25) : 1f;
            if (civilian) k /= 2f;
            if (k < 1f) k = 1f;
            return Math.Max(10f, v * k);
        }

        static readonly float[] ExampleTons = { 500f, 1000f, 3000f, 5000f, 10000f, 20000f };

        /// <summary>
        /// Tooltip of a type the game scales with displacement: a small table of the final Integrity of warships of 500 ... 20 000 t -
        /// the game's and, when it differs, the one this value gives. Null for a type that is not scaled.
        /// </summary>
        static FrameworkElement ModuleTip(string n, float v)
        {
            if (!ModuleNames.ScalesWithDisplacement(n)) return null;
            float gi = ModuleNames.GameIntegrity(n);
            bool own = Math.Abs(v - gi) > 0.001f;
            var g = new Grid { Margin = new Thickness(2f) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80f) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56f) });
            if (own) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56f) });
            Action<int, int, string, Color, bool> cell = (r, c, text, col, bold) =>
            {
                var t = new TextBlock { Text = text, Foreground = B(col), TextAlignment = c == 0 ? TextAlignment.Left : TextAlignment.Right };
                if (bold) t.FontWeight = FontWeight.Bold;
                Grid.SetRow(t, r); Grid.SetColumn(t, c); g.Children.Add(t);
            };
            g.RowDefinitions.Add(new RowDefinition());
            cell(0, 0, n, CText, true); cell(0, 1, "game", CDim, false); if (own) cell(0, 2, "yours", CDim, false);
            for (int i = 0; i < ExampleTons.Length; i++)
            {
                float t = ExampleTons[i];
                g.RowDefinitions.Add(new RowDefinition());
                cell(i + 1, 0, t.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ') + " t", CText, false);
                cell(i + 1, 1, ScaledIntegrity(gi, t, false).ToString("0", CultureInfo.InvariantCulture), CText, false);
                if (own) cell(i + 1, 2, ScaledIntegrity(v, t, false).ToString("0", CultureInfo.InvariantCulture), CWarn, false);
            }
            return g;
        }

        /// <summary>The help pane's one line for such a type.</summary>
        static string ModuleHelp(string n, float v)
        {
            if (!ModuleNames.ScalesWithDisplacement(n)) return null;
            var parts = new List<string>();
            foreach (var t in ExampleTons)
                parts.Add(t.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ') + " t: " + ScaledIntegrity(v, t, false).ToString("0", CultureInfo.InvariantCulture));
            return "Integrity on a warship of " + string.Join(" · ", parts.ToArray()) + ".";
        }

        /// <summary>
        /// Rows "name = value" of a profile's list (single ships: multiplier / single module types: integrity) and a row to add one.
        /// gameOf: the game's value for the right-hand column; defaultOf: the value put in when a name is picked.
        /// </summary>
        static void MapRows(string title, Func<Profile, SortedDictionary<string, string>> mapOf, Func<string, string> display, Func<string, int, List<string>> filter,
                            Func<string, string> toKey, string addLabel, string errText, string removeTip, string helpKey,
                            Func<string, string> gameOf, Func<string, float> defaultOf, float step, float min, float max,
                            Func<string, float, FrameworkElement> detailOf = null, Func<string, bool> accentOf = null, Func<string, float, string> helpOf = null)
        {
            _body.Children.Add(GroupTitle(title));
            foreach (var kv in new List<KeyValuePair<string, string>>(mapOf(_work)))
            {
                string name = kv.Key;
                var g = RowGrid();
                var outer = new Border { Child = g, Padding = new Thickness(12f, 4f, 6f, 4f) };
                bool changed = _from == null || !mapOf(_from).ContainsKey(name) || !AdmPlugin.SameValue(mapOf(_from)[name], kv.Value);
                var lab = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                lab.Children.Add(new Ellipse { Width = 6f, Height = 6f, Fill = B(CWarn), Margin = new Thickness(0f, 0f, 6f, 0f), VerticalAlignment = VerticalAlignment.Center,
                                               Visibility = changed ? Visibility.Visible : Visibility.Hidden });
                var nm = Text(display(name), CText);
                try { ToolTipService.SetToolTip(nm, name); } catch { }
                lab.Children.Add(nm);
                g.Children.Add(lab);
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var box = Edit(kv.Value, 66f);
                Action<float> put = v =>
                {
                    v = Math.Max(min, Math.Min(max, v));
                    string s = new Factor(v).ToString();
                    box.Text = s;
                    if (AdmPlugin.SameValue(mapOf(_work)[name], s)) return;
                    mapOf(_work)[name] = s; BecomeOwn(); ShowTab(_tab);
                };
                var minus = new Button { Content = "−", Width = 24f, Height = 24f, Padding = new Thickness(0f) };
                var plus = new Button { Content = "+", Width = 24f, Height = 24f, Padding = new Thickness(0f) };
                minus.Click += (o, e) => put(Num(box.Text) - step);
                plus.Click += (o, e) => put(Num(box.Text) + step);
                string was = kv.Value;
                Action typed = () => { float v; if (TryNum(box.Text, out v)) put(v); else box.Text = was; };
                box.LostFocus += (o, e) => typed();
                box.KeyDown += (o, e) => { if (e.Key == Key.Return) { typed(); e.Handled = true; } };
                sp.Children.Add(minus); sp.Children.Add(box); sp.Children.Add(plus);
                Grid.SetColumn(sp, 1); g.Children.Add(sp);
                var gt = GameLabel(gameOf(name), accentOf != null && accentOf(name));
                var detail = detailOf == null ? null : detailOf(name, Num(kv.Value));
                if (detail != null) try { ToolTipService.SetToolTip(gt, detail); } catch { }
                Grid.SetColumn(gt, 2); g.Children.Add(gt);
                var rm = new Button { Content = Cross(8f), Width = 24f, Height = 22f, Padding = new Thickness(0f), HorizontalAlignment = HorizontalAlignment.Right,
                                      HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                try { ToolTipService.SetToolTip(rm, removeTip); } catch { }
                rm.Click += (o, e) => { mapOf(_work).Remove(name); BecomeOwn(); ShowTab(_tab); };
                Grid.SetColumn(rm, 3); g.Children.Add(rm);
                Hover(outer, display(name), helpKey, helpOf == null ? null : helpOf(name, Num(kv.Value)));
                _body.Children.Add(outer);
            }
            // add a ship
            var ag = RowGrid();
            var aouter = new Border { Child = ag, Padding = new Thickness(12f, 4f, 6f, 8f) };
            // the ship: a drop-down of the game's ships by their (localised) names; typing filters it, any ini name can be typed too
            var nameBox = new ComboBox { IsEditable = true, Width = 190f, MinHeight = 24f, HorizontalAlignment = HorizontalAlignment.Left, StaysOpenOnEdit = true };
            Action<string> fill = f =>
            {
                var items = filter(f, 300);
                nameBox.Items.Clear();
                foreach (var it in items) nameBox.Items.Add(it);
            };
            fill("");
            string lastFilter = "";
            nameBox.KeyUp += (o, e) =>
            {
                if (e.Key == Key.Return || e.Key == Key.Up || e.Key == Key.Down) return;
                string t = nameBox.Text ?? "";
                if (t == lastFilter) return;
                lastFilter = t;
                fill(t);
                nameBox.Text = t;
                try { var tb = nameBox.Template.FindName("PART_EditableTextBox", nameBox) as TextBox; if (tb != null) tb.CaretIndex = t.Length; } catch { }
                nameBox.IsDropDownOpen = nameBox.Items.Count > 0 && t.Length > 0;
            };
            nameBox.GotFocus += (o, e) => Typing(true);
            nameBox.LostFocus += (o, e) => Typing(false);
            ag.Children.Add(nameBox);
            var asp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var val = Edit(new Factor(defaultOf("")).ToString(), 66f);
            var agame = GameLabel("", false);
            Grid.SetColumn(agame, 2); ag.Children.Add(agame);
            // a name picked (or typed): its game value is put in
            Action picked = () =>
            {
                string n = toKey((nameBox.SelectedItem as string ?? nameBox.Text ?? "").Trim());
                agame.Text = n.Length > 0 ? gameOf(n) : "";
                agame.Foreground = B(accentOf != null && n.Length > 0 && accentOf(n) ? CGame : CDim);
                var det = detailOf == null || n.Length == 0 ? null : detailOf(n, defaultOf(n));
                try { ToolTipService.SetToolTip(agame, det); } catch { }
                float d = n.Length > 0 ? defaultOf(n) : 0f;
                if (d > 0f) val.Text = new Factor(d).ToString();
            };
            nameBox.SelectionChanged += (o, e) => picked();
            nameBox.LostFocus += (o, e) => picked();
            var err = Text("", CErr, 11f); err.Margin = new Thickness(8f, 0f, 0f, 0f);
            var addB = new Button { Content = addLabel, Padding = new Thickness(10f, 2f, 10f, 2f), Margin = new Thickness(8f, 0f, 0f, 0f), MinHeight = 24f };
            addB.Click += (o, e) =>
            {
                string n = toKey((nameBox.Text ?? "").Trim());
                if (n.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
                Factor fv;
                if (n.Length == 0) { err.Text = errText; return; }
                string bad = AdmPlugin.BadKey(n);
                if (bad != null) { err.Text = bad; return; }
                if (!Factor.TryParse(val.Text, out fv) || fv.V <= 0f) { err.Text = "a positive number"; return; }
                mapOf(_work)[n] = new Factor(Math.Max(min, Math.Min(max, fv.V))).ToString();
                BecomeOwn(); ShowTab(_tab);
            };
            asp.Children.Add(val); asp.Children.Add(addB); asp.Children.Add(err);
            Grid.SetColumn(asp, 1); ag.Children.Add(asp);
            Hover(aouter, addLabel.TrimStart('+', ' '), helpKey);
            _body.Children.Add(aouter);
        }

        // ------------------------------------------------------------ profile actions

        static string Suggest()
        {
            if (!AdmPlugin.IsBuiltIn(_selected) && !string.Equals(_selected, AdmPlugin.CustomName, StringComparison.OrdinalIgnoreCase)) return _selected + " 2";
            return "My profile";
        }

        static string SaveAs(string name)
        {
            string err = P.SaveProfileAs(name, _work);
            if (err != null) return err;
            _selected = name.Trim();
            _from = P.LoadProfile(_selected) ?? _work.Copy(_selected);
            _work = _from.Copy(_selected);
            RebuildProfiles();
            ShowTab(_tab);
            return null;
        }

        static string Rename(string name)
        {
            bool saved = Array.Exists(AdmPlugin.ProfileNames().ToArray(), n => string.Equals(n, _selected, StringComparison.OrdinalIgnoreCase));
            if (saved)
            {
                string err = P.RenameProfile(_selected, name);
                if (err != null) return err;
            }
            else
            {
                string bad = AdmPlugin.BadName(name);
                if (bad != null) return bad;
            }
            bool wasFrom = _from != null && string.Equals(_from.Name, _selected, StringComparison.OrdinalIgnoreCase);
            _selected = name.Trim(); _work.Name = _selected;
            if (wasFrom) _from.Name = _selected;
            RebuildProfiles();
            ShowTab(_tab);
            return null;
        }

        static string Delete()
        {
            string err = P.DeleteProfile(_selected);
            if (err != null) return err;
            LoadFrom(P.ActiveProfileName);
            RebuildProfiles();
            ShowTab(_tab);
            return null;
        }

        static void ResetAll()
        {
            if (_from == null) return;
            _selected = _from.Name;
            _work = _from.Copy(_from.Name);
            foreach (var kv in _globalsFrom) _globals[kv.Key] = kv.Value;
            RebuildProfiles();
            ShowTab(_tab);
        }

        static void Commit(bool close)
        {
            Typing(false);
            var globals = new Dictionary<ConfigEntryBase, string>();
            foreach (var kv in _globals)
            {
                string v;
                if (_globalsFrom.TryGetValue(kv.Key, out v) && AdmPlugin.SameValue(v, kv.Value)) continue;
                var e = Entry(kv.Key);
                if (e != null) globals[e] = kv.Value;
            }
            string name = P.Commit(_selected, _work, globals);
            if (close) { Close(); return; }
            LoadFrom(name);
            RebuildProfiles();
            ShowTab(_tab);
        }

        // ------------------------------------------------------------ small dialogs over the window

        static void Dialog(string title, FrameworkElement content)
        {
            var box = new Border { Background = B(CBg), BorderBrush = B(Color.FromRgb(91, 98, 109)), BorderThickness = new Thickness(1f), CornerRadius = new CornerRadius(4f),
                                   Width = 360f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var sp = new StackPanel { Orientation = Orientation.Vertical };
            var t = Text(title, CText, 14f);
            sp.Children.Add(new Border { Child = t, Background = B(CTitle), Padding = new Thickness(10f, 7f, 10f, 7f) });
            sp.Children.Add(new Border { Child = content, Padding = new Thickness(12f) });
            box.Child = sp;
            _overlay.Child = box;
            _overlay.Visibility = Visibility.Visible;
        }

        static void HideDialog() { Typing(false); _overlay.Visibility = Visibility.Collapsed; _overlay.Child = null; }

        static void Ask(string title, string initial, Func<string, string> act)
        {
            var sp = new StackPanel { Orientation = Orientation.Vertical };
            var box = Edit(initial, 334f); box.TextAlignment = TextAlignment.Left; box.Height = 26f;
            sp.Children.Add(box);
            var where = new TextBlock { Text = "Saved to " + AdmPlugin.ProfileFolder, Foreground = B(CDim), FontSize = 11f, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0f, 6f, 0f, 4f) };
            sp.Children.Add(where);
            var err = new TextBlock { Foreground = B(CErr), FontSize = 11f, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0f, 0f, 0f, 8f) };
            sp.Children.Add(err);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Action go = () =>
            {
                string e = act(box.Text);
                if (e == null) HideDialog(); else err.Text = e;
            };
            row.Children.Add(Btn("Cancel", HideDialog, 70f));
            var ok = Btn("Save", go, 70f); ok.Margin = new Thickness(0f);
            row.Children.Add(ok);
            sp.Children.Add(row);
            box.KeyDown += (o, e) => { if (e.Key == Key.Return) { go(); e.Handled = true; } };
            Dialog(title, sp);
            try { box.Focus(); } catch { }
        }

        static void Confirm(string question, Func<string> act)
        {
            var sp = new StackPanel { Orientation = Orientation.Vertical };
            var err = new TextBlock { Foreground = B(CErr), FontSize = 11f, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0f, 0f, 0f, 8f) };
            sp.Children.Add(new TextBlock { Text = "Its file is deleted. If it is the profile in use, Advanced comes into use.", Foreground = B(CDim), FontSize = 12f,
                                            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0f, 0f, 0f, 8f) });
            sp.Children.Add(err);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            row.Children.Add(Btn("Cancel", HideDialog, 70f));
            var ok = Btn("Delete", () => { string e = act(); if (e == null) HideDialog(); else err.Text = e; }, 70f); ok.Margin = new Thickness(0f);
            row.Children.Add(ok);
            sp.Children.Add(row);
            Dialog(question, sp);
        }
    }
}
