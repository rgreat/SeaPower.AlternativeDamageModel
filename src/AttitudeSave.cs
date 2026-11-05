using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace AlternativeDamageModel
{
    /// <summary>
    /// The game saves a ship's position (with its depth) and heading, but not its list and trim: after a load the ship stands on an even keel at the
    /// saved depth and the physics settles it again within seconds (a damaged ship goes down by the stern / to one side, the heel jumps, Auto DC's plan
    /// with it). Here the list and trim are saved with the compartments ("AlternativeDamageModel_Attitude" = pitch,roll; Euler x and z of the hull) and
    /// put back on the first physics step of the loaded ship (its heading kept; the rigidbody's rotation set too, its angular velocity cleared).
    /// </summary>
    static class AttitudeSave
    {
        public static Func<bool> IsEnabled = () => false;
        public static Action<string> Trace = s => { };
        public const string Key = "AlternativeDamageModel_Attitude";

        static FieldInfo _fBase, _fSinking;
        static readonly ConditionalWeakTable<object, float[]> Pending = new ConditionalWeakTable<object, float[]>();

        static Component Base(object comp)
        {
            if (_fBase == null) _fBase = comp.GetType().GetField("_baseObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return _fBase == null ? null : _fBase.GetValue(comp) as Component;
        }

        static bool Sinking(object comp)
        {
            if (_fSinking == null) _fSinking = comp.GetType().GetField("_isSinking", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            try { return _fSinking != null && (bool)_fSinking.GetValue(comp); } catch { return false; }
        }

        static float Wrap(float a) { a %= 360f; if (a > 180f) a -= 360f; else if (a < -180f) a += 360f; return a; }

        /// <summary>"pitch,roll" of the hull, or null.</summary>
        public static string Save(object comp)
        {
            try
            {
                if (comp == null || !IsEnabled()) return null;
                var b = Base(comp);
                if (b == null) return null;
                var e = b.transform.localEulerAngles;
                return Wrap(e.x).ToString("0.###", CultureInfo.InvariantCulture) + "," + Wrap(e.z).ToString("0.###", CultureInfo.InvariantCulture);
            }
            catch (Exception ex) { Trace("AttitudeSave.Save: " + ex.Message); return null; }
        }

        public static void Load(object comp, string text)
        {
            try
            {
                if (comp == null || string.IsNullOrEmpty(text)) return;
                var p = text.Split(',');
                float x, z;
                if (p.Length < 2 || !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return;
                if (Math.Abs(x) > 60f || Math.Abs(z) > 80f) return;   // a capsized / upended hull is left to the game
                Pending.Remove(comp);
                Pending.Add(comp, new[] { x, z });
            }
            catch (Exception ex) { Trace("AttitudeSave.Load: " + ex.Message); }
        }

        /// <summary>On the ship's physics step (Compartments.OnFixedUpdate prefix): puts the saved list / trim back once.</summary>
        public static void Tick(object comp)
        {
            float[] a;
            if (comp == null || !Pending.TryGetValue(comp, out a)) return;
            Pending.Remove(comp);
            try
            {
                if (!IsEnabled() || Sinking(comp)) return;
                var b = Base(comp);
                if (b == null) return;
                var t = b.transform;
                var e = t.localEulerAngles;
                float y = e.y;
                t.localEulerAngles = new Vector3(a[0], y, a[1]);
                foreach (var c in b.GetComponents<Component>())   // the Rigidbody (its module is not referenced: by reflection)
                {
                    if (c == null || c.GetType().Name != "Rigidbody") continue;
                    var ct = c.GetType();
                    var pr = ct.GetProperty("rotation"); if (pr != null) pr.SetValue(c, t.rotation, null);
                    var pa = ct.GetProperty("angularVelocity"); if (pa != null) pa.SetValue(c, Vector3.zero, null);
                    break;
                }
                Trace("Attitude restored after the load: pitch " + a[0].ToString("0.0", CultureInfo.InvariantCulture) + ", roll " + a[1].ToString("0.0", CultureInfo.InvariantCulture) +
                      " (it was " + Wrap(e.x).ToString("0.0", CultureInfo.InvariantCulture) + " / " + Wrap(e.z).ToString("0.0", CultureInfo.InvariantCulture) + ")");
            }
            catch (Exception ex) { Trace("AttitudeSave.Tick: " + ex.Message); }
        }
    }
}
