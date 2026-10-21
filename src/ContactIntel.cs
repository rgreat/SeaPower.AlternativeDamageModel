using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Noesis;

namespace AlternativeDamageModel
{
    /// <summary>
    /// [UI] ShowEnemyDamage: a short damage note under a contact's icon on the map (small type, one or two words) and a greyer icon
    /// when the contact is going to sink. What the note says depends on how the contact is seen (Vehicle.DetectingSensors):
    ///   direct sight (Visual / TV) and identified - hull and flooding in %, fire, speed, a sinking forecast in minutes;
    ///   direct sight, not identified (far, bad weather) - the same in words only;
    ///   radar - only "dead" / "slow" (a ship that does not move or moves slowly), nothing else;
    ///   sonar - flooding noises (by strength), propulsion, "sinking";
    ///   nothing otherwise (ESM only, dormant track), and nothing for a ship already sunk or destroyed.
    /// The forecast comes from the ship's own history: the flooding / hull trend over the last minute of game time against the
    /// sinking limits (Limits.LevelFull; hull 10% = the game's own "destroy by explosion" point).
    /// </summary>
    static class ContactIntel
    {
        public static Action<string> Trace = s => { };
        public static Func<bool> IsEnabled = () => false;

        public struct Info { public int Fire, Flood, Hull, Engine; public bool Guess; public bool Doomed; public bool Any { get { return Fire + Flood + Hull + Engine > 0; } } public string Text { get { return Any ? (Fire > 0 ? "F" + Fire + " " : "") + (Flood > 0 ? "W" + Flood + " " : "") + (Hull > 0 ? "H" + Hull + " " : "") + (Engine > 0 ? "E" + Engine + (Guess ? "?" : "") : "") : null; } } }

        // ------------------------------------------------------------ reflection

        static bool _ok;
        static Type _tVehicle, _tSensors;
        static FieldInfo _vObject;
        static PropertyInfo _vSensors, _vObserved, _vIdentified, _vDormant, _rpValue;
        static PropertyInfo _oCompartments, _oDestroyed, _cFlood, _cInt, _oPlayer;
        static FieldInfo _cSys, _cCount, _scMax, _oTaskforce, _cSinking, _cFires, _cProp, _cImmobile, _cMaxInt, _gPlayerTf;
        static PropertyInfo _sInoperable, _sDamage, _scSev;
        static MethodInfo _oSpeed;
        static PropertyInfo _gameTime;
        static Type _tSub;
        static PropertyInfo _mUnit;

        public static bool Init(Harmony harmony, Assembly asm)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                _tVehicle = asm.GetType("SeaPower.Vehicle");
                _tSensors = asm.GetType("SeaPower.SensorSystem+SensorTypeSet");
                var tObj = asm.GetType("SeaPower.ObjectBase");
                var tComp = asm.GetType("SeaPower.Compartments");
                var tSys = asm.GetType("SeaPower.BaseSystem");
                var tGlobals = asm.GetType("SeaPower.Globals");
                _tSub = asm.GetType("SeaPower.Submarine");
                _gameTime = asm.GetType("SeaPower.GameTime").GetProperty("missionElapsedTime", all);
                _vObject = _tVehicle.GetField("Object", all);
                _vSensors = _tVehicle.GetProperty("DetectingSensors", all);
                _vObserved = _tVehicle.GetProperty("IsObservedByVisual", all);
                _vIdentified = _tVehicle.GetField("Identified", all) == null ? _tVehicle.GetProperty("Identified", all) : null;
                _vDormant = _tVehicle.GetProperty("IsDormant", all);
                _oCompartments = tObj.GetProperty("Compartments", all);
                _oDestroyed = tObj.GetProperty("IsDestroyed", all);
                _oTaskforce = tObj.GetField("_taskforce", all);
                _oPlayer = tObj.GetProperty("IsPlayerObject", all);
                _gPlayerTf = tGlobals.GetField("_playerTaskforce", all);
                _cFlood = tComp.GetProperty("FloodingPercentage", all);
                _cInt = tComp.GetProperty("IntegrityPercentage", all);
                _cSinking = tComp.GetField("_isSinking", all);
                _cFires = tComp.GetField("_currentFires", all);
                _cProp = tComp.GetField("_propulsionSystems", all);
                _cImmobile = tComp.GetField("_immobile", all);
                _sInoperable = tSys.GetProperty("Inoperable", all);
                _sDamage = tSys.GetProperty("CurrentDamageType", all);
                _cSys = tComp.GetField("_systemCompartments", all); _cCount = tComp.GetField("_compartmentsCount", all);
                var tSc = asm.GetType("SeaPower.SystemCompartment");
                _scSev = tSc.GetProperty("FireSeverity", all); _scMax = tSc.GetField("_maxFireSeverity", all);
                _oSpeed = tObj.GetMethod("getVelocityInKnots", all);
                var tMapVm = asm.GetType("SeapowerUI.MapUnitViewModel");
                _mUnit = tMapVm == null ? null : tMapVm.GetProperty("Unit", all);
                _ok = _tVehicle != null && _vObject != null && _vSensors != null && _oCompartments != null && _cFlood != null && _cInt != null &&
                      _cSinking != null && _gameTime != null && _mUnit != null && _vIdentified != null;
                if (!_ok) { Trace("ContactIntel: members not found"); return false; }

