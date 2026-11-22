using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Added to a ship (its GameObject with the Rigidbody, next to the game's Vessel / Submarine) on its first collision with another ship:
    /// the game's classes have OnCollisionEnter only, and a ram does not hit once - it climbs onto the other ship and slides over it, the
    /// contact lasts. OnCollisionStay gives the contact every physics step.
    /// </summary>
    public sealed class CollisionRelay : MonoBehaviour
    {
        object _unit, _comps;                // the unit and its Compartments, found once
        void OnCollisionStay(Collision c)
        {
            if (!Collisions.Active) return;
            if (_unit == null) _unit = Collisions.UnitOf(this);
            if (_unit != null) Collisions.Step(_unit, c);
        }
        void FixedUpdate()
        {
            if (!Collisions.Active) return;
            if (_unit == null) _unit = Collisions.UnitOf(this);
            if (_unit != null) Collisions.Tick(_unit, ref _comps);
        }
    }

    /// <summary>
    /// [HullIntegrity] CollisionDamage: a ship that runs into another ship is damaged. The game does nothing about it: the ships only push
    /// each other (its OnCollisionEnter looks at the terrain only). Postfix of Vessel.OnCollisionEnter / Submarine.OnCollisionEnter and
    /// CollisionRelay.OnCollisionStay: every physics step of a contact, each of the two ships damages itself.
    ///
    /// The damage is the energy the physics wears down in the contact, step by step, not a function of the first blow. A step of a contact
    /// gives the contact impulse J (Collision.impulse) and the relative velocity v before it; the energy lost along the contact normal is
    /// J*v - J^2/(2*mu) (mu = the reduced mass; for a perfectly inelastic blow J = mu*v and that is the familiar mu*v^2/2; for a bounce it is
    /// 0), and what is lost to the friction of the scraping is the same expression for the sliding direction. A ship that just rests on
    /// another loses nothing (no relative velocity), one that slides over it keeps losing hull as long as it grinds. The ship takes the
    /// share of the energy that the heavier one does not, m_other / (m_self + m_other). Per ton of the ship's own displacement
    ///     fraction = Strength * share * E / (Toughness * M_self)        (E in kJ = t * (m/s)^2, Toughness = 10 kJ per ton)
    /// of what one compartment can take: for one blow that is v^2 / (2 * Toughness) * share^2, so two equal ships meeting at 10 m/s (19 knots)
    /// ruin about one and a quarter of a compartment each, and a 500 t boat on an 8000 t ship at 15 knots is wrecked, the ship scratched.
    /// The damage is in the game's points of the compartment (its integrity before the mod's hull multiplier), so a stronger hull takes
    /// the same blow with less loss. It goes to the compartments of the contact points of that step (the point in the ship's frame gives
    /// the section by its place along the ship and the side by its sign), so it moves along the hull as the bow climbs; what a destroyed
    /// compartment cannot take spills to its neighbour towards the middle of the ship, as in DamageSpill.
    /// The leak follows the damage a compartment has taken in the contact so far: the game's rule for a hit (the damage in compartments'
    /// worth is the rate of the flooding, times the game's flooding multiplier and [SideEffects] HullFloodingVulnerability), raised to
    /// that, never added up step after step; a contact over the waterline counts only for a half-destroyed compartment.
    /// </summary>
    static class Collisions
    {
        public static float Strength;                  // [HullIntegrity] CollisionDamage, a multiplier; 0 = off
        public static float MinSpeed = 1f;             // [HullIntegrity] CollisionMinSpeed, knots (relative speed of the contact)
        public static float Braking = 1f;              // [Collisions] Braking: how much of the speed a blow takes the ships really lose; 0 = they keep their speed
        public static bool DamageOn = true;            // [Collisions] Enabled: collisions damage the hulls
        public static bool Active { get { return DamageOn || Braking > 0f; } }   // collisions do anything at all
        public static bool AIShips = false;            // [Collisions] AIShips: the ships of the AI are damaged and slowed too
        public static bool PushApart;                  // [Collisions] PushApart: the ships in contact are moved apart a little
        public static float BowStrength = 4f;          // [Collisions] BowStrength: how many times the bow (the stem) takes more than the side
        public static float WaterResistance = 1f;      // [Collisions] WaterResistance: how much the water holds a ship that is shoved sideways or turned (x the added mass)
        public static float FloodFactor = 1f;          // [Collisions] Flooding: x the leak a collision opens
        public static float GripPress = 0.75f;         // [Collisions] GripPress: the same, added when the ships are pressed together
        public static float FloodVuln = 1f;            // [SideEffects] HullFloodingVulnerability
        public static Action<string> Trace = s => { };
        public static Func<double> GameNow = () => 0.0;
        public static Func<object, float> HullGameMax = c => 0f;   // the game's integrity of one compartment (before the mod's multiplier)

        const float UnityToMetre = 67.200066f;         // the game's UNITY_TO_METER
        const float MetreSecToKnot = 1f / 0.514444f;
        const float MassUnitToTon = 40f;               // the game's rigidbody mass is the displacement / 40
        const float Toughness = 10f;                   // kJ per ton of displacement that wreck one compartment
        const float KnotToUnit = 0.0076554087f;        // the game's Unity units per second for a knot
        const float Restitution = 0.05f;               // the share of the closing speed that comes back: hulls crumple, they hardly bounce
        const float BrakeTime = 0.7f;                  // s: the time constant the speed change of a blow is spread over
        const double ForceTime = 3.0;                  // s a contact lasts at least when the physics gives no steps (the ships are moved by the game)
        const double MaxTime = 20.0;                   // s a contact lasts at most
        const float AddedAlong = 0.1f;                 // the water that moves with a ship along its heading, x its displacement
        const float BrakeGain = 2f;                    // Braking counts twice
        const float AddedAcross = 4f;                  // ... across it, with the resistance to turning and the drag: a ship is hard to shove sideways
        const float SlipTime = 1.5f;                   // s: the sideways speed a blow gives a ship dies away in this time (the water holds a hull that moves sideways)
        const float PushSpeed = 0.5f;                  // m/s the ships are pushed apart at ([Collisions] PushApart)
        const float MaxFraction = 6f;                  // compartments' worth, at most, from one contact (a stuck, jittering pair)
        const float HoleBand = 3f;                     // m above the sea: still counts as at the waterline
        const double Gap = 1.5;                        // game seconds without a contact step: the next one is a new collision
        const double QuietStart = 5.0;                 // game seconds at the start of a mission: ships may be placed overlapping
        const double LogEvery = 0.5;                   // trace line of a lasting contact, game seconds

        /// <summary>The contact of two ships: shared, so the speed change and the energy are worked out once per step for both.</summary>
        sealed class Pair
        {
            public object UA, UB;                      // the units (A: the ship whose step came first)
            public object CA, CB;                      // their Compartments
            public float MA, MB;                       // tons
            public Vector3 N;                          // the contact normal, horizontal, from B to A
            public double Start, Touch, Step, LastLog;
            public int Steps;
            public double Cap, Spent;                  // kJ: the energy of the blow at its start (0.5 * mu * v^2) and what the hulls have taken of it
        }

        /// <summary>The sideways speed of a ship that a blow gave it: the game moves a ship along its heading only, so this is moved by the mod and dies away.</summary>
        sealed class Slip { public Vector3 V; public double Last; }
        static readonly ConditionalWeakTable<object, Slip> Slips = new ConditionalWeakTable<object, Slip>();
        static Vector3 SlipOf(object u) { Slip sl; return Slips.TryGetValue(u, out sl) ? sl.V : Vector3.zero; }
        static void AddSlip(object u, Vector3 dv, double now) { var sl = Slips.GetOrCreateValue(u); if (sl.V.sqrMagnitude < 1e-12f) sl.Last = now; sl.V += dv; }
        static void SlipStep(object unit, double now)
        {
            Slip sl;
            if (!Slips.TryGetValue(unit, out sl) || sl.V.sqrMagnitude < 1e-12f) return;
            double dt = Math.Min(0.1, now - sl.Last);
            if (dt <= 0.0) return;
            sl.Last = now;
            var c = unit as Component;
            if (c != null) c.transform.Translate(sl.V * (float)dt, Space.World);
            sl.V *= (float)Math.Exp(-dt / SlipTime);
            if (sl.V.magnitude * UnityToMetre < 0.05f) sl.V = Vector3.zero;
        }

        sealed class Episode
        {
            public double Start, Last, LastLog; public int Steps;
            public Pair P;                             // the contact this ship is in
            public Vector3[] Loc; public float[] WY; public int N;   // its contact points: in the ship's frame, and the heights in the world
            public float Fraction;                     // compartments' worth so far
            public double Energy;                      // kJ lost in the contact so far
            public float[] Leak;                       // per slot: damage points counted for the leak
            public double[] Direct;                    // the hull-loss log entry of this contact
            public double Lost;                        // hull points lost so far
        }
        static readonly ConditionalWeakTable<object, Episode> Episodes = new ConditionalWeakTable<object, Episode>();

        static bool _init, _ok;
        static Type _tVessel, _tSub, _tObjBase, _tGlobals;
        static PropertyInfo _pRel, _pRb, _pCount, _pImpulse, _pComps, _pRbSelf, _pMass, _pPlayer;
        static MethodInfo _mGetV, _mSetV;
        static MethodInfo _mContact, _mIncrease;
        static PropertyInfo _pPoint, _pNormal;
        static FieldInfo _fShipT, _fPort, _fStar, _fObject, _fCur, _fMax, _fRate, _fFloodMult, _fMaxTotal;
        static PropertyInfo _pPct, _pName, _pNameValue;

        static void Init(object self, object collision)
        {
            _init = true;
            try
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var asm = self.GetType().Assembly;
                _tVessel = asm.GetType("SeaPower.Vessel");
                _tSub = asm.GetType("SeaPower.Submarine");
                _tObjBase = asm.GetType("SeaPower.ObjectBase");
                _tGlobals = asm.GetType("SeaPower.Globals");
                var tCol = collision.GetType();
                _pRel = tCol.GetProperty("relativeVelocity", all);
                _pImpulse = tCol.GetProperty("impulse", all);
                _pRb = tCol.GetProperty("rigidbody", all);
                _pCount = tCol.GetProperty("contactCount", all);
                _mContact = tCol.GetMethod("GetContact", all, null, new[] { typeof(int) }, null);
                if (_mContact != null)
                {
                    _pPoint = _mContact.ReturnType.GetProperty("point", all);
                    _pNormal = _mContact.ReturnType.GetProperty("normal", all);
                }
                _pComps = _tObjBase == null ? null : _tObjBase.GetProperty("Compartments", all);
                _pRbSelf = _tObjBase == null ? null : _tObjBase.GetProperty("RigidBody", all);
                _pPlayer = _tObjBase == null ? null : _tObjBase.GetProperty("IsPlayerObject", all);
                _mGetV = _tObjBase == null ? null : _tObjBase.GetMethod("getVelocityInKnots", all);
                _mSetV = _tObjBase == null ? null : _tObjBase.GetMethod("setVelocityInKnots", all);
                _pName = _tObjBase == null ? null : _tObjBase.GetProperty("Name", all);
                _pNameValue = _pName == null ? null : _pName.PropertyType.GetProperty("Value", all);
                var tComps = asm.GetType("SeaPower.Compartments");
                var tFc = asm.GetType("SeaPower.FloodingCompartment");
                if (tComps != null)
                {
                    _fShipT = tComps.GetField("_compartmentsTransform", all);
                    _fPort = tComps.GetField("_portCompartments", all);
                    _fStar = tComps.GetField("_starboardCompartments", all);
                    _fMaxTotal = tComps.GetField("_maxTotalIntegrity", all);
                }
                if (tFc != null)
                {
                    _fObject = tFc.GetField("_object", all);
                    _fCur = tFc.GetField("_currentIntegrity", all);
                    _fMax = tFc.GetField("_maxIntegrity", all);
                    _fRate = tFc.GetField("_floodingRate", all);
                    _pPct = tFc.GetProperty("CurrentIntegrityPercent", all);
                    _mIncrease = tFc.GetMethod("IncreaceFloodingRate", all);
                }
                if (_tGlobals != null) _fFloodMult = _tGlobals.GetField("_floodingRateMultiplier", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                _ok = _tObjBase != null && _pRel != null && _pImpulse != null && _pRb != null && _pCount != null && _mContact != null && _pPoint != null && _pNormal != null &&
                      _pComps != null && _mGetV != null && _mSetV != null && _fPort != null && _fStar != null && _fObject != null && _fCur != null && _fMax != null && _mIncrease != null;
                if (!_ok) Trace("Collisions: members missing (game updated?): the damage from collisions is off");
            }
            catch (Exception e) { Trace("Collisions.Init: " + e.Message); }
        }

        static FieldInfo _fCompObpR, _fObpRolesR;
        static readonly ConditionalWeakTable<object, StrongBox<bool>> RaftMemo = new ConditionalWeakTable<object, StrongBox<bool>>();

        /// <summary>The unit has the LifeRaft role (an inflatable raft: a vessel for the game, but not a ship for the collision logic).</summary>
        static bool IsRaft(object comps)
        {
            if (comps == null) return false;
            StrongBox<bool> m;
            if (RaftMemo.TryGetValue(comps, out m)) return m.Value;
            bool raft = false;
            try
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                if (_fCompObpR == null) _fCompObpR = comps.GetType().GetField("_obp", all);
                var obp = _fCompObpR == null ? null : _fCompObpR.GetValue(comps);
                if (obp != null)
                {
                    if (_fObpRolesR == null) _fObpRolesR = obp.GetType().GetField("_unitRoles", all);
                    var roles = _fObpRolesR == null ? null : _fObpRolesR.GetValue(obp) as System.Collections.IEnumerable;
                    if (roles != null) foreach (var r in roles) if (r != null && r.ToString() == "LifeRaft") { raft = true; break; }
                }
            }
            catch { }
            RaftMemo.Add(comps, new StrongBox<bool>(raft));
            return raft;
        }

        static bool IsShip(object o)
        {
            var t = o == null ? null : o.GetType();
            return t != null && ((_tVessel != null && _tVessel.IsAssignableFrom(t)) || (_tSub != null && _tSub.IsAssignableFrom(t)));
        }

        static string NameOf(object ob)
        {
            try { var n = _pName == null ? null : _pName.GetValue(ob, null); var v = n == null || _pNameValue == null ? null : _pNameValue.GetValue(n, null); return v == null ? "?" : v.ToString(); }
            catch { return "?"; }
        }

        // Debug trace: why a callback did nothing (once in 3 s per ship and reason), so a collision that leaves no damage can be explained
        static readonly Dictionary<string, double> Told = new Dictionary<string, double>();
        static void Skip(object self, string why)
        {
            try
            {
                string key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(self) + "|" + why;
                double now = GameNow(), last;
                if (Told.TryGetValue(key, out last) && now - last < 3.0 && now >= last) return;
                if (Told.Count > 200) Told.Clear();
                Told[key] = now;
                Trace("Collisions: " + NameOf(self) + ": " + why);
            }
            catch { }
        }

        static string RbMotion(object rb)
        {
            if (rb == null) return "none";
            var t = rb.GetType();
            var pv = t.GetProperty("linearVelocity") ?? t.GetProperty("velocity");
            var pk = t.GetProperty("isKinematic");
            float kn = pv == null ? -1f : ((Vector3)pv.GetValue(rb, null)).magnitude * UnityToMetre * MetreSecToKnot;
            bool kin = pk != null && (bool)pk.GetValue(rb, null);
            return kn.ToString("0.00", CultureInfo.InvariantCulture) + " kn" + (kin ? " kinematic" : "");
        }

        static string Describe(object col)
        {
            try
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var t = col.GetType();
                var coll = t.GetProperty("collider", all) == null ? null : t.GetProperty("collider", all).GetValue(col, null) as Component;
                var rb = _pRb.GetValue(col, null) as Component;
                return "other collider '" + (coll == null ? "?" : coll.name) + "' of '" + (coll == null ? "?" : coll.transform.root.name) + "', rigidbody " + (rb == null ? "none" : "'" + rb.name + "'");
            }
            catch { return "?"; }
        }

        /// <summary>The ship (Vessel / Submarine) a relay sits next to.</summary>
        public static object UnitOf(Component relay)
        {
            return _ok && _tObjBase != null ? relay.GetComponent(_tObjBase) : null;
        }

        /// <summary>Postfix of Vessel.OnCollisionEnter(Collision) / Submarine.OnCollisionEnter(Collision): the first step of a contact.</summary>
        public static void Postfix(object __instance, object[] __args)
        {
            if (!Active || __instance == null || __args == null || __args.Length < 1 || __args[0] == null) return;
            try
            {
                if (!_init) Init(__instance, __args[0]);
                if (!_ok) return;
                var me = __instance as Component;
                if (me != null && IsShip(__instance) && me.GetComponent<CollisionRelay>() == null) me.gameObject.AddComponent<CollisionRelay>();   // the steps that follow
                Step(__instance, __args[0]);
            }
            catch (Exception e) { Trace("Collisions: " + e); }
        }

        /// <summary>One physics step of a contact of this ship (Vessel / Submarine) with whatever the Collision says.</summary>
        public static void Step(object self, object col)
        {
            try { if (_ok && Strength > 0f) StepCore(self, col); }
            catch (Exception e) { Trace("Collisions.Step: " + e); }
        }

        static void StepCore(object self, object col)
        {
            var me = self as Component;
            var otherBody = _pRb.GetValue(col, null) as Component;
            if (me == null) return;
            if (otherBody == null) { Skip(self, "no rigidbody on the other side (terrain or a static collider): not a ship"); return; }   // the terrain and other static things: the game's own business
            Component other = otherBody.GetComponent(_tObjBase) ?? otherBody.transform.root.GetComponentInChildren(_tObjBase);
            if (other == null || ReferenceEquals(other, me) || !IsShip(other) || !IsShip(self)) { Skip(self, "the other body is not a ship (" + (other == null ? "no unit on it" : other.GetType().Name) + ")"); return; }
            object comps = _pComps.GetValue(self, null);
            if (comps == null || SubmergedFlooding.IsDestroyed(comps)) { Skip(self, "no compartments or already destroyed"); return; }
            double now = GameNow();
            if (now < QuietStart) { Skip(self, "the first " + QuietStart + " s of the mission (game time " + now.ToString("0.0", CultureInfo.InvariantCulture) + ")"); return; }

            object compsOther = _pComps.GetValue(other, null);
            if (compsOther == null) { Skip(self, "the other ship has no compartments"); return; }
            if (IsRaft(comps) || IsRaft(compsOther)) { Skip(self, "a life raft is in the contact: the game's own business"); return; }   // inflatable rafts are not ships: no slowing, no hull damage from the mod

            // the contact points, the mean normal (horizontal)
            int n = Math.Min(16, Convert.ToInt32(_pCount.GetValue(col, null)));
            if (n < 1) { Skip(self, "no contact points"); return; }
            var pts = new Vector3[n];
            Vector3 normal = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                object cp = _mContact.Invoke(col, new object[] { i });
                pts[i] = (Vector3)_pPoint.GetValue(cp, null);
                normal += (Vector3)_pNormal.GetValue(cp, null);
            }

            // masses (tons): the game's rigidbody mass is the displacement / 40
            object rbSelf = _pRbSelf == null ? null : _pRbSelf.GetValue(self, null);
            if (rbSelf == null) rbSelf = me.GetComponent(otherBody.GetType());
            if (_pMass == null) _pMass = otherBody.GetType().GetProperty("mass", BindingFlags.Instance | BindingFlags.Public);
            if (rbSelf == null || _pMass == null) return;
            float mSelf = Convert.ToSingle(_pMass.GetValue(rbSelf, null)) * MassUnitToTon, mOther = Convert.ToSingle(_pMass.GetValue(otherBody, null)) * MassUnitToTon;
            if (mSelf <= 0f || mOther <= 0f) return;

            // the contact of the pair (the other ship's step of the same contact finds it)
            var ep = Episodes.GetOrCreateValue(comps); var epO = Episodes.GetOrCreateValue(compsOther);
            Pair P = ep.P;
            bool same = P != null && now - P.Touch <= Gap && ((ReferenceEquals(P.UA, self) && ReferenceEquals(P.UB, other)) || (ReferenceEquals(P.UA, other) && ReferenceEquals(P.UB, self)));
            if (!same) P = new Pair { UA = self, UB = other, CA = comps, CB = compsOther, Start = now };
            ep.P = P; epO.P = P;
            bool selfIsA = ReferenceEquals(P.UA, self);
            P.MA = selfIsA ? mSelf : mOther; P.MB = selfIsA ? mOther : mSelf;
            Vector3 between = ((Component)P.UA).transform.position - ((Component)P.UB).transform.position; between.y = 0f;
            normal.y = 0f;
            if (normal.sqrMagnitude < 1e-6f) normal = between;
            if (normal.sqrMagnitude < 1e-6f) return;
            normal.Normalize();
            if (Vector3.Dot(normal, between) < 0f) normal = -normal;      // from B to A
            P.N = normal; P.Touch = now;
            Store(ep, comps, me, pts, n); Store(epO, compsOther, other, pts, n);
            Advance(P, now);
        }

        /// <summary>The contact points in the ship's frame (the ship moves on; the points stay where they touched it) and their heights.</summary>
        static void Store(Episode ep, object comps, Component unit, Vector3[] pts, int n)
        {
            var shipT = (_fShipT == null ? null : _fShipT.GetValue(comps) as Transform) ?? unit.transform;
            ep.Loc = new Vector3[n]; ep.WY = new float[n]; ep.N = n;
            for (int i = 0; i < n; i++) { ep.Loc[i] = shipT.InverseTransformPoint(pts[i]); ep.WY[i] = pts[i].y; }
        }

        static float KnotsSigned(object unit) { return Convert.ToSingle(_mGetV.Invoke(unit, null)); }
        static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude < 1e-8f ? Vector3.forward : v.normalized; }

        /// <summary>
        /// One physics step of a contact, once for the pair. The game moves its ships itself (Vessel: _velocityInKnots, translate along the heading), so
        /// the velocities are the game's, along the headings. The relative velocity is brought towards what a blow leaves: across the contact
        /// (1 - Restitution) of the closing speed gone, along it a share kappa = GripPress * closing / speed of the sliding speed gone (the hulls
        /// scrape and crumple); the change is spread over BrakeTime. The impulse is mu * the change of the relative velocity; each ship's
        /// speed along its own heading changes by its part of it (a ship cannot be pushed sideways by the game's movement), by Braking. The energy
        /// lost, 0.5 * mu * (v^2 - v'^2), is what the hulls take, the bow K times harder than the side.
        /// </summary>
        static void Advance(Pair P, double now)
        {
            if (P.Step >= now) return;
            var ua = P.UA as Component; var ub = P.UB as Component;
            if (ua == null || ub == null) return;
            if (SubmergedFlooding.IsDestroyed(P.CA) || SubmergedFlooding.IsDestroyed(P.CB)) return;
            bool first = P.Step <= 0.0;
            double dt = first ? 0.02 : Math.Min(0.1, now - P.Step);
            P.Step = now;
            float kA = KnotsSigned(P.UA), kB = KnotsSigned(P.UB);
            Vector3 fA = Flat(ua.transform.forward), fB = Flat(ub.transform.forward);
            Vector3 rel = fA * (kA * KnotToUnit) + SlipOf(P.UA) - fB * (kB * KnotToUnit) - SlipOf(P.UB);
            float relKn = rel.magnitude / KnotToUnit;
            bool playerA = IsPlayer(P.UA), playerB = IsPlayer(P.UB);
            if (!AIShips && !playerA && !playerB) return;                // two AI ships: nothing
            if (relKn < MinSpeed) { Skip(P.UA, "relative speed " + relKn.ToString("0.00", CultureInfo.InvariantCulture) + " kn is under the slowest damaging contact"); return; }

            Vector3 n = P.N;
            float vn = Vector3.Dot(rel, n);
            Vector3 vt = rel - vn * n;
            float closing = Math.Max(0f, -vn);
            float kappa = Mathf.Clamp(GripPress * closing / rel.magnitude, 0f, 0.9f);
            Vector3 target = (vn < 0f ? -Restitution * vn : vn) * n + (1f - kappa) * vt;
            float f = (float)(1.0 - Math.Exp(-dt / BrakeTime));
            Vector3 d = f * (target - rel);
            float mu = P.MA * P.MB / (P.MA + P.MB);                                      // the hulls' energy: the real masses
            float aA = Math.Abs(Vector3.Dot(n, fA)), aB = Math.Abs(Vector3.Dot(n, fB));     // how much the contact is along each ship
            float eA = P.MA * (1f + AddedAlong * aA * aA + AddedAcross * WaterResistance * (1f - aA * aA));   // what each ship moves like: with the water it drags along
            float eB = P.MB * (1f + AddedAlong * aB * aB + AddedAcross * WaterResistance * (1f - aB * aB));
            float mue = eA * eB / (eA + eB);
            float e = 0.5f * mu * (rel.sqrMagnitude - (rel + d).sqrMagnitude) * UnityToMetre * UnityToMetre;   // kJ
            if (first) P.Cap = 0.5 * mu * rel.sqrMagnitude * UnityToMetre * UnityToMetre;          // a blow has this energy, not more
            e = (float)Math.Min(e, P.Cap - P.Spent);
            if (e <= 0f) return;
            P.Spent += e;

            // the speeds
            float dA = 0f, dB = 0f;
            if (Braking > 0f)
            {
                float sA = Math.Min(1f, BrakeGain * Braking * mue / eA), sB = Math.Min(1f, BrakeGain * Braking * mue / eB);   // doubled, but a ship never changes by more than the whole relative change
                Vector3 vA = d * sA, vB = -d * sB;     // each ship's change of velocity
                if (AIShips || playerA) { dA = Vector3.Dot(vA, fA) / KnotToUnit; _mSetV.Invoke(P.UA, new object[] { kA + dA }); AddSlip(P.UA, vA - Vector3.Dot(vA, fA) * fA, now); }
                if (AIShips || playerB) { dB = Vector3.Dot(vB, fB) / KnotToUnit; _mSetV.Invoke(P.UB, new object[] { kB + dB }); AddSlip(P.UB, vB - Vector3.Dot(vB, fB) * fB, now); }
            }
            if (PushApart)
            {
                float step = PushSpeed / UnityToMetre * (float)dt;
                ua.transform.Translate(n * (step * P.MB / (P.MA + P.MB)), Space.World);
                ub.transform.Translate(-n * (step * P.MA / (P.MA + P.MB)), Space.World);
            }

            // the hulls
            Hurt(P, true, now, e, relKn, aA);
            Hurt(P, false, now, e, relKn, aB);

            P.Steps++;
            if (P.Steps <= 4 || now - P.LastLog >= LogEvery)
            {
                P.LastLog = now;
                Trace("Collision pair: " + NameOf(P.UA) + " " + kA.ToString("0.0", CultureInfo.InvariantCulture) + " -> " + (kA + dA).ToString("0.0", CultureInfo.InvariantCulture) + " kn, " + NameOf(P.UB) + " " +
                      kB.ToString("0.0", CultureInfo.InvariantCulture) + " -> " + (kB + dB).ToString("0.0", CultureInfo.InvariantCulture) + " kn; step " + P.Steps + ", closing " + (closing / KnotToUnit).ToString("0.0", CultureInfo.InvariantCulture) +
                      " kn, sliding " + (vt.magnitude / KnotToUnit).ToString("0.0", CultureInfo.InvariantCulture) + " kn, kappa " + kappa.ToString("0.00", CultureInfo.InvariantCulture) + ", sideways " + (SlipOf(P.UA).magnitude * UnityToMetre).ToString("0.0", CultureInfo.InvariantCulture) + " / " + (SlipOf(P.UB).magnitude * UnityToMetre).ToString("0.0", CultureInfo.InvariantCulture) + " m/s, lost " + e.ToString("0", CultureInfo.InvariantCulture) + " kJ");
            }
        }

        static bool IsPlayer(object unit)
        {
            try { return _pPlayer != null && (bool)_pPlayer.GetValue(unit, null); } catch { return false; }
        }

        /// <summary>The share of one ship in the energy of a step: the hull damage. a = how much the contact is along the ship (1: bow or stern, 0: side).</summary>
        static void Hurt(Pair P, bool isA, double now, float e, float relKn, float a)
        {
            object unit = isA ? P.UA : P.UB, otherUnit = isA ? P.UB : P.UA, comps = isA ? P.CA : P.CB;
            var me = unit as Component; var other = otherUnit as Component;
            float mSelf = isA ? P.MA : P.MB, mOther = isA ? P.MB : P.MA;
            var ep = Episodes.GetOrCreateValue(comps);
            if (ep.Loc == null || me == null || other == null) return;
            if (!DamageOn || (!AIShips && !IsPlayer(unit))) return;                       // a ship of the AI: not touched
            bool fresh = ep.Last == 0.0 || now - ep.Last > Gap;
            if (fresh) { ep.Start = now; ep.LastLog = now; ep.Steps = 0; ep.Fraction = 0f; ep.Energy = 0; ep.Leak = null; ep.Direct = null; ep.Lost = 0; }
            ep.Last = now;
            if (ep.Fraction >= MaxFraction) { Skip(unit, "the contact has reached the most one contact may do (" + MaxFraction + " compartments)"); return; }
            // the same part of a compartment for both hulls (the crumpling is set by the lighter, weaker ship), the bow / stern 1 / BowStrength of the side;
            // the heavier ship's share of the mass makes it what it was for the lighter ship: 0.5 for equal ships, towards 1 for a boat against a cruiser
            float zone = 1f / (1f + (BowStrength - 1f) * a * a);
            float heavy = Math.Max(mSelf, mOther) / (mSelf + mOther);
            Apply(unit, me, other, comps, ep, fresh, now, e, relKn, mSelf, mOther, heavy * zone);
        }

        /// <summary>The damage of one step of a contact: the energy e (kJ) spent on the ship, where the contact points are.</summary>
        static void Apply(object self, Component me, Component other, object comps, Episode ep, bool fresh, double now, float e, float relKn, float mSelf, float mOther, float scale)
        {
            float fraction = Math.Min(MaxFraction - ep.Fraction, Strength * scale * e / (Toughness * Math.Min(mSelf, mOther)));
            if (fraction <= 0f) return;
            ep.Fraction += fraction; ep.Energy += e;

            // where: the sections by the contact points in the ship's frame
            var ports = _fPort.GetValue(comps) as Array; var stars = _fStar.GetValue(comps) as Array;
            if (ports == null || stars == null || ports.Length < 1 || stars.Length != ports.Length) return;
            int sections = ports.Length;
            if (ep.Leak == null || ep.Leak.Length != 2 * sections) ep.Leak = new float[2 * sections];
            int n = ep.N;
            var z = new float[sections];
            for (int s = 0; s < sections; s++)
            {
                var go = _fObject.GetValue(ports.GetValue(s)) as GameObject;
                z[s] = go == null ? s : go.transform.localPosition.z;
            }
            float sea = DeckCorners.SeaLevel();
            var under = new int[2 * sections]; var over = new int[2 * sections];
            for (int i = 0; i < n; i++)
            {
                Vector3 lp = ep.Loc[i];
                int best = 0; float bd = float.MaxValue;
                for (int s = 0; s < sections; s++) { float d = Math.Abs(z[s] - lp.z); if (d < bd) { bd = d; best = s; } }
                int slot = (lp.x <= 0f ? 0 : sections) + best;               // port first, then starboard
                if ((ep.WY[i] - sea) * UnityToMetre <= HoleBand) under[slot]++; else over[slot]++;
            }

            float gameMax = HullGameMax(comps);
            if (gameMax <= 0f) gameMax = Convert.ToSingle(_fMax.GetValue(ports.GetValue(0)));
            float total = fraction * gameMax;                                 // in the game's points
            float floodMult = _fFloodMult == null ? 1f : Convert.ToSingle(_fFloodMult.GetValue(null));
            int hitSlot = 0, count = 0;
            for (int s = 0; s < 2 * sections; s++) { count += under[s] + over[s]; if (under[s] + over[s] > under[hitSlot] + over[hitSlot]) hitSlot = s; }
            double lost = 0; var report = new System.Text.StringBuilder();
            for (int slot = 0; slot < 2 * sections; slot++)
            {
                int c = under[slot] + over[slot];
                if (c == 0) continue;
                bool port = slot < sections; int sec = slot % sections;
                float dmg = total * c / count;
                object fc = (port ? ports : stars).GetValue(sec);
                float have = Math.Max(0f, Convert.ToSingle(_fCur.GetValue(fc)));
                float taken = Math.Min(have, dmg);
                _fCur.SetValue(fc, have - taken);
                lost += taken;
                float surplus = dmg - taken;
                if (surplus > 0.0001f) lost += Spill(ports, stars, sections, sec, port, surplus * 0.5f);

                // the leak: the game's rule for a hit, raised to what this compartment has taken in the contact so far
                float u = (float)under[slot] / c;
                float pct = _pPct == null ? 100f : Convert.ToSingle(_pPct.GetValue(fc, null));
                float overFactor = pct < 25f ? 0.5f : pct < 50f ? 0.25f : 0f;
                overFactor = Math.Max(overFactor, Mathf.Clamp01((ep.Fraction - 0.1f) / 0.4f));   // a strong blow (from half a compartment on) leaks as at the waterline, a weak one does not
                ep.Leak[slot] += dmg * (u + (1f - u) * overFactor);
                float target = ep.Leak[slot] / gameMax * FloodVuln * floodMult * FloodFactor;
                float now0 = _fRate == null ? 0f : Convert.ToSingle(_fRate.GetValue(fc));
                if (target > 0f && now0 < target) _mIncrease.Invoke(fc, new object[] { target });   // below it: set to it (the game's rule); never added up
                if (report.Length < 200) report.Append(' ').Append(port ? 'P' : 'S').Append(sec + 1).Append(':').Append(dmg.ToString("0.##", CultureInfo.InvariantCulture)).Append("/leak ").Append(target.ToString("0.###", CultureInfo.InvariantCulture));
            }
            ep.Lost += lost;
            double max = _fMaxTotal == null ? 0 : Convert.ToDouble(_fMaxTotal.GetValue(comps), CultureInfo.InvariantCulture);
            if (lost > 0 && max > 0)
            {
                double pctLost = 100.0 * lost / max;
                if (ep.Direct == null) ep.Direct = HitLog.AddCollision(comps, now, hitSlot % sections, hitSlot < sections, pctLost);
                else { ep.Direct[0] = now; ep.Direct[1] += pctLost; }
            }
            ep.Steps++;
            if (fresh || ep.Steps <= 4 || now - ep.LastLog >= LogEvery)
            {
                ep.LastLog = now;
                Trace("Collision: " + NameOf(self) + " with " + NameOf(other) + (fresh ? " starts" : ", step " + ep.Steps + ", " + (now - ep.Start).ToString("0.##", CultureInfo.InvariantCulture) + " s") + ": relative speed " +
                      relKn.ToString("0.0", CultureInfo.InvariantCulture) + " kn, masses " + mSelf.ToString("0", CultureInfo.InvariantCulture) + " / " + mOther.ToString("0", CultureInfo.InvariantCulture) +
                      " t, so far " + ep.Energy.ToString("0", CultureInfo.InvariantCulture) + " kJ = " + ep.Fraction.ToString("0.00", CultureInfo.InvariantCulture) + " compartments' worth, hull lost " +
                      ep.Lost.ToString("0.#", CultureInfo.InvariantCulture) + " points; this step" + report);
            }
        }


        /// <summary>
        /// Every physics step of a ship in a contact: the contact goes on (the speeds, the hulls) whether or not Unity keeps calling OnCollisionStay
        /// (it does not seem to: the ships are moved by the game) - for ForceTime after it began, longer while the steps keep coming.
        /// </summary>
        public static void Tick(object self, ref object comps)
        {
            try
            {
                if (_pComps == null) return;
                double now = GameNow();
                SlipStep(self, now);
                if (comps == null) comps = _pComps.GetValue(self, null);
                Episode ep;
                if (comps == null || !Episodes.TryGetValue(comps, out ep) || ep.P == null) return;
                var P = ep.P;
                if (now <= P.Step || now - P.Start > MaxTime || (now - P.Touch > Gap && now - P.Start > ForceTime)) return;
                Advance(P, now);
            }
            catch (Exception ex) { Trace("Collisions.Tick: " + ex.Message); }
        }

        /// <summary>What a destroyed compartment cannot take goes to its neighbour towards the middle of the ship (same side); returns what was taken.</summary>
        static float Spill(Array ports, Array stars, int sections, int sec, bool port, float amount)
        {
            int mid = sections / 2;
            int dir = sec < mid ? 1 : sec > mid ? -1 : 0;
            if (dir == 0)
            {   // the middle one: to the side with more hull left
                float lo = Left(port ? ports : stars, sec - 1), hi = Left(port ? ports : stars, sec + 1);
                dir = lo >= hi ? -1 : 1;
            }
            int n1 = sec + dir;
            if (n1 < 0 || n1 >= sections) return 0f;
            object fc = (port ? ports : stars).GetValue(n1);
            float have = Math.Max(0f, Convert.ToSingle(_fCur.GetValue(fc)));
            float taken = Math.Min(have, amount);
            if (taken > 0f) _fCur.SetValue(fc, have - taken);
            return taken;
        }

        static float Left(Array side, int i)
        {
            if (i < 0 || i >= side.Length) return 0f;
            return Math.Max(0f, Convert.ToSingle(_fCur.GetValue(side.GetValue(i))));
        }
    }
}
