using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
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
    ///
    /// [Sinking] DeckFloodingSpeed (default 1.0): the game's own AddFloodingForHeavyListing rate (meant for 45° of list,
    /// a rare, extreme angle) read as-is by this file for trim too; at a merely-over-the-limit trim it floods faster
    /// than feels right, so this file's own result is divided by a flat 10 in the formula first, then multiplied by Speed - 1.0 is that already-tenth-speed rate, 2.0 twice that, and so on.
    /// List past ListLimit floods the whole low side the same way, here too (FloodList; the game's own call is switched off by AdmPlugin.HeavyList).
    /// (The trim angle itself at high time compression: TimeCompressionFix.)
    /// </summary>
    static class TrimFlooding
    {
        public static Func<bool> IsEnabled = () => false;
        public static float Speed = 1f;
        public static float Imbalance = 1f;   // [Sinking] FloodImbalanceEffect
        public static Action<string> Trace = s => { };
        public static Action<string> Log = s => { };
        public static bool LogHeel;   // [General] LogHeel

        /// <summary>Patches the compartment's buoyancy input for [Sinking] FloodImbalanceEffect (see CfvPost).</summary>
        public static bool InitList(Harmony harmony, Assembly asm)
        {
            try
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var tF = asm.GetType("SeaPower.FloodingCompartment");
                _cfvGetter = tF == null ? null : tF.GetProperty("_currentFloodingValue", all)?.GetGetMethod(true);
                if (_cfvGetter != null)
                    try { harmony.Patch(_cfvGetter, postfix: new HarmonyMethod(typeof(TrimFlooding).GetMethod(nameof(CfvPost), BindingFlags.Static | BindingFlags.NonPublic))); Trace("patched FloodingCompartment._currentFloodingValue (list from uneven flooding)"); }
                    catch (Exception e) { Trace("TrimFlooding.InitList(imbalance): " + e.Message); _cfvGetter = null; }
                return _cfvGetter != null;
            }
            catch (Exception e) { Trace("TrimFlooding.InitList: " + e.Message); return false; }
        }

        static MethodInfo _cfvGetter;
        static bool _rawRead;
        static readonly ConditionalWeakTable<object, float[]> Skewed = new ConditionalWeakTable<object, float[]>();

        /// <summary>
        /// The buoyancy of a compartment falls with the water in it (the game reads FloodingCompartment._currentFloodingValue - in the
        /// physics job too). The list comes from the difference between the two sides of a section; here that difference is multiplied by
        /// [Sinking] FloodImbalanceEffect (the section's average is kept: the draught and the trim do not change from it).
        /// Skewed values are made once per tick in Tick; this getter's postfix returns them.
        /// </summary>
        static void CfvPost(object __instance, ref float __result)
        {
            if (_rawRead) return;
            float[] v;
            if (Imbalance != 1f && IsEnabled() && Skewed.TryGetValue(__instance, out v)) __result = v[0];
        }

        static float Raw(object fc)
        {
            _rawRead = true;
            try { return Convert.ToSingle(_cfvGetter.Invoke(fc, null)); } finally { _rawRead = false; }
        }

        static void SkewTick(object comp)
        {
            if (_cfvGetter == null || Imbalance == 1f || _port == null || _star == null || _count == null) return;
            var ship = Get(comp);
            if (!ship.Vessel) return;
            var port = _port.GetValue(comp) as Array; var star = _star.GetValue(comp) as Array;
            for (int i = 0; i < ship.Count && i < port.Length && i < star.Length; i++)
            {
                object a = port.GetValue(i), b = star.GetValue(i);
                float p = Raw(a), s = Raw(b), mean = 0.5f * (p + s);
                // the difference grows, the sum stays: the side with less water cannot go below zero, so the shift stops at the mean
                float h = Math.Max(-mean, Math.Min(mean, (p - mean) * Imbalance));
                Put(a, mean + h);
                Put(b, mean - h);
            }
        }

        static void Put(object fc, float v)
        {
            float[] box;
            if (Skewed.TryGetValue(fc, out box)) box[0] = v; else Skewed.Add(fc, new[] { v });
        }

        static bool _init, _ok;
        static FieldInfo _base, _vessel, _sinking, _capsized, _count, _port, _star;
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
                _base = t.GetField("_baseObject", all); _vessel = t.GetField("_vessel", all); _sinking = t.GetField("_isSinking", all); _capsized = t.GetField("_capSized", all);
                _count = t.GetField("_compartmentsCount", all); _port = t.GetField("_portCompartments", all); _star = t.GetField("_starboardCompartments", all);
                var tF = _port == null ? null : _port.FieldType.GetElementType();
                _addFlood = tF == null ? null : tF.GetMethod("AddFloodingForHeavyListing", all);
                _cur = tF == null ? null : tF.GetField("_currentFlooding", all); _max = tF == null ? null : tF.GetField("_maxFlooding", all);
                _ok = _cur != null && _max != null && _base != null && _vessel != null && _sinking != null && _count != null && _port != null && _star != null && _addFlood != null;
            }
            catch (Exception e) { Trace("TrimFlooding.Init: " + e.Message); _ok = false; }
            if (!_ok) Trace("TrimFlooding: game members not found; trim past the limit does nothing");
        }

        /// <summary>What does not change during a mission, read once per ship (not every physics tick).</summary>
        sealed class Ship { public bool Vessel; public int Count; public Component Base; public float Length; }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Ship> Ships = new System.Runtime.CompilerServices.ConditionalWeakTable<object, Ship>();

        static Ship Get(object comp)
        {
            Ship s;
            if (Ships.TryGetValue(comp, out s)) return s;
            s = new Ship { Vessel = _vessel.GetValue(comp) != null, Count = (int)_count.GetValue(comp), Base = _base.GetValue(comp) as Component, Length = DcTeams.ShipLength(comp) };
            Ships.Add(comp, s);
            return s;
        }

        static float Wrap(float a) { a %= 360f; if (a > 180f) a -= 360f; else if (a < -180f) a += 360f; return a; }

        /// <summary>Prefix of Compartments.OnFixedUpdate.</summary>
        public static void Tick(object comp)
        {
            if (comp == null || !IsEnabled()) return;
            Init(comp);
            if (!_ok) return;
            try { SkewTick(comp); } catch (Exception e) { Trace("TrimFlooding.SkewTick: " + e.Message); }
            if (LogHeel) try { HeelLog(comp); } catch (Exception e) { Trace("TrimFlooding.HeelLog: " + e.Message); }
            if (SubmergedFlooding.Covers(comp)) return;   // exact deck points: the water over the deck edge is SubmergedFlooding's
            try { FloodList(comp); } catch (Exception e) { Trace("TrimFlooding.FloodList: " + e.Message); }
            try
            {
                var ship = Get(comp);
                if (!ship.Vessel || ship.Count < 2 || ship.Base == null) return;
                int count = ship.Count;
                float pitch = Wrap(ship.Base.transform.localEulerAngles.x);   // + bow down
                float limit = Limits.TrimFlood(ship.Length);                   // from the red zone of the TRIM gauge
                if (Math.Abs(pitch) <= limit) return;
                if ((bool)_sinking.GetValue(comp)) return;
                // each side: the outermost compartment of the end going down that is not full yet
                var port = _port.GetValue(comp) as Array; var star = _star.GetValue(comp) as Array;
                foreach (var side in new[] { port, star })
                    for (int k = 0; k < count; k++)
                    {
                        var fc = side.GetValue(pitch > 0f ? k : count - 1 - k);
                        float before = (float)_cur.GetValue(fc);
                        if (before >= (float)_max.GetValue(fc) * 0.999f) continue;   // full: the next one
                        _addFlood.Invoke(fc, null);
                        // the game's own method just added its flooding at its own rate - replace that delta with ours:
                        // a flat /10 (see the class doc comment), times the player's Speed (DeckFloodingSpeed)
                        float after = (float)_cur.GetValue(fc);
                        _cur.SetValue(fc, before + (after - before) / 10f * Speed * SpeedEffect.Flood(comp));
                        break;
                    }
            }
            catch (Exception e) { Trace("TrimFlooding: " + e.Message); }
        }

        static readonly ConditionalWeakTable<object, float[]> LastLog = new ConditionalWeakTable<object, float[]>();
        static FieldInfo _fObp, _fTotalForce, _fSubmerged, _fRb;
        static PropertyInfo _pFloodPct;

        /// <summary>Diagnostics (LogHeel): once per 2 s per ship while it lists or trims noticeably - what the physics does.</summary>
        static void HeelLog(object comp)
        {
            var ship = Get(comp);
            if (!ship.Vessel || ship.Base == null) return;
            float list = Wrap(ship.Base.transform.localEulerAngles.z), trim = Wrap(ship.Base.transform.localEulerAngles.x);
            if (Math.Abs(list) < 8f && Math.Abs(trim) < 3f) return;
            float[] last;
            if (!LastLog.TryGetValue(comp, out last)) { last = new float[1]; LastLog.Add(comp, last); }
            if (Time.realtimeSinceStartup - last[0] < 2f) return;
            last[0] = Time.realtimeSinceStartup;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = comp.GetType();
            if (_fObp == null)
            {
                _fObp = t.GetField("_obp", all); _fTotalForce = t.GetField("_totalForce", all); _fSubmerged = t.GetField("_submergedVolume", all);
                _pFloodPct = t.GetProperty("FloodingPercentage", all);
            }
            string lift = "?";
            try
            {
                var obp = _fObp.GetValue(comp);
                if (_fRb == null) _fRb = obp.GetType().GetField("_rigidbody", all);
                var rb = _fRb.GetValue(obp);
                var rt = rb.GetType();
                float mass = Convert.ToSingle(rt.GetProperty("mass").GetValue(rb, null));
                Vector3 vel = (Vector3)rt.GetProperty("linearVelocity").GetValue(rb, null);
                Vector3 f = (Vector3)_fTotalForce.GetValue(comp);
                lift = (f.y / (mass * 9.81f)).ToString("0.00") + " of weight; vy " + vel.y.ToString("0.00");
            }
            catch { }
            float water = 0f; try { water = Convert.ToSingle(_pFloodPct.GetValue(comp, null)); } catch { }
            Log("Heel " + ship.Base.name + ": list " + list.ToString("0.0") + ", trim " + trim.ToString("0.0") + ", y " + ship.Base.transform.position.y.ToString("0.00") +
                ", lift " + lift + ", submerged " + Convert.ToSingle(_fSubmerged.GetValue(comp)).ToString("0.00") + ", water " + water.ToString("0.0") +
                "%, sinking " + _sinking.GetValue(comp) + ", capsized " + (_capsized == null ? "?" : _capsized.GetValue(comp)));
        }

        /// <summary>
        /// List past [Sinking] ListLimit's red zone: every compartment of the low side floods (the game's rule, Compartments.OnFixedUpdate:
        /// list over 45 degrees, not capsized, not sinking) - done here, with the game's own per-compartment amount (AddFloodingForHeavyListing)
        /// divided by 10 and multiplied by DeckFloodingSpeed, the same as for the trim. The game's own call is switched off (AdmPlugin.HeavyList).
        /// </summary>
        static void FloodList(object comp)
        {
            var ship = Get(comp);
            if (!ship.Vessel || ship.Count < 1 || ship.Base == null) return;
            if ((bool)_sinking.GetValue(comp) || (_capsized != null && (bool)_capsized.GetValue(comp))) return;
            float list = Wrap(ship.Base.transform.localEulerAngles.z);   // + : port side down
            if (Math.Abs(list) <= Limits.ListFlood) return;
            var side = (list > 0f ? _port : _star).GetValue(comp) as Array;
            for (int k = 0; k < ship.Count && k < side.Length; k++)
            {
                var fc = side.GetValue(k);
                float before = (float)_cur.GetValue(fc);
                _addFlood.Invoke(fc, null);
                float after = (float)_cur.GetValue(fc);
                _cur.SetValue(fc, before + (after - before) / 10f * Speed * SpeedEffect.Flood(comp));
            }
        }

        /// <summary>For the Damage Control tooltips: true while Tick floods this compartment (section i, that side) from trim
        /// past the limit - the same choice as Tick: the outermost not-full compartment of the end going down. pitch: + bow down.</summary>
        public static bool FloodsNow(object comp, int i, bool port, out float pitch)
        {
            pitch = 0f;
            if (comp == null || !IsEnabled() || SubmergedFlooding.Covers(comp)) return false;
            Init(comp);
            if (!_ok) return false;
            try
            {
                var ship = Get(comp);
                if (!ship.Vessel || ship.Count < 2 || ship.Base == null) return false;
                pitch = Wrap(ship.Base.transform.localEulerAngles.x);
                if (Math.Abs(pitch) <= Limits.TrimFlood(ship.Length) || (bool)_sinking.GetValue(comp)) return false;
                var side = (port ? _port : _star).GetValue(comp) as Array;
                for (int k = 0; k < ship.Count; k++)
                {
                    int idx = pitch > 0f ? k : ship.Count - 1 - k;
                    var fc = side.GetValue(idx);
                    if ((float)_cur.GetValue(fc) >= (float)_max.GetValue(fc) * 0.999f) continue;
                    return idx == i;
                }
            }
            catch (Exception e) { Trace("TrimFlooding.FloodsNow: " + e.Message); }
            return false;
        }
    }
}
