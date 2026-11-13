using System;
using System.Reflection;

namespace AlternativeDamageModel
{
    /// <summary>
    /// [FireAndFlooding] GunFireSeverity: a gun shell that starts a fire also gives it a starting severity, as a bomb or a missile does.
    /// The game gives a shell only a growth rate (AddCompartmentFire(0, growth)): the fire starts at 0 and has to grow. A bomb or a missile adds both: severity
    /// num10 / 2 and growth num10 / 20, num10 = damage / integrity. For a shell the game cuts the severity to 0 (and in a neighbouring compartment
    /// also halves the damage and the growth). Here the severity is put back, times the option (1 = as for bombs and missiles, 0 = the game's).
    /// Where the game calls AddCompartmentFire(0, growth) is known from the context: OnHitUnit (the hit compartment: growth = num10 / 20, so
    /// severity = growth x 10) or ProcessCompartmentHit inside it (a neighbour: growth = damage / integrity / 80, so severity = damage / integrity / 2 = growth x 40).
    /// The growth already carries ADM's integrity (hull multiplier, HullFireVulnerability), so the severity does too.
    /// A call that brings a severity itself (an incendiary shell) is left alone.
    /// </summary>
    static class GunFire
    {
        /// <summary>The option; 0 = off.</summary>
        public static float Start;

        [ThreadStatic] static int _mode;     // 0 none, 1 hit compartment of a shell, 2 a neighbour
        [ThreadStatic] static int _frame;
        static bool _init;
        static FieldInfo _fType, _fSecondary;

        public static void HitPre(object blastzone)
        {
            _mode = 0;
            if (Start <= 0f || blastzone == null || !AdmPlugin.ModelOn) return;
            try
            {
                if (!_init)
                {
                    _init = true;
                    _fType = ReserveHits.Field(blastzone.GetType(), "_weaponType");
                    _fSecondary = ReserveHits.Field(blastzone.GetType(), "_isSecondaryExplosion");
                }
                if (_fType == null || _fSecondary == null) return;
                if ("Projectile".Equals(_fType.GetValue(blastzone).ToString()) && !(bool)_fSecondary.GetValue(blastzone)) { _mode = 1; _frame = UnityEngine.Time.frameCount; }
            }
            catch { _mode = 0; }
        }

        public static void HitPost() { _mode = 0; }
        public static void CompPre() { if (_mode == 1) _mode = 2; }
        public static void CompPost() { if (_mode == 2) _mode = 1; }

        /// <summary>The starting severity (share of the compartment's maximum) for AddCompartmentFire(0, growth) made by a shell's hit; 0 elsewhere.</summary>
        public static float Severity(float growth)
        {
            if (_mode == 0 || Start <= 0f || growth <= 0f || _frame != UnityEngine.Time.frameCount) return 0f;
            return Start * growth * (_mode == 1 ? 10f : 40f);
        }

        /// <summary>The starting severity for the hit compartment's fire rolled by ShellFire (as OnHitUnit gives it: growth x 10).</summary>
        public static float Own(float growth) { return Start <= 0f || growth <= 0f ? 0f : Start * growth * 10f; }

        /// <summary>The same for a neighbouring compartment lit by a shell the armour stopped (BlockedFire).</summary>
        public static float Neighbour(float growth) { return Start <= 0f || growth <= 0f ? 0f : Start * growth * 40f; }
    }
}
