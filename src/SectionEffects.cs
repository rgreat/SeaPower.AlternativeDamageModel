using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Three effects of fire and water on a ship's sections (Compartments.OnFixedUpdate prefix), for every ship with the damage model on:
    ///  1. Fire reopens leaks. The largest leak each side of a section has had is remembered ([FireAndFlooding] FireReopenLeaks); while the
    ///     section burns, a plugged leak grows back by FireReopenLeaks % of that largest leak a second at fire 100% (less at a smaller fire),
    ///     up to FireReopenLimit % of it. The game's own increases (damage) are never lowered.
    ///  2. A fully flooded section (water above 75%, the mean of its two sides) loses its systems: each is brought down to the
    ///     severe damage level and made inoperable, and kept there while the water stays above 75% (so repairs do nothing;
    ///     <see cref="Locked"/> tells the teams not to try).
    ///  3. Water limits the fire: a section's fire cannot be stronger than (100 - the section's water %) of its maximum. A stronger fire falls to that limit at
    ///     FullFloodFireDecay % of the maximum a second, and a fire at the limit stops growing.
    /// </summary>
    static class SectionEffects
    {
        public static Func<bool> IsEnabled = () => false;
        public static Action<string> Trace = s => { };

        public static float ReopenRate;      // [FireAndFlooding] FireReopenLeaks: % of the largest leak a second at fire 100%; 0 = off
        public static float ReopenLimit = 50f;   // [FireAndFlooding] FireReopenLimit: % of the largest leak
        public const float FullLevel = 75f;     // a section is fully flooded above this % of water (the mean of its two sides)
        public static bool SystemsOut;           // [FireAndFlooding] FullFloodSystems
        public static float FireDecay;           // [FireAndFlooding] FullFloodFireDecay: % of the maximum fire a second it falls to the limit set by the water; 0 = off

        sealed class State { public float[] MaxLeak = new float[10]; public bool[] Full = new bool[5]; public float Acc; }
        const float Step = 0.1f;   // game seconds between the runs: the rates are applied for the whole time gathered, a few dozen reflection calls per ship are not paid in every physics tick
        static readonly ConditionalWeakTable<object, State> States = new ConditionalWeakTable<object, State>();

        static bool _init, _ok;
        static FieldInfo _fPort, _fStar, _fSys, _fCount, _fRate, _fScMax, _fScGrow, _fList, _fMaxInt;
        static PropertyInfo _pFloodPct, _pSev, _pCur, _pFixedDt;
        static StaticNum _nFixedDt;
        static Fld<float> _gRate, _gScMax, _gScGrow; static Prop<float> _gPct, _gSev; static Fld<Array> _gPort, _gStar, _gSys; static Fld<int> _gCount;   // fast access in the tick
        static MethodInfo _mInoperable;
        static PropertyInfo _pIsDestroyed;
        static bool _firstTraced, _sysTraced, _fireTraced;

        static void Init(object comp)
        {
            _init = true;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var asm = comp.GetType().Assembly;
                var tC = comp.GetType();
                var tF = asm.GetType("SeaPower.FloodingCompartment"); var tS = asm.GetType("SeaPower.SystemCompartment"); var tB = asm.GetType("SeaPower.BaseSystem");
                _fPort = tC.GetField("_portCompartments", all); _fStar = tC.GetField("_starboardCompartments", all);
                _fSys = tC.GetField("_systemCompartments", all); _fCount = tC.GetField("_compartmentsCount", all);
                _fRate = tF.GetField("_floodingRate", all); _pFloodPct = tF.GetProperty("CurrentFloodingPercent", all);
                _pSev = tS.GetProperty("FireSeverity", all); _fScMax = tS.GetField("_maxFireSeverity", all); _fScGrow = tS.GetField("_fireGrowRate", all);
                _fList = tS.GetField("_listOfSystems", all);
                _pCur = tB.GetProperty("CurrentIntegrity", all); _fMaxInt = tB.GetField("_maxIntegrity", all);
                _mInoperable = tB.GetMethod("MakeInoperable", all); _pIsDestroyed = tB.GetProperty("IsDestroyed", all);
                var tGt = asm.GetType("SeaPower.GameTime");
                _pFixedDt = tGt == null ? null : tGt.GetProperty("fixedDeltaTime", all);
                _ok = _fPort != null && _fStar != null && _fSys != null && _fCount != null && _fRate != null && _pFloodPct != null && _pSev != null && _fScMax != null &&
                      _fScGrow != null && _fList != null && _pCur != null && _fMaxInt != null && _mInoperable != null && _pIsDestroyed != null && _pFixedDt != null;
                if (_pFixedDt != null) _nFixedDt = new StaticNum(_pFixedDt);
                if (_ok) { _gRate = new Fld<float>(_fRate); _gPct = new Prop<float>(_pFloodPct); _gSev = new Prop<float>(_pSev); _gScMax = new Fld<float>(_fScMax); _gScGrow = new Fld<float>(_fScGrow); _gPort = new Fld<Array>(_fPort); _gStar = new Fld<Array>(_fStar); _gSys = new Fld<Array>(_fSys); _gCount = new Fld<int>(_fCount); }
                if (!_ok) Trace("SectionEffects: game members not found; fire / flooding effects on sections are off");
            }
            catch (Exception e) { Trace("SectionEffects.Init: " + e.Message); _ok = false; }
        }

        static float F(object v) { try { return Convert.ToSingle(v); } catch { return 0f; } }

        /// <summary>True while the section's systems are out of order by flooding (repairing them is pointless).</summary>
        public static bool Locked(object comp, int section)
        {
            State s;
            return SystemsOut && comp != null && section >= 0 && section < 5 && States.TryGetValue(comp, out s) && s.Full[section];
        }

        /// <summary>
        /// True while section i burns and every hole in it that leaks is held open by the fire: not above the level the fire reopens it to
        /// (FireReopenLimit % of its largest rate). Plugging it is pointless until the fire is out (it opens again): the fire comes first.
        /// A hole above that level (fresh, larger) is still worth plugging - down to it.
        /// </summary>
        public static bool LeaksHeldByFire(object comp, int i)
        {
            long _p0 = Prof.Start();
            try { return LeaksHeldByFireCore(comp, i); }
            finally { Prof.Stop(Prof.LeakChecks, _p0); }
        }

        static bool LeaksHeldByFireCore(object comp, int i)
        {
            if (comp == null || i < 0 || i > 4 || ReopenRate <= 0f || !_ok || !IsEnabled()) return false;
            State st;
            if (!States.TryGetValue(comp, out st)) return false;
            try
            {
                var port = _fPort.GetValue(comp) as Array; var star = _fStar.GetValue(comp) as Array; var sys = _fSys.GetValue(comp) as Array;
                object sc = sys.GetValue(i);
                float sev = F(_pSev.GetValue(sc, null)), max = F(_fScMax.GetValue(sc));
                if (sev <= 0f || max <= 0f) return false;   // no fire: plug the holes
                bool any = false;
                for (int side = 0; side < 2; side++)
                {
                    float r = F(_fRate.GetValue((side == 0 ? port : star).GetValue(i)));
                    if (r <= 0f) continue;
                    any = true;
                    float cap = st.MaxLeak[i + side * 5] * ReopenLimit / 100f;
                    if (r > cap * 1.02f + 1e-5f) return false;   // a hole above the fire's level: worth plugging
                }
                return any;
            }
            catch { return false; }
        }

        public static void Tick(object comp)
        {
            if (comp == null || !IsEnabled()) return;
            if (ReopenRate <= 0f && !SystemsOut && FireDecay <= 0f) return;
            if (!_init) Init(comp);
            if (!_ok) return;
            try
            {
                float dt = (float)_nFixedDt.Get();
                if (dt <= 0f) return;
                var st = States.GetOrCreateValue(comp);
                st.Acc += dt;
                if (st.Acc < Step) return;
                dt = st.Acc; st.Acc = 0f;
                int n = Math.Min(5, _gCount.Get(comp));
                var port = _gPort.Get(comp); var star = _gStar.Get(comp); var sys = _gSys.Get(comp);
                if (port == null || star == null || sys == null) return;
                for (int i = 0; i < n && i < port.Length && i < star.Length && i < sys.Length; i++)
                {
                    object p = port.GetValue(i), s = star.GetValue(i), sc = sys.GetValue(i);
                    float sev = _gSev.Get(sc), max = _gScMax.Get(sc), grow = _gScGrow.Get(sc);
                    float fire = max > 0f ? Math.Min(1f, sev / max) : 0f;

                    // 1. leaks: the largest one is remembered; a burning section opens a plugged leak again
                    for (int side = 0; side < 2; side++)
                    {
                        object fc = side == 0 ? p : s;
                        int k = i + side * 5;
                        float r = _gRate.Get(fc);
                        if (r > st.MaxLeak[k]) st.MaxLeak[k] = r;
                        float mx = st.MaxLeak[k];
                        if (ReopenRate > 0f && fire > 0f && mx > 0f)
                        {
                            float cap = mx * ReopenLimit / 100f;
                            if (r < cap)
                            {
                                float r2 = Math.Min(cap, r + mx * ReopenRate / 100f * fire * dt);
                                _gRate.Set(fc, r2);
                                if (!_firstTraced) { _firstTraced = true; Trace("SectionEffects: fire in section " + (i + 1) + " reopens a leak (largest " + mx.ToString("0.###") + ", now " + r2.ToString("0.###") + ")"); }
                            }
                        }
                    }

                    // 2. a fully flooded section loses its systems; 3. water limits the fire: it cannot be stronger than (100 - water %) of its maximum
                    float water = (_gPct.Get(p) + _gPct.Get(s)) * 0.5f;
                    bool full = SystemsOut && FullLevel > 0f && water > FullLevel;
                    st.Full[i] = full;
                    if (full) SwampSystems(sc, i, water);
                    if (FireDecay > 0f && water > 0f && (sev > 0f || grow > 0f) && max > 0f)
                    {
                        float cap = max * Math.Max(0f, 100f - water) / 100f;
                        if (sev > cap)
                        {
                            _pSev.SetValue(sc, Math.Max(cap, sev - FireDecay / 100f * max * dt), null);
                            if (!_fireTraced) { _fireTraced = true; Trace("SectionEffects: section " + (i + 1) + " is flooded to " + water.ToString("0") + "%, its fire (" + (sev / max * 100f).ToString("0") + "%) dies down to " + (100f - water).ToString("0") + "% at " + FireDecay.ToString("0.#") + " %/s"); }
                        }
                        if (sev >= cap && grow > 0f) _gScGrow.Set(sc, 0f);   // at the limit the fire does not grow
                    }
                }
            }
            catch (Exception e) { Trace("SectionEffects.Tick: " + e.Message); }
        }

        /// <summary>Every system of the section is brought down to the severe damage level (19% of its integrity) and made inoperable; kept there.</summary>
        static void SwampSystems(object sc, int section, float water)
        {
            var list = _fList.GetValue(sc) as IEnumerable;
            if (list == null) return;
            foreach (var b in list)
            {
                if (b == null) continue;
                try
                {
                    float max = F(_fMaxInt.GetValue(b));
                    if (max < 1f || (bool)_pIsDestroyed.GetValue(b, null)) continue;
                    float cap = max * 0.19f;
                    float cur = F(_pCur.GetValue(b, null));
                    if (cur > cap)
                    {
                        _pCur.SetValue(b, cap, null);
                        _mInoperable.Invoke(b, null);
                        if (!_sysTraced) { _sysTraced = true; Trace("SectionEffects: section " + (section + 1) + " is flooded to " + water.ToString("0") + "%, its systems go out of order"); }
                    }
                }
                catch { }
            }
        }
    }
}
