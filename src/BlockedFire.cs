using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace AlternativeDamageModel
{
    /// <summary>
    /// [FireAndFlooding] BlockedHitFireChance: a gun shell stopped by the main armour can still start a fire.
    /// The game rolls the fire of the compartments around the hit one inside "if (!blockedByMainArmor)" of Blastzone.ProcessCompartmentHit, so a
    /// blocked shell never lights them (the hit compartment itself is rolled in OnHitUnit, armour or not). This prefix of ProcessCompartmentHit(hitObject,
    /// hc, blockedByMainArmor, damage) does the same roll for a blocked shell: the chance of a penetrating shell (20 % x 0.6-0.9, less
    /// Globals._catchFireChanceReduction for the player's ship, plus Globals._causeFireChanceBonus for the player's shot) times the option;
    /// the compartment is the one the collider names (flooding_&lt;side&gt;_&lt;n&gt;), only when it is not the hit one (as the game does). The fire is the shell's:
    /// growth (damage x 0.6-0.9 x 0.5 / integrity) / 20 / 2, severity 0 or GunFireSeverity's (see GunFire), integrity as ADM sees it (hull multiplier, HullFireVulnerability).
    /// Nothing else is touched: no hull damage, no flooding. Secondary explosions and other weapons are not affected.
    /// </summary>
    static class BlockedFire
    {
        /// <summary>The option: multiplier of the game's chance; 0 = off (the game's behaviour).</summary>
        public static float Chance;
        public static Action<string> Trace = s => { };

        static bool _init, _ok;
        static FieldInfo _fImpact, _fImpactComp, _fHitIndex, _fPortHit, _fWeaponType, _fSecondary, _fWeapon, _fTaskforce;
        static FieldInfo _fCatchReduction, _fCauseBonus, _fRnd, _fPlayerTf, _fFcMax, _fFcComps, _fSystems;
        static MethodInfo _mFlooding, _mAddFire, _mUpdateFire;
        static FieldInfo _fCompMax;
        static readonly Dictionary<Type, PropertyInfo> PlayerProp = new Dictionary<Type, PropertyInfo>();

        static void Init(object blastzone, object impact)
        {
            _init = true;
            try
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var bt = blastzone.GetType();
                _fImpact = ReserveHits.Field(bt, "_unitImpactData");
                _fWeaponType = ReserveHits.Field(bt, "_weaponType");
                _fSecondary = ReserveHits.Field(bt, "_isSecondaryExplosion");
                _fWeapon = ReserveHits.Field(bt, "_weapon");
                _fTaskforce = ReserveHits.Field(bt, "_taskforce");
                _fImpactComp = ReserveHits.Field(impact.GetType(), "_compartments");
                _fHitIndex = ReserveHits.Field(impact.GetType(), "_hitCompartmentIndex");
                _fPortHit = ReserveHits.Field(impact.GetType(), "_portSideHit");
                var g = bt.Assembly.GetType("SeaPower.Globals");
                if (g != null)
                {
                    _fCatchReduction = g.GetField("_catchFireChanceReduction", all);
                    _fCauseBonus = g.GetField("_causeFireChanceBonus", all);
                    _fRnd = g.GetField("_rnd", all);
                    _fPlayerTf = g.GetField("_playerTaskforce", all);
                }
                var comps = _fImpactComp == null ? null : _fImpactComp.GetValue(impact);
                if (comps != null)
                {
                    _mFlooding = comps.GetType().GetMethod("getFloodingCompartment", all);
                    _fSystems = ReserveHits.Field(comps.GetType(), "_systemCompartments");
                    _fCompMax = ReserveHits.Field(comps.GetType(), "_maxIntegrity");
                    _mUpdateFire = comps.GetType().GetMethod("updateFireEffect", all);
                    if (_mFlooding != null)
                    {
                        _fFcMax = ReserveHits.Field(_mFlooding.ReturnType, "_maxIntegrity");
                        _fFcComps = ReserveHits.Field(_mFlooding.ReturnType, "_compartments");
                    }
                }
                var sc = bt.Assembly.GetType("SeaPower.SystemCompartment");
                _mAddFire = sc == null ? null : sc.GetMethod("AddCompartmentFire", all);
                _ok = _fImpact != null && _fWeaponType != null && _fSecondary != null && _fImpactComp != null && _fHitIndex != null && _fPortHit != null &&
                      _fCatchReduction != null && _fCauseBonus != null && _fRnd != null && _fPlayerTf != null && _mFlooding != null && _fSystems != null &&
                      _fFcMax != null && _fFcComps != null && _mAddFire != null;
                if (!_ok) Trace("BlockedFire: members missing (game updated?): the fire of blocked hits is off");
            }
            catch (Exception e) { Trace("BlockedFire.Init: " + e.Message); }
        }

        static bool IsPlayer(object unit)
        {
            if (unit == null) return false;
            PropertyInfo p;
            var t = unit.GetType();
            if (!PlayerProp.TryGetValue(t, out p))
            {
                p = t.GetProperty("IsPlayerObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                PlayerProp[t] = p;
            }
            return p != null && (bool)p.GetValue(unit, null);
        }

        static float Rand(System.Random r, float min, float max) { return min + (max - min) * (float)r.NextDouble(); }

        /// <summary>Prefix of Blastzone.ProcessCompartmentHit(hitObject, hc, blockedByMainArmor, damage).</summary>
        public static void Prefix(object blastzone, object[] args)
        {
            if (args == null || args.Length < 4 || !(args[2] is bool) || !AdmPlugin.ModelOn) return;
            bool blocked = (bool)args[2];
            if (!ShellFire.On && (Chance <= 0f || !blocked)) return;
            try
            {
                var hitObject = args[0];
                var hc = args[1] as UnityEngine.Component;
                if (hitObject == null || hc == null) return;
                var parts = hc.transform.name.ToLowerInvariant().Split('_');
                int number;
                if (parts.Length < 3 || parts[0] != "flooding" || !int.TryParse(parts[2], out number) || number < 1 || number > 5) return;
                bool port = parts[1] == "port";
                var map = ReserveHits.Field(blastzone.GetType(), "_unitImpactData").GetValue(blastzone) as IDictionary;
                if (map == null || !map.Contains(hitObject)) return;
                var impact = map[hitObject];
                if (!_init) Init(blastzone, impact);
                if (!_ok) return;
                if (!"Projectile".Equals(_fWeaponType.GetValue(blastzone).ToString()) || (bool)_fSecondary.GetValue(blastzone)) return;
                bool own = Convert.ToInt32(_fHitIndex.GetValue(impact)) == number;      // the hit compartment: the game rolls it itself in OnHitUnit, armour or not
                if (own && !ShellFire.On) return;
                if (!own && !blocked) return;                                           // a shell that got through: the game rolls the neighbours (ShellFire.NeighbourGame)
                if (!own && Chance <= 0f) return;
                var comps = _fImpactComp.GetValue(impact);
                if (comps == null) return;
                var fc = _mFlooding.Invoke(comps, new object[] { number, port });
                if (fc == null) return;
                var systems = _fSystems.GetValue(_fFcComps.GetValue(fc)) as IList;
                if (systems == null || number > systems.Count) return;
                var sc = systems[number - 1];
                if (sc == null) return;

                var rnd = (System.Random)_fRnd.GetValue(null);
                if (own) { OwnFire(blastzone, hitObject, impact, comps, sc, number, blocked, Convert.ToSingle(args[3]), rnd); return; }
                // the game's chance of a penetrating shell: 20, x 0.6-0.9, the player's side bonuses
                float sp;
                float chance = (ShellFire.Shell(blastzone, out sp) ? ShellFire.Percent(sp) : 20f) * Rand(rnd, 0.6f, 0.9f);   // ShellFire: by the power
                var weapon = _fWeapon.GetValue(blastzone);
                if (IsPlayer(hitObject)) chance -= chance * Convert.ToSingle(_fCatchReduction.GetValue(null));
                else if (weapon != null) { if (IsPlayer(weapon)) chance += chance * Convert.ToSingle(_fCauseBonus.GetValue(null)); }
                else if (ReferenceEquals(_fTaskforce.GetValue(blastzone), _fPlayerTf.GetValue(null))) chance += chance * Convert.ToSingle(_fCauseBonus.GetValue(null));
                chance *= Chance;
                if (!(chance > rnd.Next(0, 101))) return;

                // the shell's fire: a compartment next to the hit one takes 0.6-0.9 of the damage; half of it for a shell; severity 0
                float damage = Convert.ToSingle(args[3]) * Rand(rnd, 0.6f, 0.9f);
                float reference = AdmPlugin.FireIntegrityFc(fc, Convert.ToSingle(_fFcMax.GetValue(fc)));   // ADM's integrity: hull multiplier, HullFireVulnerability
                if (reference <= 0f) return;
                float growth = damage * 0.5f / reference / 20f / 2f;
                _mAddFire.Invoke(sc, new object[] { GunFire.Neighbour(growth), growth });   // the starting severity as GunFireSeverity gives to a shell that gets through
                HitLog.NoteBlockedFire(blastzone, hitObject);
                if (AdmPlugin.DebugOn)
                    Trace("blocked hit lights a fire: section " + number + (port ? " port" : " stbd") + ", chance " + chance.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                          " %, growth +" + (100f * growth).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " % of the maximum");
            }
            catch (Exception e) { Trace("BlockedFire: " + e.Message); }
        }

        /// <summary>
        /// The fire of the hit compartment of a shell (ShellFire): the game's roll is off, this is the roll - once per shell and ship, with the chance by power
        /// (times BlockedHitFireChance when the armour stopped the shell). The fire is the game's: growth damage / integrity / 20 (integrity as ADM sees it), the starting severity of GunFireSeverity.
        /// </summary>
        static void OwnFire(object blastzone, object hitObject, object impact, object comps, object sc, int number, bool blocked, float damage, System.Random rnd)
        {
            float power;
            if (!ShellFire.Shell(blastzone, out power)) return;
            if (!ReserveHits.First(impact, 100)) return;
            float chance = ShellFire.Percent(power) * (blocked ? Chance : 1f);   // a stopped shell: BlockedHitFireChance
            if (!(chance > rnd.Next(0, 101))) return;
            if (_fCompMax == null) return;
            float reference = AdmPlugin.FireIntegrity(comps, Convert.ToSingle(_fCompMax.GetValue(comps)));
            if (reference <= 0f) return;
            float growth = damage / reference / 20f;
            _mAddFire.Invoke(sc, new object[] { GunFire.Own(growth), growth });
            HitLog.NoteBlockedFire(blastzone, hitObject);
            if (_mUpdateFire != null) _mUpdateFire.Invoke(comps, null);
            if (AdmPlugin.DebugOn)
                Trace("shell fire in the hit compartment " + number + ": power " + power.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + ", chance " + chance.ToString("0") + " %" + (blocked ? " (stopped by armour)" : "") +
                      ", growth +" + (100f * growth).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " % of the maximum");
        }
    }
}
