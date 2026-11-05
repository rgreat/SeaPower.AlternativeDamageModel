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

        public struct Info { public int Fire, Flood, Hull, Engine; public bool Guess, Seen, Ident, Sinking, Dead; public int EtaMin; public string Cause, Via, Attitude, EngineVia; public bool Doomed; public bool Any { get { return Fire + Flood + Hull + Engine > 0; } } public string Text { get { return Any ? (Fire > 0 ? "F" + Fire + " " : "") + (Flood > 0 ? "W" + Flood + " " : "") + (Hull > 0 ? "H" + Hull + " " : "") + (Engine > 0 ? "E" + Engine + (Guess ? "?" : "") : "") : null; } } }

        // ------------------------------------------------------------ reflection

        static bool _ok;
        static Type _tVehicle, _tSensors;
        static FieldInfo _vObject;
        static PropertyInfo _vSensors, _vObserved, _vIdentified, _vDormant, _rpValue;
        static PropertyInfo _oCompartments, _oDestroyed, _cFlood, _cInt, _oPlayer;
        static FieldInfo _oPower, _cBase, _cSys, _cCount, _scMax, _oTaskforce, _cSinking, _cProp, _cImmobile, _gPlayerTf;
        static PropertyInfo _sInoperable, _sDamage, _scSev;
        static MethodInfo _oSpeed;
        static PropertyInfo _gameTime;
        static Type _tSub;
        static PropertyInfo _mUnit;
        static FieldInfo _cPortC, _cStarC, _fcCurI, _fcMaxI;
        static PropertyInfo _fcAllowed;
        static Type _tGizmoHost, _tMapPanel;
        static bool _layerTried;
        static DependencyProperty _dpGizmo, _dpLayer, _dpLat, _dpLon;

        /// <summary>The Noesis properties of the layer host, read once when the first note is made (Noesis is running then); false: not available.</summary>
        static bool LayerHostReady()
        {
            if (_layerTried) return _dpGizmo != null && _dpLayer != null && _dpLat != null && _dpLon != null;
            _layerTried = true;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                if (_tGizmoHost == null || _tMapPanel == null) return false;
                _dpGizmo = _tGizmoHost.GetField("GizmoProperty", all).GetValue(null) as DependencyProperty;
                _dpLayer = _tGizmoHost.GetField("LayerProperty", all).GetValue(null) as DependencyProperty;
                _dpLat = _tMapPanel.GetField("LatitudeProperty", all).GetValue(null) as DependencyProperty;
                _dpLon = _tMapPanel.GetField("LongitudeProperty", all).GetValue(null) as DependencyProperty;
            }
            catch (Exception e) { _tGizmoHost = null; Trace("ContactIntel: layer host unavailable: " + e.Message); }
            return _dpGizmo != null && _dpLayer != null && _dpLat != null && _dpLon != null;
        }
        sealed class Note { public Grid Host; public FrameworkElement Gizmo; public object Vm; }
        static readonly ConditionalWeakTable<Canvas, Note> Notes = new ConditionalWeakTable<Canvas, Note>();

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
                _cProp = tComp.GetField("_propulsionSystems", all);
                _cImmobile = tComp.GetField("_immobile", all);
                _oPower = tObj.GetField("_powerSystemInUse", all);
                _sInoperable = tSys.GetProperty("Inoperable", all);
                _sDamage = tSys.GetProperty("CurrentDamageType", all);
                _cBase = tComp.GetField("_baseObject", all); _cSys = tComp.GetField("_systemCompartments", all); _cCount = tComp.GetField("_compartmentsCount", all);
                _cPortC = tComp.GetField("_portCompartments", all); _cStarC = tComp.GetField("_starboardCompartments", all);
                var tFc = asm.GetType("SeaPower.FloodingCompartment");
                if (tFc != null) { _fcCurI = tFc.GetField("_currentIntegrity", all); _fcMaxI = tFc.GetField("_maxIntegrity", all); _fcAllowed = tFc.GetProperty("MaxAllowedIntegrity", all); }
                var tSc = asm.GetType("SeaPower.SystemCompartment");
                _scSev = tSc.GetProperty("FireSeverity", all); _scMax = tSc.GetField("_maxFireSeverity", all);
                _oSpeed = tObj.GetMethod("getVelocityInKnots", all);
                // only the types here: reading their static fields runs the static constructors, which register Noesis properties, and Noesis is not
                // ready yet while the plugin starts (the game crashes) - the properties are taken at the first use, in a mission (LayerHostReady)
                _tGizmoHost = asm.GetType("SeapowerUI.GizmoHost");
                _tMapPanel = asm.GetType("MapControl.MapPanel");
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
        
        const double Window = 90.0;   // game seconds the trend is measured over
        const double MinSpan = 12.0;  // ... and the least of them before there is a forecast

        static double Now() { try { return Convert.ToDouble(_gameTime.GetValue(null, null)); } catch { return 0; } }

        static object Rp(object rp)
        {
            if (rp == null) return null;
            if (_rpValue == null || _rpValue.DeclaringType != rp.GetType()) _rpValue = rp.GetType().GetProperty("Value");
            return _rpValue == null ? null : _rpValue.GetValue(rp, null);
        }

        public static readonly SolidColorBrush RedBrush = new SolidColorBrush(Color.FromRgb(255, 72, 60)), OrangeBrush = new SolidColorBrush(Color.FromRgb(255, 140, 60)), TextBrush = new SolidColorBrush(Color.FromRgb(214, 218, 222));
        public static object ObjectOf(object vehicle) { return _vObject == null ? null : _vObject.GetValue(vehicle); }
        /// <summary>A contact that is a ship with compartments (not destroyed).</summary>
        public static bool HasCompartments(object vehicle)
        {
            try
            {
                var obj = ObjectOf(vehicle);
                if (obj == null || (_oDestroyed != null && (bool)_oDestroyed.GetValue(obj, null))) return false;
                return _oCompartments.GetValue(obj, null) != null;
            }
            catch { return false; }
        }
        public static bool Flag(object rp) { var v = Rp(rp); return v is bool && (bool)v; }

        // ------------------------------------------------------------ what a contact shows

        // SensorTypeSet bits
        const long Visual = 2, Tv = 4, Radar = 8 | 0x10 | 0x20, Sonar = 0x40 | 0x80;

        public static bool Mine(object obj)
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

        /// <summary>The hull damage that is left cannot be welded any more (every damaged compartment is at its MaxAllowedIntegrity): it is repaired as far as it can be.</summary>
        static bool HullWeldedUp(object comp)
        {
            try
            {
                if (_cPortC == null || _cStarC == null || _fcCurI == null || _fcMaxI == null || _fcAllowed == null) return false;
                var port = _cPortC.GetValue(comp) as Array; var star = _cStarC.GetValue(comp) as Array;
                if (port == null || star == null) return false;
                int n = Math.Min(5, Math.Min(port.Length, star.Length));
                bool damaged = false;
                for (int side = 0; side < 2; side++)
                    for (int i = 0; i < n; i++)
                    {
                        var fc = (side == 0 ? port : star).GetValue(i);
                        if (fc == null) continue;
                        float max = Convert.ToSingle(_fcMaxI.GetValue(fc)), cur = Convert.ToSingle(_fcCurI.GetValue(fc));
                        if (max <= 0f || cur >= max * 0.9995f) continue;
                        damaged = true;
                        if (Convert.ToSingle(_fcAllowed.GetValue(fc, null)) - cur > max * 0.0005f) return false;   // something can still be welded
                    }
                return damaged;
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
                bool hit = mine || HitLog.Records(comp).Count > 0;   // the engine of an enemy is judged only once hits on the ship are registered
                if (!seen && !radar && !sonar) return none;

                bool sinking = (bool)_cSinking.GetValue(comp);
                float flood = Convert.ToSingle(_cFlood.GetValue(comp, null));
                float hull = Convert.ToSingle(_cInt.GetValue(comp, null));
                float prop = Propulsion(comp);

                // the trend, for the forecast
                string cause; double eta = Trend(obj, sinking, flood, hull, out cause);
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
                    if (!mine && info.Flood <= 1) info.Flood = 0;   // light flooding of an enemy cannot be seen
                    info.Hull = HullStage(hull, ModelOn());
                    if (info.Hull <= 1 || (mine && HullWeldedUp(comp))) info.Hull = 0;   // light damage is not shown; own ship: nor damage repaired as far as it can be (a contact's repair plans are not known)
                    if (mine && !sinking && info.Flood > 0 && NoPumpableWater(comp) && (info.Hull == 0 || HullWeldedUp(comp))) info.Flood = 0;   // the water the hull keeps in, with nothing left to weld: not a problem damage control can still solve
                    info.Engine = hit ? (dead ? 4 : EngineLevel(prop)) : 0;   // an enemy's engine: only after hits on the ship are registered
                    if (hit) Latch(obj, info.Engine);
                }
                else if (sonar)
                {
                    info.Engine = hit ? (dead ? 4 : EngineLevel(prop)) : 0;   // heard: engines and screws (after hits)
                    if (hit) Latch(obj, info.Engine);
                    doomed = sinking; info.Doomed = doomed;
                }
                else if (radar)
                {
                    // radar only guesses from the track: a ship hit lately that goes slowly
                    info.Doomed = false;
                    var recs = HitLog.Records(comp);
                    double t = Now();
                    int known = Latched(obj);
                    float kn0 = _oSpeed == null ? 99f : Math.Abs(Convert.ToSingle(_oSpeed.Invoke(obj, null)));
                    if (known == 4 && kn0 < 3f) info.Engine = 4;   // the engine was seen/heard dead and the ship still does not move: it is the same
                    else if (known > 0 && known < 4 && kn0 <= 10f) info.Engine = known;
                    else if (recs.Count > 0 && t - recs[recs.Count - 1].Time <= 600.0 && _oSpeed != null)
                    {
                        float kn = Math.Abs(Convert.ToSingle(_oSpeed.Invoke(obj, null)));
                        info.Engine = kn < 3f ? 4 : kn <= 10f ? 1 : 0;
                        info.Guess = info.Engine > 0;
                    }
                }
                else info.Doomed = false;
                info.Seen = seen; info.Ident = ident; info.Sinking = sinking; info.Dead = dead;
                // the sinking forecast by flooding is shown only when the flooding itself is detected (an enemy's light flooding, or one not seen at all, is not)
                if (!mine && !sinking && cause == "flooding" && info.Flood == 0) { soon = false; info.Doomed = false; }
                info.EtaMin = !sinking && soon && minutes < 1e6 ? Math.Max(1, (int)Math.Ceiling(minutes)) : 0;
                info.Cause = cause;
                info.Attitude = seen ? Attitude(obj) : "";
                var via = new List<string>();
                if (seen) via.Add(ident ? Loc.T("visual observation") : Loc.T("visual observation (not identified)"));
                if (sonar) via.Add(Loc.T("sonar (engine noise)"));
                if (radar) via.Add(Loc.T("radar track (estimate)"));
                info.Via = string.Join(", ", via.ToArray());
                if (!mine && info.Engine > 0)
                {
                    var ev = new List<string>();
                    if (seen) ev.Add(Loc.T("visual"));
                    if (sonar) ev.Add(Loc.T("sonar"));
                    if (radar) ev.Add(info.Guess ? Loc.T("radar estimate") : Loc.T("radar"));
                    info.EngineVia = string.Join(", ", ev.ToArray());
                }
                return info;
            }
            catch (Exception e) { Trace("ContactIntel.Of: " + e.Message); return none; }
        }

        // the engine state seen or heard last: it stays while only the radar sees the ship and the ship has not got going (the sonar loses a stopped ship)
        static readonly ConditionalWeakTable<object, Lt> Latches = new ConditionalWeakTable<object, Lt>();
        sealed class Lt { public int L; }
        static void Latch(object obj, int level) { try { Latches.GetOrCreateValue(obj).L = level; } catch { } }
        static int Latched(object obj) { Lt a; return Latches.TryGetValue(obj, out a) ? a.L : 0; }

        /// <summary>
        /// The trend over the window: (the last - the first) / the time between them. For the hull the direct damage of hits is added
        /// back first (HitLog.Direct: the loss of integrity in each hit), so what is left is the damage that goes on by itself (fire,
        /// water): the hits are taken out of the general trend, even when they come all the time.
        /// </summary>
        static double Rate(Hist h, List<float> v, List<double[]> direct)
        {
            int n = h.T.Count;
            if (n < 2) return 0;
            double span = h.T[n - 1] - h.T[0];
            if (span < MinSpan) return 0;
            double first = v[0], last = v[n - 1];
            if (direct != null)
                foreach (var d in direct)
                    if (d[0] > h.T[0] && d[0] <= h.T[n - 1]) last += d[1];   // hull: what the hits took inside the window
            return (last - first) / span;
        }

        // ---- saved games: "time,flood,hull|..." (the samples of the trend: after loading the forecast goes on, it does not start over)

        public static string SaveTrend(object comp)
        {
            try
            {
                var obj = _cBase == null ? null : _cBase.GetValue(comp);
                Hist h;
                if (obj == null || !History.TryGetValue(obj, out h) || h.T.Count < 2) return null;
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < h.T.Count; i++)
                {
                    if (i > 0) sb.Append('|');
                    sb.Append(h.T[i].ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                      .Append(h.Flood[i].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                      .Append(h.Hull[i].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                }
                sb.Append('/').Append(HitLog.SaveDirect(comp));
                return sb.ToString();
            }
            catch { return null; }
        }

        public static void LoadTrend(object comp, string text)
        {
            try
            {
                var obj = _cBase == null ? null : _cBase.GetValue(comp);
                if (obj == null) return;
                var h = History.GetOrCreateValue(obj);
                h.T.Clear(); h.Flood.Clear(); h.Hull.Clear();
                double now = Now();
                int slash = text.IndexOf('/');
                if (slash >= 0) { HitLog.LoadDirect(comp, text.Substring(slash + 1)); text = text.Substring(0, slash); }
                foreach (var part in text.Split('|'))
                {
                    var f = part.Split(',');
                    double t; float fl, hu;
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    if (f.Length < 3 || !double.TryParse(f[0], System.Globalization.NumberStyles.Float, inv, out t) || !float.TryParse(f[1], System.Globalization.NumberStyles.Float, inv, out fl) ||
                        !float.TryParse(f[2], System.Globalization.NumberStyles.Float, inv, out hu)) continue;
                    if (t > now + 5.0) continue;   // not from this game's time
                    h.T.Add(t); h.Flood.Add(fl); h.Hull.Add(hu);
                }
            }
            catch (Exception e) { Trace("ContactIntel.LoadTrend: " + e.Message); }
        }

        /// <summary>Game seconds to sinking by the trend of the last minute (flooding to its limit, hull to 10%); double.MaxValue - none.</summary>
        static double Trend(object obj, bool sinking, float flood, float hull, out string cause)
        {
            cause = "";
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
                List<double[]> direct = null;
                try { direct = HitLog.Direct(_oCompartments.GetValue(obj, null)); } catch { }
                double rf = Rate(h, h.Flood, null), rh = Rate(h, h.Hull, direct);   // % a second, the direct damage of hits taken out of the hull
                float limit = _tSub != null && _tSub.IsInstanceOfType(obj) ? 50f : Limits.LevelFull;
                double ef = rf > 0.0005 ? (limit - flood) / rf : double.MaxValue, eh = rh < -0.0005 ? (hull - 10f) / -rh : double.MaxValue;
                eta = Math.Min(ef, eh);
                cause = ef <= eh ? "flooding" : "hull damage";
            }
            return eta;
        }

        /// <summary>
        /// The sinking forecast of an own ship for the Damage Control tab: the main line and a cause line;
        /// sev 0 - nothing to fear, 1 - will sink in over 20 minutes, 2 - within 20 minutes, sinking, lost or critical, 3 - a fire no team fights (yellow).
        /// </summary>
        public static void Forecast(object comp, out string main, out string sub, out int sev)
        {
            main = Loc.T("Stable"); sub = ""; sev = 0;
            try
            {
                var obj = _cBase == null ? null : _cBase.GetValue(comp);
                if (obj == null) return;
                if (_oDestroyed != null && (bool)_oDestroyed.GetValue(obj, null)) { main = Loc.T("Lost"); sev = 2; return; }
                bool sinking = (bool)_cSinking.GetValue(comp);
                float flood = Convert.ToSingle(_cFlood.GetValue(comp, null)), hull = Convert.ToSingle(_cInt.GetValue(comp, null));
                if (sinking) { main = Loc.T("Sinking"); sev = 2; return; }
                string cause; double eta = Trend(obj, false, flood, hull, out cause);
                // no sinking in sight, but the state is critical (the teams get no rest for fatigue - see Heavy): "Critical" in red instead of "Stable"
                if (eta == double.MaxValue || eta / 60.0 > 90.0)
                {
                    int hv = HeavyLevel(comp);
                    if (hv >= 2) { main = Loc.T("Critical"); sev = 2; }
                    else if (hv == 1) { main = Loc.T("Uncontrolled Fires"); sev = 3; }   // only a fire no team fights yet: no danger by itself
                    return;
                }
                double min = eta / 60.0;
                main = Loc.F("Sinks in ~{0} min", Math.Max(1, (int)Math.Ceiling(min)));
                sev = min <= 20.0 ? 2 : 1;
                sub = Loc.T(cause);
            }
            catch (Exception e) { Trace("ContactIntel.Forecast: " + e.Message); }
        }

        // ---- list and trim, in words (for what is seen from a distance): smoothed over a few seconds of game time (the rolling on the waves is not a list)

        sealed class Att { public List<double> T = new List<double>(); public List<float> Roll = new List<float>(), Pitch = new List<float>(); }
        static readonly ConditionalWeakTable<object, Att> Atts = new ConditionalWeakTable<object, Att>();

        /// <summary>"list to port" / "heavy list to starboard", "trim by the bow" ...; "" when there is nothing noticeable.
        /// The angles are averaged over the whole observation window (Window, 90 s): waves are slow too, and the mean of a rolling ship is its list.</summary>
        public static string Attitude(object obj)
        {
            try
            {
                var c = obj as UnityEngine.Component;
                if (c == null) return "";
                var a = Atts.GetOrCreateValue(obj);
                var e = c.transform.localEulerAngles;
                Func<float, float> ang = x => { x %= 360f; if (x > 180f) x -= 360f; else if (x < -180f) x += 360f; return x; };
                double t = Now();
                int n = a.T.Count;
                if (n > 0 && (t < a.T[n - 1] || t - a.T[n - 1] > 30.0)) { a.T.Clear(); a.Roll.Clear(); a.Pitch.Clear(); n = 0; }   // time went back (load) or a long gap
                if (n == 0 || t - a.T[n - 1] >= 0.5)
                {
                    // planing boats and hydrofoils trim and heel by design with speed and turn: that is taken off (DynAttitude)
                    float fp, fr;
                    DynAttitude.Fix(_oCompartments.GetValue(obj, null), ang(e.x), ang(e.z), out fp, out fr);
                    a.T.Add(t); a.Roll.Add(fr); a.Pitch.Add(fp);
                }
                while (a.T.Count > 2 && t - a.T[0] > Window) { a.T.RemoveAt(0); a.Roll.RemoveAt(0); a.Pitch.RemoveAt(0); }
                if (a.T[a.T.Count - 1] - a.T[0] < MinSpan) return "";   // too short to tell a list from a wave
                float sr = 0f, sp = 0f;
                for (int i = 0; i < a.T.Count; i++) { sr += a.Roll[i]; sp += a.Pitch[i]; }
                float roll = sr / a.T.Count, pitch = sp / a.T.Count;
                var parts = new List<string>();
                float ar = Math.Abs(roll), ap = Math.Abs(pitch);
                if (ar >= 4f) parts.Add(roll > 0 ? (ar >= 12f ? Loc.T("heavy list to port") : Loc.T("list to port")) : (ar >= 12f ? Loc.T("heavy list to starboard") : Loc.T("list to starboard")));   // +z: port side down
                if (ap >= 1.5f) parts.Add(pitch > 0 ? (ap >= 4f ? Loc.T("heavy trim by the bow") : Loc.T("trim by the bow")) : (ap >= 4f ? Loc.T("heavy trim by the stern") : Loc.T("trim by the stern")));   // +x: the bow down
                return string.Join(", ", parts.ToArray());
            }
            catch { return ""; }
        }

        /// <summary>The engine system (the Engine module in use) when it is out of order, else null.</summary>
        public static object EngineSystem(object comp)
        {
            if (!_ok || comp == null || _oPower == null || _cBase == null) return null;
            try
            {
                var obj = _cBase.GetValue(comp);
                var ps = obj == null ? null : _oPower.GetValue(obj);
                if (ps == null) return null;
                bool out_ = (_sInoperable != null && Flag(_sInoperable.GetValue(ps, null))) || (_sDamage != null && Convert.ToInt32(_sDamage.GetValue(ps, null)) >= 4);
                return out_ ? ps : null;
            }
            catch { return null; }
        }

        /// <summary>The ship's engine (the power system in use: the Engine module, not the propulsion / screws) is out of order: inoperable or destroyed.</summary>
        public static bool EngineOut(object comp)
        {
            if (!_ok || comp == null || _oPower == null || _cBase == null) return false;
            try
            {
                var obj = _cBase.GetValue(comp);
                var ps = obj == null ? null : _oPower.GetValue(obj);
                if (ps == null) return false;
                if (_sInoperable != null && Flag(_sInoperable.GetValue(ps, null))) return true;
                return _sDamage != null && Convert.ToInt32(_sDamage.GetValue(ps, null)) >= 4;   // DamageType.Destroyed
            }
            catch { return false; }
        }

        /// <summary>Any damage at all: water, hull, fire, a lost engine, or sinking (the Forecast card is shown only then).</summary>
        public static bool AnyDamage(object comp)
        {
            if (!_ok || comp == null) return false;
            try
            {
                if ((bool)_cSinking.GetValue(comp)) return true;
                float flood = Convert.ToSingle(_cFlood.GetValue(comp, null)), hull = Convert.ToSingle(_cInt.GetValue(comp, null));
                return FloodStage(flood) > 0 || HullStage(hull, ModelOn()) > 0 || FireTotal(comp) > 0f || EngineOut(comp);
            }
            catch { return false; }
        }

        /// <summary>The own ship is in a bad way (DC rests the teams in a shortened way): sinking, or fire, water or hull at the third stage or worse.</summary>
        /// <summary>
        /// The ship is in a critical state (the teams get no rest for fatigue): sinking; water at stage 3+ (FloodingPercentage 20+) while a hole
        /// still leaks; the hull under 50% while something burns or leaks; fires 50+ in total, or a fire nobody fights.
        /// </summary>
        public static bool Heavy(object comp) { return HeavyLevel(comp) > 0; }

        /// <summary>
        /// The critical state by its kind: 2 - the ship is in danger (sinking; flooding stage 3+ with a leak; hull stage 3+ with a fire or a leak;
        /// fires 50% in all), 1 - only a fire that no team fights (not a danger by itself, but no team rests while it burns), 0 - none.
        /// </summary>
        public static int HeavyLevel(object comp)
        {
            long _p0 = Prof.Start();
            try { return HeavyLevelCore(comp); }
            finally { Prof.Stop(Prof.Heavy, _p0); }
        }

        static int HeavyLevelCore(object comp)
        {
            if (!_ok || comp == null) return 0;
            try
            {
                if ((bool)_cSinking.GetValue(comp)) return 2;
                float flood = Convert.ToSingle(_cFlood.GetValue(comp, null)), hull = Convert.ToSingle(_cInt.GetValue(comp, null));
                bool leak = AnyLeak(comp);
                var sys = _cSys == null ? null : _cSys.GetValue(comp) as Array;
                int count = sys == null ? 0 : Math.Min(sys.Length, Convert.ToInt32(_cCount.GetValue(comp)));
                bool fire = false, unfought = false;
                for (int i = 0; i < count; i++)
                {
                    var sc = sys.GetValue(i);
                    if (sc == null || Convert.ToSingle(_scSev.GetValue(sc, null)) <= 0f) continue;
                    fire = true;
                    if (!DcTeams.FireFought(comp, i)) unfought = true;
                }
                if (FloodStage(flood) >= 3 && leak) return 2;
                if (HullStage(hull, ModelOn()) >= 3 && (fire || leak)) return 2;
                if (FireTotal(comp) >= 50f) return 2;
                return unfought ? 1 : 0;
            }
            catch { return 0; }
        }

        static PropertyInfo _fcDeltaP;
        /// <summary>No water above the compartments' minimum is left (what the hull keeps in cannot be pumped out) and no hole leaks.</summary>
        static bool NoPumpableWater(object comp)
        {
            try
            {
                if (AnyLeak(comp)) return false;
                foreach (var arr in new[] { _cPortC == null ? null : _cPortC.GetValue(comp) as Array, _cStarC == null ? null : _cStarC.GetValue(comp) as Array })
                {
                    if (arr == null) continue;
                    foreach (var fc in arr)
                    {
                        if (fc == null) continue;
                        if (_fcDeltaP == null) _fcDeltaP = fc.GetType().GetProperty("FloodingDelta", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (_fcDeltaP == null) return false;
                        if (Convert.ToSingle(_fcDeltaP.GetValue(fc, null)) > 0.5f) return false;
                    }
                }
                return true;
            }
            catch { return false; }
        }

        static FieldInfo _fcRateF;
        /// <summary>A hole still lets water in somewhere (a compartment's flooding rate above 0).</summary>
        static bool AnyLeak(object comp)
        {
            foreach (var arr in new[] { _cPortC == null ? null : _cPortC.GetValue(comp) as Array, _cStarC == null ? null : _cStarC.GetValue(comp) as Array })
            {
                if (arr == null) continue;
                foreach (var fc in arr)
                {
                    if (fc == null) continue;
                    if (_fcRateF == null) _fcRateF = fc.GetType().GetField("_floodingRate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (_fcRateF != null && Convert.ToSingle(_fcRateF.GetValue(fc)) > 0f) return true;
                }
            }
            return false;
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

        sealed class VmState { public float Next; public Info Info; public bool Greyed; }
        static readonly ConditionalWeakTable<object, VmState> States = new ConditionalWeakTable<object, VmState>();
        static readonly ConditionalWeakTable<SolidColorBrush, SolidColorBrush> Greys = new ConditionalWeakTable<SolidColorBrush, SolidColorBrush>();
        static PropertyInfo _mBrush, _mLine4;
        static MethodInfo _mInvalidate;   // game 0.8.7+: the icon colour is recomputed only when the display is marked dirty
        static bool _on;

        static readonly ConditionalWeakTable<SolidColorBrush, SolidColorBrush> GreyMade = new ConditionalWeakTable<SolidColorBrush, SolidColorBrush>();   // the grey brushes themselves

        static SolidColorBrush Grey(SolidColorBrush b)
        {
            SolidColorBrush g;
            if (Greys.TryGetValue(b, out g)) return g;
            var c = b.Color;
            const float k = 0.62f, gray = 0.55f;
            Func<float, float> mix = v => v * (1f - k) + gray * k;
            g = new SolidColorBrush(Color.FromArgb(c.A, (byte)Math.Round(mix(c.R / 255f) * 255f), (byte)Math.Round(mix(c.G / 255f) * 255f), (byte)Math.Round(mix(c.B / 255f) * 255f)));
            Greys.Add(b, g);
            GreyMade.Add(g, g);
            return g;
        }

        /// <summary>The icon was made grey and the ship will not sink any more (or the option went off): the game 0.8.7+ keeps the colour it was given until its display is marked dirty - mark it
        /// (the game before 0.8.7 recomputes the colour at every Update by itself).</summary>
        static void Ungrey(object vm, VmState st)
        {
            if (!st.Greyed) return;
            st.Greyed = false;
            try
            {
                if (_mInvalidate == null) _mInvalidate = vm.GetType().GetMethod("InvalidateContactDisplay", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (_mInvalidate != null) _mInvalidate.Invoke(vm, null);
            }
            catch (Exception e) { Trace("ContactIntel.Ungrey: " + e.Message); }
        }

        /// <summary>After MapUnitViewModel.Update: the contact's note (once a second) and, for a ship that will sink, the greyer icon.</summary>
        static void UpdatePostfix(object __instance)
        {
            long _p0 = Prof.Start();
            try { UpdatePostfixCore(__instance); }
            finally { Prof.Stop(Prof.MapUnit, _p0); }
        }

        static void UpdatePostfixCore(object __instance)
        {
            try
            {
                if (!_ok) return;
                var st = States.GetOrCreateValue(__instance);
                if (!IsEnabled()) { st.Info = new Info(); Ungrey(__instance, st); return; }
                float t = UnityEngine.Time.unscaledTime;
                if (t >= st.Next)
                {
                    st.Next = t + 1f + (float)(__instance.GetHashCode() & 255) / 1000f;
                    var unit = _mUnit.GetValue(__instance, null);
                    st.Info = unit != null ? Of(unit) : new Info();
                    var cv = UiScan.IconOf(__instance);
                    if (cv != null) SetNote(cv, st.Info);
                }
                if (_mBrush == null)
                {
                    _mBrush = __instance.GetType().GetProperty("UnitColorBrush", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _mLine4 = __instance.GetType().GetProperty("ContactInfoLine4", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (!st.Info.Doomed) Ungrey(__instance, st);
                if (st.Info.Doomed && _mBrush != null)
                {
                    st.Greyed = true;
                    var b = _mBrush.GetValue(__instance, null) as SolidColorBrush;
                    SolidColorBrush already;
                    if (b != null && !GreyMade.TryGetValue(b, out already))   // not again and again: a set raises a property change
                    {
                        var g = Grey(b);
                        _mBrush.GetSetMethod(true).Invoke(__instance, new object[] { g });
                    }
                }
                // no map canvas found to hang the note under the icon on: it goes to the game's own info lines
                if (st.Info.Any && UiScan.Failed && _mLine4 != null)
                {
                    var cur = _mLine4.GetValue(__instance, null) as string;
                    _mLine4.GetSetMethod(true).Invoke(__instance, new object[] { string.IsNullOrEmpty(cur) ? st.Info.Text : cur + " " + st.Info.Text });
                }
            }
            catch (Exception e) { Trace("ContactIntel.UpdatePostfix: " + e.Message); }
        }


        public static bool Ready { get { return _ok; } }
        /// <summary>The setting changed (called every frame, acts on a change only): the notes are shown or all hidden.</summary>
        public static void SetOn(bool on)
        {
            if (!_ok || on == _on) return;
            _on = on;
            try
            {
                foreach (var cv in UiScan.Icons.ToArray())
                {
                    object dc = null;
                    try { dc = cv.DataContext; } catch { }
                    VmState st;
                    SetNote(cv, on && dc != null && States.TryGetValue(dc, out st) ? st.Info : new Info());
                }
            }
            catch (Exception e) { Trace("ContactIntel.SetOn: " + e.Message); }
        }

        /// <summary>A map icon appeared (or got its view model): its note is put under it.</summary>
        public static void IconLoaded(Canvas cv)
        {
            if (!_ok || !_on) return;
            object dc = null;
            try { dc = cv.DataContext; } catch { }
            VmState st;
            if (dc != null && States.TryGetValue(dc, out st)) SetNote(cv, st.Info);
        }

        static readonly SolidColorBrush[] Levels = {
            null, new SolidColorBrush(Color.FromRgb(240, 208, 74)), new SolidColorBrush(Color.FromRgb(255, 140, 60)), new SolidColorBrush(Color.FromRgb(255, 140, 60)), new SolidColorBrush(Color.FromRgb(255, 72, 60)) };
        // outlines: fire red, water blue, hull and engine (propeller) grey
        static readonly SolidColorBrush[] Outlines = {
            new SolidColorBrush(Color.FromRgb(200, 30, 30)), new SolidColorBrush(Color.FromRgb(30, 90, 220)),
            new SolidColorBrush(Color.FromRgb(88, 88, 88)), new SolidColorBrush(Color.FromRgb(88, 88, 88)) };
        static readonly string[] Shapes = {
            "M4,0 C5,2 7,3 7,5 C7,7 5.5,8 4,8 C2.5,8 1,7 1,5 C1,3.5 2,3 2.5,1.5 C3,2.5 3.5,2 4,0 Z",   // fire
            "M4,0 C5,2 7,4 7,5.5 C7,7 5.5,8 4,8 C2.5,8 1,7 1,5.5 C1,4 3,2 4,0 Z",                      // water drop
            "M12,1.5 L20.5,4.5 V11 C20.5,16 17,20 12,22.5 C7,20 3.5,16 3.5,11 V4.5 Z",   // hull: a shield
            "M14.89,7.71 L14.47,8.12 L15.00,8.52 L15.41,8.91 L16.06,9.83 L16.33,10.45 L16.49,10.99 L16.60,12.17 L19.67,11.79 L20.54,11.75 L21.37,11.80 L22.13,11.97 L22.60,12.16 L23.23,12.60 L23.59,13.00 L23.75,13.24 L23.93,13.96 L23.94,14.48 L23.82,15.29 L23.65,15.85 L23.27,16.69 L22.75,17.50 L22.33,18.01 L21.61,18.71 L21.07,19.13 L19.86,19.80 L19.20,20.03 L18.57,20.17 L18.03,20.21 L17.51,20.17 L17.04,20.05 L16.38,19.74 L15.99,19.47 L15.46,18.95 L15.02,18.34 L14.64,17.65 L14.35,16.91 L14.12,16.08 L13.01,16.49 L12.34,16.59 L11.77,16.59 L10.66,16.40 L10.14,16.21 L9.55,15.90 L8.08,19.27 L7.49,20.21 L6.77,20.95 L6.34,21.23 L5.86,21.42 L5.05,21.56 L4.34,21.35 L3.45,20.78 L2.84,20.16 L2.15,19.14 L1.74,18.26 L1.45,17.30 L1.29,16.29 L1.28,15.25 L1.45,14.22 L1.75,13.35 L2.17,12.69 L2.70,12.18 L3.32,11.82 L4.01,11.58 L4.75,11.46 L5.52,11.45 L6.57,11.58 L7.40,11.80 L7.60,10.66 L7.79,10.14 L8.11,9.54 L8.44,9.08 L8.91,8.59 L9.85,7.93 L7.82,5.22 L7.38,4.48 L7.05,3.74 L6.86,2.99 L6.86,2.23 L7.07,1.46 L7.36,1.02 L7.95,0.55 L8.94,0.12 L10.11,-0.09 L11.06,-0.09 L12.36,0.12 L13.33,0.44 L14.25,0.91 L15.09,1.51 L15.79,2.22 L16.22,2.91 L16.45,3.62 L16.51,4.33 L16.42,5.05 L16.10,5.99 L15.57,6.88 Z M14.20,12.07 L14.17,11.64 L14.06,11.23 L13.87,10.84 L13.61,10.50 L13.28,10.21 L12.91,10.00 L12.50,9.86 L12.07,9.80 L11.64,9.83 L11.23,9.94 L10.84,10.13 L10.50,10.39 L10.21,10.72 L10.00,11.09 L9.86,11.50 L9.80,11.93 L9.83,12.36 L9.94,12.77 L10.13,13.16 L10.39,13.50 L10.72,13.79 L11.09,14.00 L11.50,14.14 L11.93,14.20 L12.36,14.17 L12.77,14.06 L13.16,13.87 L13.50,13.61 L13.79,13.28 L14.00,12.91 L14.14,12.50 Z" };   // engine: a propeller, one outline with a hole (the union of the blades and the hub)

        /// <summary>An icon of the set (0 fire, 1 water, 2 hull, 3 engine): outlined, of a given height, its width by its shape.</summary>
        public static Noesis.Path NewIcon(int k, float height, float margin)
        {
            return new Noesis.Path
            {
                Data = Geometry.Parse(Shapes[k]), Height = height, Margin = k == 3 ? new Thickness(0.5f, 0f, 0.5f, 0f) : new Thickness(margin, 0f, margin, 0f), Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Stroke = Outlines[k], StrokeThickness = k == 3 ? 1f : 1.3f, StrokeLineJoin = PenLineJoin.Round
            };
        }

        /// <summary>The fill of an icon by a level 1..4 (0: no fill).</summary>
        public static void SetLevel(Noesis.Path p, int level)
        {
            var b = level > 0 && level < Levels.Length ? Levels[level] : null;
            if (!ReferenceEquals(p.Fill, b)) p.Fill = b;
        }

        /// <summary>The note lives on the map layer under all the other objects (UNASSIGNED_INFO, 1), above the map itself; where that is impossible it is a child of the unit icon.</summary>
        static Note NewNote(Canvas cv, StackPanel tb)
        {
            var host = new Grid { Name = "AdmIntel", Width = 90f, IsHitTestVisible = false };
            host.Children.Add(tb);
            var n = new Note { Host = host };
            object vm = null;
            try { vm = cv.DataContext; } catch { }
            if (vm != null && LayerHostReady())
            {
                try
                {
                    var pos = new Canvas { IsHitTestVisible = false };
                    pos.SetBinding(UIElement.VisibilityProperty, new Binding("Visibility") { Source = cv, Mode = BindingMode.OneWay });   // 0.8.7 merges icons of a group: a collapsed icon takes its note with it
                    pos.SetBinding(_dpLat, new Binding("Latitude") { Source = vm, Mode = BindingMode.OneWay });
                    pos.SetBinding(_dpLon, new Binding("Longitude") { Source = vm, Mode = BindingMode.OneWay });
                    Canvas.SetLeft(host, -45f);          // centred under the icon, whose centre is the position itself
                    Canvas.SetTop(host, 13.75f);
                    pos.Children.Add(host);
                    var g = (FrameworkElement)Activator.CreateInstance(_tGizmoHost);
                    g.SetValue(_dpLayer, 1);
                    g.SetValue(_dpGizmo, pos);
                    cv.Children.Add(g);
                    n.Gizmo = g; n.Vm = vm;
                    return n;
                }
                catch (Exception e) { Trace("ContactIntel: layer note failed: " + e.Message); n.Gizmo = null; }
            }
            Canvas.SetLeft(host, 11.25f - 45f);   // the icon is 22.5 wide: its centre is 11.25
            Canvas.SetTop(host, 25f);
            cv.Children.Add(host);
            return n;
        }

        static void SetNote(Canvas cv, Info info)
        {
            Note note;
            StackPanel tb = null;
            if (Notes.TryGetValue(cv, out note) && note.Host.Children.Count > 0) tb = note.Host.Children[0] as StackPanel;
            if (!info.Any)
            {
                if (note != null) note.Host.Visibility = Visibility.Collapsed;
                return;
            }
            if (tb == null)
            {
                tb = new StackPanel { Name = "AdmIntel", Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
                for (int k = 0; k < 4; k++) tb.Children.Add(NewIcon(k, k == 3 ? 17f : 15f, 0.75f));
                tb.Children.Add(new TextBlock { Text = "?", FontSize = 11f, FontWeight = FontWeight.Bold, Margin = new Thickness(0f, -2f, 0f, 0f), Foreground = Levels[1] });
                note = NewNote(cv, tb);
                Notes.Remove(cv); Notes.Add(cv, note);
            }
            else
            {
                object vm = null;
                try { vm = cv.DataContext; } catch { }
                if (note.Gizmo != null && vm != null && !ReferenceEquals(vm, note.Vm))
                {
                    // the icon got another view model: the note follows it
                    var pos = VisualTreeHelper.GetParent(note.Host) as FrameworkElement;
                    if (pos != null)
                    {
                        pos.SetBinding(_dpLat, new Binding("Latitude") { Source = vm, Mode = BindingMode.OneWay });
                        pos.SetBinding(_dpLon, new Binding("Longitude") { Source = vm, Mode = BindingMode.OneWay });
                    }
                    note.Vm = vm;
                }
            }
            tb.Visibility = Visibility.Visible;
            note.Host.Visibility = Visibility.Visible;
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
