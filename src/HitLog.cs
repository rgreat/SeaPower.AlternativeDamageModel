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
    /// Kept with the ship and written to saved games (key AlternativeDamageModel_Hits); at most 400 per ship (the oldest go).
    /// </summary>
    static class HitLog
    {
        public sealed class Rec { public double Time; public string Ammo; public int Section; public bool Port, Armor, Blast, Dud; }

        const int Max = 400;
        static readonly ConditionalWeakTable<object, List<Rec>> Logs = new ConditionalWeakTable<object, List<Rec>>();
        public static Action<string> Trace = s => { };

        static bool _init, _ok;
        static FieldInfo _fImpact, _fImpactComp, _fHitIndex, _fPortHit, _fSecondary, _fAp;
        static PropertyInfo _pTime, _pDud;

        static void Init(object blastzone, object impact)
        {
            _init = true;
            try
            {
                _fImpact = ReserveHits.Field(blastzone.GetType(), "_unitImpactData");
                _fSecondary = ReserveHits.Field(blastzone.GetType(), "_isSecondaryExplosion");
                _fAp = ReserveHits.Field(blastzone.GetType(), "_ap");
                _pDud = blastzone.GetType().GetProperty("WarheadMalfunction", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);   // the warhead failed (a dud): the game does reduced damage
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

        static bool IsDud(object blastzone)
        {
            try { return _pDud != null && (bool)_pDud.GetValue(blastzone, null); }
            catch { return false; }
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

        // ---- the direct damage of hits to the hull (for the sinking forecast: it is taken out of the hull's trend)
        // each call of ProcessCompartmentHit: the ship's total integrity before (prefix) and after (postfix); the loss in % of the maximum

        static readonly ConditionalWeakTable<object, List<double[]>> DirectLogs = new ConditionalWeakTable<object, List<double[]>>();
        static object _beforeComps; static double _beforeSum;
        static FieldInfo _fPortArr, _fStarArr, _fCurInt, _fMaxTotal;

        static object CompsOf(object blastzone, object[] args)
        {
            if (args == null || args.Length < 4 || args[0] == null) return null;
            var map = ReserveHits.Field(blastzone.GetType(), "_unitImpactData").GetValue(blastzone) as IDictionary;
            if (map == null || !map.Contains(args[0])) return null;
            var impact = map[args[0]];
            if (!_init) Init(blastzone, impact);
            if (!_ok) return null;
            return _fImpactComp.GetValue(impact);
        }

        static double IntegritySum(object comps)
        {
            if (_fPortArr == null)
            {
                var t = comps.GetType();
                _fPortArr = ReserveHits.Field(t, "_portCompartments"); _fStarArr = ReserveHits.Field(t, "_starboardCompartments"); _fMaxTotal = ReserveHits.Field(t, "_maxTotalIntegrity");
            }
            double sum = 0;
            foreach (var f in new[] { _fPortArr, _fStarArr })
            {
                var arr = f == null ? null : f.GetValue(comps) as IEnumerable;
                if (arr == null) continue;
                foreach (var fc in arr)
                {
                    if (fc == null) continue;
                    if (_fCurInt == null) _fCurInt = ReserveHits.Field(fc.GetType(), "_currentIntegrity");
                    if (_fCurInt != null) sum += Convert.ToDouble(_fCurInt.GetValue(fc), CultureInfo.InvariantCulture);
                }
            }
            return sum;
        }

        public static void Before(object blastzone, object[] args)
        {
            _beforeComps = null;
            try
            {
                var comps = CompsOf(blastzone, args);
                if (comps == null) return;
                _beforeComps = comps; _beforeSum = IntegritySum(comps);
            }
            catch (Exception e) { Trace("HitLog.Before: " + e.Message); }
        }

        public static void After(object blastzone, object[] args)
        {
            var comps = _beforeComps; _beforeComps = null;
            if (comps == null) return;
            try
            {
                double loss = _beforeSum - IntegritySum(comps);
                double max = _fMaxTotal == null ? 0 : Convert.ToDouble(_fMaxTotal.GetValue(comps), CultureInfo.InvariantCulture);
                if (loss <= 0 || max <= 0) return;
                double now = _pTime == null ? 0 : Convert.ToDouble(_pTime.GetValue(null, null), CultureInfo.InvariantCulture);
                var list = DirectLogs.GetOrCreateValue(comps);
                lock (list) { list.Add(new[] { now, 100.0 * loss / max }); if (list.Count > 600) list.RemoveAt(0); }
            }
            catch (Exception e) { Trace("HitLog.After: " + e.Message); }
        }

        /// <summary>The hull % lost to hits so far: pairs (time, loss %), oldest first.</summary>
        public static List<double[]> Direct(object comps)
        {
            List<double[]> list;
            if (comps == null || !DirectLogs.TryGetValue(comps, out list)) return new List<double[]>();
            lock (list) return new List<double[]>(list);
        }

        public static string SaveDirect(object comps)
        {
            var d = Direct(comps);
            if (d.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var r in d) { if (sb.Length > 0) sb.Append('|'); sb.Append(r[0].ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(r[1].ToString("0.###", CultureInfo.InvariantCulture)); }
            return sb.ToString();
        }

        public static void LoadDirect(object comps, string text)
        {
            var list = DirectLogs.GetOrCreateValue(comps);
            lock (list)
            {
                list.Clear();
                foreach (var part in (text ?? "").Split('|'))
                {
                    var f = part.Split(',');
                    double t, l;
                    if (f.Length == 2 && double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out t) && double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out l)) list.Add(new[] { t, l });
                }
            }
        }

        /// <summary>A collision with another ship (Collisions): one record in the hit list, and an entry (time, hull % lost) in the hull-loss log;
        /// the entry is returned, the lasting contact adds to it.</summary>
        public static double[] AddCollision(object comps, double time, int section, bool port, double lossPercent)
        {
            if (comps == null) return null;
            try
            {
                var list = Logs.GetOrCreateValue(comps);
                lock (list) { list.Add(new Rec { Time = time, Ammo = Loc.T("Collision"), Section = section, Port = port }); if (list.Count > Max) list.RemoveAt(0); }
                var entry = new[] { time, lossPercent };
                var d = DirectLogs.GetOrCreateValue(comps);
                lock (d) { d.Add(entry); if (d.Count > 600) d.RemoveAt(0); }
                return entry;
            }
            catch (Exception e) { Trace("HitLog.AddCollision: " + e.Message); return null; }
        }

        // ---- secondary explosions of the ship's own systems (BaseSystem.ExplodeSystem): the game builds a Blastzone without an ammunition name (it would be "?"),
        // hits its own ship with it and logs "Secondary explosion!" only in its own message log. While ExplodeSystem runs, the hits it causes are named after the
        // system ("Explosion of Mk 45 gun mount"); if none of them came to the exploding ship's own log, one record for the section of the system is added.
        static string _exploding; static object _explodingComps; static bool _explodingLogged;
        static PropertyInfo _pSysName; static FieldInfo _fSysBase, _fSysColliders, _fSysMeshColliders, _fSysMount, _fSysLists, _fSysCompList;

        static object ShipCompsOf(object system)
        {
            try
            {
                if (_fSysBase == null) _fSysBase = ReserveHits.Field(system.GetType(), "_baseObject");
                var bo = _fSysBase == null ? null : _fSysBase.GetValue(system);
                var p = bo == null ? null : bo.GetType().GetProperty("Compartments", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return p == null ? null : p.GetValue(bo, null);
            }
            catch { return null; }
        }

        static string SystemName(object system)
        {
            try
            {
                if (_pSysName == null) _pSysName = system.GetType().GetProperty("DisplayName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var rp = _pSysName == null ? null : _pSysName.GetValue(system, null);
                var v = rp == null ? null : rp.GetType().GetProperty("Value").GetValue(rp, null) as string;
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
            catch { }
            return "?";
        }

        /// <summary>Prefix of BaseSystem.ExplodeSystem.</summary>
        public static void ExplodePre(object system)
        {
            try { _exploding = SystemName(system); _explodingComps = ShipCompsOf(system); _explodingLogged = false; }
            catch (Exception e) { _exploding = null; Trace("HitLog.ExplodePre: " + e.Message); }
        }

        /// <summary>Postfix of BaseSystem.ExplodeSystem: the explosion itself, when the blast did not reach the ship's own log.</summary>
        public static void ExplodePost(object system)
        {
            try
            {
                if (_exploding == null || _explodingLogged || _explodingComps == null) return;
                int section = -1; bool port = false;
                var arr = ReserveHits.Field(_explodingComps.GetType(), "_systemCompartments").GetValue(_explodingComps) as Array;
                if (arr != null)
                    for (int i = 0; i < arr.Length && section < 0; i++)
                    {
                        var sc = arr.GetValue(i); if (sc == null) continue;
                        if (_fSysLists == null) _fSysLists = ReserveHits.Field(sc.GetType(), "_listOfSystems");
                        var list = _fSysLists == null ? null : _fSysLists.GetValue(sc) as IEnumerable;
                        if (list != null) foreach (var b in list) if (ReferenceEquals(b, system)) { section = i; break; }
                    }
                if (section < 0) return;
                // the side: the system's position in the ship's frame (as the game: the first collider, else the mount)
                try
                {
                    var bo = _fSysBase.GetValue(system) as UnityEngine.Component;
                    UnityEngine.Vector3? pos = null;
                    var mount = ReserveHits.Field(system.GetType(), "_mountObject") == null ? null : ReserveHits.Field(system.GetType(), "_mountObject").GetValue(system) as UnityEngine.Component;
                    if (mount != null) pos = mount.transform.position;
                    else
                    {
                        var cols = ReserveHits.Field(system.GetType(), "_colliders") == null ? null : ReserveHits.Field(system.GetType(), "_colliders").GetValue(system) as IEnumerable;
                        if (cols != null) foreach (var c in cols) { var go = ReserveHits.Field(c.GetType(), "_go").GetValue(c) as UnityEngine.GameObject; if (go != null) pos = go.transform.position; break; }
                    }
                    if (bo != null && pos.HasValue) port = bo.transform.InverseTransformPoint(pos.Value).x < 0f;
                }
                catch { }
                var list2 = Logs.GetOrCreateValue(_explodingComps);
                lock (list2) { list2.Add(new Rec { Time = Now(system), Ammo = Loc.F("Explosion of {0}", _exploding), Section = section, Port = port, Blast = true }); if (list2.Count > Max) list2.RemoveAt(0); }
            }
            catch (Exception e) { Trace("HitLog.ExplodePost: " + e.Message); }
            finally { _exploding = null; _explodingComps = null; }
        }

        static double Now(object any)
        {
            try
            {
                var pt = _pTime;
                if (pt == null) { var gt = any.GetType().Assembly.GetType("SeaPower.GameTime"); pt = gt == null ? null : gt.GetProperty("missionElapsedTime", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
                return pt == null ? 0 : Convert.ToDouble(pt.GetValue(null, null), CultureInfo.InvariantCulture);
            }
            catch { return 0; }
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
                    Ammo = AmmoName(__instance), Section = number - 1, Port = port, Armor = (bool)__args[2], Blast = (bool)_fSecondary.GetValue(__instance), Dud = IsDud(__instance)
                };
                if (rec.Blast && _exploding != null)
                {
                    rec.Ammo = Loc.F("Explosion of {0}", _exploding);                    // a system of a ship blew up: the blast has no ammunition of its own
                    if (ReferenceEquals(comps, _explodingComps)) _explodingLogged = true;
                }
                if (AdmPlugin.DebugOn)
                    Trace("hit " + rec.Ammo + ": section " + number + (port ? " port" : " stbd") + (rec.Armor ? ", stopped by armour" : "") + (rec.Blast ? ", explosion" : "") + (rec.Dud ? ", dud" : "") +
                          ", damage " + Convert.ToSingle(__args[3]).ToString("0.##", CultureInfo.InvariantCulture) + " -> " + ReserveHits.Describe(comps, number, port));
                var list = Logs.GetOrCreateValue(comps);
                lock (list) { list.Add(rec); if (list.Count > Max) list.RemoveAt(0); }
            }
            catch (Exception e) { Trace("HitLog: " + e.Message); }
        }

        // ---- saved games: "ammunition|ammunition|.../time,ammo,section,flags|..." (flags: 1 port, 2 stopped by armour, 4 explosion, 8 dud)

        static string Clean(string a) { return (a ?? "?").Replace('|', '-').Replace('/', '-').Replace('#', ' ').Replace(',', ' ').Replace(';', ' ').Replace('=', ' ').Trim(); }

        public static string Save(object comps)
        {
            var recs = Records(comps);
            if (recs.Count == 0) return null;
            var names = new List<string>();
            var sb = new System.Text.StringBuilder();
            var body = new System.Text.StringBuilder();
            foreach (var r in recs)
            {
                string a = Clean(r.Ammo);
                int i = names.IndexOf(a);
                if (i < 0) { names.Add(a); i = names.Count - 1; }
                if (body.Length > 0) body.Append('|');
                body.Append(r.Time.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(i).Append(',').Append(r.Section).Append(',')
                    .Append((r.Port ? 1 : 0) | (r.Armor ? 2 : 0) | (r.Blast ? 4 : 0) | (r.Dud ? 8 : 0));
            }
            sb.Append(string.Join("|", names.ToArray())).Append('/').Append(body);
            return sb.ToString();
        }

        public static void Load(object comps, string text)
        {
            try
            {
                int hash = text.IndexOf('/');   // not '#': the game's ini reader takes the rest of the line after it for a comment
                if (hash < 0) return;
                var names = text.Substring(0, hash).Split('|');
                var list = new List<Rec>();
                foreach (var part in text.Substring(hash + 1).Split('|'))
                {
                    var f = part.Split(',');
                    int idx, sec, flags; double t;
                    if (f.Length < 4 || !double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out t) || !int.TryParse(f[1], out idx) ||
                        !int.TryParse(f[2], out sec) || !int.TryParse(f[3], out flags)) continue;
                    list.Add(new Rec { Time = t, Ammo = idx >= 0 && idx < names.Length ? names[idx] : "?", Section = sec, Port = (flags & 1) != 0, Armor = (flags & 2) != 0, Blast = (flags & 4) != 0, Dud = (flags & 8) != 0 });
                }
                if (list.Count > Max) list.RemoveRange(0, list.Count - Max);
                var cur = Logs.GetOrCreateValue(comps);
                lock (cur) { cur.Clear(); cur.AddRange(list); }
            }
            catch (Exception e) { Trace("HitLog.Load: " + e.Message); }
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