                Patch(harmony, asm);
                return true;
            }
            catch (Exception e) { Trace("ContactIntel.Init: " + e); _ok = false; return false; }
        }

        // ------------------------------------------------------------ the ship's trend

        sealed class Hist
        {
            public readonly List<double> T = new List<double>();
            public readonly List<float> Flood = new List<float>(), Hull = new List<float>();
        }
        static readonly ConditionalWeakTable<object, Hist> History = new ConditionalWeakTable<object, Hist>();
        
        const double Window = 60.0;   // game seconds the trend is measured over
        const double MinSpan = 12.0;  // ... and the least of them before there is a forecast

        static double Now() { try { return Convert.ToDouble(_gameTime.GetValue(null, null)); } catch { return 0; } }

        static object Rp(object rp)
        {
            if (rp == null) return null;
            if (_rpValue == null || _rpValue.DeclaringType != rp.GetType()) _rpValue = rp.GetType().GetProperty("Value");
            return _rpValue == null ? null : _rpValue.GetValue(rp, null);
        }

        static bool Flag(object rp) { var v = Rp(rp); return v is bool && (bool)v; }

        // ------------------------------------------------------------ what a contact shows

        // SensorTypeSet bits
        const long Visual = 2, Tv = 4, Radar = 8 | 0x10 | 0x20, Sonar = 0x40 | 0x80;

        static bool Mine(object obj)
        {
            try
            {
                if (_oPlayer != null && (bool)_oPlayer.GetValue(obj, null)) return true;
                var tf = _oTaskforce == null ? null : _oTaskforce.GetValue(obj);
                var mine = _gPlayerTf == null ? null : _gPlayerTf.GetValue(null);
                return tf != null && ReferenceEquals(tf, mine);
            }
            catch { return false; }
        }

        /// <summary>The note and the forecast for one contact (a Vehicle); Text == null: nothing to show.</summary>
        public static Info Of(object vehicle)
        {
            var none = new Info();
            if (!_ok || vehicle == null) return none;
            try
            {
                // a contact (Vehicle) or an own unit (the ObjectBase itself: it is known exactly)
                bool contact = _tVehicle.IsInstanceOfType(vehicle);
                var obj = contact ? _vObject.GetValue(vehicle) : vehicle;
                if (obj == null) return none;
                bool mine = !contact || Mine(obj);
                if (_oDestroyed != null && (bool)_oDestroyed.GetValue(obj, null)) return none;
                if (!mine && _vDormant != null && Flag(_vDormant.GetValue(vehicle, null))) return none;
                var comp = _oCompartments.GetValue(obj, null);
                if (comp == null) return none;

                long sensors = mine ? 0 : Convert.ToInt64(_vSensors.GetValue(vehicle, null));
                bool seen = mine || (sensors & (Visual | Tv)) != 0 || (_vObserved != null && (bool)_vObserved.GetValue(vehicle, null));
                bool ident = mine || Flag(_vIdentified.GetValue(vehicle, null));
                bool radar = (sensors & Radar) != 0, sonar = (sensors & Sonar) != 0;
                if (!seen && !radar && !sonar) return none;

                bool sinking = (bool)_cSinking.GetValue(comp);
                float flood = Convert.ToSingle(_cFlood.GetValue(comp, null));
                float hull = Convert.ToSingle(_cInt.GetValue(comp, null));
                int fires = 0;
                var fa = _cFires == null ? null : _cFires.GetValue(comp) as Array;
                if (fa != null) foreach (var f in fa) if (Convert.ToInt32(f) != 0) fires++;
                float prop = Propulsion(comp);

                // the trend, for the forecast
                double eta = double.MaxValue;
                var h = History.GetOrCreateValue(obj);
                double now = Now();
                if (h.T.Count == 0 || now - h.T[h.T.Count - 1] >= 1.0)
                {
                    h.T.Add(now); h.Flood.Add(flood); h.Hull.Add(hull);
                    while (h.T.Count > 2 && now - h.T[1] > Window) { h.T.RemoveAt(0); h.Flood.RemoveAt(0); h.Hull.RemoveAt(0); }
                }
                double span = h.T.Count < 2 ? 0 : h.T[h.T.Count - 1] - h.T[0];
                if (!sinking && span >= MinSpan)
                {
                    double rf = (h.Flood[h.Flood.Count - 1] - h.Flood[0]) / span;   // % a second
                    double rh = (h.Hull[h.Hull.Count - 1] - h.Hull[0]) / span;
                    float limit = _tSub != null && _tSub.IsInstanceOfType(obj) ? 50f : Limits.LevelFull;
                    if (rf > 0.0005) eta = Math.Min(eta, (limit - flood) / rf);
                    if (rh < -0.0005) eta = Math.Min(eta, (hull - 10f) / -rh);
                }
                double minutes = eta / 60.0;
                bool soon = sinking || minutes <= 90.0;
                bool doomed = sinking || minutes <= 20.0;

                // levels 0..4 as on the Damage Control panel: fire, water, hull; the engine: 1 part lost, 4 none works
                var info = new Info { Doomed = doomed };
                bool dead = prop <= 0.01f || (_cImmobile != null && (bool)_cImmobile.GetValue(comp));
                if (seen)
                {
                    // direct sight: the fires' total strength, water and hull by the panel's classes, the engine
                    info.Fire = FireStage(FireTotal(comp));
                    info.Flood = sinking ? 4 : FloodStage(flood);
                    info.Hull = HullStage(hull, ModelOn());
                    info.Engine = dead ? 4 : EngineLevel(prop);
                }
                else if (sonar)
                {
                    info.Engine = dead ? 4 : EngineLevel(prop);   // heard: engines and screws
                    doomed = sinking; info.Doomed = doomed;
                }
                else if (radar)
                {
                    // radar only guesses from the track: a ship hit lately that goes slowly
                    info.Doomed = false;
                    var recs = HitLog.Records(comp);
                    double t = Now();
                    if (recs.Count > 0 && t - recs[recs.Count - 1].Time <= 600.0 && _oSpeed != null)
                    {
                        float kn = Math.Abs(Convert.ToSingle(_oSpeed.Invoke(obj, null)));
                        info.Engine = kn < 3f ? 4 : kn <= 10f ? 1 : 0;
                        info.Guess = info.Engine > 0;
                    }
                }
                else info.Doomed = false;
                return info.Any || info.Doomed ? info : none;
            }
            catch (Exception e) { Trace("ContactIntel.Of: " + e.Message); return none; }
        }

        /// <summary>The fires' total strength: the sum over the sections of their fire in % of the section's own maximum (100 = one section burning at full).</summary>
        public static float FireTotal(object comp)
        {
            try
            {
                var sys = _cSys == null ? null : _cSys.GetValue(comp) as Array;
                if (sys == null) return 0f;
                int count = Math.Min(sys.Length, Convert.ToInt32(_cCount.GetValue(comp)));
                float sum = 0f;
                for (int i = 0; i < count; i++)
                {
                    var sc = sys.GetValue(i);
                    if (sc == null) continue;
                    float max = Convert.ToSingle(_scMax.GetValue(sc)), sev = Convert.ToSingle(_scSev.GetValue(sc, null));
                    if (max > 0f && sev > 0f) sum += 100f * sev / max;
                }
                return sum;
            }
            catch { return 0f; }
        }
        /// <summary>1 Minor / 2 Moderate / 3 Severe by the total strength (the game's classes of one section: 25% and 50% of its maximum).</summary>
        public static int FireStage(float total) { return total <= 0f ? 0 : total < 25f ? 1 : total < 50f ? 2 : 3; }
        /// <summary>The stages of the Damage Control panel (0..4): hull by integrity %, water by the game's flooding value.</summary>
        public static int HullStage(float integ, bool model)
        {
            float dmg = Math.Max(0f, Math.Min(100f, 100f - integ));
            if (model) return dmg < 1f ? 0 : dmg <= 25f ? 1 : dmg <= 50f ? 2 : dmg <= 75f ? 3 : 4;
            return integ >= 99f ? 0 : integ >= 75f ? 1 : integ >= 25f ? 2 : integ >= 1f ? 3 : 4;
        }
        public static int FloodStage(float fp) { return fp >= 30f ? 4 : fp >= 20f ? 3 : fp >= 10f ? 2 : fp >= 1f ? 1 : 0; }
        public static Func<bool> ModelOn = () => true;

        static int EngineLevel(float prop) { return prop >= 0.99f ? 0 : prop > 0.01f ? 1 : 4; }   // part lost: yellow; none works: red

        /// <summary>The propulsion's share that still works, 1..0 (as the game counts the speed: Compartments.UpdateWantedVelocityInKnots).</summary>
        static float Propulsion(object comp)
        {
            try
            {
                var list = _cProp == null ? null : _cProp.GetValue(comp) as IList;
                if (list == null || list.Count == 0) return 1f;
                float sum = 0f;
                foreach (var s in list)
                {
                    if (s == null) { sum += 4f; continue; }
                    if (_sInoperable != null && Flag(_sInoperable.GetValue(s, null))) sum += 4f;
                    else sum += _sDamage == null ? 0f : Convert.ToSingle(Convert.ToInt32(_sDamage.GetValue(s, null)));
                }
                return 1f - sum / (list.Count * 4f);
            }
            catch { return 1f; }
        }

        // ------------------------------------------------------------ the map: a note under the icon, a grey icon

        sealed class VmState { public float Next; public Info Info; }
        static readonly ConditionalWeakTable<object, VmState> States = new ConditionalWeakTable<object, VmState>();
        static readonly ConditionalWeakTable<SolidColorBrush, SolidColorBrush> Greys = new ConditionalWeakTable<SolidColorBrush, SolidColorBrush>();
        static PropertyInfo _mBrush, _mLine4;
        static int _scans, _found;
        static float _nextScan;

        static SolidColorBrush Grey(SolidColorBrush b)
        {
            SolidColorBrush g;
            if (Greys.TryGetValue(b, out g)) return g;
            var c = b.Color;
            const float k = 0.62f, gray = 0.55f;
            Func<float, float> mix = v => v * (1f - k) + gray * k;
            g = new SolidColorBrush(Color.FromArgb(c.A, (byte)Math.Round(mix(c.R / 255f) * 255f), (byte)Math.Round(mix(c.G / 255f) * 255f), (byte)Math.Round(mix(c.B / 255f) * 255f)));
            Greys.Add(b, g);
            return g;
        }

        /// <summary>After MapUnitViewModel.Update: the contact's note (once a second) and, for a ship that will sink, the greyer icon.</summary>
        static void UpdatePostfix(object __instance)
        {
            try
            {
                if (!_ok) return;
                var st = States.GetOrCreateValue(__instance);
                if (!IsEnabled()) { st.Info = new Info(); return; }
                float t = UnityEngine.Time.unscaledTime;
                if (t >= st.Next)
                {
                    st.Next = t + 1f + (float)(__instance.GetHashCode() & 255) / 1000f;
                    var unit = _mUnit.GetValue(__instance, null);
                    st.Info = unit != null ? Of(unit) : new Info();
                }
                if (_mBrush == null)
                {
                    _mBrush = __instance.GetType().GetProperty("UnitColorBrush", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _mLine4 = __instance.GetType().GetProperty("ContactInfoLine4", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (st.Info.Doomed && _mBrush != null)
                {
                    var b = _mBrush.GetValue(__instance, null) as SolidColorBrush;
                    if (b != null)
                    {
                        var g = Grey(b);
                        _mBrush.GetSetMethod(true).Invoke(__instance, new object[] { g });
                    }
                }
                // no map canvas found to hang the note under the icon on: it goes to the game's own info lines
                if (st.Info.Any && _scans >= 4 && _found == 0 && _mLine4 != null)
                {
                    var cur = _mLine4.GetValue(__instance, null) as string;
                    _mLine4.GetSetMethod(true).Invoke(__instance, new object[] { string.IsNullOrEmpty(cur) ? st.Info.Text : cur + " " + st.Info.Text });
                }
            }
            catch (Exception e) { Trace("ContactIntel.UpdatePostfix: " + e.Message); }
        }


        /// <summary>Called every frame; works once a second: puts the notes under the map icons (a TextBlock "AdmIntel" in each icon canvas).</summary>
        public static void Tick(float now, FrameworkElement root, bool on)
        {
            if (!_ok) return;
            if (now < _nextScan) return;
            _nextScan = now + 1f;
            if (root == null) return;
            try { Scan(root, on); } catch (Exception e) { Trace("ContactIntel.Scan: " + e.Message); }
        }

        static void Scan(FrameworkElement root, bool on)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            int budget = 60000, found = 0;
            while (stack.Count > 0 && budget-- > 0)
            {
                var e = stack.Pop();
                var cv = e as Canvas;
                if (cv != null && cv.Name == "UnitIcon")
                {
                    found++;
                    object dc = null;
                    try { dc = cv.DataContext; } catch { }
                    VmState st;
                    Info info = new Info();
                    if (on && dc != null && States.TryGetValue(dc, out st)) info = st.Info;
                    SetNote(cv, info);
                }
                int n;
                try { n = VisualTreeHelper.GetChildrenCount(e); } catch { n = 0; }
                for (int k = 0; k < n; k++)
                {
                    DependencyObject c = null;
                    try { c = VisualTreeHelper.GetChild(e, k); } catch { }
                    if (c != null) stack.Push(c);
                }
            }
            _scans++;
            if (found > 0) _found = found;
        }

        static readonly SolidColorBrush[] Levels = {
            null, new SolidColorBrush(Color.FromRgb(240, 208, 74)), new SolidColorBrush(Color.FromRgb(255, 140, 60)), new SolidColorBrush(Color.FromRgb(255, 140, 60)), new SolidColorBrush(Color.FromRgb(255, 72, 60)) };
        // outlines: fire red, water blue, hull black, engine orange
        static readonly SolidColorBrush[] Outlines = {
            new SolidColorBrush(Color.FromRgb(200, 30, 30)), new SolidColorBrush(Color.FromRgb(30, 90, 220)),
            new SolidColorBrush(Color.FromRgb(0, 0, 0)), new SolidColorBrush(Color.FromRgb(255, 130, 0)) };
        static readonly string[] Shapes = {
            "M4,0 C5,2 7,3 7,5 C7,7 5.5,8 4,8 C2.5,8 1,7 1,5 C1,3.5 2,3 2.5,1.5 C3,2.5 3.5,2 4,0 Z",   // fire
            "M4,0 C5,2 7,4 7,5.5 C7,7 5.5,8 4,8 C2.5,8 1,7 1,5.5 C1,4 3,2 4,0 Z",                      // water drop
            "M0,2 L8,2 L6.5,7 L1.5,7 Z",                                                                 // hull
            "M1,2.5 H3 V1 H5 V2.5 H7 V7 H1 Z" };                                                        // engine

        static void SetNote(Canvas cv, Info info)
        {
            StackPanel tb = null;
            int n = cv.Children.Count;
            for (int k = 0; k < n; k++)
            {
                var c = cv.Children[k] as Grid;
                if (c != null && c.Name == "AdmIntel" && c.Children.Count > 0) { tb = c.Children[0] as StackPanel; break; }
            }
            if (!info.Any)
            {
                if (tb != null) { var hg = VisualTreeHelper.GetParent(tb) as UIElement; if (hg != null) hg.Visibility = Visibility.Collapsed; }
                return;
            }
            if (tb == null)
            {
                // a fixed-width grid centred under the icon (the icons inside are packed to its centre)
                tb = new StackPanel { Name = "AdmIntel", Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
                for (int k = 0; k < 4; k++)
                    tb.Children.Add(new Noesis.Path
                    {
                        Data = Geometry.Parse(Shapes[k]), Width = 13f, Height = 13f, Margin = new Thickness(1.5f, 0f, 1.5f, 0f), Stretch = Stretch.Uniform,
                        Stroke = Outlines[k], StrokeThickness = 1.3f
                    });
                tb.Children.Add(new TextBlock { Text = "?", FontSize = 11f, FontWeight = FontWeight.Bold, Margin = new Thickness(0f, -2f, 0f, 0f), Foreground = Levels[1] });
                var host = new Grid { Name = "AdmIntel", Width = 90f, IsHitTestVisible = false };
                host.Children.Add(tb);
                Canvas.SetLeft(host, 11.25f - 45f);   // the icon is 22.5 wide: its centre is 11.25
                Canvas.SetTop(host, 25f);
                cv.Children.Add(host);
                tb = null;
                for (int k = 0; k < cv.Children.Count; k++) { var g = cv.Children[k] as Grid; if (g != null && g.Name == "AdmIntel") { tb = g.Children[0] as StackPanel; break; } }
                if (tb == null) return;
            }
            tb.Visibility = Visibility.Visible;
            var hostGrid = VisualTreeHelper.GetParent(tb) as UIElement;
            if (hostGrid != null) hostGrid.Visibility = Visibility.Visible;
            int[] lv = { info.Fire, info.Flood, info.Hull, info.Engine };
            var q = tb.Children[4] as TextBlock;
            if (q != null) q.Visibility = info.Guess ? Visibility.Visible : Visibility.Collapsed;
            for (int k = 0; k < 4; k++)
            {
                var p = tb.Children[k] as Noesis.Path;
                if (p == null) continue;
                p.Visibility = lv[k] > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (lv[k] > 0 && !ReferenceEquals(p.Fill, Levels[lv[k]])) p.Fill = Levels[lv[k]];
            }
        }

        public static void Patch(Harmony harmony, Assembly asm)
        {
            var tVm = asm.GetType("SeapowerUI.MapUnitViewModel");
            var m = tVm == null ? null : tVm.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (m == null) throw new InvalidOperationException("MapUnitViewModel.Update not found");
            harmony.Patch(m, postfix: new HarmonyMethod(typeof(ContactIntel).GetMethod(nameof(UpdatePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
        }
    }
}
