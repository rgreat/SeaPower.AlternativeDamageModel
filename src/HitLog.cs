using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlternativeDamageModel
{
    /// <summary>
    /// The hits on a ship, for the tooltip of the "Hull Damage / Flooding / Fires" block of the Damage Control tab: when (mission time),
    /// by what ammunition, on which section and side. One record per impact: the call of Blastzone.ProcessCompartmentHit for the compartment
    /// that was hit first (not the neighbours the blast reaches); explosions that follow a hit and hits stopped by the armour are marked.
    /// Kept in memory with the ship, not in saved games; at most 400 per ship (the oldest go).
    /// </summary>
    static class HitLog
    {
        public sealed class Rec { public double Time; public string Ammo; public int Section; public bool Port, Armor, Blast; }

        const int Max = 400;
        static readonly ConditionalWeakTable<object, List<Rec>> Logs = new ConditionalWeakTable<object, List<Rec>>();
        public static Action<string> Trace = s => { };

        static bool _init, _ok;
        static FieldInfo _fImpact, _fImpactComp, _fHitIndex, _fPortHit, _fSecondary, _fAp;
        static PropertyInfo _pTime;

        static void Init(object blastzone, object impact)
        {
            _init = true;
            try
            {
                _fImpact = ReserveHits.Field(blastzone.GetType(), "_unitImpactData");
                _fSecondary = ReserveHits.Field(blastzone.GetType(), "_isSecondaryExplosion");
                _fAp = ReserveHits.Field(blastzone.GetType(), "_ap");
                _fImpactComp = ReserveHits.Field(impact.GetType(), "_compartments");
                _fHitIndex = ReserveHits.Field(impact.GetType(), "_hitCompartmentIndex");
                _fPortHit = ReserveHits.Field(impact.GetType(), "_portSideHit");
                var gt = blastzone.GetType().Assembly.GetType("SeaPower.GameTime");
                _pTime = gt == null ? null : gt.GetProperty("missionElapsedTime", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                _ok = _fImpact != null && _fImpactComp != null && _fHitIndex != null && _fPortHit != null && _fSecondary != null;
                if (!_ok) Trace("HitLog: members missing (game updated?)");
            }
            catch (Exception e) { Trace("HitLog.Init: " + e.Message); }
        }

        static string AmmoName(object blastzone)
        {
            try
            {
                var ap = _fAp == null ? null : _fAp.GetValue(blastzone);
                if (ap == null) return "?";
                foreach (var n in new[] { "_displayedName", "DisplayedName", "_nativeDisplayedName", "_ammunitionFileName" })
                {
                    string v = null;
                    var f = ReserveHits.Field(ap.GetType(), n);
                    if (f != null) v = f.GetValue(ap) as string;
                    else { var p = ap.GetType().GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (p != null) v = p.GetValue(ap, null) as string; }
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
            }
            catch { }
            return "?";
        }

        /// <summary>Postfix of Blastzone.ProcessCompartmentHit(hitObject, hc, blockedByMainArmor, damage).</summary>
        public static void Postfix(object __instance, object[] __args)
        {
            if (__args == null || __args.Length < 4) return;
            try
            {
                var hitObject = __args[0];
                var hc = __args[1] as UnityEngine.Component;
                if (hitObject == null || hc == null) return;
                var parts = hc.transform.name.ToLowerInvariant().Split('_');
                int number;
                if (parts.Length < 3 || parts[0] != "flooding" || !int.TryParse(parts[2], out number)) return;
                bool port = parts[1] == "port";
                var map = ReserveHits.Field(__instance.GetType(), "_unitImpactData").GetValue(__instance) as IDictionary;
                if (map == null || !map.Contains(hitObject)) return;
                var impact = map[hitObject];
                if (!_init) Init(__instance, impact);
                if (!_ok) return;
                if (Convert.ToInt32(_fHitIndex.GetValue(impact)) != number || (bool)_fPortHit.GetValue(impact) != port) return;   // a neighbour of the hit one
                var comps = _fImpactComp.GetValue(impact);
                if (comps == null) return;
                var rec = new Rec
                {
                    Time = _pTime == null ? 0 : Convert.ToDouble(_pTime.GetValue(null, null), CultureInfo.InvariantCulture),
                    Ammo = AmmoName(__instance), Section = number - 1, Port = port, Armor = (bool)__args[2], Blast = (bool)_fSecondary.GetValue(__instance)
                };
                var list = Logs.GetOrCreateValue(comps);
                lock (list) { list.Add(rec); if (list.Count > Max) list.RemoveAt(0); }
            }
            catch (Exception e) { Trace("HitLog: " + e.Message); }
        }

        /// <summary>A copy of the ship's records (oldest first); empty when none.</summary>
        public static List<Rec> Records(object comps)
        {
            List<Rec> list;
            if (comps == null || !Logs.TryGetValue(comps, out list)) return new List<Rec>();
            lock (list) return new List<Rec>(list);
        }
    }
}
