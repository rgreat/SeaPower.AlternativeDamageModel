using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace AlternativeDamageModel
{
    /// <summary>
    /// [Collisions] OwnHelicopters: a ship takes no notice of the helicopters of its own deck.
    /// 1. The game's collision avoidance (CollisionAvoidanceJobScheduler) is meant to skip a helicopter that has just taken off from the ship
    ///    (_ignoreInCollisionAvoidanceAfterLaunch, 10 s), but its job version asks "objectBase._homeBase == objectBase" - the object with itself,
    ///    which is never true for a helicopter - so the ship always "avoids" its own helicopters: the state "Avoiding collision" puts the rudder to
    ///    +-50 degrees (the sign follows the side the helicopter is on) and the ship rolls in the turns. After the job's results are handed to the
    ///    units (CollisionAvoidanceJobScheduler.Complete) this clears UnitToAvoid of a ship when it is a helicopter whose home base is that ship.
    /// 2. The (kinematic) helicopter's colliders touch the hull: the contact throws the ship about. The pair (every collider of the helicopter with
    ///    every collider of its home ship) is put on the physics' ignore list once.
    /// </summary>
    static class OwnHelicopters
    {
        public static bool On = true;
        public static Assembly Asm;
        /// <summary>Harmony postfix target (no arguments).</summary>
        public static void Postfix() { Complete(Asm); }
        public static Action<string> Trace = s => { };

        static bool _init, _ok;
        static Type _tShip, _tSub, _tHeli, _tObj, _tCollider, _tPhysics, _tSingleton, _tManager;
        static Fld<object> _fHome;
        static Prop<object> _pAvoid;                     // ObjectBase.CollisionAvoidance
        static Prop<object> _pUnit;                      // ObjectCollisionAvoidance.UnitToAvoid
        static MethodInfo _mSetUnit, _mIgnore;
        static FieldInfo _fUnits;
        static PropertyInfo _pInstance;
        static object _manager;
        static readonly ConditionalWeakTable<object, object> Ignored = new ConditionalWeakTable<object, object>();   // helicopter -> the ship its colliders ignore
        static int _cleared;

        static void Init(Assembly asm)
        {
            _init = true;
            try
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                _tShip = asm.GetType("SeaPower.Vessel"); _tSub = asm.GetType("SeaPower.Submarine"); _tHeli = asm.GetType("SeaPower.Helicopter");
                _tObj = asm.GetType("SeaPower.ObjectBase"); _tSingleton = asm.GetType("SeaPower.Singleton`1"); _tManager = asm.GetType("SeaPower.ObjectsManager");
                var tCa = asm.GetType("SeaPower.ObjectCollisionAvoidance");
                if (_tObj == null || _tHeli == null || tCa == null || _tSingleton == null || _tManager == null) { Trace("OwnHelicopters: types not found (game updated?): off"); return; }
                _fHome = new Fld<object>(_tObj.GetField("_homeBase", all));
                _pAvoid = new Prop<object>(_tObj.GetProperty("CollisionAvoidance", all));
                var pu = tCa.GetProperty("UnitToAvoid", all);
                _pUnit = new Prop<object>(pu);
                _mSetUnit = pu == null ? null : pu.GetSetMethod(true);
                _fUnits = _tManager.GetField("_listOfAllUnits", all);
                _pInstance = _tSingleton.MakeGenericType(_tManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
                var tCol = typeof(Collider);
                _tCollider = tCol; _tPhysics = typeof(Physics);
                _mIgnore = _tPhysics.GetMethod("IgnoreCollision", new[] { tCol, tCol, typeof(bool) });
                _ok = _tObj.GetField("_homeBase", all) != null && pu != null && _mSetUnit != null && _fUnits != null && _pInstance != null;
                if (!_ok) Trace("OwnHelicopters: members missing (game updated?): off");
            }
            catch (Exception e) { Trace("OwnHelicopters.Init: " + e.Message); }
        }

        static bool IsShip(object o)
        {
            var t = o.GetType();
            return (_tShip != null && _tShip.IsAssignableFrom(t)) || (_tSub != null && _tSub.IsAssignableFrom(t));
        }

        /// <summary>Postfix of CollisionAvoidanceJobScheduler.Complete: the units have just got the job's UnitToAvoid.</summary>
        public static void Complete(Assembly asm)
        {
            if (!On) return;
            try
            {
                if (!_init) Init(asm);
                if (!_ok) return;
                var mgr = _manager as UnityEngine.Object;
                if (mgr == null) { _manager = _pInstance.GetValue(null, null); mgr = _manager as UnityEngine.Object; if (mgr == null) return; }
                var units = _fUnits.GetValue(_manager) as IList;
                if (units == null) return;
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u == null) continue;
                    if (_tHeli.IsInstanceOfType(u)) { IgnoreContacts(u); continue; }
                    if (!IsShip(u)) continue;
                    var ca = _pAvoid.Get(u);
                    if (ca == null) continue;
                    var other = _pUnit.Get(ca);
                    if (other == null || !_tHeli.IsInstanceOfType(other) || !ReferenceEquals(_fHome.Get(other), u)) continue;
                    _mSetUnit.Invoke(ca, new object[] { null });
                    Note(u);
                }
            }
            catch (Exception e) { Trace("OwnHelicopters: " + e.Message); }
        }

        static void Note(object ship)
        {
            if (++_cleared > 1 && _cleared % 200 != 0) return;
            Trace("OwnHelicopters: a ship no longer avoids its own helicopter (the game would turn its rudder to +-50) - cleared " + _cleared + " times so far");
        }

        /// <summary>Puts every collider of the helicopter and every collider of its home ship on the physics' ignore list (once per pair).</summary>
        static void IgnoreContacts(object heli)
        {
            if (_mIgnore == null) return;
            var home = _fHome.Get(heli);
            if (home == null || !IsShip(home)) return;
            object done;
            if (Ignored.TryGetValue(heli, out done) && ReferenceEquals(done, home)) return;
            var ch = heli as Component; var cs = home as Component;
            if (ch == null || cs == null) return;
            var a = ch.GetComponentsInChildren<Collider>(true);
            var b = cs.GetComponentsInChildren<Collider>(true);
            if (a.Length == 0 || b.Length == 0) return;
            int n = 0;
            for (int i = 0; i < a.Length; i++)
                for (int j = 0; j < b.Length; j++)
                    if (a[i] != null && b[j] != null && a[i] != b[j]) { Physics.IgnoreCollision(a[i], b[j], true); n++; }
            Ignored.Remove(heli); Ignored.Add(heli, home);
            Trace("OwnHelicopters: " + ch.name + " and its ship " + cs.name + ": " + n + " collider pairs ignore each other");
        }
    }
}
