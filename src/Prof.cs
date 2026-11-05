using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Diagnostics ([General] ProfilePerformance): the time the mod's hooks take. Every hook adds its time (Stopwatch) and its calls to a slot;
    /// every 10 seconds of real time a summary goes to the trace: frames and physics steps in that time, and per slot the calls (per physics
    /// step), the total time in ms and in % of the real time, the mean per call. Slots marked '>' are parts of another slot (counted in it too).
    /// Off: one bool check per hook.
    /// </summary>
    static class Prof
    {
        public static bool On;
        public static Action<string> Trace = s => { };

        // slots: top-level hooks first, then their parts ('>')
        public const int ShipTick = 0, AttTick = 1, TeamsTick = 2, TrimTick = 3, SubTick = 4, SecTick = 5, CheckDc = 6, Repair = 7, PumpGuard = 8,
            Score = 9, Margins = 10, AllocHook = 11, AllocRun = 12, Cfv = 13, FloodMul = 14, FlameUp = 15, Wreck = 16, SinkCom = 17, Hits = 18,
            FireHooks = 19, SysRepair = 20, CoG = 21, Heavy = 22, MapUnit = 23, UiTick = 24, EnemyStatus = 25, DeckCorners = 26, LeakChecks = 27,
            Slots = 28;

        static readonly string[] Names =
        {
            "Compartments.OnFixedUpdate prefix (ship tick)", "> AttitudeSave.Tick", "> DcTeams.Tick", "> TrimFlooding.Tick", "> SubmergedFlooding.Tick", "> SectionEffects.Tick",
            "CheckDamageControl prefix (teams' work)", "> repair calls (game's Repair*)", "> PumpGuard (pumping by heel)", "> DcControl.Score (heel)", "SubmergedFlooding.Margins (deck edges)",
            "CheckDCTeamsAllocation prefix", "> DcAuto.Allocate full passes (time: in the allocation hook)", "_currentFloodingValue getter postfix", "FloodingMul (per compartment)", "FlameUpMul (per section)",
            "WreckDrag postfix", "Sink postfix (centre of mass)", "hit postfixes (reserve / hit log)", "fire hooks (state / ignite / add / fought / check)", "RepairSystems prefix (priorities / fix)",
            "TimeCompressionFix.CoG", "ContactIntel.Heavy (critical state)", "MapUnitViewModel.Update postfix (map icons)", "Damage Control view (frame)", "EnemyStatus.Tick (frame)",
            "DeckCorners.Tick (frame)", "leak / flooded-hull checks (LeaksHeldByFire, HullHoldsWater, LeakRisk)"
        };

        static readonly long[] Ticks = new long[Slots];
        static readonly long[] Calls = new long[Slots];
        static double _since = -1;
        static long _frames, _steps;
        static float _lastFixed = -1f;

        public static long Start() { return On ? Stopwatch.GetTimestamp() : 0L; }

        public static void Stop(int slot, long t0)
        {
            if (t0 == 0L) return;
            Ticks[slot] += Stopwatch.GetTimestamp() - t0;
            Calls[slot]++;
        }

        /// <summary>A call without its own time (its time is in the slot of the hook that makes it).</summary>
        public static void Count(int slot) { if (On) Calls[slot]++; }

        /// <summary>Once per physics step of a ship (the ship tick): counts the steps (Time.fixedTime changes once a step, for all ships).</summary>
        public static void Step()
        {
            if (!On) return;
            float ft = UnityEngine.Time.fixedTime;
            if (ft != _lastFixed) { _lastFixed = ft; _steps++; }
        }

        /// <summary>Once per frame (the plugin's Update): counts frames and writes the summary every 10 s of real time.</summary>
        public static void Frame()
        {
            if (!On) { _since = -1; return; }
            double now = UnityEngine.Time.realtimeSinceStartup;
            if (_since < 0) { Reset(now); return; }
            _frames++;
            double span = now - _since;
            if (span < 10.0) return;
            try { Report(span); } catch (Exception e) { Trace("Prof: " + e.Message); }
            Reset(now);
        }

        static void Reset(double now)
        {
            _since = now; _frames = 0; _steps = 0;
            Array.Clear(Ticks, 0, Slots); Array.Clear(Calls, 0, Slots);
        }

        static void Report(double span)
        {
            var ci = CultureInfo.InvariantCulture;
            double f = Stopwatch.Frequency;
            var sb = new StringBuilder();
            sb.Append("Perf ").Append(span.ToString("0.0", ci)).Append(" s: frames ").Append(_frames).Append(" (").Append((_frames / span).ToString("0", ci)).Append("/s), physics steps ")
              .Append(_steps).Append(" (").Append((_steps / span).ToString("0", ci)).Append("/s), time compression ").Append(UnityEngine.Time.timeScale.ToString("0.#", ci));
            double top = 0;
            var order = new List<int>();
            for (int k = 0; k < Slots; k++) if (Calls[k] > 0) order.Add(k);
            order.Sort((a, b) => Ticks[b].CompareTo(Ticks[a]));
            foreach (int k in order)
            {
                double ms = Ticks[k] * 1000.0 / f;
                if (!Names[k].StartsWith(">", StringComparison.Ordinal) && k != Margins && k != Heavy && k != LeakChecks) top += ms;
                sb.Append("\n    ").Append(Names[k]).Append(": calls ").Append(Calls[k]);
                if (_steps > 0) sb.Append(" (").Append(((double)Calls[k] / _steps).ToString("0.##", ci)).Append("/step)");
                sb.Append(", ").Append(ms.ToString("0.0", ci)).Append(" ms (").Append((ms / 10.0 / span).ToString("0.00", ci)).Append("% of real time), mean ")
                  .Append((ms * 1000.0 / Calls[k]).ToString("0.0", ci)).Append(" us");
            }
            sb.Append("\n    total of the top-level hooks: ").Append(top.ToString("0.0", ci)).Append(" ms (").Append((top / 10.0 / span).ToString("0.0", ci)).Append("% of real time)");
            Trace(sb.ToString());
        }
    }
}
