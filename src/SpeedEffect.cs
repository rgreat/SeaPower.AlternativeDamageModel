using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace AlternativeDamageModel
{
    /// <summary>
    /// The ship's speed changes how fast water comes in and fires grow: a broken line through the points "knots=flooding/fire" from [FireAndFlooding] SpeedEffect.
    /// The speed is taken as an absolute value; below the first point the first point's multiplier holds, above the last - the last.
    /// Also: the speed above which a team cannot plug holes (DcControl).
    /// </summary>
    static class SpeedEffect
    {
        public static Action<string> Trace = s => { };

        public static float[] Knots = { 0f, 50f }, FloodMul = { 1f, 1f }, FireMul = { 1f, 1f };
        public static float MaxPlugKnots;   // 0 = no limit
        public static bool SmallAiFree;     // [FireAndFlooding] SmallAiNoSpeedLimit: AI ships under SmallAiLength do not slow down from flooding
        public const float SmallAiLength = 50f;

        static FieldInfo _fBaseObj; static PropertyInfo _pControllable; static bool _initAi;

        /// <summary>A ship under 50 m that nobody controls (an AI ship: an enemy or an ally of the player) while the setting is on: the game's cut of speed by flooding is not applied.</summary>
        public static bool NoCutFor(object comp)
        {
            if (!SmallAiFree || comp == null) return false;
            try
            {
                if (!_initAi)
                {
                    _initAi = true;
                    _fBaseObj = comp.GetType().GetField("_baseObject", All);
                    _pControllable = _fBaseObj == null ? null : _fBaseObj.FieldType.GetProperty("IsControllable", All);
                }
                if (_fBaseObj == null || _pControllable == null) return false;
                var bo = _fBaseObj.GetValue(comp);
                if (bo == null || (bool)_pControllable.GetValue(bo, null)) return false;   // the player's own ship
                float len = DcTeams.ShipLength(comp);
                return len > 0f && len < SmallAiLength;
            }
            catch (Exception e) { Trace("SpeedEffect.NoCutFor: " + e.Message); return false; }
        }

        /// <summary>"0=0.75/1, 10=1/1, ..." - speed, knots = flooding multiplier / fire multiplier. Null when nothing usable.</summary>
        public static float[][] Parse(string s)
        {
            var rows = new List<float[]>();
            if (s == null) return null;
            foreach (var part in s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Trim().Split('=');
                if (kv.Length != 2) continue;
                var fm = kv[1].Trim().Split('/');
                float k, a, b;
                if (!float.TryParse(kv[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out k) || k < 0f) continue;
                if (!float.TryParse(fm[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a) || a < 0f) continue;
                b = a;
                if (fm.Length > 1 && (!float.TryParse(fm[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out b) || b < 0f)) continue;
                rows.Add(new[] { k, a, b });
            }
            if (rows.Count == 0) return null;
            rows.Sort((x, y) => x[0].CompareTo(y[0]));
            return rows.ToArray();
        }

        public static void Set(string s)
        {
            var t = Parse(s);
            if (t == null) t = new[] { new[] { 0f, 1f, 1f }, new[] { 50f, 1f, 1f } };
            Knots = new float[t.Length]; FloodMul = new float[t.Length]; FireMul = new float[t.Length];
            for (int i = 0; i < t.Length; i++) { Knots[i] = t[i][0]; FloodMul[i] = t[i][1]; FireMul[i] = t[i][2]; }
        }

        static float Lerp(float[] ys, float v)
        {
            int n = Knots.Length;
            if (n == 0) return 1f;
            if (v <= Knots[0]) return ys[0];
            if (v >= Knots[n - 1]) return ys[n - 1];
            for (int i = 1; i < n; i++)
                if (v <= Knots[i])
                {
                    float span = Knots[i] - Knots[i - 1];
                    return span <= 1e-6f ? ys[i] : ys[i - 1] + (ys[i] - ys[i - 1]) * (v - Knots[i - 1]) / span;
                }
            return ys[n - 1];
        }

        public static float FloodAt(float knots) { return Lerp(FloodMul, Math.Abs(knots)); }
        public static float FireAt(float knots) { return Lerp(FireMul, Math.Abs(knots)); }

        static readonly Dictionary<Type, FieldInfo> Owners = new Dictionary<Type, FieldInfo>();
        static FieldInfo _fBase; static MethodInfo _mSpeed; static bool _init;
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>The ship's speed in knots (absolute value) for a Compartments, FloodingCompartment or SystemCompartment; 0 when unknown.</summary>
        public static float KnotsOf(object any)
        {
            try
            {
                if (any == null) return 0f;
                object comp = any;
                var t = any.GetType();
                if (t.Name != "Compartments")
                {
                    FieldInfo fo;
                    if (!Owners.TryGetValue(t, out fo)) { fo = t.GetField("_compartments", All); Owners[t] = fo; }
                    if (fo == null) return 0f;
                    comp = fo.GetValue(any);
                    if (comp == null) return 0f;
                }
                if (!_init)
                {
                    _init = true;
                    _fBase = comp.GetType().GetField("_baseObject", All);
                    _mSpeed = _fBase == null ? null : _fBase.FieldType.GetMethod("getVelocityInKnots", All);
                }
                if (_fBase == null || _mSpeed == null) return 0f;
                var bo = _fBase.GetValue(comp);
                return bo == null ? 0f : Math.Abs(Convert.ToSingle(_mSpeed.Invoke(bo, null)));
            }
            catch (Exception e) { Trace("SpeedEffect.KnotsOf: " + e.Message); return 0f; }
        }

        public static float Flood(object any) { return FloodAt(KnotsOf(any)); }
        public static float Fire(object any) { return FireAt(KnotsOf(any)); }

        /// <summary>Too fast for a team to plug holes.</summary>
        public static bool TooFastToPlug(object any) { return MaxPlugKnots > 0f && KnotsOf(any) > MaxPlugKnots; }
    }
}
