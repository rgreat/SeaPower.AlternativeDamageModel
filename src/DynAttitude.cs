using System;
using System.Reflection;
using UnityEngine;

namespace AlternativeDamageModel
{
    /// <summary>
    /// The game moves the sample points of the hull's buoyancy (Compartments._portCompartmentOffsets / _starboardCompartmentOffsets, set by speed and turn in
    /// setTiltAndPitch from the ini PitchFactor, LiftFactor, TiltFactor): planing boats and hydrofoils trim and heel by design, and the hull's Euler angles
    /// show it as a "trim" and a "list". The rigid body settles where the buoyancy torque of the 10 sample points is zero, i.e. where the points' effective
    /// heights (y + offset) have no slope along and across the hull; so the angle the offsets alone cause is the least-squares slope of the offsets over the
    /// points' positions along the ship (pitch, + = bow down) and across it (roll, + = port side down). That is computed here from the game's own arrays
    /// and taken off. A uniform lift gives no slope (no angle). Flooding and damage are not in the offsets: what is left is theirs.
    /// </summary>
    static class DynAttitude
    {
        static FieldInfo _fPortOff, _fStarOff, _fPortPts, _fStarPts, _fBase;
        static bool _init, _ok;
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> Seen = new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();
        public static Action<string> Trace = s => { };

        static void Init(object comp)
        {
            if (_init) return;
            _init = true;
            const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var t = comp.GetType();
            _fPortOff = t.GetField("_portCompartmentOffsets", f); _fStarOff = t.GetField("_starboardCompartmentOffsets", f);
            _fPortPts = t.GetField("_portWorldPoints", f); _fStarPts = t.GetField("_starboardWorldPoints", f);
            _fBase = t.GetField("_baseObject", f);
            _ok = _fPortOff != null && _fStarOff != null && _fPortPts != null && _fStarPts != null && _fBase != null;
            if (!_ok) Trace("DynAttitude: offsets / world points not found - angles are shown as they are");
        }

        /// <summary>Degrees that the game's speed / turn offsets alone give: pitch + = bow down, roll + = port side down.</summary>
        public static bool Dynamic(object comp, out float pitch, out float roll)
        {
            pitch = 0f; roll = 0f;
            if (comp == null) return false;
            try
            {
                Init(comp);
                if (!_ok) return false;
                var bo = _fBase.GetValue(comp) as Component;
                var po = _fPortOff.GetValue(comp) as float[]; var so = _fStarOff.GetValue(comp) as float[];
                var pp = _fPortPts.GetValue(comp) as Vector3[]; var sp = _fStarPts.GetValue(comp) as Vector3[];
                if (bo == null || po == null || so == null || pp == null || sp == null) return false;
                int n = Math.Min(Math.Min(po.Length, so.Length), Math.Min(pp.Length, sp.Length));
                if (n < 2) return false;
                Vector3 fwd = bo.transform.forward, right = bo.transform.right;
                fwd.y = 0f; right.y = 0f;
                if (fwd.sqrMagnitude < 1e-6f || right.sqrMagnitude < 1e-6f) return false;   // the hull is on its side
                fwd.Normalize(); right.Normalize();
                int m = 2 * n;
                var s = new float[m]; var t = new float[m]; var d = new float[m];
                Vector3 c = Vector3.zero;
                for (int i = 0; i < n; i++) c += pp[i] + sp[i];
                c /= m;
                float sm = 0f, tm = 0f, dm = 0f;
                for (int i = 0; i < n; i++)
                {
                    s[2 * i] = Vector3.Dot(pp[i] - c, fwd); t[2 * i] = Vector3.Dot(pp[i] - c, right); d[2 * i] = po[i];
                    s[2 * i + 1] = Vector3.Dot(sp[i] - c, fwd); t[2 * i + 1] = Vector3.Dot(sp[i] - c, right); d[2 * i + 1] = so[i];
                }
                for (int i = 0; i < m; i++) { sm += s[i]; tm += t[i]; dm += d[i]; }
                sm /= m; tm /= m; dm /= m;
                float sdd = 0f, sss = 0f, tdd = 0f, ttt = 0f;
                for (int i = 0; i < m; i++)
                {
                    sdd += (s[i] - sm) * (d[i] - dm); sss += (s[i] - sm) * (s[i] - sm);
                    tdd += (t[i] - tm) * (d[i] - dm); ttt += (t[i] - tm) * (t[i] - tm);
                }
                // effective height y + d is level when the hull's own slope cancels the offsets' slope: bow down by +slope_s, port down by -slope_t
                if (sss > 1e-9f) pitch = Mathf.Atan(sdd / sss) * Mathf.Rad2Deg;
                if (ttt > 1e-9f) roll = -Mathf.Atan(tdd / ttt) * Mathf.Rad2Deg;
                if ((Mathf.Abs(pitch) > 0.5f || Mathf.Abs(roll) > 0.5f) && Seen.TryGetValue(comp, out _) == false)
                {
                    Seen.Add(comp, new object());
                    Trace("DynAttitude: speed / turn give pitch " + pitch.ToString("F1") + ", roll " + roll.ToString("F1") + " degrees on '" + bo.name + "' (taken off the list / trim)");
                }
                return true;
            }
            catch (Exception e) { Trace("DynAttitude: " + e.Message); return false; }
        }

        /// <summary>The raw angles without what speed and turn do by design.</summary>
        public static void Fix(object comp, float rawPitch, float rawRoll, out float pitch, out float roll)
        {
            float dp, dr;
            if (Dynamic(comp, out dp, out dr)) { pitch = rawPitch - dp; roll = rawRoll - dr; }
            else { pitch = rawPitch; roll = rawRoll; }
        }
    }
}
