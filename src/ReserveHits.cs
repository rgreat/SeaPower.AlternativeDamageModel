using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace AlternativeDamageModel
{
    /// <summary>
    /// "Hits in reserve": the resting place of the damage control teams is the central compartment. A hit on it can wound the
    /// teams waiting in the reserve, with the chance the game uses for a team knocked out in a hit section (damage x (1 - integrity),
    /// only under 50% integrity; at most 20% (40% civilian) for the enemy, less for the player's side by _dcTeamDisableReduction).
    /// Runs after Blastzone.ProcessCompartmentHit (the game has taken the damage off the compartment by then).
    /// </summary>
    static class ReserveHits
    {
        public static bool Enabled = true;
        public static Action<string> Trace = s => { };

        static bool _init, _ok;
        static FieldInfo _fWeaponType, _fAp, _fPower, _fDmgMul, _fImpact, _fSecondary, _fImpactComp, _fCompObp, _fReduction;
        static MemberInfo _mTaskforce;   // ObjectBase._taskforce: a property in 0.9.0 and 0.9.1 (a field in older games)
        static PropertyInfo _pIsAi;   // Taskforce.IsAI (every enemy side)
        static PropertyInfo _pCivilian, _pIntegrity;
        static MethodInfo _mFlooding;
        static readonly BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>The _taskforce member of a game object: a property in 0.9.x (a field in older games).</summary>
        internal static MemberInfo TaskforceMember(Type t)
        {
            for (; t != null; t = t.BaseType)
            {
                var f = t.GetField("_taskforce", All | BindingFlags.DeclaredOnly); if (f != null) return f;
                var p = t.GetProperty("_taskforce", All | BindingFlags.DeclaredOnly); if (p != null) return p;
            }
            return null;
        }
        internal static Type TypeOfMember(MemberInfo m) { return m is PropertyInfo ? ((PropertyInfo)m).PropertyType : ((FieldInfo)m).FieldType; }
        internal static object TaskforceOf(MemberInfo m, object obj)
        {
            if (m == null || obj == null) return null;
            return m is PropertyInfo ? ((PropertyInfo)m).GetValue(obj, null) : ((FieldInfo)m).GetValue(obj);
        }

        internal static FieldInfo Field(Type t, string name)
        {
            for (; t != null; t = t.BaseType) { var f = t.GetField(name, All | BindingFlags.DeclaredOnly); if (f != null) return f; }
            return null;
        }

        static void Init(object blastzone, object comps, object hitObject)
        {
            _init = true;
            try
            {
                var asm = blastzone.GetType().Assembly;
                _fImpact = Field(blastzone.GetType(), "_unitImpactData");
                _fSecondary = Field(blastzone.GetType(), "_isSecondaryExplosion");
                _fWeaponType = Field(blastzone.GetType(), "_weaponType");
                _fAp = Field(blastzone.GetType(), "_ap");
                var tImpact = asm.GetType("SeaPower.ImpactData");
                _fImpactComp = tImpact == null ? null : Field(tImpact, "_compartments");
                _fCompObp = Field(comps.GetType(), "_obp");
                _mFlooding = comps.GetType().GetMethod("getFloodingCompartment", All);
                _mTaskforce = TaskforceMember(hitObject.GetType());
                var tGlobals = asm.GetType("SeaPower.Globals");
                _pIsAi = _mTaskforce == null ? null : TypeOfMember(_mTaskforce).GetProperty("IsAI", All);
                _fReduction = tGlobals == null ? null : Field(tGlobals, "_dcTeamDisableReduction");
                if (_mFlooding != null) _pIntegrity = _mFlooding.ReturnType.GetProperty("CurrentIntegrityPercent", All);
                if (_fCompObp != null) _pCivilian = _fCompObp.FieldType.GetProperty("_isCivilian", All);
                _ok = _fImpact != null && _fSecondary != null && _fImpactComp != null && _mFlooding != null && _pIntegrity != null && _pIsAi != null;
                if (!_ok) Trace("ReserveHits: members missing (game updated?)");
            }
            catch (Exception e) { Trace("ReserveHits.Init: " + e.Message); }
        }

        static FieldInfo _fRate;

        /// <summary>For the trace: integrity and the leak rate of the compartment a hit came to (after the game's processing).</summary>
        internal static string Describe(object comps, int number, bool port)
        {
            try
            {
                if (_mFlooding == null || _pIntegrity == null) return "";
                var fc = _mFlooding.Invoke(comps, new object[] { number, port });
                if (fc == null) return "";
                if (_fRate == null) _fRate = Field(fc.GetType(), "_floodingRate");
                string rate = _fRate == null ? "?" : Convert.ToSingle(_fRate.GetValue(fc)).ToString("0.###", CultureInfo.InvariantCulture);
                return "integrity " + Convert.ToSingle(_pIntegrity.GetValue(fc, null)).ToString("0.#", CultureInfo.InvariantCulture) + "%, leak " + rate;
            }
            catch { return ""; }
        }

        /// <summary>The power of the hit as the game takes it for the knock-out chance: the warhead power x damage multiplier for a projectile, else the damage.</summary>
        static float Power(object blastzone, float damage)
        {
            try
            {
                var wt = _fWeaponType == null ? null : _fWeaponType.GetValue(blastzone);
                if (wt != null && wt.ToString() == "Projectile" && _fAp != null)
                {
                    var ap = _fAp.GetValue(blastzone);
                    if (ap != null)
                    {
                        if (_fPower == null) { _fPower = Field(ap.GetType(), "_warheadPower"); _fDmgMul = Field(ap.GetType(), "_damageMultiplier"); }
                        if (_fPower != null && _fDmgMul != null) return Convert.ToSingle(_fPower.GetValue(ap)) * Convert.ToSingle(_fDmgMul.GetValue(ap));
                    }
                }
            }
            catch { }
            return damage;
        }

        static readonly Dictionary<string, int> _skips = new Dictionary<string, int>();
        /// <summary>Trace: why a hit did not reach the teams (the first few times for every reason, then silent).</summary>
        static void Skip(string why)
        {
            int n; _skips.TryGetValue(why, out n); _skips[why] = ++n;
            if (n <= 3) Trace("ReserveHits: hit skipped - " + why + " (" + n + ")");
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, HashSet<int>> Seen = new System.Runtime.CompilerServices.ConditionalWeakTable<object, HashSet<int>>();

        /// <summary>
        /// True the first time a key is seen for this hit (the game's ImpactData: one per shell and ship). The game calls ProcessCompartmentHit for every collider
        /// the blast touches (port and starboard of a section, the neighbouring sections), so one shell reaches the same section several times;
        /// the damage control teams count a shell once per section.
        /// </summary>
        internal static bool First(object impact, int key)
        {
            if (impact == null) return true;
            return Seen.GetOrCreateValue(impact).Add(key);
        }

        /// <summary>Postfix of Blastzone.ProcessCompartmentHit(hitObject, hc, blockedByMainArmor, damage).</summary>
        public static void Postfix(object __instance, object[] __args)
        {
            if (!DcTeams.HealthOn) { Skip("team health is off"); return; }
            if (!DcTeams.IsEnabled()) { Skip("alternative damage control is off"); return; }
            if (__args == null || __args.Length < 4) { Skip("unexpected arguments"); return; }
            try
            {
                var hitObject = __args[0];
                var hc = __args[1] as UnityEngine.Component;
                if (hitObject == null || hc == null) { Skip("no hit object or collider"); return; }
                WoundStats.Calls++;
                if ((bool)__args[2]) { WoundStats.Blocked++; return; }
                var parts = hc.transform.name.ToLowerInvariant().Split('_');
                int number;
                if (parts.Length < 3 || parts[0] != "flooding" || !int.TryParse(parts[2], out number)) { WoundStats.NotSection++; return; }
                var map = null as IDictionary;
                if (!_init)
                {
                    map = Field(__instance.GetType(), "_unitImpactData") == null ? null : Field(__instance.GetType(), "_unitImpactData").GetValue(__instance) as IDictionary;
                    if (map == null || !map.Contains(hitObject)) { Skip("first call: the hit object is not in _unitImpactData" + (map == null ? " (field missing)" : "")); return; }
                    var c0 = Field(map[hitObject].GetType(), "_compartments");
                    var comps0 = c0 == null ? null : c0.GetValue(map[hitObject]);
                    if (comps0 == null) { Skip("first call: no compartments"); return; }
                    Init(__instance, comps0, hitObject);
                }
                if (!_ok) { Skip("members missing"); return; }
                map = _fImpact.GetValue(__instance) as IDictionary;
                if (map == null || !map.Contains(hitObject)) { Skip("the hit object is not in _unitImpactData"); return; }
                var comps = _fImpactComp.GetValue(map[hitObject]);
                if (comps == null) { Skip("no compartments"); return; }
                if ((bool)_fSecondary.GetValue(__instance)) { Skip("secondary explosion"); return; }        // the game: no knock-outs by secondary explosions
                float power = Power(__instance, Convert.ToSingle(__args[3]));
                DcTeams.NoteHit(comps, power);                              // the wound of a team knocked out in a hit section
                if (DcTeams.AnyIntegrity)
                {
                    if (!First(map[hitObject], number)) { WoundStats.Dup++; return; }   // 1 shell = 1 roll per section
                    Skip("passed to the teams"); DcTeams.QueueHit(comps, number - 1, power);             // the section's teams, at any integrity
                    if (Enabled && number == 3) DcTeams.ReserveHit(comps, 100f, power);
                    return;
                }
                if (!Enabled || number != 3) return;                        // the central compartment of five
                if (!First(map[hitObject], number)) { WoundStats.Dup++; return; }
                var fc = _mFlooding.Invoke(comps, new object[] { number, parts[1] == "port" });
                if (fc == null) return;
                float integ = Convert.ToSingle(_pIntegrity.GetValue(fc, null));
                if (integ >= 50f) return;
                float chance = power * (1f - integ / 100f);
                var tf = TaskforceOf(_mTaskforce, hitObject);
                bool enemy = tf != null && _pIsAi != null && (bool)_pIsAi.GetValue(tf, null);
                if (enemy)
                {
                    bool civ = false;
                    try { var obp = _fCompObp == null ? null : _fCompObp.GetValue(comps); civ = obp != null && _pCivilian != null && (bool)_pCivilian.GetValue(obp, null); } catch { }
                    chance = Math.Min(chance, civ ? 40f : 20f);
                }
                else if (_fReduction != null) chance -= chance * Convert.ToSingle(_fReduction.GetValue(null));
                DcTeams.ReserveHit(comps, chance, power);
            }
            catch (Exception e) { Trace("ReserveHits: " + e.Message); }
        }
    }
}
