using System;

namespace AlternativeDamageModel
{
    /// <summary>
    /// The points past which the ship floods much faster - the game's own thresholds (Compartments.OnFixedUpdate), moved to
    /// [Sinking] ListLimit / TrimLimit / FloodLimit: water over the deck edge from this list, sinking from this trim (with a flooded
    /// section) or from this much of the ship flooded. Capsizing at 70° of list is left as it is. Also the full scale of the
    /// List / Trim / Level gauges on the Damage Control tab. Defaults: Advanced (list 45 less 30%, trim 5.2 at 100 m by length, water 40 as in the game).
    /// </summary>
    static class Limits
    {
        public static float ListFull = 45f * 0.7f;    // 31.5 degrees (game: 45)
        public static float TrimFull = 5.2f;          // degrees (game: 8), for a ship TrimReferenceLength long
        public static float TrimRefLength = 100f;     // metres; 0 = the same trim limit for every ship (as in the game)

        /// <summary>
        /// The trim limit of one ship. At a trim angle θ the bow and stern move up / down by L/2·sin θ: the longer the ship, the
        /// sooner its ends go under at the same angle; freeboard grows with size, but slower than the length.
        /// tan θ = tan θref · (Lref / L)^0.442. Advanced (5.2° at 100 m): 30 m 8.8°, 60 m 6.5°, 150 m 4.4°, 200 m 3.8°, 300 m 3.2°
        /// (the first version - 7° at 100 m, exponent 0.5 - less 30% for short ships, less 20% for long ones).
        /// </summary>
        public const double TrimExponent = 0.442;

        public static float Trim(float length)
        {
            if (TrimRefLength <= 0f || length <= 1f) return TrimFull;
            double t = Math.Tan(TrimFull * Math.PI / 180.0) * Math.Pow(TrimRefLength / length, TrimExponent);
            return (float)Math.Max(1.0, Math.Min(30.0, Math.Atan(t) * 180.0 / Math.PI));
        }
        public static float LevelFull = 40f;          // % of the ship flooded (game: 40)
    }
}
