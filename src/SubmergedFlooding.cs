using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Water over the deck edge, for ships whose deck points were found by the full mesh algorithm (DeckCorners.PointsOf; other
    /// ships keep the old list/trim rules). Every compartment (5 sections x port/starboard) has its deck point. While that point
    /// is under the sea level, a timer runs in GAME seconds (GameTime.fixedDeltaTime, so time compression counts: 10 s of game
    /// time, not of real time); a surfacing point resets it. After <see cref="Delay"/> seconds the compartment takes on water that
    /// nothing in damage control can remove at the source: <see cref="Rate"/> % of its volume per second per metre of submersion (0.1 x DeckFloodingSpeed)
    /// (deeper = faster). When the compartment is 100% full the water goes to its neighbour - the lowest-lying not-full one next to it
    /// (the neighbours are the sections before and after on the same side, and the other side of the same section); when that one
    /// is full too, to the next one in the chain, and so on.
    /// Section 0 is the bow (+z), port is -x (Compartments constructor).
    /// </summary>
    static class SubmergedFlooding
    {
        public static Func<bool> IsEnabled = () => false;
        public static Action<string> Trace = s => { };
        public static float Rate = 0.1f;          // % per second per metre of submersion: 0.1 x [Sinking] DeckFloodingSpeed ("Water over the deck")
        public static float FireDecay = 1f;       // [FireAndFlooding] SubmergedFireDecay: % of a fire per second per metre a section is under water; 0 = off
        public static float Delay = 3f;           // [Sinking] DeckFloodingDelay: game seconds under water before it starts
        const float UnityToMetre = 67.200066f;    // the game's UNITY_TO_METER

        static bool _init, _ok;
        static FieldInfo _fBase, _fVessel, _fCount, _fPort, _fStar, _fCur, _fMax, _fWater, _fObject;
        static Func<float> _gameDt;

        sealed class State
        {
            public DeckCorners.ShipPoints P;
            public int Count;
            public int[] Map;          // compartment k (= side * Count + section) -> point index in P.Points (side 0 port, 1 starboard)
            public float[] Under;      // continuous game seconds the point has been under water
            public float[] Depth;      // m, current (negative = above)
            public float[] Own;        // %/s of its own inflow now (0 before Delay)
            public float[] Got;        // %/s received as overflow from others
            public int[] GotFrom;      // compartment it comes from (the last one), -1 none
            public int[] PassedTo;     // where this full compartment's water goes now, -1 none
            public bool Deep;                    // a destroyed ship with all its deck points under water: no more wave sampling
            public float Zc;                     // mean z of the compartments (the pitch axis)
            public float[] M;                    // last Margins: list to port, to starboard, trim to the stern, to the bow (degrees at which the first point touches)
            public float ListTouch, TrimTouch;   // degrees of list / trim at which the first deck point touches the flat sea (0 = unknown)
        }
        static readonly ConditionalWeakTable<object, State> States = new ConditionalWeakTable<object, State>();

        static void Init(object comp)
        {
            if (_init) return;
            _init = true;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var t = comp.GetType();
                _fBase = t.GetField("_baseObject", all); _fVessel = t.GetField("_vessel", all); _fCount = t.GetField("_compartmentsCount", all);
                _fPort = t.GetField("_portCompartments", all); _fStar = t.GetField("_starboardCompartments", all);
                var tF = _fPort == null ? null : _fPort.FieldType.GetElementType();
                _fCur = tF == null ? null : tF.GetField("_currentFlooding", all); _fMax = tF == null ? null : tF.GetField("_maxFlooding", all);
                _fWater = tF == null ? null : tF.GetField("_currentWaterLevel", all); _fObject = tF == null ? null : tF.GetField("_object", all);
                var tGt = t.Assembly.GetType("SeaPower.GameTime");
                var getter = tGt == null ? null : tGt.GetProperty("fixedDeltaTime", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var m = getter == null ? null : getter.GetGetMethod(true);
                if (m != null) _gameDt = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), m);
                _ok = _fBase != null && _fVessel != null && _fCount != null && _fPort != null && _fStar != null && _fCur != null && _fMax != null && _fWater != null && _fObject != null && _gameDt != null;
            }
            catch (Exception e) { Trace("SubmergedFlooding.Init: " + e.Message); _ok = false; }
            if (!_ok) Trace("SubmergedFlooding: game members not found; flooding over the deck edge does nothing");
        }

        static PropertyInfo _pDestroyed;
        /// <summary>The ship is destroyed (ObjectBase.IsDestroyed): the game no longer samples the waves for it.</summary>
        public static bool IsDestroyed(object comp)
        {
            try
            {
                object bo = _fBase.GetValue(comp);
                if (bo == null) return false;
                if (_pDestroyed == null) _pDestroyed = bo.GetType().GetProperty("IsDestroyed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return _pDestroyed != null && (bool)_pDestroyed.GetValue(bo, null);
            }
            catch { return false; }
        }

        static State Get(object comp)
        {
            State s;
            if (States.TryGetValue(comp, out s)) return s.P == null ? null : s;
            if (_fVessel.GetValue(comp) == null) { States.Add(comp, new State()); return null; }   // submarines: no
            object unit = _fBase.GetValue(comp);
            var sp = DeckCorners.PointsOf(unit);
            if (sp == null) return null;                       // not ready yet: try again next tick
            if (!sp.Exact || sp.Points == null || sp.ShipT == null) { States.Add(comp, new State()); return null; }
            int n = (int)_fCount.GetValue(comp);
            var port = _fPort.GetValue(comp) as Array; var star = _fStar.GetValue(comp) as Array;
            if (n < 1 || port == null || star == null || port.Length < n || star.Length < n) { States.Add(comp, new State()); return null; }
            s = new State { P = sp, Count = n, Map = new int[2 * n], Under = new float[2 * n], Depth = new float[2 * n], Own = new float[2 * n], Got = new float[2 * n], GotFrom = new int[2 * n], PassedTo = new int[2 * n] };
            for (int k = 0; k < 2 * n; k++)
            {
                var fc = (k < n ? port : star).GetValue(k % n);
                var go = _fObject.GetValue(fc) as GameObject;
                s.Map[k] = -1; s.GotFrom[k] = -1; s.PassedTo[k] = -1;
                if (go == null) continue;
                Vector3 lp = go.transform.localPosition;     // the compartment's own place in the ship's frame
                float best = float.MaxValue;
                for (int j = 0; j < sp.Points.Length; j++)
                {
                    bool portPoint = sp.Points[j].x <= 0f;
                    if (portPoint != (k < n)) continue;
                    float d = Mathf.Abs(sp.Points[j].z - lp.z);
                    if (d < best) { best = d; s.Map[k] = j; }
                }
            }
            for (int k = 0; k < 2 * n; k++) if (s.Map[k] < 0) { States.Add(comp, new State()); return null; }
            try { Touch(s, port, star); } catch (Exception e) { Trace("SubmergedFlooding.Touch: " + e.Message); }
            States.Add(comp, s);
            Trace("SubmergedFlooding: " + (2 * n) + " compartments matched to deck points");
            return s;
        }

        /// <summary>
        /// The list and the trim at which a deck point first touches the flat sea (no waves) - where the red zones of the LIST / TRIM
        /// gauges start. The ship's rest waterline is where the game puts the compartments (their local y: TrimDepth, TrimAngle);
        /// freeboard f = the point's height above it. The ship turns about the waterline at the centreline (list) / at the middle of the
        /// compartments (trim): a point at distance d from the axis touches the sea at tan(angle) = f / d. The least angle over all points.
        /// </summary>
        static void Touch(State s, Array port, Array star)
        {
            int n = s.Count;
            var pos = new Vector3[2 * n]; var ok = new bool[2 * n];
            float zc = 0f; int cnt = 0;
            for (int k = 0; k < 2 * n; k++)
            {
                var go = _fObject.GetValue((k < n ? port : star).GetValue(k % n)) as GameObject;
                if (go == null) continue;
                pos[k] = go.transform.localPosition; ok[k] = true; zc += pos[k].z; cnt++;
            }
            if (cnt == 0) return;
            zc /= cnt; s.Zc = zc;
            double list = 90.0, trim = 90.0;
            for (int k = 0; k < 2 * n; k++)
            {
                if (!ok[k]) continue;
                Vector3 p = s.P.Points[s.Map[k]];
                float f = p.y - pos[k].y;
                float dx = Mathf.Abs(p.x), dz = Mathf.Abs(p.z - zc);
                if (dx > 1e-4f) list = Math.Min(list, Math.Atan2(f, dx) * 180.0 / Math.PI);
                if (dz > 1e-4f) trim = Math.Min(trim, Math.Atan2(f, dz) * 180.0 / Math.PI);
            }
            s.ListTouch = (float)Math.Max(1.0, Math.Min(60.0, list));
            s.TrimTouch = (float)Math.Max(0.5, Math.Min(30.0, trim));
            Trace("SubmergedFlooding: deck points touch the flat sea at list " + s.ListTouch.ToString("0.0") + ", trim " + s.TrimTouch.ToString("0.0"));
        }

        /// <summary>The touch angles of this ship (see Touch) when it floods by the deck-edge rule; false otherwise.</summary>
        public static bool TouchAngles(object comp, out float list, out float trim)
        {
            list = trim = 0f;
            if (!Covers(comp)) return false;
            var s = Get(comp);
            if (s == null || s.ListTouch <= 0f || s.TrimTouch <= 0f) return false;
            list = s.ListTouch; trim = s.TrimTouch;
            return true;
        }

        /// <summary>
        /// The ABSOLUTE list / trim limits of the ship (degrees, as the gauges read them): the value at which the first deck point of that
        /// side touches the flat sea (Ocean level). Exact solution, not an approximation: the point p is taken in the ship's own frame
        /// relative to the axis (the centreline at sea level, middle of the compartments, found from the present pose - so the present draft,
        /// flooding included, counts); the height of the point at list a and trim b is h = (Rx(b)·Rz(a)·p).y - the Unity order of the
        /// angles - and the limit is the first root of h = 0 going from 0 in the direction of that side (a > 0 port down, a &lt; 0 starboard
        /// down; b &gt; 0 bow down, b &lt; 0 stern down), the other angle staying at its present value. A point already under water at 0 gives 0.
        /// For a box hull this is the textbook "deck edge immersion": tan a = freeboard / half-beam. list: present list (+ port down),
        /// trim: present trim (+ bow down).
        /// </summary>
        public static bool Margins(object comp, float list, float trim, out float toPort, out float toStbd, out float toStern, out float toBow)
        {
            toPort = toStbd = toStern = toBow = 0f;
            if (!Covers(comp)) return false;
            var s = Get(comp);
            if (s == null || s.P.ShipT == null) return false;
            Transform t = s.P.ShipT;
            Vector3 upw = t.up;
            if (upw.y < 0.2f) return false;
            float sea = DeckCorners.SeaLevel();
            Vector3 o = t.TransformPoint(new Vector3(0f, 0f, s.Zc));
            Vector3 a = o + upw * ((sea - o.y) / upw.y);        // the centreline at the flat sea level, world
            Vector3 aLocal = t.InverseTransformPoint(a);
            float mp = 89f, ms = 89f, mst = 89f, mb = 89f;
            for (int k = 0; k < 2 * s.Count; k++)
            {
                Vector3 p = s.P.Points[s.Map[k]] - aLocal;       // the point relative to the axis, in the ship's frame
                if (Mathf.Abs(p.x) > 1e-4f)
                {
                    if (p.x < 0f) mp = Mathf.Min(mp, TouchAngle(p, trim, true, 1f)); else ms = Mathf.Min(ms, TouchAngle(p, trim, true, -1f));
                }
                if (Mathf.Abs(p.z) > 1e-4f)
                {
                    if (p.z > 0f) mb = Mathf.Min(mb, TouchAngle(p, list, false, 1f)); else mst = Mathf.Min(mst, TouchAngle(p, list, false, -1f));
                }
            }
            toPort = mp; toStbd = ms; toStern = mst; toBow = mb;
            // the same as the tilt still available from the present pose (what the hint shows): to port / starboard (list + port down),
            // to the stern / bow (trim + bow down); 0 once the point is under water
            s.M = new[] { Mathf.Max(0f, toPort - list), Mathf.Max(0f, toStbd + list), Mathf.Max(0f, toStern + trim), Mathf.Max(0f, toBow - trim) };
            return true;
        }

        /// <summary>First angle (degrees, magnitude) in the direction <paramref name="dir"/> at which the point reaches the sea level;
        /// roll: the point's list is turned (a = list, b = other = trim), else its trim (a = trim, b = other = list). 0 if it is under already, 89 if never.</summary>
        static float TouchAngle(Vector3 p, float other, bool roll, float dir)
        {
            Func<float, float> h = ang => (Quaternion.Euler(roll ? other : ang, 0f, roll ? ang : other) * p).y;
            if (h(0f) <= 0f) return 0f;
            float prev = 0f;
            for (float m = 0.5f; m <= 89f; m += 0.5f)
            {
                if (h(dir * m) <= 0f)
                {
                    float lo = prev, hi = m;
                    for (int i = 0; i < 24; i++) { float mid = 0.5f * (lo + hi); if (h(dir * mid) <= 0f) hi = mid; else lo = mid; }
                    return 0.5f * (lo + hi);
                }
                prev = m;
            }
            return 89f;
        }

        /// <summary>The last Margins of this ship as a line of text, null if none yet.</summary>
        public static string MarginText(object comp)
        {
            State s;
            if (!States.TryGetValue(comp, out s) || s.M == null) return null;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return "Tilt to the sea: list " + s.M[0].ToString("0.#", ci) + "° port / " + s.M[1].ToString("0.#", ci) + "° stbd · trim " +
                   s.M[2].ToString("0.0", ci) + "° stern / " + s.M[3].ToString("0.0", ci) + "° bow";
        }

        /// <summary>
        /// The extra condition of sinking from flooding for ships with exact deck points (the water level threshold itself is checked by the
        /// game): both central compartments (the middle section, port and starboard) are under the sea, or at least <see cref="SinkPoints"/>
        /// of the 10 compartments are - "under the sea" as in the rule above (the deck point under water for <see cref="Delay"/> seconds
        /// in a row). Ships without exact points: always true.
        /// </summary>
        public static bool SinkAllowed(object comp)
        {
            try
            {
                if (comp == null || !IsEnabled()) return true;
                Init(comp);
                if (!_ok) return true;
                if (IsDestroyed(comp)) return true;   // already destroyed: nothing more to wait for
                var s = Get(comp);
                if (s == null) return true;
                int n = s.Count, total = 2 * n, count = 0;
                for (int k = 0; k < total; k++) if (s.Depth[k] > 0f && s.Under[k] >= Delay) count++;
                if (count >= SinkPoints) return true;
                if (n % 2 == 1)
                {
                    int mid = n / 2;
                    if (s.Depth[mid] > 0f && s.Under[mid] >= Delay && s.Depth[n + mid] > 0f && s.Under[n + mid] >= Delay) return true;
                }
                return false;
            }
            catch { return true; }
        }
        public const int SinkPoints = 5;

        /// <summary>True when this ship floods over the deck edge by this file's rule (exact deck points, rule on): the old list / trim
        /// flooding (TrimFlooding) is then not used for it - the same water must not come in twice for one cause.</summary>
        public static bool Covers(object comp)
        {
            try
            {
                if (comp == null || !IsEnabled() || Rate <= 0f) return false;
                Init(comp);
                return _ok && Get(comp) != null;
            }
            catch { return false; }
        }

        static object Fc(object comp, int n, int k)
        {
            var arr = (k < n ? _fPort : _fStar).GetValue(comp) as Array;
            return arr.GetValue(k % n);
        }

        static bool Full(object fc) { return (float)_fCur.GetValue(fc) >= (float)_fMax.GetValue(fc) * 0.999f; }

        /// <summary>"Port Aft midships": the side and the section's name as the game writes it in the section's header.</summary>
        static string Name(State s, int k, string[] names)
        {
            int l = k % s.Count;
            string sec = names != null && l < names.Length && !string.IsNullOrEmpty(names[l]) ? names[l] : "section " + (l + 1);
            return (k < s.Count ? "Port " : "Starboard ") + sec;
        }

        /// <summary>Prefix of Compartments.OnFixedUpdate.</summary>
        public static void Tick(object comp)
        {
            if (comp == null || !IsEnabled()) return;
            Init(comp);
            if (!_ok) return;
            try
            {
                State s = Get(comp);
                if (s == null) return;
                float dt = _gameDt();
                if (dt <= 0f) return;
                int n = s.Count, total = 2 * n;
                Matrix4x4 ltw = s.P.ShipT.localToWorldMatrix;
                bool destroyed = IsDestroyed(comp);
                var worldY = new float[total];
                for (int k = 0; k < total; k++)
                {
                    var fc = Fc(comp, n, k);
                    worldY[k] = ltw.MultiplyPoint3x4(s.P.Points[s.Map[k]]).y;
                    float water = (float)_fWater.GetValue(fc);
                    if (destroyed && !s.Deep)
                    {
                        // the game stops sampling the waves for a destroyed ship (Compartments' wave job skips IsDestroyed): its water level stays as it was
                        // at the moment of destruction - take the wave at the point ourselves
                        Vector3 wp = ltw.MultiplyPoint3x4(s.P.Points[s.Map[k]]);
                        float wv = DeckCorners.QueryWave(wp.x, wp.z);
                        if (!float.IsNaN(wv)) { water = wv; _fWater.SetValue(fc, wv); }
                    }
                    float depth = float.IsNaN(water) ? -1f : (water - worldY[k]) * UnityToMetre;
                    s.Depth[k] = depth;
                    s.Under[k] = depth > 0f ? s.Under[k] + dt : 0f;
                    s.Got[k] = 0f; s.GotFrom[k] = -1; s.PassedTo[k] = -1; s.Own[k] = 0f;
                }
                if (destroyed && !s.Deep)
                {
                    bool all = true;
                    for (int k = 0; k < total && all; k++) all = s.Depth[k] > 0f;
                    if (all) { s.Deep = true; Trace("SubmergedFlooding: destroyed ship is under water, wave sampling off"); }
                }
                if (FireDecay > 0f) PutOutFires(comp, s, dt);
                if (Rate <= 0f) return;
                for (int k = 0; k < total; k++)
                {
                    if (s.Under[k] < Delay || s.Depth[k] <= 0f) continue;
                    float perSec = Rate * s.Depth[k];
                    s.Own[k] = perSec;
                    float amount = perSec * dt;
                    int at = k;
                    for (int guard = 0; guard < total && amount > 1e-6f; guard++)
                    {
                        var fc = Fc(comp, n, at);
                        float cur = (float)_fCur.GetValue(fc), max = (float)_fMax.GetValue(fc);
                        float space = Math.Max(0f, max - cur);
                        float add = Math.Min(amount, space);
                        if (add > 0f)
                        {
                            _fCur.SetValue(fc, cur + add);
                            if (at != k) { s.Got[at] += add / dt; s.GotFrom[at] = k; }
                            amount -= add;
                        }
                        if (amount <= 1e-6f) break;
                        // this one is full: on to its nearest not-full neighbour
                        int next = Next(comp, s, worldY, at);
                        if (next < 0) break;
                        if (s.PassedTo[k] < 0) s.PassedTo[k] = next;
                        at = next;
                    }
                }
            }
            catch (Exception e) { Trace("SubmergedFlooding: " + e.Message); }
        }

        static FieldInfo _fSys, _fScMax, _fScGrow;
        static PropertyInfo _pScSev;
        static bool _fireInit, _fireOk;

        /// <summary>
        /// A section under the sea puts its fire out: the fire loses FireDecay % of its maximum per second for every metre the section's deck edge is
        /// under water (a section's depth = the mean of its two sides' depths, a dry side counting 0), and its growth stops. Works for ships with deck points.
        /// </summary>
        static void PutOutFires(object comp, State s, float dt)
        {
            if (!_fireInit)
            {
                _fireInit = true;
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                try
                {
                    _fSys = comp.GetType().GetField("_systemCompartments", all);
                    var tSc = _fSys == null ? null : _fSys.FieldType.GetElementType();
                    _pScSev = tSc == null ? null : tSc.GetProperty("FireSeverity", all);
                    _fScMax = tSc == null ? null : tSc.GetField("_maxFireSeverity", all);
                    _fScGrow = tSc == null ? null : tSc.GetField("_fireGrowRate", all);
                    _fireOk = _fSys != null && _pScSev != null && _fScMax != null && _fScGrow != null;
                }
                catch (Exception e) { Trace("SubmergedFlooding.PutOutFires init: " + e.Message); }
                if (!_fireOk) Trace("SubmergedFlooding: fire members not found; a submerged section does not put its fire out");
            }
            if (!_fireOk) return;
            var sys = _fSys.GetValue(comp) as Array;
            if (sys == null) return;
            int n = s.Count;
            for (int l = 0; l < n && l < sys.Length; l++)
            {
                float depth = (Math.Max(0f, s.Depth[l]) + Math.Max(0f, s.Depth[n + l])) * 0.5f;
                if (depth <= 0f) continue;
                var sc = sys.GetValue(l);
                float sev = (float)_pScSev.GetValue(sc, null), grow = (float)_fScGrow.GetValue(sc);
                if (sev <= 0f && grow <= 0f) continue;
                float max = (float)_fScMax.GetValue(sc);
                if (max <= 0f) continue;
                if (sev > 0f) _pScSev.SetValue(sc, Math.Max(0f, sev - FireDecay * depth / 100f * max * dt), null);
                if (grow > 0f) _fScGrow.SetValue(sc, 0f);
                if (!_fireTraced) { _fireTraced = true; Trace("SubmergedFlooding: section " + (l + 1) + " is " + depth.ToString("0.0") + " m under water: its fire dies down at " + (FireDecay * depth).ToString("0.0") + " %/s"); }
            }
        }
        static bool _fireTraced;

        /// <summary>Where the water of the full compartment <paramref name="from"/> goes: its own neighbours first (the lowest not-full one by the
        /// deck point); when all of them are full too, theirs, and so on along the chain - the nearest not-full compartment, the lowest of those
        /// equally near. -1 if there is none. (Each full compartment looks from itself, so both full compartments of a section feed their own
        /// neighbours, not one common one.)</summary>
        static int Next(object comp, State s, float[] worldY, int from)
        {
            int n = s.Count;
            var seen = new HashSet<int> { from };
            var frontier = new List<int> { from };
            while (frontier.Count > 0)
            {
                var cand = new List<int>();
                foreach (int k in frontier)
                {
                    int side = k / n, l = k % n;
                    int[] nb = { l > 0 ? side * n + l - 1 : -1, l < n - 1 ? side * n + l + 1 : -1, (1 - side) * n + l };
                    foreach (int j in nb) if (j >= 0 && seen.Add(j)) cand.Add(j);
                }
                int best = -1; float bestY = float.MaxValue;
                foreach (int j in cand)
                    if (!Full(Fc(comp, n, j)) && worldY[j] < bestY) { bestY = worldY[j]; best = j; }
                if (best >= 0) return best;
                frontier = cand;
            }
            return -1;
        }

        /// <summary>Lines for the flooding arrow tooltips of section i, that side: why this compartment takes on water over the deck edge.</summary>
        public static List<string> Reasons(object comp, int i, bool port, string[] names)
        {
            var list = new List<string>();
            try
            {
                if (comp == null || !IsEnabled() || !_init || !_ok || Rate <= 0f) return list;
                State s;
                if (!States.TryGetValue(comp, out s) || s.P == null) return list;
                int k = (port ? 0 : s.Count) + i;
                if (k < 0 || k >= 2 * s.Count) return list;
                string r = Rate.ToString(Rate < 0.1f ? "0.00" : "0.0", System.Globalization.CultureInfo.InvariantCulture);
                if (s.Depth[k] > 0f && s.Under[k] < Delay)
                    list.Add("Deck edge " + s.Depth[k].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m under water for " + ((int)s.Under[k]) + " of " + ((int)Delay) + " s");
                if (s.Own[k] > 0f)
                {
                    string t = "Deck edge " + s.Depth[k].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m under water: +" + s.Own[k].ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "%/s (" + r + "%/s per m)";
                    if (s.PassedTo[k] >= 0) t += ", full: goes to " + Name(s, s.PassedTo[k], names);
                    list.Add(t);
                }
                if (s.GotFrom[k] >= 0) list.Add("Overflow from " + Name(s, s.GotFrom[k], names) + ": +" + s.Got[k].ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "%/s");
            }
            catch (Exception e) { Trace("SubmergedFlooding.Reasons: " + e.Message); }
            return list;
        }
    }
}
