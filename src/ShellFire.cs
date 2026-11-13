using System;
using System.Reflection;

namespace AlternativeDamageModel
{
    /// <summary>
    /// [FireAndFlooding] ShellFireByPower: the chance that a gun shell starts a fire in a compartment depends on the power of its warhead.
    /// The game rolls a flat 20 % for the compartment that was hit (Blastzone.OnHitUnit) and 20 % x 0.6-0.9 for each neighbouring compartment the blast reaches
    /// (Blastzone.ProcessCompartmentHit, only when the shell got through the armour), whatever the shell. Here the chance is 90 % x power / 5 (a 100 mm shell, power 5,
    /// and bigger: 90 %); a shell stopped by the armour: times BlockedHitFireChance; the neighbours keep their 0.6-0.9 of it.
    /// The game's roll of the hit compartment is switched off (OnHitUnit, a constant) and made in BlockedFire.Prefix instead: only there it is known whether
    /// the armour stopped the shell (the game finds out later, with a random roll that cannot be repeated). Incendiary shells (_addFire) and other weapons are left to the game.
    /// </summary>
    static class ShellFire
    {
        public static bool On;
        
        static bool _init;
        static FieldInfo _fType, _fSecondary, _fAp, _fPower, _fAddFire;

        static bool Init(object bz)
        {
            if (_init) return _fType != null;
            _init = true;
            try
            {
                var bt = bz.GetType();
                _fType = ReserveHits.Field(bt, "_weaponType");
                _fSecondary = ReserveHits.Field(bt, "_isSecondaryExplosion");
                _fAp = ReserveHits.Field(bt, "_ap");
                if (_fAp != null)
                {
                    var ap = _fAp.GetValue(bz);
                    if (ap != null) { _fPower = ReserveHits.Field(ap.GetType(), "_warheadPower"); _fAddFire = ReserveHits.Field(ap.GetType(), "_addFire"); }
                }
                if (_fType == null || _fSecondary == null || _fAp == null || _fPower == null) _fType = null;
            }
            catch { _fType = null; }
            return _fType != null;
        }

        /// <summary>A gun shell's blast (not a secondary explosion, not an incendiary shell): the power of the warhead; false for anything else.</summary>
        public static bool Shell(object bz, out float power)
        {
            power = 0f;
            if (!On || bz == null || !AdmPlugin.ModelOn || !Init(bz)) return false;
            try
            {
                if (!"Projectile".Equals(_fType.GetValue(bz).ToString()) || (bool)_fSecondary.GetValue(bz)) return false;
                var ap = _fAp.GetValue(bz);
                if (ap == null) return false;
                if (_fAddFire != null && Convert.ToInt32(_fAddFire.GetValue(ap)) > 0) return false;   // incendiary: the game's own fire
                power = Convert.ToSingle(_fPower.GetValue(ap));
                return true;
            }
            catch { return false; }
        }

        /// <summary>The chance in percent: 90 at power 5 and more, in proportion below. A shell stopped by the armour: times BlockedHitFireChance (BlockedFire).</summary>
        public static float Percent(float power)
        {
            float p = 90f * Math.Min(1f, Math.Max(0f, power) / 5f);
            return Math.Max(0f, Math.Min(100f, p));
        }

        /// <summary>OnHitUnit: the game's chance for the hit compartment (a constant 20 for a projectile) - 0 for a shell, the roll is made in BlockedFire.Prefix.</summary>
        public static int OwnGame(int v, object bz) { float p; return Shell(bz, out p) ? 0 : v; }

        /// <summary>ProcessCompartmentHit: the game's chance for a neighbouring compartment (a constant 20 for a projectile, then x 0.6-0.9) - for a shell that got through.</summary>
        public static float NeighbourGame(float v, object bz) { float p; return Shell(bz, out p) ? Percent(p) : v; }
    }
}
