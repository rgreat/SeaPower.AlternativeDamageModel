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
    ///  1. every section's work is rated in the order of the ship's DC Priority (Auto: leak rate > fire rate > fires > flooding > systems > hull;
    ///     Fire: fire rate > fires > leak rate > flooding > ...; Flooding: leak rate > flooding > fire rate > fires > ...). Hull counts from 5% to restore, systems from medium damage. Fire and water are watched
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
    ///     often closer than the reserve - while the ship has work somewhere; 10 s after the last work is done everywhere, all
    ///     teams walk back to the reserve.
    /// </summary>
    static class DcAuto
    {
        public static Action<string> Trace = s => { };

        const double Period = 2.0, Debounce = 4.0, MinStay = 60.0, Tau = 10.0;
        const float HullMin = 5f;                 // % of the side's integrity to restore before hull work counts
        const float LeakBad = 0.5f, FireBad = 0.5f, WaterBad = 33f;
        const double ExtraEvery = 10.0;           // s: one more team for a section with work, while free teams stand idle
        const int ExtraMax = 4;                   // teams in one section, by this rule
        const float FireFast = 0.005f, WaterFast = 0.3f;   // a second: fraction of the fire's maximum / % of the section

        sealed class Ship
        {
            public double Next, Last, NextExtra;
            public float[] Fire = new float[5], Water = new float[5];          // last values
            public float[] FireRise = new float[5], WaterRise = new float[5];  // smoothed growth a second
            public double[] Seen = new double[5];                              // since when the section has had work (0 = none)
            public bool Primed;
            public double LastWork = -1;   // game time the ship last had any work (any section)
            public bool CounterOn;         // the heel is dangerous: counterflooding is a job (hysteresis)
        }

        /// <summary>A team with nothing to do waits where it is only while the ship still has work somewhere (or had, within
        /// the last 10 s); once all work is done the teams walk back to the reserve, as the game's own Auto DC does.</summary>
        public static bool AnyWork(object comp)
        {
            Ship s;
            if (!_ok || comp == null || !Ships.TryGetValue(comp, out s) || s.LastWork < 0) return false;
            double now;
            try { now = Convert.ToDouble(_gameTime.GetValue(null, null)); } catch { return true; }
            return now - s.LastWork < IdleGrace;
        }
        const double IdleGrace = 10.0;
        static readonly ConditionalWeakTable<object, Ship> Ships = new ConditionalWeakTable<object, Ship>();

        static bool _ok;
        public static bool Ok { get { return _ok; } }
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
        const int FireRate = 100;   // not a task: the fire's growth (Fire Rate) ranks above the fire's size (Fires)

        static int[] Order(int mode)
        {
            switch (mode)
            {
                case DcControl.ModeFire: return new[] { FireRate, Fire, Leak, Pump, Systems, Hull };
                case DcControl.ModeFlooding: return new[] { Leak, Pump, FireRate, Fire, Systems, Hull };
                default: return new[] { Leak, FireRate, Fire, Pump, Systems, Hull };
            }
        }

        sealed class Need { public int Section, Rank, Want; public bool ByRate; public float Size; public bool Urgent; }

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
                if (DcTeams.Out(comp)) return false;
                DcTeams.RestPull(comp);   // wounded and exhausted teams go to rest (before the counts are read)

                int count = Math.Min(5, (int)_count.GetValue(comp));
                var port = _port.GetValue(comp) as Array; var star = _star.GetValue(comp) as Array; var sys = _sys.GetValue(comp) as Array;
                int mode = DcControl.GetMode(comp);
                var order = Order(mode);

                var counts = _teams.GetValue(comp, null) as int[];
                if (counts == null) return false;

                // 1. the work in each section
                var needs = new List<Need>();
                bool anyLeak = false;          // some hole still lets water in
                var holds = new bool[count];   // fire / flooding work that keeps its teams there
                var work = new bool[count];
                for (int i = 0; i < count; i++)
                {
                    object p = port.GetValue(i), s = star.GetValue(i), sc = sys.GetValue(i);
                    float leak = Math.Max(F(_floodRate.GetValue(p)), F(_floodRate.GetValue(s)));
                    float sev = F(_severity.GetValue(sc, null)), max = F(_maxSev.GetValue(sc)), grow = F(_grow.GetValue(sc));
                    float fire = max > 0f ? sev / max : 0f;
                    float water = Math.Max(F(_floodDelta.GetValue(p, null)), F(_floodDelta.GetValue(s, null)));
                    bool onSite = i < counts.Length && counts[i] > 0;   // a team there finishes what the game would still repair
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

                    if (leak > 0f) anyLeak = true;
                    bool hasLeak = leak > 0f, hasFire = sev > 0f || grow > 0f, hasWater = water > 0f;
                    holds[i] = mode == DcControl.ModeFire ? hasFire : mode == DcControl.ModeFlooding ? hasLeak || hasWater : hasLeak || hasFire || hasWater;

                    bool systems = false;
                    var list = SectionEffects.Locked(comp, i) ? null : _systems.GetValue(sc) as IEnumerable;   // a flooded section's systems cannot be repaired
                    if (list != null)
                        foreach (var b in list)
                        {
                            try
                            {
                                int cur = Convert.ToInt32(_curDmg.GetValue(b, null));
                                bool destroyed = cur == 4;   // DamageType.Destroyed
                                if ((bool)_repairable.GetValue(b, null) && (!destroyed || (bool)_always.GetValue(b)) &&
                                    Convert.ToInt32(_bigDmg.GetValue(b, null)) <= cur && cur >= (onSite ? 1 : 2)) { systems = true; break; }   // from medium damage; light too where a team works already
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
                            case FireRate: has = grow > 0f; size = grow; break;
                            case Fire: has = hasFire; size = fire; break;
                            case Pump: has = hasWater; size = water; break;
                            case Hull: has = hull >= HullMin || (onSite && hull > 0.01f); size = hull; break;
                            default: has = systems; size = 1f; break;
                        }
                        if (!has) continue;
                        work[i] = true;
                        if (ship.Seen[i] <= 0) ship.Seen[i] = now;
                        if (now - ship.Seen[i] >= Debounce)
                        {
                            // plugging holes or pumping out in a section that also burns: two teams, one of them fights the fire (DcControl)
                            int want = 1 + extra;
                            if ((kind == Leak || kind == Pump) && hasFire) want = Math.Max(want, 2);
                            needs.Add(new Need { Section = i, Rank = r * 2, Size = size, Want = want, Urgent = urgent, ByRate = kind == FireRate || kind == Leak });
                        }
                        break;
                    }
                    if (!work[i]) ship.Seen[i] = 0;
                    else ship.LastWork = now;
                }
                ship.Primed = true;

                // counterflooding: a dangerous heel is a job of its own, in the section where letting water in or pumping it out helps most;
                // it ranks just before the flooding (pumping) job. The teams there hold their place while they are at it.
                ship.CounterOn = DcControl.AutoCounterfloodOn && DcControl.HeelDanger(comp, ship.CounterOn, anyLeak);
                DcControl.SetAutoCounter(comp, ship.CounterOn);
                if (ship.CounterOn)
                {
                    float cs; int ci = DcControl.CounterSection(comp, out cs);
                    if (ci >= 0 && ci < count)
                    {
                        int pumpAt = Array.IndexOf(order, Pump);
                        int baseWant = 0;
                        foreach (var n0 in needs) if (n0.Section == ci) baseWant = Math.Max(baseWant, n0.Want);
                        needs.Add(new Need { Section = ci, Rank = pumpAt * 2 - 1, Size = cs, Want = Math.Min(3, baseWant + 1), Urgent = true, ByRate = false });
                        work[ci] = true; ship.LastWork = now;
                        if (DcControl.CounterAct(comp, ci) != 0) holds[ci] = true;
                    }
                }
                if (needs.Count == 0) return false;

                // teams: where they are, and where the walking ones are going
                var teams = DcTeams.Snapshot(comp);
                int pool = counts.Length > 5 ? Math.Min(counts[5], DcTeams.ReadyInPool(comp)) : 0;   // teams that rest are not sent
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
                    if (a.ByRate && b.ByRate) return b.Size.CompareTo(a.Size);   // the fastest growing first
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
                int sent = 0;
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
                            // a section with work (also fresh, still debounced) keeps one team: only the fallback below takes it
                            if (j == i || holds[j] || here[j] <= Math.Max(target[j], work[j] ? 1 : 0)) continue;
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
                        coming[i]++; sent++;
                        Trace("Auto DC: a team for section " + i + " from " + (from < 0 ? "the reserve" : "section " + from) + " (" + best + " s)");
                    }
                }

                // 7. free teams stand idle while a section has work: when nobody else is needed, one more team is added every few seconds
                //    (from the reserve, or from a section with no work at all), to the section with work that has the fewest teams
                if (sent == 0 && now >= ship.NextExtra)
                {
                    Need pick = null;
                    foreach (var n in needs)
                    {
                        int have = here[n.Section] + coming[n.Section];
                        if (have >= ExtraMax) continue;
                        if (pick == null || have < here[pick.Section] + coming[pick.Section]) pick = n;   // the order of 'needs' decides between equals
                    }
                    if (pick != null)
                    {
                        int i = pick.Section, from = -2; float best = float.MaxValue;
                        if (pool > 0) { best = DcTeams.TransferTime(comp, -1, i); from = -1; }
                        for (int j = 0; j < count; j++)
                        {
                            if (j == i || work[j] || holds[j] || here[j] < 1) continue;
                            float tt = DcTeams.TransferTime(comp, j, i);
                            if (tt < best) { best = tt; from = j; }
                        }
                        if (from == -1) { DcTeams.Send(comp, i); Trace("Auto DC: one more team for section " + i + " from the reserve"); ship.NextExtra = now + ExtraEvery; }
                        else if (from >= 0 && DcTeams.MoveToSection(comp, from, i, true)) { Trace("Auto DC: one more team for section " + i + " from section " + from); ship.NextExtra = now + ExtraEvery; }
                    }
                }
                return false;
            }
            catch (Exception e) { Trace("DcAuto.Allocate: " + e.Message); return true; }
        }

        static float Pct(object delta, object max) { float m = F(max); return m > 0f ? F(delta) / m * 100f : 0f; }
    }
}
