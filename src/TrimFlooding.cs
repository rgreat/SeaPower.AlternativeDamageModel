using System;
using System.Reflection;
using UnityEngine;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Trim past the ship's limit no longer sinks it at once (the game: trim over 8° with a flooded section = sunk): the end that
    /// goes down floods fast instead - the way the game floods the low side at heavy list (FloodingCompartment.AddFloodingForHeavyListing,
    /// each side): the outermost compartment of that end; once it is full, the next one, and so on. The game's own check is
    /// switched off by AdmPlugin.SinkTrim (no limit). List past [Sinking] ListLimit only floods (the game's rule, its threshold moved);
    /// capsizing at 70° of list and the ship's water over FloodLimit still sink it.
    /// Section 0 is the bow (Forward), the last one the stern.
    /// </summary>
    static class TrimFlooding
    {
        public static Func<bool> IsEnabled = () => false;
        public static Action<string> Trace = s => { };

        static bool _init, _ok;
        static FieldInfo _base, _vessel, _sinking, _count, _port, _star;
        static MethodInfo _addFlood;
        static FieldInfo _cur, _max;

        static void Init(object comp)
        {
            if (_init) return;
            _init = true;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var t = comp.GetType();
                _base = t.GetField("_baseObject", all); _vessel = t.GetField("_vessel", all); _sinking = t.GetField("_isSinking", all);
                _count = t.GetField("_compartmentsCount", all); _port = t.GetField("_portCompartments", all); _star = t.GetField("_starboardCompartments", all);
                var tF = _port == null ? null : _port.FieldType.GetElementType();
                _addFlood = tF == null ? null : tF.GetMethod("AddFloodingForHeavyListing", all);
                _cur = tF == null ? null : tF.GetField("_currentFlooding", all); _max = tF == null ? null : tF.GetField("_maxFlooding", all);
                _ok = _cur != null && _max != null && _base != null && _vessel != null && _sinking != null && _count != null && _port != null && _star != null && _addFlood != null;
            }
            catch (Exception e) { Trace("TrimFlooding.Init: " + e.Message); _ok = false; }
            if (!_ok) Trace("TrimFlooding: game members not found; trim past the limit does nothing");
        }

        static float Wrap(float a) { a %= 360f; if (a > 180f) a -= 360f; else if (a < -180f) a += 360f; return a; }

        /// <summary>Prefix of Compartments.OnFixedUpdate.</summary>
        public static void Tick(object comp)
        {
            if (comp == null || !IsEnabled()) return;
            Init(comp);
            if (!_ok) return;
            try
            {
                if (_vessel.GetValue(comp) == null || (bool)_sinking.GetValue(comp)) return;
                int count = (int)_count.GetValue(comp);
                if (count < 2) return;
                var bo = _base.GetValue(comp) as Component;
                if (bo == null) return;
                float pitch = Wrap(bo.transform.localEulerAngles.x);   // + bow down
                float limit = Limits.Trim(DcTeams.ShipLength(comp));
                float over = Math.Abs(pitch);
                if (over <= limit) return;
                // each side: the outermost compartment of the end going down that is not full yet
                var port = _port.GetValue(comp) as Array; var star = _star.GetValue(comp) as Array;
                foreach (var side in new[] { port, star })
                    for (int k = 0; k < count; k++)
                    {
                        var fc = side.GetValue(pitch > 0f ? k : count - 1 - k);
                        if ((float)_cur.GetValue(fc) >= (float)_max.GetValue(fc) * 0.999f) continue;   // full: the next one
                        _addFlood.Invoke(fc, null);
                        break;
                    }
            }
            catch (Exception e) { Trace("TrimFlooding: " + e.Message); }
        }
    }
}
