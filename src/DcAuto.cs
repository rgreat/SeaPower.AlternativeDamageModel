using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Auto DC that keeps the walking down (replaces Compartments.CheckDCTeamsAllocation while the mod is on and transfers take
    /// time - [DamageControlTeams] TransferSpeed or MusterTime not 0). The game sends every free team, one per tick, to the single worst
    /// section and recalls all of them every minute; with walking teams that keeps them on the move all the time. Here, every 2 s:
    ///  1. every section's work is rated in the order of the ship's DC Priority (Auto: leak > fire > water > hull > systems;
    ///     Fire first; Flooding first). Hull counts from 5% to restore, systems from medium damage. Fire and water are watched
    ///     for how fast they grow (smoothed over ~10 s), as well as for how bad they are;
    ///  2. each section with work wants one team; a section with fire or flooding wants one more if it is bad (leak rate ≥ 0.5,
    ///     fire ≥ 50% of its maximum, water ≥ 33%) and one more again if it grows fast (fire ≥ 0.5% of its maximum a second,
    ///     water ≥ 0.3% a second) - up to 3;
    ///  3. a team on its way somewhere counts as there already (no second team for the same job, no turning back);
    ///  4. a team NEVER leaves a section by itself while that section still has fire or flooding work (only fire in the Fire
    ///     mode, only leak / water in the Flooding mode). Whatever it is doing there is finished first;
    ///  5. the first team for a section comes from the nearest free team: the reserve, a team in a section with no work at all,
    ///     or a team beyond what its own (fire-and-flood-free) section needs. Only if there is none, and the section has fire or
    ///     flooding, is a team taken off hull or systems work - and only one that has been there for a minute;
    ///  6. the second and third teams come from free teams only (nobody is taken off other work for them);
    ///  7. a job has to be there for 4 s before anybody walks (a fire that a hit starts and the team puts out at once moves nobody);
    ///  8. a team that runs out of work stays where it is (DcControl does not send it back): it is picked from there next time,
    ///     often closer than the reserve.
    /// </summary>
    static class DcAuto
    {
        public static Action<string> Trace = s => { };

        const double Period = 2.0, Debounce = 4.0, MinStay = 60.0, Tau = 10.0;
        const float HullMin = 5f;                 // % of the side's integrity to restore before hull work counts
        const float LeakBad = 0.5f, FireBad = 0.5f, WaterBad = 33f;
        const float FireFast = 0.005f, WaterFast = 0.3f;   // a second: fraction of the fire's maximum / % of the section

        sealed class Ship
        {
            public double Next, Last;
            public float[] Fire = new float[5], Water = new float[5];          // last values
            public float[] FireRise = new float[5], WaterRise = new float[5];  // smoothed growth a second
            public double[] Seen = new double[5];                              // since when the section has had work (0 = none)
            public bool Primed;
        }
        static readonly ConditionalWeakTable<object, Ship> Ships = new ConditionalWeakTable<object, Ship>();

        static bool _ok;
        static FieldInfo _port, _star, _sys, _count, _sinking, _floodRate, _grow, _maxSev, _systems, _always, _maxInt;
        static PropertyInfo _teams, _floodDelta, _intDelta, _severity, _gameTime, _repairable, _curDmg, _bigDmg;

        public static bool Init(Assembly asm)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var tC = asm.GetType("SeaPower.Compartments"); var tF = asm.GetType("SeaPower.FloodingCompartment");
                var tS = asm.GetType("SeaPower.SystemCompartment"); var tB = asm.GetType("SeaPower.BaseSystem");
                _port = tC.GetField("_portCompartments", all); _star = tC.GetField("_starboardCompartments", all);
                _sys = tC.GetField("_systemCompartments", all); _count = tC.GetField("_compartmentsCount", all);
                _sinking = tC.GetField("_isSinking", all); _teams = tC.GetProperty("DamageControlTeamsNumbers", all);
                _floodRate = tF.GetField("_floodingRate", all); _floodDelta = tF.GetProperty("FloodingDelta", all); _intDelta = tF.GetProperty("IntegrityDelta", all);
                _maxInt = tF.GetField("_maxIntegrity", all);
                _severity = tS.GetProperty("FireSeverity", all); _grow = tS.GetField("_fireGrowRate", all); _maxSev = tS.GetField("_maxFireSeverity", all);
                _systems = tS.GetField("_listOfSystems", all);
                _repairable = tB.GetProperty("Repairable", all); _curDmg = tB.GetProperty("CurrentDamageType", all); _bigDmg = tB.GetProperty("BiggestDamageType", all);
                _always = tB.GetField("_alwaysRepairable", all);
                _gameTime = asm.GetType("SeaPower.GameTime").GetProperty("missionElapsedTime", all);
                _ok = _port != null && _star != null && _sys != null && _count != null && _sinking != null && _teams != null && _floodRate != null &&
                      _floodDelta != null && _intDelta != null && _maxInt != null && _severity != null && _grow != null && _maxSev != null && _systems != null &&
                      _repairable != null && _curDmg != null && _bigDmg != null && _always != null && _gameTime != null;
            }
            catch (Exception e) { Trace("DcAuto.Init: " + e); _ok = false; }
            return _ok;
        }

        static float F(object v) { try { return Convert.ToSingle(v); } catch { return 0f; } }

        const int Leak = DcControl.TaskLeak, Fire = DcControl.TaskFire, Pump = DcControl.TaskPump, Hull = DcControl.TaskHull, Systems = DcControl.TaskSystems;

        static int[] Order(int mode)
        {
            switch (mode)
            {
                case DcControl.ModeFire: return new[] { Fire, Leak, Pump, Hull, Systems };
                case DcControl.ModeFlooding: return new[] { Leak, Pump, Fire, Hull, Systems };
                default: return new[] { Leak, Fire, Pump, Hull, Systems };
            }
        }

        sealed class Need { public int Section, Rank, Want; public float Size; public bool Urgent; }

        /// <summary>Prefix of CheckDCTeamsAllocation (Auto DC): false = done here.</summary>
        public static bool Allocate(object comp)
        {
            if (!_ok || comp == null || !DcTeams.TransfersOn || !DcTeams.Active(comp)) return true;
            try
            {
                double now = Convert.ToDouble(_gameTime.GetValue(null, null));
                var ship = Ships.GetOrCreateValue(comp);
                if (now < ship.Next && now >= ship.Last) return false;
                double dt = ship.Primed ? now - ship.Last : 0;
                ship.Next = now + Period; ship.Last = now;
                if ((bool)_sinking.GetValue(comp)) return false;

                int count = Math.Min(5, (int)_count.GetValue(comp));
                var port = _port.GetValue(comp) as Array; var star = _star.GetValue(comp) as Array; var sys = _sys.GetValue(comp) as Array;
                int mode = DcControl.GetMode(comp);
                var order = Order(mode);

                // 1. the work in each section
                var needs = new List<Need>();
                var holds = new bool[count];   // fire / flooding work that keeps its teams there
                var work = new bool[count];
                for (int i = 0; i < count; i++)
                {
                    object p = port.GetValue(i), s = star.GetValue(i), sc = sys.GetValue(i);
                    float leak = Math.Max(F(_floodRate.GetValue(p)), F(_floodRate.GetValue(s)));
                    float sev = F(_severity.GetValue(sc, null)), max = F(_maxSev.GetValue(sc)), grow = F(_grow.GetValue(sc));
                    float fire = max > 0f ? sev / max : 0f;
                    float water = Math.Max(F(_floodDelta.GetValue(p, null)), F(_floodDelta.GetValue(s, null)));
                    float hull = Math.Max(Pct(_intDelta.GetValue(p, null), _maxInt.GetValue(p)), Pct(_intDelta.GetValue(s, null), _maxInt.GetValue(s)));

                    // growth, smoothed
                    if (dt > 0 && dt < 30)
                    {
                        float k = (float)(1 - Math.Exp(-dt / Tau));
                        ship.FireRise[i] += k * ((fire - ship.Fire[i]) / (float)dt - ship.FireRise[i]);
                        ship.WaterRise[i] += k * ((water - ship.Water[i]) / (float)dt - ship.WaterRise[i]);
                    }
                    else { ship.FireRise[i] = 0; ship.WaterRise[i] = 0; }
                    ship.Fire[i] = fire; ship.Water[i] = water;

                    bool hasLeak = leak > 0f, hasFire = sev > 0f || grow > 0f, hasWater = water > 0f;
                    holds[i] = mode == DcControl.ModeFire ? hasFire : mode == DcControl.ModeFlooding ? hasLeak || hasWater : hasLeak || hasFire || hasWater;

                    bool systems = false;
                    var list = _systems.GetValue(sc) as IEnumerable;
                    if (list != null)
                        foreach (var b in list)
                        {
                            try
                            {
                                int cur = Convert.ToInt32(_curDmg.GetValue(b, null));
                                bool destroyed = cur == 4;   // DamageType.Destroyed
                                if ((bool)_repairable.GetValue(b, null) && (!destroyed || (bool)_always.GetValue(b)) &&
                                    Convert.ToInt32(_bigDmg.GetValue(b, null)) <= cur && cur >= 2) { systems = true; break; }   // from medium damage
                            }
                            catch { }
                        }

                    // fire and flooding together decide how many teams the section wants
                    int extra = 0;
                    bool urgent = hasLeak || hasFire || hasWater;
                    if (urgent)
                    {
                        bool bad = (hasLeak && leak >= LeakBad) || (hasFire && fire >= FireBad) || (hasWater && water >= WaterBad);
                        bool fast = (hasFire && ship.FireRise[i] >= FireFast) || (hasWater && ship.WaterRise[i] >= WaterFast);
                        extra = (bad ? 1 : 0) + (fast ? 1 : 0);
                    }

                    for (int r = 0; r < order.Length; r++)
                    {
                        int kind = order[r];
                        bool has; float size;
                        switch (kind)
                        {
                            case Leak: has = hasLeak; size = leak; break;
                            case Fire: has = hasFire; size = fire; break;
                            case Pump: has = hasWater; size = water; break;
                            case Hull: has = hull >= HullMin; size = hull; break;
                            default: has = systems; size = 1f; break;
                        }
                        if (!has) continue;
                        work[i] = true;
                        if (ship.Seen[i] <= 0) ship.Seen[i] = now;
                        if (now - ship.Seen[i] >= Debounce)
                            needs.Add(new Need { Section = i, Rank = r, Size = size, Want = 1 + extra, Urgent = urgent });
                        break;
                    }
                    if (!work[i]) ship.Seen[i] = 0;
                }
                ship.Primed = true;
                if (needs.Count == 0) return false;

                // teams: where they are, and where the walking ones are going
                var teams = DcTeams.Snapshot(comp);
                var counts = _teams.GetValue(comp, null) as int[];
                if (counts == null) return false;
                int pool = counts.Length > 5 ? counts[5] : 0;
                var here = new int[count]; var coming = new int[count];
                var minStay = new float[count];
                for (int i = 0; i < count; i++) { minStay[i] = float.MaxValue; if (i < counts.Length) here[i] = counts[i]; }
                int usable = pool;
                for (int i = 0; i < count; i++) usable += here[i];
                foreach (var t in teams)
                {
                    if (t.State == (int)DcTeams.St.Moving) { usable++; if (t.Section >= 0 && t.Section < count) coming[t.Section]++; }
                    else if (t.State == (int)DcTeams.St.InSection && t.Section >= 0 && t.Section < count) minStay[t.Section] = Math.Min(minStay[t.Section], t.Stay);
                }

                // 2. share them out, best first (a section that already has a team first within the same rank: it is done sooner)
                needs.Sort((a, b) =>
                {
                    if (a.Rank != b.Rank) return a.Rank.CompareTo(b.Rank);
                    bool sa = here[a.Section] + coming[a.Section] > 0, sb = here[b.Section] + coming[b.Section] > 0;
                    if (sa != sb) return sa ? -1 : 1;
                    return b.Size.CompareTo(a.Size);
                });
                var target = new int[count];
                int left = usable;
                // the teams held by fire / flooding are there already and stay: they count first
                for (int i = 0; i < count; i++) if (holds[i]) { target[i] = here[i]; left -= here[i]; }
                foreach (var n in needs) if (left > 0 && target[n.Section] < 1) { target[n.Section] = 1; left--; }
                for (int round = 2; round <= 3; round++)
                    foreach (var n in needs) if (left > 0 && n.Want >= round && target[n.Section] < round) { target[n.Section]++; left--; }

                // 3-6. fill the gaps
                foreach (var n in needs)
                {
                    int i = n.Section;
                    while (here[i] + coming[i] < target[i])
                    {
                        bool first = here[i] + coming[i] == 0;
                        int from = -2; float best = float.MaxValue;
                        if (pool > 0) { best = DcTeams.TransferTime(comp, -1, i); from = -1; }
                        // free teams: in a section with no work, or more than a section without fire / flooding needs
                        for (int j = 0; j < count; j++)
                        {
                            if (j == i || holds[j] || here[j] <= target[j]) continue;
                            float tt = DcTeams.TransferTime(comp, j, i);
                            if (tt < best) { best = tt; from = j; }
                        }
                        // nobody free: the first team for fire / flooding may come off hull or systems work
                        if (from == -2 && first && n.Urgent)
                            for (int j = 0; j < count; j++)
                            {
                                if (j == i || holds[j] || here[j] < 1 || minStay[j] < MinStay) continue;
                                float tt = DcTeams.TransferTime(comp, j, i);
                                if (tt < best) { best = tt; from = j; }
                            }
                        if (from == -2) break;   // nobody to send
                        if (from == -1) { DcTeams.Send(comp, i); pool--; }
                        else if (DcTeams.MoveToSection(comp, from, i, true)) { here[from]--; if (target[from] > here[from]) target[from] = here[from]; }
                        else break;
                        coming[i]++;
                        Trace("Auto DC: a team for section " + i + " from " + (from < 0 ? "the reserve" : "section " + from) + " (" + best + " s)");
                    }
                }
                return false;
            }
            catch (Exception e) { Trace("DcAuto.Allocate: " + e.Message); return true; }
        }

        static float Pct(object delta, object max) { float m = F(max); return m > 0f ? F(delta) / m * 100f : 0f; }
    }
}
