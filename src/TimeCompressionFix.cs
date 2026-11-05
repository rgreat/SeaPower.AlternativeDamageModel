using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace AlternativeDamageModel
{
    /// <summary>
    /// [General] FixHighTimeCompressionTrimError (default on): fixes the game's own trim error above 10x time compression.
    ///
    /// The cause (Vessel.OnFixedUpdate): applyMainThrust moves a ship not by a force but by transform.Translate(velocity *
    /// GameTime.fixedDeltaTime), before Compartments.OnFixedUpdate computes the buoyancy forces. There every compartment's point
    /// comes from the (already moved) transform, while the centre of mass the torque is taken about - _centerOfGravity =
    /// _rigidbody.worldCenterOfMass - comes from the physics body, which stays at the old position until the next physics step
    /// (Physics.autoSyncTransforms is off; the game calls SyncTransforms by hand). So every buoyancy force gets a lever arm
    /// that is one movement step too far forward: an extra pitch torque Cross(step, total buoyancy). Up to 10x the step is
    /// constant (part of the trim a ship has at speed, which the game is tuned with); above GameTime._physicsTimeScaleCap the
    /// game stretches GameTime.fixedDeltaTime by TimeCompression / cap, the step with it, and the trim grows linearly with
    /// the time compression - measured on wp_pt_p6 (25.5 m, 47 kn): 2.6° up to 10x, 3.3 at 20x, 5.5 at 50x, 9.1 at 100x.
    /// Short, fast ships suffer most (the same step, a small restoring moment).
    ///
    /// Fix: a transpiler on Compartments.OnFixedUpdate after its read of Rigidbody.worldCenterOfMass moves that centre of
    /// mass forward by the excess of this tick's step over the 10x one: velocity * (GameTime.fixedDeltaTime - Time.fixedDeltaTime).
    /// Up to 10x that is exactly zero (the game unchanged); above it the error stays the one at 10x, whatever the time
    /// compression. Surface ships only (Compartments._vessel); submarines move and dive by their own rules.
    /// </summary>
    static class TimeCompressionFix
    {
        public static Func<bool> IsEnabled = () => false;
        public static Action<string> Trace = s => { };
        public static Action<string> Warn = s => { };

        static FieldInfo _fVessel, _fVelocity;
        static Func<float> _gameFixedDt;

        /// <summary>Resolves what CoG needs and patches Compartments.OnFixedUpdate; false if anything is missing.</summary>
        public static bool Init(Harmony harmony, Assembly asm)
        {
            try
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Type tComp = asm.GetType("SeaPower.Compartments");
                Type tObj = asm.GetType("SeaPower.ObjectBase");
                Type tGameTime = asm.GetType("SeaPower.GameTime");
                _fVessel = tComp?.GetField("_vessel", all);
                _fVelocity = tObj?.GetField("_velocityVecInUnity", all);
                var getter = tGameTime?.GetProperty("fixedDeltaTime", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod(true);
                MethodInfo target = tComp?.GetMethod("OnFixedUpdate", all);
                if (_fVessel == null || _fVelocity == null || getter == null || target == null)
                { Warn("Trim fix at high time compression: game members not found (game updated?); the game's own trim is used."); return false; }
                _gameFixedDt = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), getter);
                harmony.Patch(target, transpiler: new HarmonyMethod(typeof(TimeCompressionFix).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic)));
                Trace("patched Compartments.OnFixedUpdate (centre of mass at high time compression)");
                return true;
            }
            catch (Exception e) { Warn("Trim fix at high time compression failed: " + e.Message); Trace(e.ToString()); return false; }
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            MethodInfo cog = typeof(TimeCompressionFix).GetMethod(nameof(CoG), BindingFlags.Static | BindingFlags.Public);
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var mi = list[i].operand as MethodInfo;
                if ((list[i].opcode != OpCodes.Call && list[i].opcode != OpCodes.Callvirt) || mi == null || mi.Name != "get_worldCenterOfMass") continue;
                list.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
                list.Insert(i + 2, new CodeInstruction(OpCodes.Call, cog));
                i += 2; n++;
            }
            if (n != 1) Warn("Trim fix at high time compression: Rigidbody.worldCenterOfMass read " + n + " times in Compartments.OnFixedUpdate, 1 expected (game updated?).");
            return list;
        }

        /// <summary>The centre of mass the buoyancy torque is taken about, moved forward by the excess of this tick's
        /// movement step over the one at 10x (zero at 10x and below).</summary>
        public static Vector3 CoG(Vector3 com, object comp)
        {
            long _p0 = Prof.Start();
            try { return CoGCore(com, comp); }
            finally { Prof.Stop(Prof.CoG, _p0); }
        }

        static Vector3 CoGCore(Vector3 com, object comp)
        {
            if (!IsEnabled()) return com;
            try
            {
                float excess = _gameFixedDt() - Time.fixedDeltaTime;
                if (excess <= 1e-6f) return com;
                object vessel = _fVessel.GetValue(comp);
                if (vessel == null) return com;
                return com + (Vector3)_fVelocity.GetValue(vessel) * excess;
            }
            catch { return com; }
        }
    }
}
