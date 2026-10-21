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

        public struct Info { public string Text; public bool Doomed; public int Sev; }

        // ------------------------------------------------------------ reflection

        static bool _ok;
        static Type _tVehicle, _tSensors;
        static FieldInfo _vObject;
        static PropertyInfo _vSensors, _vObserved, _vIdentified, _vDormant, _rpValue;
        static PropertyInfo _oCompartments, _oDestroyed, _cFlood, _cInt, _oPlayer;
        static FieldInfo _oTaskforce, _cSinking, _cFires, _cProp, _cImmobile, _cMaxInt, _gPlayerTf;
        static PropertyInfo _sInoperable, _sDamage;
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

                var parts = new List<string>();
                if (seen && ident)
                {
                    if (sinking) parts.Add("sinking");
                    else if (soon) parts.Add("sinks ~" + Math.Max(1, (int)Math.Ceiling(minutes)) + " min");
                    if (prop <= 0.01f) parts.Add("dead"); else if (prop < 0.7f) parts.Add("slow");
                    if (fires > 0) parts.Add("fire");
                    if (flood >= 1f) parts.Add("flood " + (int)Math.Round(flood) + "%");
                    if (hull < 90f) parts.Add("hull " + (int)Math.Round(hull) + "%");
                }
                else if (seen)
                {
                    if (sinking) parts.Add("sinking"); else if (soon) parts.Add("sinks soon");
                    if (prop <= 0.01f) parts.Add("dead"); else if (prop < 0.7f) parts.Add("slow");
                    if (fires > 0) parts.Add("fire");
                    if (flood >= 3f) parts.Add("flooding");
                }
                else if (radar)
                {
                    if (prop <= 0.01f) parts.Add("dead"); else if (prop < 0.7f) parts.Add("slow");
                    doomed = false;
                }
                else // sonar
                {
                    if (sinking) parts.Add("sinking");
                    if (prop <= 0.01f) parts.Add("dead"); else if (prop < 0.7f) parts.Add("engine dmg");
                    if (flood >= 25f) parts.Add("flood noise!!"); else if (flood >= 12f) parts.Add("flood noise!"); else if (flood >= 3f) parts.Add("flood noise");
                    doomed = sinking;
                }
                if (parts.Count == 0) return none;
                if (parts.Count > 2) parts.RemoveRange(2, parts.Count - 2);
                var info = new Info { Text = string.Join(" ", parts.ToArray()), Doomed = doomed };
                info.Sev = doomed || (seen && (prop <= 0.01f || flood >= 25f)) ? 2 : 1;
                return info;
            }
            catch (Exception e) { Trace("ContactIntel.Of: " + e.Message); return none; }
        }

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
                if (st.Info.Text != null && _scans >= 4 && _found == 0 && _mLine4 != null)
                {
                    var cur = _mLine4.GetValue(__instance, null) as string;
                    _mLine4.GetSetMethod(true).Invoke(__instance, new object[] { string.IsNullOrEmpty(cur) ? st.Info.Text : cur + " " + st.Info.Text });
                }
            }
            catch (Exception e) { Trace("ContactIntel.UpdatePostfix: " + e.Message); }
        }

        static readonly SolidColorBrush Amber = new SolidColorBrush(Color.FromRgb(240, 208, 74));
        static readonly SolidColorBrush Orange = new SolidColorBrush(Color.FromRgb(255, 140, 60));
        static readonly SolidColorBrush LightRed = new SolidColorBrush(Color.FromRgb(255, 96, 84));

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

        static void SetNote(Canvas cv, Info info)
        {
            TextBlock tb = null;
            int n = cv.Children.Count;
            for (int k = 0; k < n; k++)
            {
                var c = cv.Children[k] as TextBlock;
                if (c != null && c.Name == "AdmIntel") { tb = c; break; }
            }
            if (info.Text == null)
            {
                if (tb != null) tb.Visibility = Visibility.Collapsed;
                return;
            }
            if (tb == null)
            {
                tb = new TextBlock
                {
                    Name = "AdmIntel", FontSize = 8f, Width = 90f, TextAlignment = TextAlignment.Center,
                    IsHitTestVisible = false, FontWeight = FontWeight.Bold
                };
                Canvas.SetLeft(tb, -33.75f);   // the 22.5 icon is centred: 11.25 - 45
                Canvas.SetTop(tb, 23f);
                cv.Children.Add(tb);
            }
            tb.Visibility = Visibility.Visible;
            if (tb.Text != info.Text) tb.Text = info.Text;
            var br = info.Sev >= 2 ? LightRed : Amber;
            if (!ReferenceEquals(tb.Foreground, br)) tb.Foreground = br;
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
