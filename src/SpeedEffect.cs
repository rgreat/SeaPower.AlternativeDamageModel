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
        public static float SmallBoost = 1f;   // [FireAndFlooding] SmallShipSpeedUp: x fire growth and flooding at SmallToTons and below, 1 = off
        public const float SmallFromTons = 1000f, SmallToTons = 50f;
        public static float MaxPlugKnots;   // 0 = no limit
        public static bool SmallAiFree;     // [FireAndFlooding] SmallAiNoSpeedLimit: AI ships under SmallAiLength do not slow down from flooding
        public const float SmallAiLength = 50f;

        static FieldInfo _fBaseObj; static PropertyInfo _pPlayer; static bool _initAi;

        /// <summary>A ship under 50 m that nobody controls (an AI ship: an enemy or an ally of the player) while the setting is on: the game's cut of speed by flooding is not applied.</summary>
        public static bool Failed;   // the last NoCutFor could not tell (asked again next time)
        public static bool NoCutFor(object comp)
        {
            Failed = false;
            if (!SmallAiFree || comp == null) return false;
            try
            {
                if (!_initAi)
                {
                    _initAi = true;
                    _fBaseObj = comp.GetType().GetField("_baseObject", All);
                    _pPlayer = _fBaseObj == null ? null : _fBaseObj.FieldType.GetProperty("IsPlayerObject", All);
                }
                if (_fBaseObj == null || _pPlayer == null) return false;
                var bo = _fBaseObj.GetValue(comp);
                bool ctl = bo != null && (bool)_pPlayer.GetValue(bo, null);
                float len = DcTeams.ShipLength(comp);
                if (bo == null || ctl) return false;   // the player's own ship
                return len > 0f && len < SmallAiLength;
            }
            catch (Exception e) { Failed = true; Trace("SpeedEffect.NoCutFor: " + e.Message); return false; }
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

        // Called for every flooding / fire compartment of every ship in every physics tick (the transpilers): the owner of a
        // compartment is found once, the speed is read once per frame and ship (a reflection call each time was most of the mod's cost)
        sealed class Owner { public object Comp; public float Size = -1f; }
        sealed class Kn { public int Frame = -1; public float Value; }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Owner> OwnerOf = new System.Runtime.CompilerServices.ConditionalWeakTable<object, Owner>();
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Kn> Speeds = new System.Runtime.CompilerServices.ConditionalWeakTable<object, Kn>();

        /// <summary>The ship's speed in knots (absolute value) for a Compartments, FloodingCompartment or SystemCompartment; 0 when unknown.</summary>
        public static float KnotsOf(object any)
        {
            try
            {
                if (any == null) return 0f;
                Owner ow;
                if (!OwnerOf.TryGetValue(any, out ow))
                {
                    object comp = any;
                    var t = any.GetType();
                    FieldInfo fo;
                    if (!Owners.TryGetValue(t, out fo)) { fo = t.Name == "Compartments" ? null : t.GetField("_compartments", All); Owners[t] = fo; }
                    if (fo != null)
                    {
                        comp = fo.GetValue(any);
                        if (comp == null) return 0f;   // not set yet: not remembered
                    }
                    else if (t.Name != "Compartments") return 0f;
                    ow = new Owner { Comp = comp };
                    OwnerOf.Add(any, ow);
                }
                var ship = ow.Comp;
                Kn k;
                if (!Speeds.TryGetValue(ship, out k)) { k = new Kn(); Speeds.Add(ship, k); }
                int frame = UnityEngine.Time.frameCount;
                if (k.Frame == frame) return k.Value;
                if (!_init)
                {
                    _init = true;
                    _fBase = ship.GetType().GetField("_baseObject", All);
                    _mSpeed = _fBase == null ? null : _fBase.FieldType.GetMethod("getVelocityInKnots", All);
                }
                float v = 0f;
                if (_fBase != null && _mSpeed != null)
                {
                    var bo = _fBase.GetValue(ship);
                    v = bo == null ? 0f : Math.Abs(Convert.ToSingle(_mSpeed.Invoke(bo, null)));
                }
                k.Frame = frame; k.Value = v;
                return v;
            }
            catch (Exception e) { Trace("SpeedEffect.KnotsOf: " + e.Message); return 0f; }
        }

        /// <summary>
        /// The ship's size changes how fast fires grow and water comes in: a small ship burns and floods faster. 1 from SmallFromTons (1000 t) up; below it the
        /// multiplier grows smoothly (along the logarithm of the displacement) to SmallBoost at SmallToTons (50 t) and stays there. 1 when off or the displacement is unknown.
        /// </summary>
        public static float SizeFactorOf(float tons)
        {
            if (SmallBoost <= 1f || tons <= 0f || tons >= SmallFromTons) return 1f;
            float t = tons <= SmallToTons ? 1f : (float)(Math.Log(SmallFromTons / tons) / Math.Log(SmallFromTons / SmallToTons));
            return 1f + (SmallBoost - 1f) * t;
        }

        static FieldInfo _fDisp; static bool _initDisp;
        static float SizeOf(object any)
        {
            try
            {
                Owner ow;
                if (any == null || !OwnerOf.TryGetValue(any, out ow)) return 1f;   // KnotsOf has just filled it in for every ship it knows
                if (ow.Size < 0f)
                {
                    if (!_initDisp) { _initDisp = true; _fDisp = ow.Comp.GetType().GetField("_displacement", All); }
                    float tons = _fDisp == null ? 0f : Convert.ToSingle(_fDisp.GetValue(ow.Comp));
                    ow.Size = tons > 0f ? SizeFactorOf(tons) : 1f;
                }
                return ow.Size;
            }
            catch { return 1f; }
        }

        public static float Flood(object any) { float k = KnotsOf(any); return FloodAt(k) * SizeOf(any); }
        public static float Fire(object any) { float k = KnotsOf(any); return FireAt(k) * SizeOf(any); }

        /// <summary>Too fast for a team to plug holes.</summary>
        public static bool TooFastToPlug(object any) { return MaxPlugKnots > 0f && KnotsOf(any) > MaxPlugKnots; }
    }
}
