using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Capsizing. The game keeps the hull's centre of mass fixed (Compartments.Init: the middle compartment's place + the ini CenterOfMass) and water
    /// only takes lift away from its compartment point (F = m g/10 · s / (density + flooding)) - so a flooded ship keeps its stability, and when it is
    /// sunk by flooding Sink() puts the centre of mass at the hull origin, level with the lift points: on its side nothing turns it further, and it
    /// lies there. Here the centre of mass rises with the water in the ship, which is what the water really does to the stability (a lost metacentric height):
    /// rise = Factor · GM(LevelFull) · water/LevelFull, where GM(w) = x² / (H · (density + w)) is the model's own roll stiffness (x - half the distance between
    /// the port and starboard points, H - the compartments' height, w - the flooding value at that water). The rise is scaled by RiseScale = 0.5 (the influence of the setting was halved): at Factor 2 the stability is gone where the ship sinks;
    /// more - sooner. A rolled ship with the centre of mass above the lift points keeps turning over (the lever grows up to 90°, then it turns it keel up).
    /// Every tick for surface ships with the model on; the game's own centre of mass of a capsized ship (0, 1, 0) is left alone.
    /// </summary>
    static class CapsizeFix
    {
        public static Func<bool> IsEnabled = () => false;
        public static Action<string> Trace = s => { };
        const float RiseScale = 0.5f;        // the influence of the setting is halved (1.0 = half of the rise it used to be)
        public static float Factor = 1f;     // [Sinking] CapsizeFactor; 0 = off
        public static bool KeepCom = true;   // [Sinking] KeepCentreOfMass: Sink() does not reset the centre of mass

        static bool _init, _ok;
        static FieldInfo _fObp, _fVessel, _fCount, _fPort, _fSinking, _fCapsized, _fHeight, _fRb, _fCom, _fCompCom, _fFc, _fBase, _fMaxFlood, _fObject;
        static PropertyInfo _pFlood, _pRbCom;
        static FieldInfo _fDamageValues, _fMaxVal;

        sealed class State { public bool Ok; public float Gm; public float Last = float.NaN; public float Dens; }
        static readonly ConditionalWeakTable<object, State> States = new ConditionalWeakTable<object, State>();

        static void Init(object comp)
        {
            if (_init) return;
            _init = true;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var t = comp.GetType();
                _fObp = t.GetField("_obp", all); _fVessel = t.GetField("_vessel", all); _fCount = t.GetField("_compartmentsCount", all);
                _fPort = t.GetField("_portCompartments", all); _fSinking = t.GetField("_isSinking", all); _fCapsized = t.GetField("_capSized", all);
                _fHeight = t.GetField("_compartmentsHeight", all); _pFlood = t.GetProperty("FloodingPercentage", all);
                var tF = _fPort == null ? null : _fPort.FieldType.GetElementType();
                _fMaxFlood = tF == null ? null : tF.GetField("_maxFlooding", all);
                _fObject = tF == null ? null : tF.GetField("_object", all);
                _fFc = tF == null ? null : tF.GetField("_baseDensity", all);
                var tG = t.Assembly.GetType("SeaPower.Globals");
                _fDamageValues = tG == null ? null : tG.GetField("_damageValues", all);
                _fMaxVal = _fDamageValues == null ? null : _fDamageValues.FieldType.GetField("_maxFloodingValueWhenSinking", all);
                _ok = _fObp != null && _fVessel != null && _fCount != null && _fPort != null && _fSinking != null && _fCapsized != null && _fHeight != null && _pFlood != null && _fObject != null && _fFc != null;
            }
            catch (Exception e) { Trace("CapsizeFix.Init: " + e.Message); _ok = false; }
            if (!_ok) Trace("CapsizeFix: game members not found; the centre of mass does not rise with the water");
        }

        static float MaxVal()
        {
            try { var v = _fMaxVal == null ? null : _fMaxVal.GetValue(_fDamageValues.GetValue(null)); if (v is float && (float)v > 0f) return (float)v; } catch { }
            return 2f;
        }

        /// <summary>Prefix of Compartments.OnFixedUpdate (before the game reads the centre of mass for its torques).</summary>
        public static void Tick(object comp)
        {
            if (comp == null || Factor <= 0f || !IsEnabled()) return;
            Init(comp);
            if (!_ok) return;
            try
            {
                State s;
                if (!States.TryGetValue(comp, out s)) { s = Make(comp); States.Add(comp, s); }
                if (!s.Ok) return;
                if ((bool)_fCapsized.GetValue(comp)) return;     // the game's own (0, 1, 0)
                float f = Convert.ToSingle(_pFlood.GetValue(comp, null));
                float L = Math.Max(5f, Limits.LevelFull);
                float rise = RiseScale * Factor * s.Gm * Math.Min(f / L, 2.5f);
                if (!float.IsNaN(s.Last) && Math.Abs(rise - s.Last) < 1e-5f && !(bool)_fSinking.GetValue(comp)) return;
                var obp = _fObp.GetValue(comp);
                if (_fRb == null) { var to = obp.GetType(); _fRb = to.GetField("_rigidbody", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); _fCom = to.GetField("_centerOfMass", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); _fCompCom = to.GetField("_compartmentsCenterOfMass", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
                var rb = _fRb.GetValue(obp);
                if (_pRbCom == null) _pRbCom = rb.GetType().GetProperty("centerOfMass");
                Vector3 baseCom = (Vector3)_fCompCom.GetValue(obp) + (Vector3)_fCom.GetValue(obp);
                _pRbCom.SetValue(rb, baseCom + new Vector3(0f, rise, 0f), null);
                s.Last = rise;
            }
            catch (Exception e) { Trace("CapsizeFix: " + e.Message); }
        }

        /// <summary>
        /// Prefix of Compartments.Sink: Sink() sets the rigidbody's centre of mass to the hull origin ((0, 1, 0) for a capsized ship) - a jump of the centre of mass
        /// (and of every torque) at the moment the ship starts to sink. With KeepCom the centre of mass is remembered here and put back in AfterSink.
        /// Returns the state for AfterSink (null = nothing to restore).
        /// </summary>
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
            if (!(state is Vector3)) return;
            try
            {
                var rb = Rb(comp);
                if (rb == null) return;
                var was = (Vector3)state;
                var now = (Vector3)_pRbCom.GetValue(rb, null);
                _pRbCom.SetValue(rb, was, null);
                Trace("CapsizeFix: the ship starts to sink, the centre of mass stays " + was + " (the game set " + now + ")");
            }
            catch (Exception e) { Trace("CapsizeFix.AfterSink: " + e.Message); }
        }

        static object Rb(object comp)
        {
            var obp = _fObp.GetValue(comp);
            if (_fRb == null) { var to = obp.GetType(); _fRb = to.GetField("_rigidbody", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
            var rb = _fRb == null ? null : _fRb.GetValue(obp);
            if (rb != null && _pRbCom == null) _pRbCom = rb.GetType().GetProperty("centerOfMass");
            return _pRbCom == null ? null : rb;
        }

        static State Make(object comp)
        {
            var s = new State();
            try
            {
                if (_fVessel.GetValue(comp) == null) return s;
                int n = (int)_fCount.GetValue(comp);
                var port = _fPort.GetValue(comp) as Array;
                if (n < 2 || port == null || port.Length < 1) return s;
                var fc = port.GetValue(0);
                var go = _fObject.GetValue(fc) as GameObject;
                if (go == null) return s;
                float x = Mathf.Abs(go.transform.localPosition.x);
                float H = (float)_fHeight.GetValue(comp);
                float rho = (float)_fFc.GetValue(fc);
                if (x < 1e-5f || H < 1e-5f) return s;
                float w = MaxVal() * Math.Max(5f, Limits.LevelFull) / 100f;
                s.Gm = x * x / (H * (rho + w));
                s.Ok = true;
                Trace("CapsizeFix: GM at the sinking level " + (s.Gm * 67.200066f).ToString("0.0") + " m (half-beam " + (x * 67.200066f).ToString("0.0") + " m, H " + (H * 67.200066f).ToString("0.0") + " m)");
            }
            catch (Exception e) { Trace("CapsizeFix.Make: " + e.Message); }
            return s;
        }
    }
}
