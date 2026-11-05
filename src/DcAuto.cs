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

        const double Period = 2.0, MinStay = 60.0, Tau = 10.0;
        const float HullMin = 5f;                 // % of the side's integrity to restore before hull work counts
        const float LeakBad = 0.5f, FireBad = 0.5f, WaterBad = 33f;
        const float FloodedHullMin = 1f;   // % of a compartment the hull keeps in (MinimumFlooding) for its repair to count as Flooded Structure
        const float SmallWater = 10f;   // % of a compartment (above its minimum): less water is small pumping - no job of its own (see the "small pumping" rule)
        const double ExtraEvery = 10.0;           // s: one more team for a section with work, while free teams stand idle
        const int ExtraMax = 4;                   // teams in one section, by this rule
        const float FireFast = 0.005f, WaterFast = 0.3f;   // a second: fraction of the fire's maximum / % of the section

        sealed class Ship
        {
            public double Next, Last, NextExtra, NextRelief;
            public float[] Fire = new float[5], Water = new float[5];          // last values
            public float[] FireRise = new float[5], WaterRise = new float[5];  // smoothed growth a second
            public bool Primed;
            public double LastWork = -1;   // game time the ship last had any work (any section)
            public bool CounterOn;         // the heel is dangerous: counterflooding is a job (hysteresis)
            public int CounterKeep = -1;   // the section counterflooding was planned in last time (kept unless another is clearly better: no flip-flop)
            public double CounterIdle;
            public int PumpYield, HeelStop;
            public bool EngineAll;         // the engine is out: every team on the fires at the engine / its repair (for the trace)          // bits: sections whose pumping gave way to higher work (for the trace)     // since when counterflooding has nothing to level (0 = it has); after CounterIdleOff s the mode goes off
            public string Snap; public double NextSnap; public int Bottle = -1; public readonly bool[] Released = new bool[5];
            public double NextDiag, NextHeelDiag;        // next diagnostic line of the pairs rule
            public int Avail;              // Av* bits: which kinds of work the ship has now
            public bool EngineNow, PrioNow; // a priority repair of the engine / of other systems is on (for the status line)
            public bool EngineFire;         // a section of the engine burns: the fire is fought first (it keeps damaging the engine), the repair waits
            public int EngineFireMask;      // which of the engine's sections burn (bits, for the trace)
            public int EngineSec = -1;     // the section of the engine that is out of order
            public bool EngineMarked;      // the lost engine was marked for priority repair (once for each outage)
            public bool CounterCrit;       // the heel is critical: counterflooding is the first job (hysteresis)
        }

        /// <summary>A team with nothing to do waits where it is only while the ship still has work somewhere (or had, within
        /// the last 10 s); once all work is done the teams walk back to the reserve, as the game's own Auto DC does.</summary>
        public static bool AnyWork(object comp)
        {
            Ship s;
            if (!_ok || comp == null || !Ships.TryGetValue(comp, out s) || s.LastWork < 0) return false;
            double now;
            try { now = GameNow(); } catch { return true; }
            return now - s.LastWork < IdleGrace;
        }
        const double IdleGrace = 10.0;
        public const int AvLeak = 1, AvFireRate = 2, AvFire = 4, AvPump = 8, AvCounter = 16, AvSystems = 32, AvHull = 64, AvFloodHull = 128;

        /// <summary>Which kinds of work the ship has now (Av* bits): the ones damage control can do at the moment.</summary>
        public static int Avail(object comp)
        {
            Ship s;
            return _ok && comp != null && Ships.TryGetValue(comp, out s) ? s.Avail : 0;
        }

        /// <summary>The critical jobs now: 1 = counterflooding at a critical heel, 2 = an engine out of order, 8 = the same while its section burns (the fire first), 4 = other priority systems.</summary>
        public static int Critical(object comp)
        {
            Ship s;
            if (!_ok || comp == null || !Ships.TryGetValue(comp, out s)) return 0;
            return (s.CounterCrit && DcControl.AutoCounterfloodOn ? 1 : 0) | (s.EngineNow ? (s.EngineFire ? 8 : 2) : 0) | (s.PrioNow ? 4 : 0);
        }

        /// <summary>What Auto DC puts before the ship's DC Priority right now (critical heel, an engine out of order, systems marked "!"), "" when nothing; for the panel's texts.</summary>
        public static string NowFirst(object comp)
        {
            Ship s;
            if (!_ok || comp == null || !Ships.TryGetValue(comp, out s)) return "";
            var l = new List<string>();
            if (s.CounterCrit && DcControl.AutoCounterfloodOn) l.Add("Counterflooding (critical heel)");
            if (s.EngineNow) l.Add(s.EngineFire ? "Fire at the engine" : "Engine repair");
            if (s.PrioNow) l.Add("Priority systems");
            return string.Join(" > ", l.ToArray());
        }
        static readonly ConditionalWeakTable<object, Ship> Ships = new ConditionalWeakTable<object, Ship>();

        static bool _ok;
        public static bool Ok { get { return _ok; } }
        static FieldInfo _port, _star, _sys, _count, _sinking, _floodRate, _grow, _maxSev, _systems, _always, _maxInt;
        static PropertyInfo _minFlood, _intPct, _floodPctF;
        static PropertyInfo _teams, _floodDelta, _intDelta, _severity, _gameTime, _repairable, _curDmg, _bigDmg;
        static StaticNum _nGameTime;
        static double GameNow() { return _nGameTime != null ? _nGameTime.Get() : Convert.ToDouble(_gameTime.GetValue(null, null)); }

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
                _minFlood = tF.GetProperty("MinimumFlooding", all); _intPct = tF.GetProperty("CurrentIntegrityPercent", all); _floodPctF = tF.GetProperty("CurrentFloodingPercent", all);
                _maxInt = tF.GetField("_maxIntegrity", all);
                _severity = tS.GetProperty("FireSeverity", all); _grow = tS.GetField("_fireGrowRate", all); _maxSev = tS.GetField("_maxFireSeverity", all);
                _systems = tS.GetField("_listOfSystems", all);
                _repairable = tB.GetProperty("Repairable", all); _curDmg = tB.GetProperty("CurrentDamageType", all); _bigDmg = tB.GetProperty("BiggestDamageType", all);
                _always = tB.GetField("_alwaysRepairable", all);
                _gameTime = asm.GetType("SeaPower.GameTime").GetProperty("missionElapsedTime", all);
                _nGameTime = _gameTime == null ? null : new StaticNum(_gameTime);
                _ok = _port != null && _star != null && _sys != null && _count != null && _sinking != null && _teams != null && _floodRate != null &&
                      _floodDelta != null && _intDelta != null && _maxInt != null && _severity != null && _grow != null && _maxSev != null && _systems != null &&
                      _repairable != null && _curDmg != null && _bigDmg != null && _always != null && _gameTime != null;
            }
            catch (Exception e) { Trace("DcAuto.Init: " + e); _ok = false; }
            return _ok;
        }

        static float F(object v) { try { return Convert.ToSingle(v); } catch { return 0f; } }

        static int SecRank(List<Need> needs, int sec)
        {
            int r = int.MaxValue;
            foreach (var n in needs) if (n.Section == sec && n.Rank < r) r = n.Rank;
            return r;
        }

        const float PumpTolerance = 0.05f;   // parts of the red zone: pumping that worsens the heel less than this is still done (it lifts the ship; no flicker around 0)

        /// <summary>
        /// The hull holds the water of section i: the game keeps MinimumFlooding = (100 - integrity %) / 2 in a compartment (pumping stops there), and
        /// welding the hull lowers it (up to MaxAllowedIntegrity). True when a side keeps at least SmallWater that way and its hull can still be repaired.
        /// Then the hull repair is the way to get the water out: it ranks as pumping (DcAuto) and comes before the systems (DcControl).
        /// </summary>
        public static bool HullHoldsWater(object comp, int i)
        {
            long _p0 = Prof.Start();
            try { return HullHoldsWaterCore(comp, i); }
            finally { Prof.Stop(Prof.LeakChecks, _p0); }
        }

        static bool HullHoldsWaterCore(object comp, int i)
        {
            if (!_ok || comp == null || _minFlood == null) return false;
            try
            {
                var port = _port.GetValue(comp) as Array; var star = _star.GetValue(comp) as Array;
                if (port == null || star == null || i < 0 || i >= port.Length) return false;
                foreach (var fc in new[] { port.GetValue(i), star.GetValue(i) })
                    if (F(_minFlood.GetValue(fc, null)) >= FloodedHullMin && Pct(_intDelta.GetValue(fc, null), _maxInt.GetValue(fc)) > 0.01f) return true;
            }
            catch { }
            return LeakRisk(comp, i);
        }

        const float FullSide = 99.5f;          // % of a compartment's water: full - no leak can let more in
        const float LeakRiskIntegrity = 19f;   // the game: a compartment under 19% integrity left without a team leaks again (300 s after its leak was stopped)

        /// <summary>
        /// The game opens the leak of a compartment again (+0.001/s) while its integrity is under 19%, no team is in its section and 300 s have passed
        /// since its leak was last stopped (FloodingCompartment.OnFixedUpdate). True for section i when a side is under 19% and its hull can still be
        /// repaired: the section must not be left empty (its last team does not leave for rest or lower work) and the hull repair there is Flooded Structure.
        /// </summary>
        public static bool LeakRisk(object comp, int i)
        {
            if (!_ok || comp == null || _intPct == null) return false;
            try
            {
                var port = _port.GetValue(comp) as Array; var star = _star.GetValue(comp) as Array;
                if (port == null || star == null || i < 0 || i >= port.Length) return false;
                for (int side = 0; side < 2; side++)
                {
                    var fc = (side == 0 ? port : star).GetValue(i);
                    // full of water already and its deck point above the sea: a leak that opens again lets nothing more in (with the deck under water the
                    // compartment stays at risk - user's rule)
                    if (_floodPctF != null && F(_floodPctF.GetValue(fc, null)) >= FullSide && !SubmergedFlooding.DeckUnder(comp, i, side == 0)) continue;
                    if (F(_intPct.GetValue(fc, null)) < LeakRiskIntegrity && Pct(_intDelta.GetValue(fc, null), _maxInt.GetValue(fc)) > 0.01f) return true;
                }
            }
            catch { }
            return false;
        }

        static string KindName(int k)
        {
            switch (k)
            {
                case DcControl.TaskLeak: return "holes";
                case DcControl.TaskFire: return "fire";
                case DcControl.TaskPump: return "pumping";
                case DcControl.TaskHull: return "hull";
                case DcControl.TaskSystems: return "systems";
                case 200: return "counterflooding";
                case 201: return "priority repair";
                default: return k == FireRate ? "fire growth" : "job " + k;
            }
        }
        const int Leak = DcControl.TaskLeak, Fire = DcControl.TaskFire, Pump = DcControl.TaskPump, Hull = DcControl.TaskHull, Systems = DcControl.TaskSystems;
        const int FloodHull = 101;  // not a task: hull repair where the hull holds water (Flooded Structure), before Systems; plain Structure after them
        const int FireRate = 100;   // not a task: the fire's growth (Fire Rate) ranks above the fire's size (Fires)

        static int[] Order(int mode)
        {
            switch (mode)
            {
                case DcControl.ModeFire: return new[] { FireRate, Fire, Leak, Pump, FloodHull, Systems, Hull };
                case DcControl.ModeFlooding: return new[] { Leak, Pump, FireRate, Fire, FloodHull, Systems, Hull };
                default: return new[] { Leak, FireRate, Fire, Pump, FloodHull, Systems, Hull };
            }
        }

        const int Counter = 200, PrioSys = 201;   // kinds of the ship-wide jobs (not tasks of a section)

        const float CounterUseful = 0.25f;    // parts of the red zone counterflooding must take off the heel to be worth it (to start, and to stay on)
        const double CounterIdleOff = 10.0;   // s with nothing to level before Auto DC's counterflooding (and its critical mode) goes off
        const float HeelGainMin = 0.02f;   // the part of the red zone of the heel a job must take off to count as levelling the ship
        sealed class Need { public int Section, Rank, Want, Kind, Bonus; public bool ByRate, Crit; public float Size; public bool Urgent; public float Gain; public bool Heel; public bool All; public bool Small; }   // All: the engine is out - this job takes every team it can get

        /// <summary>Prefix of CheckDCTeamsAllocation (Auto DC): false = done here.</summary>
        public static bool Allocate(object comp)
        {
            if (!_ok || comp == null || !DcTeams.TransfersOn || !DcTeams.Active(comp)) return true;
            try
            {
                double now = GameNow();
                var ship = Ships.GetOrCreateValue(comp);
                if (now < ship.Next && now >= ship.Last) return false;
                double dt = ship.Primed ? now - ship.Last : 0;
                ship.Next = now + Period; ship.Last = now;
                Prof.Count(Prof.AllocRun);
                if (DcTeams.Out(comp)) { DcControl.SetPlan(comp, false, -1, null); return false; }
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
                // an engine put out of order: its repair becomes a priority (once, so a mark taken off by hand stays off)
                var engine = ContactIntel.EngineSystem(comp);
                if (engine == null) { ship.EngineMarked = false; ship.EngineSec = -1; }
                else if (!ship.EngineMarked)
                {
                    ship.EngineMarked = true;
                    for (int i = 0; i < count; i++)
                    {
                        var list0 = _systems.GetValue(sys.GetValue(i)) as IEnumerable;
                        if (list0 == null) continue;
                        bool found = false;
                        foreach (var b0 in list0) if (ReferenceEquals(b0, engine)) { found = true; break; }
                        if (found) { ship.EngineSec = i; if (SysPriority.Mark(sys.GetValue(i), engine)) Trace("Auto DC: the engine is out of order, its repair is a priority (section " + i + ")"); break; }
                    }
                }
                bool engineNow = false, prioNow = false, engineFire = false;
                int fireAll = 0;   // bits: the engine's sections that burn - all their teams fight the fire (DcControl)
                // the engine lies in several sections (the game puts a system into every section its model spans) and a fire in any of them damages
                // it (`UpdateFireDamage`): while one of them burns, repairing the engine is wasted - those fires come first. Only while its repair is
                // a priority (the mark on its section, which can be taken off by hand)
                if (engine != null && ship.EngineSec >= 0 && ship.EngineSec < count && SysPriority.IsMarked(sys.GetValue(ship.EngineSec), engine))
                {
                    int engIn = 0, engFire = 0;
                    for (int i = 0; i < count; i++)
                    {
                        var list1 = _systems.GetValue(sys.GetValue(i)) as IEnumerable;
                        bool has = false;
                        if (list1 != null) foreach (var b1 in list1) if (ReferenceEquals(b1, engine)) { has = true; break; }
                        if (!has) continue;
                        engIn |= 1 << i;
                        object sc1 = sys.GetValue(i);
                        if (F(_severity.GetValue(sc1, null)) > 0f || F(_grow.GetValue(sc1)) > 0f) engFire |= 1 << i;
                    }
                    // the repair goes where the teams are: any section of the engine repairs it (its own "!" on the engine), so once the fires at the engine
                    // are out the teams do not walk to the section that was marked first - the mark moves to the not flooded section with the most teams
                    if (engFire == 0)
                    {
                        int cur = ship.EngineSec, best = SectionEffects.Locked(comp, cur) ? -1 : cur;
                        for (int i = 0; i < count; i++)
                            if ((engIn & (1 << i)) != 0 && !SectionEffects.Locked(comp, i) && i < counts.Length &&
                                (best < 0 || counts[i] > (best < counts.Length ? counts[best] : 0))) best = i;
                        if (best >= 0 && best != cur && SysPriority.Move(sys.GetValue(cur), sys.GetValue(best), engine))
                        {
                            ship.EngineSec = best;
                            Trace("Auto DC: the engine repair moves from section " + cur + " to section " + best + " (it holds the engine too; " + (best < counts.Length ? counts[best] : 0) + " team(s) there)");
                        }
                    }
                    if (!SectionEffects.Locked(comp, ship.EngineSec) && SysPriority.Pending(sys.GetValue(ship.EngineSec))) fireAll = engFire;
                }
                int avail = 0;
                var holds = new bool[count];   // fire / flooding work that keeps its teams there
                var work = new bool[count];
                var wet = new bool[count];     // a hole or water in the section
                var burning = new bool[count]; // a fire in the section (the first team there fights it)
                var pumpOnly = new bool[count]; // only water to pump out there (no hole, no fire)
                var smallPumps = new List<Need>();   // small pumping, for rule 7 only
                int heelMask = 0;                    // sections whose pumping would worsen the list / trim: no pumping job there (the other work of the section counts)
                var minorWork = new int[count];      // 1 = light system damage, 2 = hull under HullMin that no team is there for: a job only when nothing else is left
                var released = new bool[count]; // critical heel: its pumping teams are set free (may leave the section empty)
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
                    burning[i] = hasFire; wet[i] = hasLeak || hasWater; pumpOnly[i] = hasWater && !hasLeak && !hasFire;
                    bool fireHeld = hasLeak && SectionEffects.LeaksHeldByFire(comp, i);
                    if (hasLeak && !fireHeld) avail |= AvLeak;
                    if (grow > 0f) avail |= AvFireRate;
                    if (hasFire) avail |= AvFire;
                    if (hasWater) avail |= AvPump;
                    if (hull > 0.01f) avail |= AvHull;   // hull that can be repaired
                    if (HullHoldsWater(comp, i)) avail |= AvFloodHull;
                    // small pumping (less than SmallWater in the section): no job of its own - no team is sent for it and its water does not hold the teams
                    // (they stay while nobody needs them and pump there by the order when they have nothing better); free teams may still come for it
                    // (rule 7, after every other job, where nobody is)
                    bool bigWater = hasWater && water >= SmallWater;
                    bool heelBlock = false;
                    if (bigWater)
                    {
                        float pg = DcControl.PumpGain(comp, i);
                        if (pg < -PumpTolerance)
                        {
                            heelBlock = true; bigWater = false; heelMask |= 1 << i;   // pumping there would worsen the list / trim (the teams refuse it): no job, no hold
                            if ((ship.HeelStop & (1 << i)) == 0) Trace("Auto DC: pumping in section " + i + " would worsen the list / trim (" + pg.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "): no pumping there");
                        }
                    }
                    if (hasWater && !bigWater && !heelBlock) smallPumps.Add(new Need { Section = i, Rank = 1000, Size = water, Want = 1, Kind = Pump, Small = true });
                    holds[i] = mode == DcControl.ModeFire ? hasFire : mode == DcControl.ModeFlooding ? hasLeak || bigWater : hasLeak || hasFire || bigWater;

                    bool systems = false;
                    bool locked = SectionEffects.Locked(comp, i);
                    bool prio = !locked && SysPriority.Pending(sc);   // a system marked for priority repair: its own job, the first one
                    var list = locked ? null : _systems.GetValue(sc) as IEnumerable;   // a flooded section's systems cannot be repaired
                    bool minorSys = false;
                    if (list != null)
                        foreach (var b in list)
                        {
                            try
                            {
                                int cur = Convert.ToInt32(_curDmg.GetValue(b, null));
                                bool destroyed = cur == 4;   // DamageType.Destroyed
                                bool rep = (bool)_repairable.GetValue(b, null) && (!destroyed || (bool)_always.GetValue(b)) && Convert.ToInt32(_bigDmg.GetValue(b, null)) <= cur;
                                if (rep && cur >= 1) minorSys = true;
                                if (rep && cur >= (onSite ? 1 : 2)) { systems = true; break; }   // from medium damage; light too where a team works already
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

                    if (systems) avail |= AvSystems;
                    for (int r = 0; r < order.Length; r++)
                    {
                        int kind = order[r];
                        bool has; float size;
                        switch (kind)
                        {
                            case Leak: has = hasLeak && !fireHeld; size = leak; break;   // holes the fire keeps open wait for the fire to be out
                            case FireRate: has = grow > 0f; size = grow; break;
                            case Fire: has = hasFire; size = fire; break;
                            case Pump:
                                has = hasWater && water >= SmallWater && !heelBlock; size = water;   // small pumping: no job (below), the next kind of work of the section counts
                                break;
                            case Hull: has = hull >= HullMin || (onSite && hull > 0.01f); size = hull; break;
                            case FloodHull: has = HullHoldsWater(comp, i); size = hull; if (has) kind = Hull; break;   // Flooded Structure: the hull keeps water in - welding it lets it out
                            default: has = systems; size = 1f; break;
                        }
                        if (kind == Systems && prio) continue;   // the priority job below takes the place of the systems job
                        if (!has) continue;
                        work[i] = true;
                        // plugging holes or pumping out in a section that also burns: two teams, one of them fights the fire (DcControl)
                        int want = 1 + extra;
                        if ((kind == Leak || kind == Pump) && hasFire) want = Math.Max(want, 2);   // a burning section: one fights the fire, one works (Flooding first too: no fixed pair without a fire, the teams come in the order of the queue)
                        needs.Add(new Need { Section = i, Rank = r * 2, Size = size, Want = want, Urgent = urgent, ByRate = kind == FireRate || kind == Leak, Kind = kind });
                        break;
                    }
                    if ((fireAll & (1 << i)) != 0)
                    {
                        // a section of the engine burns: the fire keeps damaging the engine (every system of a burning section), so repairing it now is
                        // wasted - the fire there becomes a critical job instead (rank and teams of the engine repair); the repair follows once all are out
                        engineNow = true; engineFire = true;
                        work[i] = true;
                        needs.RemoveAll(x => x.Section == i && (x.Kind == Fire || x.Kind == FireRate));
                        needs.Add(new Need { Section = i, Rank = -4, Size = fire, Want = Math.Max(3, 1 + extra), Urgent = true, Kind = Fire, Crit = true });
                    }
                    else if (prio && i == ship.EngineSec && fireAll != 0) { engineNow = true; work[i] = true; }   // the repair waits for the fires at the engine
                    else if (prio)
                    {
                        if (i == ship.EngineSec) engineNow = true; else prioNow = true;
                        work[i] = true;
                        needs.Add(new Need { Section = i, Rank = i == ship.EngineSec ? -4 : -2, Size = 1f, Want = 3, Urgent = true, Kind = PrioSys, Crit = i == ship.EngineSec });   // the engine: as critical as counterflooding (-4), above the heel bottleneck (-3)
                    }
                    if (work[i]) ship.LastWork = now;
                    if (!locked && !work[i]) minorWork[i] = minorSys ? 1 : hull > 0.01f ? 2 : 0;
                }
                // nothing else to do anywhere: the small repairs that were not worth a walk (light damage, hull under HullMin) are finished now, the lowest job
                if (needs.Count == 0 && mode != DcControl.ModeFire)
                    for (int i = 0; i < count; i++)
                        if (minorWork[i] != 0)
                        {
                            work[i] = true; ship.LastWork = now;
                            needs.Add(new Need { Section = i, Rank = order.Length * 2 + 2, Size = 0.1f, Want = 1, Kind = minorWork[i] == 1 ? Systems : Hull });
                        }
                ship.HeelStop = heelMask;
                ship.Primed = true;
                if (fireAll != ship.EngineFireMask)
                {
                    var secs = new List<string>();
                    for (int i = 0; i < count; i++) if ((fireAll & (1 << i)) != 0) secs.Add(i.ToString());
                    Trace(fireAll != 0 ? "Auto DC: fire at the engine in section(s) " + string.Join(", ", secs.ToArray()) + " - the fire first (it keeps damaging the engine), all their teams fight it; the repair waits"
                                       : "Auto DC: the fires at the engine are out - its repair goes on");
                }
                ship.EngineNow = engineNow; ship.PrioNow = prioNow; ship.EngineFire = engineFire; ship.EngineFireMask = fireAll;
                ship.Avail = avail;

                // the engine is out (and its repair is a priority): every team works at NoEngineTeamEffectiveness on everything but the systems, so nothing
                // but the engine pays - the fires at the engine first, then its repair. All the other work is dropped and its teams are free; the jobs at
                // the engine take every team (shared out below, once the number of teams is known). Counterflooding at a critical heel keeps its one team
                bool engineMode = false;
                if (engineNow)
                {
                    int engMask = fireAll;
                    foreach (var n in needs) if (n.Kind == PrioSys && n.Section == ship.EngineSec) engMask |= 1 << n.Section;
                    if (engMask != 0)
                    {
                        engineMode = true;
                        needs.RemoveAll(n => !((n.Kind == PrioSys && n.Section == ship.EngineSec) || (n.Kind == Fire && (fireAll & (1 << n.Section)) != 0)));
                        foreach (var n in needs) { n.All = true; n.Crit = true; n.Rank = -4; }
                        for (int j = 0; j < count; j++) if ((engMask & (1 << j)) == 0) { holds[j] = false; released[j] = true; }
                        if (!ship.EngineAll) Trace("Auto DC: the engine is out - every team goes to " + (fireAll != 0 ? "the fires at the engine" : "the engine repair (section " + ship.EngineSec + ")") + ", the other work waits");
                    }
                }
                if (!engineMode && ship.EngineAll) Trace("Auto DC: the engine works again (or its repair is no priority) - the teams go back to the other work");
                ship.EngineAll = engineMode;

                // Flooding first: a team that fights a fire alone is not held by the section's holes or water - it can be regrouped to the sections that leak most
                if (mode == DcControl.ModeFlooding)
                    for (int j = 0; j < count; j++)
                        if (holds[j] && burning[j] && j < counts.Length && counts[j] == 1 && DcControl.CounterAct(comp, j) == 0) holds[j] = false;

                // counterflooding: a dangerous heel is a job of its own (one team), in the section where letting water in or pumping it out helps most;
                // it ranks just before the flooding (pumping) job - and first when the heel is critical. The team there holds its place while it is at it.
                // on: the heel ratio reaches HeelOn AND some section can level the ship; off: the ratio is down to HeelOff, no hole leaks, or for
                // CounterIdleOff s nothing can be levelled (the edges are even - the ratio is held up by the ship sitting deep, which only pumping helps)
                // "something to level" = the best section takes off at least CounterUseful of the red zone (a few tenths of a degree that the team's
                // narrower dead zone keeps alive do not hold the mode on). The section is kept while it is not clearly worse than the best (x1.5): no flip-flop
                float s0 = 0f; int sec0 = -1;
                {
                    bool danger = DcControl.AutoCounterfloodOn && DcControl.HeelDanger(comp, ship.CounterOn, anyLeak);
                    if (danger)
                    {
                        sec0 = DcControl.CounterSection(comp, out s0);
                        if (ship.CounterKeep >= 0 && ship.CounterKeep < count && sec0 != ship.CounterKeep)
                        {
                            float ks = DcControl.CounterScore(comp, ship.CounterKeep);
                            if (ks > 0f && s0 < 1.5f * ks) { sec0 = ship.CounterKeep; s0 = ks; }
                        }
                    }
                    bool useful = sec0 >= 0 && s0 >= CounterUseful;
                    bool was = ship.CounterOn;
                    if (!danger) { ship.CounterOn = false; ship.CounterIdle = 0; }
                    else if (!was) { ship.CounterOn = useful; ship.CounterIdle = 0; }
                    else if (useful) ship.CounterIdle = 0;
                    else
                    {
                        if (ship.CounterIdle <= 0) ship.CounterIdle = now;
                        if (now - ship.CounterIdle >= CounterIdleOff)
                        {
                            ship.CounterOn = false; ship.CounterIdle = 0;
                            Trace("Auto DC: counterflooding off - it takes off less than " + CounterUseful.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " of the red zone for " + CounterIdleOff + " s (best section " + sec0 + " score " + s0.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "; " + DcControl.HeelInfo(comp) + ")");
                        }
                    }
                }
                ship.CounterCrit = ship.CounterOn;   // water coming in through holes with a dangerous heel is critical as such: counterflooding goes first and takes a team off other work
                if (ship.CounterOn) ship.Avail |= AvCounter;
                if (anyLeak && now >= ship.NextHeelDiag)
                {
                    ship.NextHeelDiag = now + 10.0;
                    float dScore; int dSec = DcControl.CounterSection(comp, out dScore);
                    Trace("Auto DC counterflooding: " + DcControl.HeelInfo(comp) + ", holes leak, on " + ship.CounterOn + ", critical " + ship.CounterCrit + ", best section " + dSec + " score " + dScore.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + (DcControl.AutoCounterfloodOn ? "" : ", option off"));
                }
                int counterSec = -1;
                if (!ship.CounterOn) ship.CounterKeep = -1;
                else
                {
                    float cs = s0; int ci = sec0;
                    ship.CounterKeep = ci;
                    if (ci >= 0 && ci < count)
                    {
                        counterSec = ci;
                        int pumpAt = Array.IndexOf(order, Pump);
                        needs.Add(new Need { Section = ci, Rank = ship.CounterCrit ? -4 : pumpAt * 2 - 1, Size = cs, Want = 1, Urgent = true, Kind = Counter, Crit = ship.CounterCrit });
                        work[ci] = true; ship.LastWork = now;
                        if (DcControl.CounterAct(comp, ci) != 0) holds[ci] = true;
                    }
                }
                // critical counterflooding: the flooding work (holes, pumping) is re-ranked by what it does to the list / trim - the sections where it levels
                // the ship go first, the ones where it makes the heel worse go behind; teams that only pump in such a section are set free to go to the better ones
                if (ship.CounterCrit)
                {
                    float bestGain = 0f;
                    foreach (var n in needs)
                    {
                        if (n.Kind != Leak && n.Kind != Pump) continue;
                        n.Heel = true;
                        n.Gain = n.Kind == Pump ? DcControl.PumpGain(comp, n.Section) : DcControl.LeakGain(comp, n.Section);
                        if (n.Gain < HeelGainMin) n.Rank++;   // behind the jobs of its own kind that help
                        else bestGain = Math.Max(bestGain, n.Gain);
                    }
                    // the bottleneck is a critical job as well: pumping where it levels the ship most (the best gain, the section lacks teams) may take teams off other work,
                    // also off a pair in a burning section with a hole (the fire fighter stays), like counterflooding itself
                    Need bn = null;
                    foreach (var n in needs) if (n.Heel && n.Kind == Pump && n.Gain >= HeelGainMin && n.Section != counterSec && (bn == null || n.Gain > bn.Gain)) bn = n;
                    if (bn != null)
                    {
                        bn.Crit = true; bn.Rank = -3;
                        if (ship.Bottle != bn.Section) Trace("Auto DC: critical heel, pumping in section " + bn.Section + " is critical (" + bn.Gain.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "): it may take teams off other work");
                        ship.Bottle = bn.Section;
                    }
                    else ship.Bottle = -1;
                    // the bottleneck first, at once: teams that only pump in a section where that does much less for the heel than the best job (half or less,
                    // or it makes the heel worse) are set free - they go to the better sections even though they are at work
                    // pumping that does not help at all (the water is only on the side / end whose pumping would make the heel worse - the teams' PumpSafe
                    // refuses it and they fell to the systems / hull) frees its teams too, also when no other flooding job helps the heel (bestGain 0)
                    {
                        foreach (var n in needs)
                            // the counterflooding section too: its Counter need keeps the one team that counterfloods, the others are set free
                            if (n.Heel && n.Kind == Pump && !n.Crit && (n.Gain < HeelGainMin || n.Gain < 0.5f * bestGain) && pumpOnly[n.Section] && holds[n.Section] && !burning[n.Section] &&
                                (n.Section == counterSec || DcControl.CounterAct(comp, n.Section) == 0))
                            {
                                holds[n.Section] = false; released[n.Section] = true;
                                if (!ship.Released[n.Section]) Trace("Auto DC: counterflooding is critical, pumping in section " + n.Section + " does less for the heel (" + n.Gain.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ") than the best job (" + bestGain.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "): its teams are set free");
                            }
                    }
                    // pumping that makes the heel worse is refused by the teams (PumpSafe): such a job is no job at all - it would only keep a team there,
                    // which then repairs systems (and in the counterflooding section it took the place of a second counterflooding team)
                    needs.RemoveAll(n => n.Heel && n.Kind == Pump && !n.Crit && n.Gain < -PumpTolerance);
                }
                for (int j = 0; j < count && j < ship.Released.Length; j++) ship.Released[j] = released[j];
                // the teams that counterflood already (more than one: free ones joined it, rule 7) are its plan, not a surplus that the gap filling may take
                // to work of a lower rank: the Counter need wants them all (what the other work of its section wants is left to it)
                if (counterSec >= 0 && counterSec < count && counterSec < counts.Length)
                {
                    int other = 0;
                    foreach (var n in needs) if (n.Section == counterSec && n.Kind != Counter) other += Math.Max(0, n.Want);
                    foreach (var n in needs) if (n.Kind == Counter) n.Want = Math.Max(1, counts[counterSec] - other);
                }
                var sysQuota = new int[5];
                DcControl.SetPlan(comp, counterSec >= 0, counterSec, sysQuota, ship.CounterCrit, fireAll);
                if (needs.Count == 0) { DcControl.SetPlan(comp, false, -1, null); return false; }

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
                    if (a.Heel && b.Heel && a.Gain != b.Gain) return b.Gain.CompareTo(a.Gain);   // critical heel: what levels the ship most first
                    if (a.ByRate && b.ByRate) return b.Size.CompareTo(a.Size);   // the fastest growing first
                    bool sa = here[a.Section] + coming[a.Section] > 0, sb = here[b.Section] + coming[b.Section] > 0;
                    if (sa != sb) return sa ? -1 : 1;
                    return b.Size.CompareTo(a.Size);
                });
                // the engine is out: its jobs share every team that is not counterflooding (each one at least what it asked)
                {
                    int nAll = 0;
                    foreach (var n in needs) if (n.All) nAll++;
                    if (nAll > 0)
                    {
                        int rest = usable - (counterSec >= 0 ? 1 : 0), k = 0;
                        foreach (var n in needs) if (n.All) { int share = rest / nAll + (k < rest % nAll ? 1 : 0); k++; n.Want = Math.Max(n.Want, share); }
                    }
                }
                // water only (no hole, no fire) does not hold its teams against work of a higher rank that lacks teams (a fire while the order puts fires
                // before water): its teams go, all of them if that work needs them (the order decides; once that work has its teams, the water holds again).
                // Not the counterflooding section
                {
                    var bestRk = new int[count];
                    for (int j = 0; j < count; j++) bestRk[j] = int.MaxValue;
                    foreach (var n in needs) if (n.Rank < bestRk[n.Section]) bestRk[n.Section] = n.Rank;
                    for (int j = 0; j < count; j++)
                    {
                        if (!holds[j] || !pumpOnly[j] || j == counterSec || here[j] < 1) continue;
                        Need why = null;
                        foreach (var n in needs)
                            if (n.Section != j && n.Rank < bestRk[j] && n.Kind != Pump && here[n.Section] + coming[n.Section] < n.Want) { why = n; break; }
                        if (why == null) continue;
                        holds[j] = false; released[j] = true;   // released: the gap filling does not keep "one team for the work" there
                        if ((ship.PumpYield & (1 << j)) == 0) Trace("Auto DC: pumping in section " + j + " gives way to work of a higher rank that lacks teams (section " + why.Section + ", " + KindName(why.Kind) + "): its teams may go");
                        ship.PumpYield |= 1 << j;
                    }
                    for (int j = 0; j < count; j++) if (holds[j] || !pumpOnly[j]) ship.PumpYield &= ~(1 << j);
                }
                var target = new int[count];
                int left = usable;
                // the teams held by fire / flooding are there already and stay: they count first, against the wants of their section
                for (int i = 0; i < count; i++) if (holds[i]) { target[i] = here[i]; left -= here[i]; }

                // a section with a fire: its first job carries a fire fighter. Fire jobs have one already; the others ask one more
                // (leak, pumping, hull, systems: at least two teams); the next fire jobs of that section then ask one team less
                var fireTaken = new bool[count];
                foreach (var n in needs)
                {
                    if (!burning[n.Section]) continue;
                    bool fireJob = n.Kind == Fire || n.Kind == FireRate;
                    if (!fireTaken[n.Section]) { fireTaken[n.Section] = true; if (!fireJob) { if (n.Kind == Counter || n.Kind == PrioSys) { n.Want++; n.Bonus = 1; } else n.Want = Math.Max(n.Want, 2); } }
                    else if (fireJob) n.Want--;
                }
                // the counterflooding section: what its other work wants (the rest of its teams counterflood)
                int otherWant = 0;
                if (counterSec >= 0) foreach (var n in needs) if (n.Section == counterSec && n.Kind != Counter) otherWant += Math.Max(0, n.Want);
                // what the held teams already cover
                var cover = (int[])target.Clone();
                foreach (var n in needs) if (n.Kind != PrioSys && cover[n.Section] > 0) { int d = Math.Min(cover[n.Section], n.Want); n.Want -= d; cover[n.Section] -= d; }
                foreach (var n in needs)
                    if (n.Kind == PrioSys && cover[n.Section] > 0)
                    {
                        // the teams already there that cover the priority repair ARE its teams: they go into the quota (else, once the teams have
                        // arrived, the quota fell to 0 and they went back to the fire / the holes - the engine was never repaired)
                        int d = Math.Min(cover[n.Section], n.Want); n.Want -= d; cover[n.Section] -= d;
                        int b = Math.Min(d, n.Bonus); n.Bonus -= b;   // the fire fighter that came with it
                        if (n.Section < 5) sysQuota[n.Section] += d - b;
                    }

                // two jobs are critical and may take a team off fire / flooding work that goes on: counterflooding at a critical heel and the repair of an engine
                // that is out of order. When the free teams do not cover them (one team each, after what the teams held there already cover), the sections whose
                // work ranks below take one team each (`donor[]`): the lowest ranked first, then the section with more teams
                var donor = new bool[count];
                int critRank = int.MinValue, through = 0;
                var critSec = new bool[count];
                foreach (var n in needs) if (n.Crit && n.Want > 0) { through += n.Kind == PrioSys && !n.All ? 1 + n.Bonus : n.Want; critRank = Math.Max(critRank, n.Rank); critSec[n.Section] = true; }
                if (through > 0)
                {
                    int deficit = through - Math.Max(0, left);
                    var bestR = new int[count];
                    for (int i = 0; i < count; i++) bestR[i] = int.MaxValue;
                    foreach (var n in needs) if (n.Rank < bestR[n.Section]) bestR[n.Section] = n.Rank;
                    for (int pass = 0; pass < 2; pass++)   // first pass: a burning section's pair (fire fighter + worker) is not broken up
                        while (deficit > 0)
                        {
                            int dj = -1;
                            for (int j = 0; j < count; j++)
                                if (!critSec[j] && holds[j] && target[j] >= 1 && bestR[j] > critRank && DcControl.CounterAct(comp, j) == 0 &&
                                    (pass == 1 || !burning[j] || target[j] != 2) &&
                                    (dj < 0 || bestR[j] > bestR[dj] || (bestR[j] == bestR[dj] && target[j] > target[dj]))) dj = j;
                            if (dj < 0) break;
                            target[dj]--; left++; donor[dj] = true; deficit--;
                        }
                }

                // the critical jobs take their teams first (best rank first), before the free teams are shared: the half-and-half rule of DcShare does not split them off
                foreach (var n in needs)
                {
                    if (!n.Crit || n.Want <= 0 || left <= 0) continue;
                    int g = Math.Min(left, n.Kind == PrioSys && !n.All ? 1 + n.Bonus : n.Want);
                    target[n.Section] += g; left -= g; n.Want -= g;
                    if (n.Kind == PrioSys && n.Section < 5) sysQuota[n.Section] += Math.Max(0, g - n.Bonus);
                    n.Bonus = Math.Max(0, n.Bonus - g);
                }

                // share the free teams (DcShare): half to each of the two jobs of the highest priority, the rest down the list
                bool otherWork = false;
                for (int i = 0; i < count; i++) if (holds[i]) otherWork = true;
                var jobNeeds = new List<Need>(); var jobs = new List<DcShare.Job>();
                foreach (var n in needs)
                {
                    if (n.Kind == Leak || n.Kind == Pump || n.Kind == Fire || n.Kind == FireRate || n.Kind == Counter) otherWork = true;
                    if (n.Want <= 0) continue;
                    jobNeeds.Add(n); jobs.Add(new DcShare.Job { Want = n.Want, Bonus = n.Bonus, Systems = n.Kind == PrioSys });
                }
                var alloc = DcShare.Run(jobs.ToArray(), Math.Max(0, left), Math.Max(teams.Count, usable), otherWork);
                for (int k = 0; k < jobNeeds.Count; k++)
                {
                    var n = jobNeeds[k];
                    target[n.Section] += alloc[k];
                    if (n.Kind == PrioSys && n.Section < 5) sysQuota[n.Section] += Math.Max(0, alloc[k] - n.Bonus);
                }
                // counterflooding: the planned team, and the teams beyond the plan of its section (free ones added by rule 7) counterflood too
                int counterN = counterSec >= 0 && counterSec < count ? Math.Max(1, here[counterSec] - otherWant) : 1;
                DcControl.SetPlan(comp, counterSec >= 0, counterSec, sysQuota, ship.CounterCrit, fireAll, counterN);

                // trace: who is where and what the plan is (at the start, on every change, and every 30 s): section = teams here (+ walking there) -> target, L(eak) W(ater) F(ire), H(eld)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < count; i++)
                        sb.Append(i).Append(": ").Append(here[i]).Append(coming[i] > 0 ? "+" + coming[i] : "").Append("->").Append(target[i])
                          .Append(' ').Append(Math.Max(F(_floodRate.GetValue(port.GetValue(i))), F(_floodRate.GetValue(star.GetValue(i)))) > 0f ? "L" : "").Append(wet[i] ? "W" : "").Append(burning[i] ? "F" : "").Append(holds[i] ? "H" : "").Append(counterSec == i ? "C" : "").Append(" | ");
                    sb.Append("pool ").Append(pool).Append(", free ").Append(Math.Max(0, left)).Append(", mode ").Append(mode).Append(ship.CounterCrit ? ", counter critical" : ship.CounterOn ? ", counter" : "");
                    string snap = sb.ToString();
                    if (snap != ship.Snap || now >= ship.NextSnap)
                    {
                        ship.Snap = snap; ship.NextSnap = now + 30.0;
                        // what the teams in each section did last tick (I idle, L plug, F fire, P pump, H hull, S systems, C counterflood) and, at a critical heel, the pump gains
                        var did = new System.Text.StringBuilder(" | doing");
                        var tl = new List<DcTeams.Team>();
                        for (int i = 0; i < count; i++)
                        {
                            did.Append(' ').Append(i).Append(':');
                            if (DcTeams.SectionTeams(comp, i, tl)) foreach (var t in tl) did.Append("-ILFPHSC"[Math.Max(0, Math.Min(7, t.Doing))]);
                        }
                        if (ship.CounterCrit)
                        {
                            did.Append(" | pump gain");
                            foreach (var n in needs) if (n.Kind == Pump && n.Heel) did.Append(' ').Append(n.Section).Append('=').Append(n.Gain.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                        }
                        Trace("Auto DC teams: " + snap + did);
                    }
                }

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
                            // a section with work keeps one team - unless its work ranks below this job (systems or structure
                            // against water or a fire elsewhere: the order decides, the team goes); a released section keeps no team
                            int rj = SecRank(needs, j);
                            bool keepOne = work[j] && !donor[j] && !released[j] && rj <= n.Rank;
                            if (!n.Crit && here[j] <= 1 && LeakRisk(comp, j)) keepOne = true;   // hull under 19%: an empty section opens its leak again - its last team stays
                            if (j == i || (holds[j] && !donor[j]) || here[j] <= Math.Max(target[j], keepOne ? 1 : 0)) continue;
                            float tt = DcTeams.TransferTime(comp, j, i);
                            if (tt < best) { best = tt; from = j; }
                        }
                        // nobody free: the first team for fire / flooding may come off hull or systems work - work of a lower rank only
                        // (not a team its own section's better work plans for, e.g. the counterflooding team of a section whose water was let go)
                        if (from == -2 && first && n.Urgent)
                            for (int j = 0; j < count; j++)
                            {
                                if (j == i || holds[j] || here[j] < 1 || minStay[j] < MinStay) continue;
                                if (!n.Crit && here[j] <= 1 && LeakRisk(comp, j)) continue;
                                if (here[j] <= target[j] && SecRank(needs, j) <= n.Rank) continue;
                                float tt = DcTeams.TransferTime(comp, j, i);
                                if (tt < best) { best = tt; from = j; }
                            }
                        // Flooding first: teams that fight a fire alone go (the nearest first) to a section with holes or water, as many as it lacks
                        if (from == -2 && mode == DcControl.ModeFlooding && (n.Kind == Leak || n.Kind == Pump || (n.Kind == Counter && n.Crit)))
                        {
                            // counterflooding may rank above the flooding (critical heel): it is served first (sorted), with one team
                            var cand = new List<int>();
                            for (int j = 0; j < count; j++) if (j != i && j != counterSec && burning[j] && !holds[j] && here[j] == 1 && target[j] < here[j] && minStay[j] >= 5f) cand.Add(j);   // alone at a fire, nothing planned for it
                            int gapNow = target[i] - (here[i] + coming[i]);
                            int pairN = Math.Min(n.Kind == Counter ? 1 : gapNow, cand.Count);   // as many as the section lacks
                            if (now >= ship.NextDiag)
                            {
                                ship.NextDiag = now + 10.0;
                                Trace("Auto DC (Flooding): section " + i + " lacks " + gapNow + " team(s), teams that fight a fire alone: " + cand.Count + ", reserve " + pool);
                            }
                            if (pairN >= 1)
                            {
                                cand.Sort((x, y) => DcTeams.TransferTime(comp, x, i).CompareTo(DcTeams.TransferTime(comp, y, i)));
                                int moved = 0;
                                for (int c = 0; c < pairN; c++)
                                    if (DcTeams.MoveToSection(comp, cand[c], i, true)) { here[cand[c]]--; target[cand[c]] = 0; coming[i]++; sent++; moved++; Trace("Auto DC: a team for flooding in section " + i + " from the fire in section " + cand[c]); }
                                if (moved > 0) continue;
                            }
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
                //    (from the reserve, a section with no work at all, or a section whose work ranks below and does not hold its teams - e.g. pumping
                //    that does nothing for a critical heel), to the section with work that has the fewest teams (equal: the better ranked work);
                //    counterflooding takes such teams too (all teams beyond its section's plan counterflood), up to ExtraMax per section
                // relief: a rested (fresh) team from the reserve goes to a section with work whose teams are tired and that has no fresh one yet;
                // once it is there, the tired team leaves for rest (DcTeams.RestPull)
                if (sent == 0 && pool > 0 && now >= ship.NextRelief && DcTeams.PoolFresh(comp) > 0)
                {
                    int rs = -1;
                    for (int j = 0; j < count; j++)
                        if (work[j] && here[j] + coming[j] < ExtraMax && DcTeams.NeedsRelief(comp, j) && (rs < 0 || here[j] > here[rs])) rs = j;
                    if (rs >= 0)
                    {
                        DcTeams.Send(comp, rs); pool--; sent++; coming[rs]++; ship.NextRelief = now + ExtraEvery;
                        Trace("Auto DC: a fresh team for section " + rs + " from the reserve (relief of tired teams)");
                    }
                }
                bool lacking = false;   // some section still waits for teams of its plan: rule 7 does not add extras then
                for (int j = 0; j < count; j++) if (here[j] + coming[j] < target[j]) lacking = true;
                if (sent == 0 && !lacking && now >= ship.NextExtra)
                {
                    bool extraBlocked = otherWork;
                    // candidates: fewest teams first, then the order of 'needs' (rank); the first one that some team can come to gets it
                    var cands = new List<Need>();
                    foreach (var n in needs)
                    {
                        int have = here[n.Section] + coming[n.Section];
                        if (have >= ExtraMax) continue;
                        if (n.Kind == PrioSys && extraBlocked) continue;   // the third of the teams is for systems while there is fire or flooding work
                        if (cands.Exists(c => c.Section == n.Section)) continue;
                        cands.Add(n);
                    }
                    var order0 = new List<Need>(needs);
                    cands.Sort((a, b) =>
                    {
                        int ha = here[a.Section] + coming[a.Section], hb = here[b.Section] + coming[b.Section];
                        return ha != hb ? ha.CompareTo(hb) : order0.IndexOf(a).CompareTo(order0.IndexOf(b));
                    });
                    // small pumping last, only where nobody is
                    foreach (var n in smallPumps)
                        if (here[n.Section] + coming[n.Section] == 0 && !cands.Exists(c => c.Section == n.Section)) cands.Add(n);
                    var bestRj = new int[count];
                    for (int j = 0; j < count; j++) bestRj[j] = int.MaxValue;
                    foreach (var n in needs) if (n.Rank < bestRj[n.Section]) bestRj[n.Section] = n.Rank;
                    foreach (var pick in cands)
                    {
                        int i = pick.Section, from = -2; float best = float.MaxValue;
                        if (pool > 0) { best = DcTeams.TransferTime(comp, -1, i); from = -1; }
                        for (int j = 0; j < count; j++)
                        {
                            if (j == i || holds[j] || here[j] < 1) continue;
                            if (work[j] && !(bestRj[j] > pick.Rank && here[j] > target[j])) continue;   // only a team beyond its section's plan (the plan's own teams stay: no back and forth with the gap filling)
                            float tt = DcTeams.TransferTime(comp, j, i);
                            if (tt < best) { best = tt; from = j; }
                        }
                        if (from == -2) continue;
                        if (from == -1) { DcTeams.Send(comp, i); Trace("Auto DC: one more team for section " + i + " (" + KindName(pick.Kind) + ") from the reserve"); ship.NextExtra = now + ExtraEvery; }
                        else if (DcTeams.MoveToSection(comp, from, i, true))
                        {
                            // what the donor section looked like: its work, its best job in the list (int.MaxValue = none yet), its plan - tells why a team with work was free
                            Trace("Auto DC: one more team for section " + i + " (" + KindName(pick.Kind) + ", rank " + pick.Rank + ") from section " + from +
                                  " [donor: work " + work[from] + ", best job rank " + (bestRj[from] == int.MaxValue ? "none" : bestRj[from].ToString()) + ", teams " + here[from] + ", plan " + target[from] + "]");
                            ship.NextExtra = now + ExtraEvery;
                        }
                        break;
                    }
                }
                return false;
            }
            catch (Exception e) { Trace("DcAuto.Allocate: " + e.Message); return true; }
        }

        static float Pct(object delta, object max) { float m = F(max); return m > 0f ? F(delta) / m * 100f : 0f; }
    }
}
