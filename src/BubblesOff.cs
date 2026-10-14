using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace AlternativeDamageModel
{
    /// <summary>
    /// The sinking-bubbles effect (ini [Particles] SinkingBubblesEffect): the game spawns it on the hull once, in Compartments.Sink(), and never ends it
    /// while the wreck exists. Here, N game minutes after the sink started (time compression counts), its particle systems stop emitting
    /// (the particles already in the water fade out), which saves the particle load of long missions. Setting [Sinking] BubblesStopMinutes; 0 = never (the game's).
    /// </summary>
    static class BubblesOff
    {
        public static Func<bool> IsEnabled = () => false;
        public static Action<string> Trace = s => { };
        public static float Minutes = 10f;

        static bool _init, _ok;
        static FieldInfo _fSinking, _fSinkTime, _fObp, _fEffect, _fBase, _fPos, _fSingle;
        static MethodInfo _mTransform;
        static PropertyInfo _pTime;
        static readonly ConditionalWeakTable<object, object> Done = new ConditionalWeakTable<object, object>();

        static void Init(object comp)
        {
            if (_init) return;
            _init = true;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var t = comp.GetType();
                _fSinking = t.GetField("_isSinking", all); _fSinkTime = t.GetField("_sinkTime", all);
                _fObp = t.GetField("_obp", all); _fBase = t.GetField("_baseObject", all);
                var tO = _fObp == null ? null : _fObp.FieldType;
                _fEffect = tO == null ? null : tO.GetField("_sinkingBubblesEffect", all);
                var tE = _fEffect == null ? null : _fEffect.FieldType;
                _fPos = tE == null ? null : tE.GetField("_position", all);
                _fSingle = tE == null ? null : tE.GetField("_singleEffect", all);
                _mTransform = _fBase == null ? null : _fBase.FieldType.GetMethod("getTransform", all);
                var tGt = t.Assembly.GetType("SeaPower.GameTime");
                _pTime = tGt == null ? null : tGt.GetProperty("missionElapsedTime", all);
                _ok = _fSinking != null && _fSinkTime != null && _fObp != null && _fBase != null && _fEffect != null && _fPos != null && _mTransform != null && _pTime != null;
            }
            catch (Exception e) { Trace("BubblesOff.Init: " + e.Message); _ok = false; }
            if (!_ok) Trace("BubblesOff: game members not found; the sinking bubbles are not stopped");
        }

        static Type _tPs, _tStop;
        static MethodInfo _mStop;

        // ParticleSystem lives in a Unity module the build does not reference: looked up in the loaded assemblies.
        static int StopAll(Transform t)
        {
            if (_tPs == null)
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    _tPs = a.GetType("UnityEngine.ParticleSystem"); _tStop = a.GetType("UnityEngine.ParticleSystemStopBehavior");
                    if (_tPs != null && _tStop != null) break;
                }
                if (_tPs == null || _tStop == null) { Trace("BubblesOff: ParticleSystem type not found"); return 0; }
                _mStop = _tPs.GetMethod("Stop", new[] { typeof(bool), _tStop });
                if (_mStop == null) { Trace("BubblesOff: ParticleSystem.Stop not found"); return 0; }
            }
            if (_mStop == null) return 0;
            int n = 0;
            var stopEmitting = Enum.ToObject(_tStop, 1);     // StopEmitting = 1 (0 would clear the particles at once)
            foreach (var ps in t.GetComponentsInChildren(_tPs)) { _mStop.Invoke(ps, new object[] { false, stopEmitting }); n++; }
            return n;
        }

        /// <summary>Every fixed tick (TeamTickPrefix); cheap until the ship has been sinking for Minutes.</summary>
        public static void Tick(object comp)
        {
            if (comp == null || Minutes <= 0f || !IsEnabled()) return;
            Init(comp);
            if (!_ok) return;
            try
            {
                if (!(bool)_fSinking.GetValue(comp)) return;
                object d;
                if (Done.TryGetValue(comp, out d)) return;
                double now = Convert.ToDouble(_pTime.GetValue(null, null));
                double since = now - Convert.ToDouble(_fSinkTime.GetValue(comp));
                if (since < Minutes * 60.0) return;
                Done.Add(comp, comp);
                var obp = _fObp.GetValue(comp);
                var eff = _fEffect.GetValue(obp);
                var tr = _mTransform.Invoke(_fBase.GetValue(comp), null) as Transform;
                if (eff == null || tr == null) return;
                var pos = (Vector3)_fPos.GetValue(eff);
                var single = _fSingle == null ? null : _fSingle.GetValue(eff) as GameObject;
                int stopped = 0;
                for (int i = 0; i < tr.childCount; i++)
                {
                    var ch = tr.GetChild(i);
                    if ((ch.localPosition - pos).sqrMagnitude > 1e-4f) continue;
                    if (single != null && !ch.name.StartsWith(single.name, StringComparison.Ordinal)) continue;
                    stopped += StopAll(ch);
                }
                Trace("BubblesOff: " + tr.name + " sank " + (since / 60.0).ToString("0.0") + " min ago, bubbles stopped (" + stopped + " particle systems)");
            }
            catch (Exception e) { Trace("BubblesOff.Tick: " + e.Message); }
        }
    }
}
