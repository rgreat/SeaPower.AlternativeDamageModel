using System;
using System.Reflection;
using UnityEngine;

namespace AlternativeDamageModel
{
    /// <summary>
    /// The game's extra pull down on a ship that is sinking (Compartments.OnFixedUpdate): only once the hull origin is deeper than y = -2 (Unity units = 134 m!),
    /// every physics tick it adds an acceleration of y + 2 (at most -10). A wreck full of water therefore sits on the surface with its deck awash and goes down only by
    /// the buoyancy it has left (with the drag of 25 that is a crawl). Two things here:
    ///  - Factor scales the game's own (deep) pull;
    ///  - Surface: a pull from the surface, 10 x Surface x S(x), x = (submerged - 0.5) / 0.5, submerged = Compartments._submergedVolume (the mean over the ten buoyancy
    ///    points of the part under water, 0.5 = floating normally, 1 = under water), S a smooth step (x^2 (3 - 2x)). It acts while the ship is sinking; deep down the
    ///    stronger of the two pulls counts (they are not added up). Factor 1 and Surface 0 = as in the game.
    /// </summary>
    static class SinkPull
    {
        public static Func<bool> IsEnabled = () => false;
        public static Action<string> Trace = s => { };
        public static float Factor = 1f;     // [Sinking] SinkPullFactor: scales the game's own (deep) pull
        public static float Surface = 0f;    // [Sinking] SinkPullSurface: 10 x this from the surface; 0 = none

        static bool _init, _ok, _traced;
        static FieldInfo _fSinking, _fSinkTime, _fObp, _fRb, _fVessel, _fBase, _fSub;
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
                _fVessel = t.GetField("_vessel", all); _fSub = t.GetField("_submergedVolume", all); _fBase = t.GetField("_baseObject", all);
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
                _ok = _fSinking != null && _fSinkTime != null && _fObp != null && _fVessel != null && _fRb != null && _mTransform != null && _pTime != null && _mAddForce != null && _fSub != null;
            }
            catch (Exception e) { Trace("SinkPull.Init: " + e.Message); _ok = false; }
            if (!_ok) Trace("SinkPull: game members not found; the sinking pull is not scaled");
        }

        /// <summary>Prefix of Compartments.OnFixedUpdate.</summary>
        public static void Tick(object comp)
        {
            if (comp == null || (Math.Abs(Factor - 1f) < 1e-4f && Surface <= 0f) || !IsEnabled()) return;
            Init(comp);
            if (!_ok) return;
            try
            {
                if (!(bool)_fSinking.GetValue(comp) || _fVessel.GetValue(comp) == null) return;
                var tr = _mTransform.Invoke(_fBase.GetValue(comp), null) as Transform;
                if (tr == null) return;
                float y = tr.position.y;
                // the game's own pull (below y = -2), as it will be applied in this tick
                float a = 0f;
                if (y < -2f)
                {
                    double since = Convert.ToDouble(_pTime.GetValue(null, null)) - Convert.ToDouble(_fSinkTime.GetValue(comp));
                    a = Math.Min(y + 2f, (float)since / 3f);
                    if (a < -10f) a = -10f;
                }
                float want = Factor * a;
                if (Surface > 0f)
                {
                    // the buoyancy points are under the water: the ship has gone down as far as it floats, pull it on
                    float sub = Convert.ToSingle(_fSub.GetValue(comp));
                    float x = Math.Min(1f, Math.Max(0f, (sub - 0.5f) / 0.5f));
                    float shallow = -10f * Surface * x * x * (3f - 2f * x);
                    if (shallow < want) want = shallow;
                }
                if (Math.Abs(want - a) < 1e-5f) return;
                var rb = _fRb.GetValue(_fObp.GetValue(comp));
                if (rb == null) return;
                _mAddForce.Invoke(rb, new object[] { new Vector3(0f, want - a, 0f), _accel });
                if (!_traced) { _traced = true; Trace("SinkPull: the sinking ship is pulled: game " + a.ToString("0.##") + ", now " + want.ToString("0.##") + " (factor " + Factor.ToString("0.##") + ", from the surface " + Surface.ToString("0.##") + ", submerged " + Convert.ToSingle(_fSub.GetValue(comp)).ToString("0.00") + ")"); }
            }
            catch (Exception e) { Trace("SinkPull: " + e.Message); }
        }
    }
}
