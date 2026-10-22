using System;
using System.Collections;
using System.Reflection;

namespace AlternativeDamageModel
{
    /// <summary>
    /// [HullIntegrity] DamageSpill: damage that a fully destroyed compartment cannot take any more is not lost: half of it goes to the
    /// neighbouring compartment (the same side, towards the middle of the ship); if that one is destroyed too, a quarter goes to the one after it.
    /// Prefix of Blastzone.ProcessCompartmentHit(hitObject, hc, blockedByMainArmor, damage): the game's own damage of the call (x 0.6-0.9
    /// for a secondary compartment) is cut by the compartment's integrity; the surplus is what spills.
    /// </summary>
    static class DamageSpill
    {
        public static bool Enabled;
        public static Action<string> Trace = s => { };

        static bool _init, _ok;
        static FieldInfo _fImpact, _fImpactComp, _fHitIndex, _fPortHit, _fTotal, _fWeaponType, _fIntegrity;
        static MethodInfo _mFlooding;

        static void Init(object blastzone, object impact, object comps)
        {
            _init = true;
            try
            {
                _fImpact = ReserveHits.Field(blastzone.GetType(), "_unitImpactData");
                _fWeaponType = ReserveHits.Field(blastzone.GetType(), "_weaponType");
                _fImpactComp = ReserveHits.Field(impact.GetType(), "_compartments");
                _fHitIndex = ReserveHits.Field(impact.GetType(), "_hitCompartmentIndex");
                _fPortHit = ReserveHits.Field(impact.GetType(), "_portSideHit");
                _fTotal = ReserveHits.Field(impact.GetType(), "_totalDamage");
                _mFlooding = comps.GetType().GetMethod("getFloodingCompartment", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_mFlooding != null) _fIntegrity = ReserveHits.Field(_mFlooding.ReturnType, "_currentIntegrity");
                _ok = _fImpact != null && _fImpactComp != null && _fHitIndex != null && _fPortHit != null && _mFlooding != null && _fIntegrity != null;
                if (!_ok) Trace("DamageSpill: members missing (game updated?)");
            }
            catch (Exception e) { Trace("DamageSpill.Init: " + e.Message); }
        }

        /// <summary>Prefix of Blastzone.ProcessCompartmentHit.</summary>
        public static void Prefix(object __instance, object[] __args)
        {
            if (!Enabled || !DcTeams.IsEnabled() || __args == null || __args.Length < 4) return;
            try
            {
                var hitObject = __args[0];
                var hc = __args[1] as UnityEngine.Component;
                if (hitObject == null || hc == null || (bool)__args[2]) return;      // stopped by the armour: no damage at all
                var parts = hc.transform.name.ToLowerInvariant().Split('_');
                int number;
                if (parts.Length < 3 || parts[0] != "flooding" || !int.TryParse(parts[2], out number) || number < 1 || number > 5) return;
                bool port = parts[1] == "port";
                var map = ReserveHits.Field(__instance.GetType(), "_unitImpactData").GetValue(__instance) as IDictionary;
                if (map == null || !map.Contains(hitObject)) return;
                var impact = map[hitObject];
                if (!_init)
                {
                    var c0 = ReserveHits.Field(impact.GetType(), "_compartments");
                    var comps0 = c0 == null ? null : c0.GetValue(impact);
                    if (comps0 == null) return;
                    Init(__instance, impact, comps0);
                }
                if (!_ok) return;
                var comps = _fImpactComp.GetValue(impact);
                if (comps == null) return;
                int hitIdx = Convert.ToInt32(_fHitIndex.GetValue(impact));
                bool portHit = (bool)_fPortHit.GetValue(impact);
                // as the game: a torpedo's far side is not damaged
                var wt = _fWeaponType == null ? null : _fWeaponType.GetValue(__instance);
                if (wt != null && wt.ToString() == "Torpedo" && port != portHit && hitIdx != 1 && hitIdx != 5) return;
                float damage = Convert.ToSingle(__args[3]);
                if (number != hitIdx || port != portHit) damage *= 0.75f;            // the game takes 0.6-0.9 of it for a secondary compartment
                var fc = _mFlooding.Invoke(comps, new object[] { number, port });
                if (fc == null) return;
                float surplus = damage - Math.Max(0f, Convert.ToSingle(_fIntegrity.GetValue(fc)));
                if (surplus <= 0.0001f) return;

                // the way: towards the middle of the ship (section 3); from the middle itself to the side with more hull left
                int dir;
                if (number < 3) dir = 1; else if (number > 3) dir = -1;
                else dir = Left(comps, 2, port) >= Left(comps, 4, port) ? -1 : 1;
                float given = 0f;
                int n1 = number + dir, n2 = number + 2 * dir;
                float room1 = n1 >= 1 && n1 <= 5 ? Left(comps, n1, port) : 0f;
                if (room1 > 0.0001f) given += Take(comps, n1, port, surplus * 0.5f);
                else if (n2 >= 1 && n2 <= 5) given += Take(comps, n2, port, surplus * 0.25f);
                if (given > 0f && _fTotal != null)
                    try { _fTotal.SetValue(impact, Convert.ToSingle(_fTotal.GetValue(impact)) + given); } catch { }
            }
            catch (Exception e) { Trace("DamageSpill: " + e.Message); }
        }

        static float Left(object comps, int n, bool port)
        {
            var fc = _mFlooding.Invoke(comps, new object[] { n, port });
            return fc == null ? 0f : Math.Max(0f, Convert.ToSingle(_fIntegrity.GetValue(fc)));
        }

        /// <summary>Takes the damage off a compartment (at most what is left of it); returns what was taken.</summary>
        static float Take(object comps, int n, bool port, float amount)
        {
            var fc = _mFlooding.Invoke(comps, new object[] { n, port });
            if (fc == null) return 0f;
            float have = Math.Max(0f, Convert.ToSingle(_fIntegrity.GetValue(fc)));
            float taken = Math.Min(have, amount);
            if (taken <= 0f) return 0f;
            _fIntegrity.SetValue(fc, have - taken);
            return taken;
        }
    }
}
