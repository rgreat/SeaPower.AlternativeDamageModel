using System;
using System.Reflection;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Keeps the centre of mass when a ship starts to sink. Compartments.Sink() sets the rigidbody's centre of mass to the hull origin ((0, 1, 0) for a
    /// capsized ship): a jump of the centre of mass and of every torque at the moment the ship starts to sink. With [Effects] KeepCentreOfMass it is
    /// remembered before Sink() and put back after it.
    /// </summary>
    static class CapsizeFix
    {
        public static Func<bool> IsEnabled = () => false;
        public static Action<string> Trace = s => { };
        public static bool KeepCom = true;   // [Effects] KeepCentreOfMass

        static bool _init, _ok;
        static FieldInfo _fObp, _fVessel, _fSinking, _fRb;
        static PropertyInfo _pRbCom;

        static void Init(object comp)
        {
            if (_init) return;
            _init = true;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var t = comp.GetType();
                _fObp = t.GetField("_obp", all); _fVessel = t.GetField("_vessel", all); _fSinking = t.GetField("_isSinking", all);
                _ok = _fObp != null && _fVessel != null && _fSinking != null;
            }
            catch (Exception e) { Trace("CapsizeFix.Init: " + e.Message); _ok = false; }
            if (!_ok) Trace("CapsizeFix: game members not found; the centre of mass is not kept");
        }

        /// <summary>Prefix of Compartments.Sink. Returns the state for AfterSink (null = nothing to restore).</summary>
        public static object BeforeSink(object comp)
        {
            if (comp == null || !KeepCom || !IsEnabled()) return null;
            Init(comp);
            if (!_ok) return null;
            try
            {
                if (_fVessel.GetValue(comp) == null || (bool)_fSinking.GetValue(comp)) return null;   // surface ships only; a second Sink() returns at once
                var rb = Rb(comp);
                if (rb == null) return null;
                return _pRbCom.GetValue(rb, null);
            }
            catch (Exception e) { Trace("CapsizeFix.BeforeSink: " + e.Message); return null; }
        }

        public static void AfterSink(object comp, object state)
        {
            if (!(state is UnityEngine.Vector3)) return;
            try
            {
                var rb = Rb(comp);
                if (rb == null) return;
                var was = (UnityEngine.Vector3)state;
                var now = (UnityEngine.Vector3)_pRbCom.GetValue(rb, null);
                _pRbCom.SetValue(rb, was, null);
                Trace("CapsizeFix: the ship starts to sink, the centre of mass stays " + was + " (the game set " + now + ")");
            }
            catch (Exception e) { Trace("CapsizeFix.AfterSink: " + e.Message); }
        }

        static object Rb(object comp)
        {
            var obp = _fObp.GetValue(comp);
            if (_fRb == null) _fRb = obp.GetType().GetField("_rigidbody", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var rb = _fRb == null ? null : _fRb.GetValue(obp);
            if (rb != null && _pRbCom == null) _pRbCom = rb.GetType().GetProperty("centerOfMass");
            return _pRbCom == null ? null : rb;
        }
    }
}
