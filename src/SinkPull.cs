using System;
using System.Reflection;
using UnityEngine;

namespace AlternativeDamageModel
{
    /// <summary>
    /// The game's extra pull down on a ship that is sinking (Compartments.OnFixedUpdate): once the hull origin is deeper than y = -2 (Unity units),
    /// every physics tick it adds an acceleration of min(y + 2, (time since the sink) / 3), at most -10 - in practice y + 2, since the time term is
    /// positive and the depth term negative: the pull grows with depth, 1 unit/s² per unit of depth, up to 10 at y = -12. Here the pull the ship gets is
    /// Factor × Cap × S(depth / Ramp), where depth = -2 - y, Cap = 10 and S is a smooth step (x²(3 - 2x), no kink where it starts and where it stops):
    /// the mod adds the difference to the game's own acceleration (the same formula, before the game's call in the same tick). Ramp = the depth over which
    /// the pull grows to its maximum (the game's: 10 units, linear; 0 = the game's linear 10); Factor scales the maximum. Factor 1 and Ramp 0 = as in the game.
    /// </summary>
    static class SinkPull
    {
        public static Func<bool> IsEnabled = () => false;
        public static Action<string> Trace = s => { };
        public static float Factor = 1f;     // [Sinking] SinkPullFactor
        public static float Ramp = 0f;       // [Sinking] SinkPullRamp, depth units; 0 = the game's

        static bool _init, _ok, _traced;
        static FieldInfo _fSinking, _fSinkTime, _fObp, _fRb, _fVessel, _fBase;
        static PropertyInfo _pTime;
        static MethodInfo _mTransform, _mAddForce;
        static object _accel;

        static void Init(object comp)
        {
            if (_init) return;
            _init = true;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var t = comp.GetType();
                _fSinking = t.GetField("_isSinking", all); _fSinkTime = t.GetField("_sinkTime", all); _fObp = t.GetField("_obp", all);
                _fVessel = t.GetField("_vessel", all); _fBase = t.GetField("_baseObject", all);
                _fRb = _fObp == null ? null : _fObp.FieldType.GetField("_rigidbody", all);
                _mTransform = _fBase == null ? null : _fBase.FieldType.GetMethod("getTransform", all);
                var tGt = t.Assembly.GetType("SeaPower.GameTime");
                _pTime = tGt == null ? null : tGt.GetProperty("missionElapsedTime", all);
                var tRb = _fRb == null ? null : _fRb.FieldType;
                if (tRb != null)
                    foreach (var m in tRb.GetMethods())
                    {
                        if (m.Name != "AddForce") continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 2 && ps[0].ParameterType == typeof(Vector3) && ps[1].ParameterType.IsEnum) { _mAddForce = m; _accel = Enum.ToObject(ps[1].ParameterType, 5); break; }   // ForceMode.Acceleration = 5
                    }
                _ok = _fSinking != null && _fSinkTime != null && _fObp != null && _fVessel != null && _fRb != null && _mTransform != null && _pTime != null && _mAddForce != null;
            }
            catch (Exception e) { Trace("SinkPull.Init: " + e.Message); _ok = false; }
            if (!_ok) Trace("SinkPull: game members not found; the sinking pull is not scaled");
        }

        /// <summary>Prefix of Compartments.OnFixedUpdate.</summary>
        public static void Tick(object comp)
        {
            if (comp == null || (Math.Abs(Factor - 1f) < 1e-4f && Ramp <= 0f) || !IsEnabled()) return;
            Init(comp);
            if (!_ok) return;
            try
            {
                if (!(bool)_fSinking.GetValue(comp) || _fVessel.GetValue(comp) == null) return;
                var tr = _mTransform.Invoke(_fBase.GetValue(comp), null) as Transform;
                if (tr == null) return;
                float y = tr.position.y;
                if (y >= -2f) return;
                double since = Convert.ToDouble(_pTime.GetValue(null, null)) - Convert.ToDouble(_fSinkTime.GetValue(comp));
                float a = Math.Min(y + 2f, (float)since / 3f);
                if (a < -10f) a = -10f;
                var rb = _fRb.GetValue(_fObp.GetValue(comp));
                if (rb == null) return;
                float want;
                if (Ramp > 0f)
                {
                    float x = Math.Min(1f, Math.Max(0f, (-2f - y) / Ramp));
                    want = -10f * Factor * x * x * (3f - 2f * x);
                }
                else want = Factor * a;
                _mAddForce.Invoke(rb, new object[] { new Vector3(0f, want - a, 0f), _accel });
                if (!_traced) { _traced = true; Trace("SinkPull: the pull of the sinking ship starts: game " + a.ToString("0.##") + ", now " + want.ToString("0.##") + " (factor " + Factor.ToString("0.##") + ", ramp " + Ramp.ToString("0.#") + ")"); }
            }
            catch (Exception e) { Trace("SinkPull: " + e.Message); }
        }
    }
}
