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
    /// nothing in damage control can remove at the source: <see cref="Rate"/> % of its volume per second per metre of submersion
    /// (deeper = faster). When the compartment is 100% full the water goes to its neighbour - the lowest-lying not-full one next to it
    /// (the neighbours are the sections before and after on the same side, and the other side of the same section); when that one
    /// is full too, to the next one in the chain, and so on.
    /// Section 0 is the bow (+z), port is -x (Compartments constructor).
    /// </summary>
    static class SubmergedFlooding
    {
        public static Func<bool> IsEnabled = () => false;
        public static Action<string> Trace = s => { };
        public static float Rate = 0.1f;          // [Sinking] SubmergedFloodingRate: % per second per metre
        public const float Delay = 10f;           // game seconds under water before it starts
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
            States.Add(comp, s);
            Trace("SubmergedFlooding: " + (2 * n) + " compartments matched to deck points");
            return s;
        }

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

        static string Name(State s, int k) { return (k < s.Count ? "Port " : "Starboard ") + (k % s.Count + 1); }

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
                var worldY = new float[total];
                for (int k = 0; k < total; k++)
                {
                    var fc = Fc(comp, n, k);
                    worldY[k] = ltw.MultiplyPoint3x4(s.P.Points[s.Map[k]]).y;
                    float water = (float)_fWater.GetValue(fc);
                    float depth = float.IsNaN(water) ? -1f : (water - worldY[k]) * UnityToMetre;
                    s.Depth[k] = depth;
                    s.Under[k] = depth > 0f ? s.Under[k] + dt : 0f;
                    s.Got[k] = 0f; s.GotFrom[k] = -1; s.PassedTo[k] = -1; s.Own[k] = 0f;
                }
                if (Rate <= 0f) return;
                for (int k = 0; k < total; k++)
                {
                    if (s.Under[k] < Delay || s.Depth[k] <= 0f) continue;
                    float perSec = Rate * s.Depth[k];
                    s.Own[k] = perSec;
                    float amount = perSec * dt;
                    int at = k;
                    var visited = new HashSet<int>();
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
                        // this one is full: the lowest not-full neighbour of the full group around it
                        visited.Add(at);
                        int next = Lowest(comp, s, worldY, visited);
                        if (next < 0) break;
                        if (s.PassedTo[k] < 0) s.PassedTo[k] = next;
                        at = next;
                    }
                }
            }
            catch (Exception e) { Trace("SubmergedFlooding: " + e.Message); }
        }

        /// <summary>The lowest (by the deck point) not-full compartment next to any of the full, visited ones; -1 if none.</summary>
        static int Lowest(object comp, State s, float[] worldY, HashSet<int> full)
        {
            int n = s.Count, best = -1; float bestY = float.MaxValue;
            foreach (int k in full)
            {
                int side = k / n, l = k % n;
                int[] nb = { l > 0 ? side * n + l - 1 : -1, l < n - 1 ? side * n + l + 1 : -1, (1 - side) * n + l };
                foreach (int j in nb)
                {
                    if (j < 0 || full.Contains(j)) continue;
                    if (Full(Fc(comp, n, j))) { full.Add(j); return Lowest(comp, s, worldY, full); }   // full too: on along the chain
                    if (worldY[j] < bestY) { bestY = worldY[j]; best = j; }
                }
            }
            return best;
        }

        /// <summary>Lines for the flooding arrow tooltips of section i, that side: why this compartment takes on water over the deck edge.</summary>
        public static List<string> Reasons(object comp, int i, bool port)
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
                    if (s.PassedTo[k] >= 0) t += ", full: goes to " + Name(s, s.PassedTo[k]);
                    list.Add(t);
                }
                if (s.GotFrom[k] >= 0) list.Add("Overflow from " + Name(s, s.GotFrom[k]) + ": +" + s.Got[k].ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "%/s");
            }
            catch (Exception e) { Trace("SubmergedFlooding.Reasons: " + e.Message); }
            return list;
        }
    }
}
