using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlternativeDamageModel
{
    /// <summary>
    /// A destroyed ship slides on: the game sets the rigidbody's drag from the sea (Compartments.OnFixedUpdate: Lerp(outOfWaterDrag, submergedDrag, submerged volume),
    /// 25 for nearly every ship) and does not change it when the ship is destroyed. The drag of a wreck is made [Effects] WreckDragFactor times stronger for the
    /// HORIZONTAL movement only: after the game's call the horizontal velocity is divided once more by 1 + (Factor - 1) * drag * dt (the way Unity applies its own
    /// damping). The vertical velocity is left alone: the linear drag is not raised - with drag 25 the speed of sinking is already only (pull / 25) and a higher
    /// drag stopped a wreck full of water from sinking at all.
    /// </summary>
    static class WreckDrag
    {
        public static Func<bool> IsEnabled = () => false;
        public static Action<string> Trace = s => { };
        public static float Factor = 5f;

        static bool _init, _ok, _traced;
        static FieldInfo _fObp, _fRb, _fVessel;
        static PropertyInfo _pDrag, _pVel, _pKinematic;

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
                _pVel = tRb == null ? null : (tRb.GetProperty("linearVelocity") ?? tRb.GetProperty("velocity"));
                _pKinematic = tRb == null ? null : tRb.GetProperty("isKinematic");
                _ok = _fObp != null && _fVessel != null && _fRb != null && _pDrag != null && _pVel != null;
            }
            catch (Exception e) { Trace("WreckDrag.Init: " + e.Message); _ok = false; }
            if (!_ok) Trace("WreckDrag: game members not found; a destroyed ship keeps the game's drag");
        }

        /// <summary>Postfix of Compartments.OnFixedUpdate.</summary>
        public static void Tick(object comp)
        {
            if (comp == null || Factor <= 1.0001f || !IsEnabled()) return;
            Init(comp);
            if (!_ok) return;
            try
            {
                if (_fVessel.GetValue(comp) == null || !SubmergedFlooding.IsDestroyed(comp)) return;
                var rb = _fRb.GetValue(_fObp.GetValue(comp));
                if (rb == null) return;
                if (_pKinematic != null && (bool)_pKinematic.GetValue(rb, null)) return;   // a wreck at rest on the bottom: the game froze it (kinematic), Unity warns at every velocity write
                float drag = Convert.ToSingle(_pDrag.GetValue(rb, null));
                float k = 1f + (Factor - 1f) * drag * UnityEngine.Time.fixedDeltaTime;
                var v = (UnityEngine.Vector3)_pVel.GetValue(rb, null);
                _pVel.SetValue(rb, new UnityEngine.Vector3(v.x / k, v.y, v.z / k), null);
                if (!_traced) { _traced = true; Trace("WreckDrag: a destroyed ship's horizontal speed is damped x" + Factor.ToString("0.##") + " (drag " + drag.ToString("0.###") + ", vertical speed untouched)"); }
            }
            catch (Exception e) { Trace("WreckDrag: " + e.Message); }
        }
    }
}
