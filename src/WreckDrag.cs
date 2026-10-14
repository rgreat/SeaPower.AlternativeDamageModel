using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlternativeDamageModel
{
    /// <summary>
    /// A destroyed ship slides on: the game sets the rigidbody's drag from the sea (Compartments.OnFixedUpdate: Lerp(outOfWaterDrag, submergedDrag, submerged volume))
    /// and does not change it when the ship is destroyed. Here, after the game's call, the linear drag of a destroyed surface ship is multiplied by Factor
    /// ([Sinking] WreckDragFactor, 5 by default) - the wreck loses its way sooner. The angular drag (capsizing, rolling) is not touched.
    /// The game writes the drag again every tick; the value written here is remembered, so a tick in which the game did not write it is not multiplied twice.
    /// </summary>
    static class WreckDrag
    {
        public static Func<bool> IsEnabled = () => false;
        public static Action<string> Trace = s => { };
        public static float Factor = 5f;

        static bool _init, _ok, _traced;
        static FieldInfo _fObp, _fRb, _fVessel;
        static PropertyInfo _pDrag;
        sealed class State { public float Written = float.NaN; }
        static readonly ConditionalWeakTable<object, State> States = new ConditionalWeakTable<object, State>();

        static void Init(object comp)
        {
            if (_init) return;
            _init = true;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var t = comp.GetType();
                _fObp = t.GetField("_obp", all); _fVessel = t.GetField("_vessel", all);
                _fRb = _fObp == null ? null : _fObp.FieldType.GetField("_rigidbody", all);
                var tRb = _fRb == null ? null : _fRb.FieldType;
                _pDrag = tRb == null ? null : (tRb.GetProperty("linearDamping") ?? tRb.GetProperty("drag"));
                _ok = _fObp != null && _fVessel != null && _fRb != null && _pDrag != null;
            }
            catch (Exception e) { Trace("WreckDrag.Init: " + e.Message); _ok = false; }
            if (!_ok) Trace("WreckDrag: game members not found; a destroyed ship keeps the game's drag");
        }

        /// <summary>Postfix of Compartments.OnFixedUpdate.</summary>
        public static void Tick(object comp)
        {
            if (comp == null || Math.Abs(Factor - 1f) < 1e-4f || !IsEnabled()) return;
            Init(comp);
            if (!_ok) return;
            try
            {
                if (_fVessel.GetValue(comp) == null || !SubmergedFlooding.IsDestroyed(comp)) return;
                var rb = _fRb.GetValue(_fObp.GetValue(comp));
                if (rb == null) return;
                State s;
                if (!States.TryGetValue(comp, out s)) { s = new State(); States.Add(comp, s); }
                float cur = Convert.ToSingle(_pDrag.GetValue(rb, null));
                if (!float.IsNaN(s.Written) && Math.Abs(cur - s.Written) < 1e-6f) return;   // the game did not write it this tick
                float v = cur * Factor;
                _pDrag.SetValue(rb, v, null);
                s.Written = v;
                if (!_traced) { _traced = true; Trace("WreckDrag: destroyed ship's linear drag " + cur.ToString("0.###") + " -> " + v.ToString("0.###") + " (x" + Factor.ToString("0.##") + ")"); }
            }
            catch (Exception e) { Trace("WreckDrag: " + e.Message); }
        }
    }
}
