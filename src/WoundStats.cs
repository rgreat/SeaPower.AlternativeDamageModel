using System;
using System.Globalization;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Counters for the debug trace: where the hits on a ship go on their way to the damage control teams (the funnel), and a check of the wounds
    /// against the model's own statistics - the number of wounds rolled against the sum of the chances (expected), with the usual +-2 sigma band.
    /// A summary line is written after every <see cref="Every"/> rolls; nothing is kept or written with the trace off.
    /// </summary>
    static class WoundStats
    {
        public static Action<string> Trace = s => { };
        const int Every = 20;

        // the funnel (ReserveHits.Postfix, DcTeams.QueueHit)
        public static int Calls, Blocked, NotSection, NoShip, Overflow, Queued;
        // hits on a section (ApplySectionHit)
        public static int SecHits, SecNoTeam, SecRolls, SecWounds;
        public static double SecExp, SecVar, SecWoundSum;
        // hits on the reserve (ReserveHit)
        public static int ResHits, ResNoPool, ResChanceMiss, ResRolls, ResWounds;
        public static double ResExp, ResVar, ResWoundSum;
        // fire: the health the teams lost in burning sections (total, in full-health units)
        public static double FireLoss;
        static int _rolls, _printed;

        static string Verdict(int actual, double exp, double var)
        {
            if (SecRolls + ResRolls < 1 || var <= 0.0) return "";
            double sd = Math.Sqrt(var), diff = actual - exp;
            return Math.Abs(diff) <= 2.0 * sd ? " ok" : diff < 0 ? " LOW" : " HIGH";
        }

        /// <summary>One roll was made (odds = the chance, wound = the strength, 0 when it missed); writes a summary every Every rolls.</summary>
        public static void Rolled(bool reserve, double odds, float wound)
        {
            double o = Math.Min(1.0, odds);
            if (reserve) { ResRolls++; ResExp += o; ResVar += o * (1.0 - o); if (wound > 0f) { ResWounds++; ResWoundSum += wound; } }
            else { SecRolls++; SecExp += o; SecVar += o * (1.0 - o); if (wound > 0f) { SecWounds++; SecWoundSum += wound; } }
            if (++_rolls - _printed >= Every) Summary();
        }

        public static void Summary()
        {
            _printed = _rolls;
            var ic = CultureInfo.InvariantCulture;
            Trace("WoundStats: funnel: hit calls " + Calls + ", blocked by armour " + Blocked + ", not a section compartment " + NotSection + ", ship unknown " + NoShip + ", queue full " + Overflow + ", queued " + Queued +
                  " | sections: hits " + SecHits + ", no team there " + SecNoTeam + ", rolled " + SecRolls + ", wounds " + SecWounds + " (expected " + SecExp.ToString("0.0", ic) + " +-" + (2.0 * Math.Sqrt(SecVar)).ToString("0.0", ic) + ")" + Verdict(SecWounds, SecExp, SecVar) +
                  ", mean wound " + (SecWounds > 0 ? (SecWoundSum / SecWounds * 100.0).ToString("0", ic) : "-") + "%" +
                  " | reserve: hits " + ResHits + ", no team " + ResNoPool + ", chance missed " + ResChanceMiss + ", rolled " + ResRolls + ", wounds " + ResWounds + " (expected " + ResExp.ToString("0.0", ic) + " +-" + (2.0 * Math.Sqrt(ResVar)).ToString("0.0", ic) + ")" + Verdict(ResWounds, ResExp, ResVar) +
                  " | fire: " + FireLoss.ToString("0.00", ic) + " teams' health lost");
        }
    }
}
