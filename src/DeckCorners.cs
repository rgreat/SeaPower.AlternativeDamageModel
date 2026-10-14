using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AlternativeDamageModel
{
    /// <summary>
    /// [General] ShowDeckCorners: a debug visualisation, for the player's currently selected ship only - a
    /// centreline point at the bow and at the stern, plus one port/starboard pair per damage-model section
    /// (Sections, a fixed constant of 5 - the game's damage model always has exactly 5 sections, each with
    /// its own port and starboard compartment - so 10 points total), each at that section's own centre z, as
    /// a line segment from the current wave surface at that point up (or down) to the estimated deck height
    /// there. The side points line up with the ship's own actual damage-model sections, meant for telling
    /// whether a given section is taking water over the side (not just the ends pitching under).
    /// [General] ShowHullCollider: the same ship's hull-only collider meshes, drawn as a translucent overlay (to
    /// compare against the visible 3D model directly) plus a bright wireframe of the collider mesh's own actual
    /// triangle edges over it - the fill alone doesn't say whether a collider is smooth or a coarse, low-poly
    /// shape (long, shallow triangles - wp_ptg_komar was the original example), and doesn't make it easy to see
    /// exactly where a deck-corner point lands relative to the real edges; the wireframe answers both directly.
    /// [General] HideShipModel: hides the same ship's own 3D model (every MeshRenderer under its GameObject,
    /// found and disabled fresh each time this is turned on) so the lines/overlay above are seen on their own,
    /// with nothing of the real hull to compare them against or to occlude them - for diagnosing the other two
    /// without the visible model getting in the way. Turning it back off restores each MeshRenderer's own last
    /// state (not a blanket "enabled = true"), so a part the game itself had hidden for another reason - damage,
    /// a submodel swapped out, HideWithActiveDamageModel - stays exactly as the game left it.
    ///
    /// "Deck height" here means the top of the ship's HULL-ONLY geometry - turrets, masts, radars, funnels, the
    /// bridge and every other sub-model are deliberately excluded, since they say nothing about where the hull
    /// itself sits relative to the water. The hull is identified by name: every ship's [Colliders] ini section
    /// was found to key its hull pieces "coll_hull..." (one piece on a small boat, several on a destroyer, up to
    /// 8+ on a cruiser, named coll_hull1a/b/c... or coll_hull_1/_2...) - confirmed across 5 different ships
    /// including a kitbashed one (hull pieces stayed coll_hull* there too, only some of its OTHER sub-models were
    /// borrowed, by mesh name, from a sister hull). The game itself matches collider names this same way in one
    /// place (UpdateFireState's wheelhouse lookup: exact "coll_ph" first, then any key starting with it) - the
    /// same pattern is used here for "coll_hull". If a ship has no coll_hull* collider (a naming exception the
    /// game didn't need to be consistent about), its lines are simply not drawn and a warning is logged once;
    /// nothing is guessed from unrelated geometry.
    ///
    /// Height and the port/starboard edge are found directly from the hull colliders' own mesh data (TryHullTriangles),
    /// which puts a point exactly on the collider's real edge. There is no approximate fallback. A mesh with Read/Write
    /// disabled (isReadable == false; seen for civ_ms_andizhan) is read back from the GPU buffers (TryGpuMesh); if
    /// that fails too, the ship gets no points, keeps the old flooding rules and shows an orange Hull Collider overlay.
    ///
    /// The right edge isn't "the widest point of the cross-section" (a bulge below the waterline reads too low) nor
    /// "the highest point" (deck camber pulls it inboard). It is the corner where the two meet, found in two passes
    /// (DeckCornerAtMesh): first the true outboard edge at this z, from every triangle edge crossing the z plane
    /// (exact, by linear interpolation); then, among only the points within a thin strip of that edge, the highest.
    ///
    ///
    /// Runtime access: SeaPower.ObjectBaseParameters keeps a Dictionary&lt;string, MeshCollider&gt;
    /// _meshCollidersNames, built once when the ship is loaded and keyed by exactly these ini names - so the
    /// live hull MeshColliders are read directly from it, no re-parsing of ini files needed. Their mesh geometry,
    /// taken in the ship's own local frame (SeaPower.Compartments._compartmentsTransform - the same frame the
    /// game's own buoyancy sampling uses), give the ship's points once, when it's selected; every frame only
    /// re-transforms those cached local points by the ship's current local-to-world matrix, so the lines follow
    /// heading, roll, pitch and heave exactly as the hull itself moves. The translucent overlay (ShowHullCollider)
    /// is NOT parented onto the collider's own transform: with UseKinematicCollision on (every ship checked so
    /// far), the game deactivates the whole GameObject hierarchy holding the non-kinematic coll_hull* pieces once
    /// kinematic collision takes over, and a child of an inactive parent never renders - so the overlay is instead
    /// parented onto the ship's own always-active _shipTransform, given the collider's current world position,
    /// rotation and scale once, and from then on just rides along as an ordinary active child of that root.
    /// Wave height at each point: Ocean.Instance.QueryWaves(x, z, true) - Ocean isn't in any of the referenced
    /// DLLs (it isn't even Seapower-Scripts; found by scanning loaded assemblies for a type named "Ocean"),
    /// reached by reflection like the particle system calls in FireSmoke.
    /// </summary>
    static class DeckCorners
    {
        public static bool Model;                       // [General] ShowDeckCorners
        public static bool ShowCollider;                 // [General] ShowHullCollider
        public static int ModelView;                     // [General] ShipModelView: 0 Show, 1 Translucent, 2 Hide
        public static Action<string> Trace = s => { };
        public static Action<string> Warn = s => { };

        static bool _ok;
        static FieldInfo _fUnit, _fObp, _fMeshNames, _fCompTransform;
        static PropertyInfo _pCompartments, _pOceanInstance, _pSharedMesh;
        static MethodInfo _mQueryWaves;
        static Type _tUnitStatusVm;
        // UnityEngine.MeshCollider lives in UnityEngine.PhysicsModule, which isn't one of this mod's referenced DLLs
        // (like ParticleSystem in FireSmoke.cs) - its one property used here (sharedMesh) is read by reflection;
        // everything else works through its Component base (already referenced via UnityEngine.CoreModule).

        /// <summary>Patches SeapowerUI.ViewModels.UnitStatusViewModel.SwitchUnit (the player's unit-selection panel)
        /// so the currently selected ship is known here regardless of which tab (if any) is open.</summary>
        public static bool Init(Harmony harmony, Assembly asm)
        {
            try
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                _tUnitStatusVm = asm.GetType("SeapowerUI.ViewModels.UnitStatusViewModel");
                Type tObjBase = asm.GetType("SeaPower.ObjectBase");
                Type tObp = asm.GetType("SeaPower.ObjectBaseParameters");
                Type tComp = asm.GetType("SeaPower.Compartments");
                if (_tUnitStatusVm == null || tObjBase == null || tObp == null || tComp == null)
                { Warn("Deck corners: SeaPower/SeapowerUI types not found; the debug visualisation is off."); return false; }

                _fUnit = _tUnitStatusVm.GetField("_unit", all);
                MethodInfo mSwitch = _tUnitStatusVm.GetMethod("SwitchUnit", all);
                _pCompartments = tObjBase.GetProperty("Compartments", all);
                _fObp = tObjBase.GetField("_obp", all);
                _fMeshNames = tObp.GetField("_meshCollidersNames", all);
                _fCompTransform = tComp.GetField("_compartmentsTransform", all);
                Type tMeshCollider = Type.GetType("UnityEngine.MeshCollider, UnityEngine.PhysicsModule");
                _pSharedMesh = tMeshCollider == null ? null : tMeshCollider.GetProperty("sharedMesh", all);
                if (_fUnit == null || mSwitch == null || _pCompartments == null || _fObp == null || _fMeshNames == null || _fCompTransform == null || _pSharedMesh == null)
                { Warn("Deck corners: expected fields not found (game updated?); the debug visualisation is off."); return false; }

                harmony.Patch(mSwitch, postfix: new HarmonyMethod(typeof(DeckCorners).GetMethod("OnUnitSwitched", BindingFlags.Static | BindingFlags.NonPublic)));
                FindOcean();
                _ok = true;
                return true;
            }
            catch (Exception e) { Warn("Deck corners init failed: " + e.Message); Trace(e.ToString()); return false; }
        }

        static void FindOcean()
        {
            try
            {
                Type tOcean = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = a.GetTypes(); } catch { continue; }
                    foreach (var t in types) if (t.Name == "Ocean") { tOcean = t; break; }
                    if (tOcean != null) break;
                }
                if (tOcean == null) { Warn("Deck corners: no 'Ocean' type found; lines will start at deck level (no wave height)."); return; }
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                _pOceanInstance = tOcean.GetProperty("Instance", all);
                _mQueryWaves = tOcean.GetMethod("QueryWaves", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[] { typeof(float), typeof(float), typeof(bool) }, null);
                if (_pOceanInstance == null || _mQueryWaves == null)
                    Warn("Deck corners: Ocean.Instance.QueryWaves not found; lines will start at deck level (no wave height).");
            }
            catch (Exception e) { Trace("Deck corners FindOcean: " + e.Message); }
        }

        static void OnUnitSwitched(object __instance)
        {
            try { Select(_fUnit.GetValue(__instance)); }
            catch (Exception e) { Trace("Deck corners OnUnitSwitched: " + e.Message); }
        }

        // ------------------------------------------------------------ the selected ship

        static object _unit;                 // SeaPower.ObjectBase, as UnityEngine.Object for the alive-check
        static Transform _shipTransform;      // Compartments._compartmentsTransform
        static Vector3[] _points;             // local to _shipTransform; bow centre, stern centre, then one port/
                                               // starboard pair per damage-model section (PointCount = 2 + Sections*2,
                                               // a fixed constant - see LocalPoints). null while none/unusable
        static List<Component> _hullPieces;   // the ship's own coll_hull* MeshColliders, for the translucent overlay
        static float _lineWidth = 0.01f;
        static readonly HashSet<string> _warnedOnce = new HashSet<string>();

        // HideShipModel: the selected ship's own MeshRenderers, found fresh whenever the toggle turns on (not
        // cached across frames - damage or a loadout change can add/remove submodels while it's off), with each
        // one's own .enabled value saved right before being forced off, so turning the toggle off again restores
        // exactly that, not a blanket "visible".
        static List<Renderer> _shipRenderers;
        static List<Material[]> _shipOrigMaterials;   // the game's own (shared) materials, put back exactly
        static List<Material[]> _shipGhostMaterials;  // our transparent copies, destroyed on restore
        static List<bool> _shipWasEnabled;            // Hide: each renderer's own .enabled before it was forced off
        static int _appliedView;                      // the mode the renderers are in now (0: untouched)
        static bool _hidingModel;
        const float GhostAlpha = 0.3f;                // 70 % transparent

        /// <summary>What is known about a ship's deck points: computed once per ship (first Select or first flooding tick) and kept.</summary>
        public sealed class ShipPoints
        {
            public bool Exact;                 // the points are usable (full mesh algorithm, collider length ok)
            public Vector3[] Points;           // local to ShipT, 10 (port/starboard per section, from the stern: z ascending); null if !Exact
            public float Width = 0.01f;
            public Transform ShipT;            // Compartments._compartmentsTransform
            public List<Component> Hull;       // the ship's coll_hull* MeshColliders (may be empty)
        }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, ShipPoints> _shipPoints = new System.Runtime.CompilerServices.ConditionalWeakTable<object, ShipPoints>();

        /// <summary>Deck points of any ship (unit = SeaPower.ObjectBase); null if the feature did not start or the ship is not ready yet
        /// (tried again next time). A ship without usable points gets an object with Exact = false (kept).</summary>
        public static ShipPoints PointsOf(object unit)
        {
            if (!_ok || unit == null) return null;
            ShipPoints sp;
            if (_shipPoints.TryGetValue(unit, out sp)) return sp;
            bool keep;
            sp = Compute(unit, out keep);
            if (sp != null && keep) _shipPoints.Add(unit, sp);
            return sp;
        }

        static ShipPoints Compute(object unit, out bool keep)
        {
            keep = false;
            try
            {
                object comp = _pCompartments.GetValue(unit, null);
                if (comp == null) { keep = true; return new ShipPoints(); }          // not a ship with compartments
                object obp = _fObp.GetValue(unit);
                if (obp == null) { Trace("Deck corners: '" + SafeName(unit) + "' has no ObjectBaseParameters; collider overlay and points are off."); return null; }
                var hull = HullColliders(obp);
                if (hull.Count == 0)
                {
                    string name = SafeName(unit);
                    if (_warnedOnce.Add(name)) Warn("Deck corners: '" + name + "' has no coll_hull* collider; its lines and collider overlay are off.");
                    keep = true; return new ShipPoints { Hull = hull };
                }
                Transform shipT = _fCompTransform.GetValue(comp) as Transform;
                if (shipT == null) { Trace("Deck corners: '" + SafeName(unit) + "' has no compartments transform; collider overlay and points are off."); return null; }
                var sp = new ShipPoints { ShipT = shipT, Hull = hull };
                try
                {
                    int n = 0;
                    foreach (var mc in hull)
                    {
                        Mesh ms = SharedMesh(mc);
                        var bb = PieceBoxes(shipT, new List<Component> { mc })[0];
                        Trace("Deck corners hull piece " + (++n) + "/" + hull.Count + " '" + mc.gameObject.name + "' mesh '" + ms.name + "' verts " + ms.vertexCount + (ms.isReadable ? "" : " (unreadable)") + " active " + mc.gameObject.activeInHierarchy + ", ship-local z " + bb.Min.z.ToString("F1") + ".." + bb.Max.z.ToString("F1") + " x " + bb.Min.x.ToString("F1") + ".." + bb.Max.x.ToString("F1") + " y " + bb.Min.y.ToString("F1") + ".." + bb.Max.y.ToString("F1"));
                    }
                }
                catch (Exception e) { Trace("Deck corners hull piece log: " + e.Message); }
                Vector3[] points = null; bool exact = false; float width = 0.01f;
                bool sizeOk = ColliderLengthOk(unit, obp, shipT, hull);
                try { exact = sizeOk && LocalPoints(shipT, hull, out points, out width); }
                catch (Exception e) { Trace("Deck corners LocalPoints '" + SafeName(unit) + "': " + e); exact = false; }
                sp.Exact = exact; sp.Points = exact ? points : null; sp.Width = width;
                keep = true; return sp;
            }
            catch (Exception e) { Trace("Deck corners Compute: " + e.Message); return null; }
        }

        static void Select(object unit)
        {
            ClearColliderOverlays();
            RestoreShipModel();
            _unit = null; _points = null; _shipTransform = null; _hullPieces = null; PointsExact = false;
            if (unit == null) return;
            try
            {
                object comp = _pCompartments.GetValue(unit, null);
                if (comp == null) return;          // not a ship with compartments (aircraft, weapon, ...)
                // The unit is remembered first, whatever else fails below: the ship-model view (translucent / hidden)
                // needs only the unit, and must not depend on the collider or the points being available.
                _unit = unit;
                var sp = PointsOf(unit);
                if (sp == null || sp.ShipT == null || sp.Hull == null || sp.Hull.Count == 0) return;
                _shipTransform = sp.ShipT; _hullPieces = sp.Hull; _lineWidth = sp.Width;
                PointsExact = sp.Exact;
                _points = sp.Exact ? sp.Points : null;   // no points: the ship keeps the old rules; the collider overlay (ShowHullCollider) is orange
                RebuildVisual(sp.Exact ? sp.Points.Length : 0);
            }
            catch (Exception e) { Trace("Deck corners Select: " + e.Message); }
        }

        /// <summary>A hull collider whose length differs from the ship's own length (ini Length, metres) by more than
        /// MaxLengthMismatch is treated as bad (a broken or partial collider mesh in a mod): no points, old rules,
        /// orange overlay. If the length is unknown (0) the check is skipped.</summary>
        const float MaxLengthMismatch = 0.10f;
        const float UnityToMetre = 67.200066f;   // the game's UNITY_TO_METER
        static bool ColliderLengthOk(object unit, object obp, Transform shipT, List<Component> hull)
        {
            try
            {
                var pl = obp.GetType().GetProperty("_length", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pl == null) return true;
                float shipLen = Convert.ToSingle(pl.GetValue(obp, null));
                if (shipLen <= 0.5f) return true;
                var pieces = PieceBoxes(shipT, hull);
                if (pieces.Count == 0) return true;
                float zMin = pieces[0].Min.z, zMax = pieces[0].Max.z;
                foreach (var p in pieces) { zMin = Mathf.Min(zMin, p.Min.z); zMax = Mathf.Max(zMax, p.Max.z); }
                // world units are not metres: the game's METER_TO_UNITY = 0.0148809375 (1 unit = 67.2 m)
                float collLen = (zMax - zMin) * Mathf.Abs(shipT.lossyScale.z) * UnityToMetre;
                float diff = Mathf.Abs(collLen - shipLen) / shipLen;
                Trace("Deck corners: hull collider length " + collLen.ToString("F1") + " m, ship length " + shipLen.ToString("F1") + " m (" + (diff * 100f).ToString("F0") + "% off)");
                if (diff > MaxLengthMismatch)
                {
                    if (_warnedOnce.Add("badlen:" + SafeName(unit))) Warn("Deck corners: '" + SafeName(unit) + "' hull collider is " + collLen.ToString("F0") + " m long vs ship " + shipLen.ToString("F0") + " m; treated as bad (no deck points, old flooding rules).");
                    return false;
                }
                return true;
            }
            catch (Exception e) { Trace("Deck corners ColliderLengthOk: " + e.Message); return true; }
        }

        static string SafeName(object unit)
        {
            try { var go = (unit as Component)?.gameObject; return go != null ? go.name : unit.ToString(); }
            catch { return "?"; }
        }

        // The game's damage-model architecture always has exactly 5 sections (each with its own port/starboard
        // compartment, per Compartments) - a fixed fact of the game, not something read per-ship. Confirmed by
        // the user: the earlier per-ship Compartments._compartmentsCount reflection read was unneeded churn.
        const int Sections = 5;
        const int PointCount = Sections * 2;   // one port/starboard pair per section = 10
        const float InwardShift = 0.25f;       // the points sit this share of their distance from the centreline closer to it than where they were found

        static GameObject ShipGameObject(object unit)
        {
            try { return (unit as Component)?.gameObject; }
            catch { return null; }
        }

        /// <summary>Puts back whatever .enabled HideShipModel found on each MeshRenderer right before it forced
        /// them off - called before switching to another ship (or none) and whenever the toggle turns off, so a
        /// ship is never left with its model hidden once this feature stops touching it.</summary>
        static void RestoreShipModel()
        {
            if (_hidingModel && _shipRenderers != null)
                for (int i = 0; i < _shipRenderers.Count; i++)
                {
                    try
                    {
                        if (_shipRenderers[i] == null) continue;
                        if (_appliedView == 2) _shipRenderers[i].enabled = _shipWasEnabled[i];
                        else _shipRenderers[i].sharedMaterials = _shipOrigMaterials[i];
                    } catch { }
                    try { if (_shipGhostMaterials[i] != null) foreach (var m in _shipGhostMaterials[i]) if (m != null) UnityEngine.Object.Destroy(m); } catch { }
                }
            _hidingModel = false; _appliedView = 0;
            ForgetOpaqueTextures();
            _shipRenderers = null; _shipOrigMaterials = null; _shipGhostMaterials = null; _shipWasEnabled = null;
        }

        // Ship albedo textures often carry something else in the alpha channel (gloss...), and Sprites/Default multiplies the colour by it -
        // the ghost came out far more transparent than GhostAlpha. So each texture is copied once (<= 2048 px) with alpha 255; a texture with
        // real cut-out (more than 2% of the pixels nearly transparent - rails, rigging, nets) keeps its alpha.
        static readonly Dictionary<Texture, Texture> _opaqueTex = new Dictionary<Texture, Texture>();

        static Texture OpaqueCopy(Texture t)
        {
            Texture done;
            if (_opaqueTex.TryGetValue(t, out done)) return done;
            Texture result = t;
            try
            {
                int w = t.width, h = t.height;
                while (w * h > 2048 * 2048) { w /= 2; h /= 2; }
                if (w >= 1 && h >= 1)
                {
                    var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                    var prev = RenderTexture.active;
                    try
                    {
                        Graphics.Blit(t, rt);
                        RenderTexture.active = rt;
                        var tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
                        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                        var px = tex.GetPixels32();
                        int low = 0;
                        for (int i = 0; i < px.Length; i++) if (px[i].a < 26) low++;
                        if (low * 50 > px.Length) UnityEngine.Object.Destroy(tex);   // cut-out: leave the original
                        else
                        {
                            for (int i = 0; i < px.Length; i++) px[i].a = 255;
                            tex.SetPixels32(px); tex.Apply(true);
                            result = tex;
                        }
                    }
                    finally { RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt); }
                }
            }
            catch (Exception e) { Trace("Ghost model texture: " + e.Message); }
            _opaqueTex[t] = result;
            return result;
        }

        static void ForgetOpaqueTextures()
        {
            foreach (var kv in _opaqueTex) if (kv.Value != null && kv.Value != kv.Key) UnityEngine.Object.Destroy(kv.Value);
            _opaqueTex.Clear();
        }

        /// <summary>A hull part: the renderer's own object or any of its parents below the ship's root has "hull" in its name (mods have hull-type submodels).</summary>
        static bool IsHullPart(Renderer r, GameObject root)
        {
            try
            {
                for (var t = r.transform; t != null && t.gameObject != root; t = t.parent)
                    if (t.name.IndexOf("hull", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch { }
            return false;
        }

        static Shader _ghostShader;
        static readonly HashSet<string> _ghostSeen = new HashSet<string>();
        static readonly string[] TexProps = { "_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo", "_Diffuse", "_AlbedoMap" };
        static readonly string[] ColProps = { "_Color", "_BaseColor", "_Tint", "_Albedo_Color" };

        /// <summary>
        /// A transparent copy of the game's material. The ship shaders come from asset bundles and are not known to take alpha, so the copy is
        /// built on a shader that is surely in the build and always blends ("Sprites/Default", which the game itself uses): the main texture and
        /// colour of the original are copied, alpha is GhostAlpha. (Lighting is lost - this is a debug view.) Fallback: the old way, flipping
        /// the original material into Standard-style transparency.
        /// </summary>
        static Material MakeGhost(Material src, bool hull)
        {
            string sh = src.shader != null ? src.shader.name : "?";
            if (_ghostSeen.Add(sh)) Trace("Ghost model: material shader '" + sh + "'");
            if (_ghostShader == null) _ghostShader = Shader.Find("Sprites/Default");
            if (_ghostShader != null)
            {
                var m = new Material(_ghostShader);
                foreach (var tp in TexProps)
                    if (src.HasProperty(tp)) { var t = src.GetTexture(tp); if (t != null) { m.mainTexture = hull ? OpaqueCopy(t) : t; try { m.mainTextureScale = src.GetTextureScale(tp); m.mainTextureOffset = src.GetTextureOffset(tp); } catch { } break; } }
                Color c = Color.white;
                foreach (var cp in ColProps) if (src.HasProperty(cp)) { c = src.GetColor(cp); break; }
                c.a = GhostAlpha;
                m.color = c;
                m.renderQueue = 3000;
                return m;
            }
            var g = new Material(src);
            if (g.HasProperty("_Mode")) g.SetFloat("_Mode", 3f);
            if (g.HasProperty("_SrcBlend")) g.SetInt("_SrcBlend", 5);
            if (g.HasProperty("_DstBlend")) g.SetInt("_DstBlend", 10);
            if (g.HasProperty("_ZWrite")) g.SetInt("_ZWrite", 0);
            g.DisableKeyword("_ALPHATEST_ON"); g.EnableKeyword("_ALPHABLEND_ON"); g.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            g.renderQueue = 3000;
            if (g.HasProperty("_Color")) { var cc = g.color; cc.a = GhostAlpha; g.color = cc; }
            return g;
        }

        static int _hideCheck;

        /// <summary>HideShipModel, applied/undone as the toggle and the selected ship change: the ship's whole model (every MeshRenderer /
        /// SkinnedMeshRenderer under its GameObject, submodels included) becomes 70 % transparent. Each renderer gets transparent copies of
        /// its materials; the game's own materials are put back untouched when the toggle turns off or another ship is selected. The renderer
        /// list is re-scanned about once a second while on (damage or a loadout change can add submodels). The translucent hull overlay
        /// (ShowHullCollider) hangs under the same GameObject - its renderers (_colliderOverlays) are excluded.</summary>
        static void ApplyHideModel(bool alive)
        {
            int want = alive ? ModelView : 0;
            if (want != 0 && _hidingModel && want == _appliedView && ++_hideCheck < 60) return;
            _hideCheck = 0;
            if (want != 0)
            {
                GameObject go = ShipGameObject(_unit);
                if (go == null) return;
                var all = go.GetComponentsInChildren<Renderer>(true);
                var fresh = new List<Renderer>();
                foreach (var r in all)
                {
                    if (r == null || !(r is MeshRenderer || r is SkinnedMeshRenderer) || _colliderOverlays.Contains(r.gameObject)) continue;
                    fresh.Add(r);
                }
                if (_hidingModel && want == _appliedView && _shipRenderers != null && _shipRenderers.Count == fresh.Count) return;   // nothing new
                RestoreShipModel();
                _shipRenderers = new List<Renderer>(); _shipOrigMaterials = new List<Material[]>(); _shipGhostMaterials = new List<Material[]>(); _shipWasEnabled = new List<bool>();
                foreach (var r in fresh)
                {
                    try
                    {
                        var orig = r.sharedMaterials;
                        bool hull = IsHullPart(r, go);
                        if (want == 1) Trace("Ghost model: " + (hull ? "hull " : "sub ") + r.gameObject.name);
                        Material[] ghost = null;
                        if (want == 1)
                        {
                            ghost = new Material[orig.Length];
                            for (int i = 0; i < orig.Length; i++)
                            {
                                if (orig[i] == null) continue;
                                ghost[i] = MakeGhost(orig[i], hull);
                            }
                            r.sharedMaterials = ghost;
                        }
                        else { _shipWasEnabled.Add(r.enabled); r.enabled = false; }
                        if (want == 1) _shipWasEnabled.Add(true);
                        _shipRenderers.Add(r); _shipOrigMaterials.Add(orig); _shipGhostMaterials.Add(ghost);
                    }
                    catch (Exception e) { Trace("Ship model view: " + e.Message); }
                }
                _hidingModel = true; _appliedView = want;
                Trace("Deck corners: " + (want == 1 ? "made " : "hid ") + _shipRenderers.Count + " renderer(s) of the selected ship's model" + (want == 1 ? " transparent." : "."));
            }
            else if (_hidingModel) RestoreShipModel();
        }

        static List<Component> HullColliders(object obp)
        {
            var result = new List<Component>();
            var dict = _fMeshNames.GetValue(obp) as IDictionary;
            if (dict == null) return result;
            foreach (DictionaryEntry e in dict)
            {
                string name = e.Key as string;
                if (name == null) continue;
                if (!name.StartsWith("coll_hull", StringComparison.OrdinalIgnoreCase)) continue;
                var mc = e.Value as Component;   // the actual MeshCollider; used only via its Component base and reflection
                if (mc == null) { Trace("Deck corners: hull collider '" + name + "' has no component - skipped."); continue; }
                Mesh mesh = SharedMesh(mc);
                if (mesh != null) result.Add(mc);
                else Trace("Deck corners: hull collider '" + name + "' has no mesh (sharedMesh is null) - skipped.");
            }
            return result;
        }

        static Mesh SharedMesh(Component mc)
        {
            try { return _pSharedMesh.GetValue(mc, null) as Mesh; }
            catch { return null; }
        }

        struct PieceBox { public Vector3 Min, Max; }

        /// <summary>Every hull piece's own bounds, in shipT's local space (used for the overall hull bounds).</summary>
        static List<PieceBox> PieceBoxes(Transform shipT, List<Component> hull)
        {
            var list = new List<PieceBox>();
            Matrix4x4 worldToShip = shipT.worldToLocalMatrix;
            foreach (var mc in hull)
            {
                Mesh mesh = SharedMesh(mc);
                if (mesh == null) continue;
                Bounds mb = mesh.bounds;
                Matrix4x4 m = worldToShip * mc.transform.localToWorldMatrix;
                Vector3 c = mb.center, e = mb.extents;
                Vector3 mn = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                Vector3 mx = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                for (int ix = -1; ix <= 1; ix += 2)
                    for (int iy = -1; iy <= 1; iy += 2)
                        for (int iz = -1; iz <= 1; iz += 2)
                        {
                            Vector3 p = m.MultiplyPoint3x4(c + Vector3.Scale(e, new Vector3(ix, iy, iz)));
                            mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
                        }
                list.Add(new PieceBox { Min = mn, Max = mx });
            }
            return list;
        }

        static readonly List<Transform> _reactivatedTransforms = new List<Transform>();
        static readonly List<Component> _reenabledColliders = new List<Component>();

        static readonly List<float> _hcX = new List<float>();
        static readonly List<float> _hcY = new List<float>();

        // ------------------------------------------------------------ exact (mesh-based) point placement

        struct Tri { public Vector3 A, B, C; }

        /// <summary>Every hull piece's own triangles, transformed once into shipT's local space - true, exact mesh
        /// geometry, so DeckCornerAtMesh can put a point exactly on the collider's own real edge. A mesh with Read/Write off is read back from the GPU (TryGpuMesh); this is all-or-nothing for
        /// the whole ship: any hull piece that cannot be read means no points for this ship.</summary>
        static bool TryHullTriangles(Transform shipT, List<Component> hull, out List<Tri> tris)
        {
            tris = new List<Tri>();
            Matrix4x4 worldToShip = shipT.worldToLocalMatrix;
            foreach (var mc in hull)
            {
                Mesh mesh = SharedMesh(mc);
                if (mesh == null) continue;
                Vector3[] v = null; int[] t = null;
                // An unreadable mesh (Read/Write off) still lives in the GPU (the overlay draws it): read its buffers back.
                // CPU access to such a mesh makes Unity write an ERROR to the game's log, so ask first (isReadable).
                if (mesh.isReadable)
                {
                    try { v = mesh.vertices; t = mesh.triangles; }
                    catch (Exception e) { Trace("Deck corners TryHullTriangles (mesh read failed): " + e.Message); }
                }
                else if (!TryGpuMesh(mesh, out v, out t))
                {
                    if (_warnedOnce.Add("unreadable:" + mesh.name))
                        Warn("Deck corners: hull mesh '" + mesh.name + "' is not readable and the GPU read-back failed; no deck points for this ship (old flooding rules).");
                    tris = null; return false;
                }
                if (v == null || t == null) { tris = null; return false; }
                if (v.Length == 0 || t.Length == 0) { tris = null; return false; }
                Matrix4x4 m = worldToShip * mc.transform.localToWorldMatrix;
                var lv = new Vector3[v.Length];
                for (int i = 0; i < v.Length; i++) lv[i] = m.MultiplyPoint3x4(v[i]);
                for (int i = 0; i + 2 < t.Length; i += 3)
                    tris.Add(new Tri { A = lv[t[i]], B = lv[t[i + 1]], C = lv[t[i + 2]] });
            }
            return true;
        }

        /// <summary>Reads positions and triangle indices of a mesh with Read/Write off straight from its GPU buffers
        /// (Mesh.GetVertexBuffer/GetIndexBuffer + AsyncGPUReadback, waited for synchronously - once per ship selection).
        /// Only Float32 positions and triangle-list submeshes are handled.</summary>
        static bool TryGpuMesh(Mesh mesh, out Vector3[] verts, out int[] tris)
        {
            verts = null; tris = null;
            GraphicsBuffer vb = null, ib = null;
            try
            {
                if (!mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Position)) return false;
                if (mesh.GetVertexAttributeFormat(UnityEngine.Rendering.VertexAttribute.Position) != UnityEngine.Rendering.VertexAttributeFormat.Float32) { Trace("GPU read: position format is not Float32 in '" + mesh.name + "'"); return false; }
                int stream = mesh.GetVertexAttributeStream(UnityEngine.Rendering.VertexAttribute.Position);
                int offset = mesh.GetVertexAttributeOffset(UnityEngine.Rendering.VertexAttribute.Position);
                int stride = mesh.GetVertexBufferStride(stream);
                int count = mesh.vertexCount;
                vb = mesh.GetVertexBuffer(stream);
                byte[] vbytes = ReadBack(vb);
                if (vbytes == null || vbytes.Length < (long)count * stride) { Trace("GPU read: vertex buffer of '" + mesh.name + "' too small / unreadable"); return false; }
                verts = new Vector3[count];
                for (int i = 0; i < count; i++)
                {
                    int p = i * stride + offset;
                    verts[i] = new Vector3(BitConverter.ToSingle(vbytes, p), BitConverter.ToSingle(vbytes, p + 4), BitConverter.ToSingle(vbytes, p + 8));
                }
                bool idx32 = mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32;
                ib = mesh.GetIndexBuffer();
                byte[] ibytes = ReadBack(ib);
                if (ibytes == null) { Trace("GPU read: index buffer of '" + mesh.name + "' unreadable"); return false; }
                var list = new List<int>();
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var sm = mesh.GetSubMesh(s);
                    if (sm.topology != MeshTopology.Triangles) continue;
                    for (int k = 0; k < sm.indexCount; k++)
                    {
                        int pos = (sm.indexStart + k) * (idx32 ? 4 : 2);
                        if (pos + (idx32 ? 4 : 2) > ibytes.Length) return false;
                        int ix = idx32 ? BitConverter.ToInt32(ibytes, pos) : BitConverter.ToUInt16(ibytes, pos);
                        list.Add(ix + sm.baseVertex);
                    }
                }
                foreach (int ix in list) if (ix < 0 || ix >= count) { Trace("GPU read: index out of range in '" + mesh.name + "'"); return false; }
                tris = list.ToArray();
                Trace("GPU read of '" + mesh.name + "': " + count + " vertices, " + (tris.Length / 3) + " triangles");
                return tris.Length > 0;
            }
            catch (Exception e) { Trace("GPU read of '" + mesh.name + "' failed: " + e.Message); return false; }
            finally { if (vb != null) vb.Dispose(); if (ib != null) ib.Dispose(); }
        }

        static byte[] ReadBack(GraphicsBuffer src)
        {
            var req = UnityEngine.Rendering.AsyncGPUReadback.Request(src);
            req.WaitForCompletion();
            if (req.hasError) return null;
            return req.GetData<byte>().ToArray();
        }

        // scratch lists reused by DeckCornerAtMesh - this runs only once per ship selection, not per frame
        static readonly List<float> _crossX = new List<float>();
        static readonly List<float> _crossY = new List<float>();

        /// <summary>The port (or starboard) deck corner at longitudinal position z, EXACT: every triangle edge that
        /// actually crosses the z = query plane gives a precise (x, y) there, by linear interpolation along that
        /// edge - so every candidate is a real point on the collider's own surface, not a sample a physics sweep
        /// happened to land near. Two-pass logic, for the same reason (taking the single
        /// most-outboard crossing regardless of height can land below the gunwale wherever the hull is wider
        /// somewhere other than right at the top; taking the single highest crossing regardless of X can land
        /// inboard at a taller secondary feature, such as a raised forecastle): first the true outboard X among
        /// every crossing on this side (edgeX), then, among only the crossings within beamTol of that X, the
        /// highest - the exact corner where that side's wall meets the deck.</summary>
        static bool DeckCornerAtMesh(List<Tri> tris, float z, bool port, float beamTol, out float x, out float y)
        {
            var xs = _crossX; var ys = _crossY; xs.Clear(); ys.Clear();
            for (int i = 0; i < tris.Count; i++)
            {
                var t = tris[i];
                TryCrossing(t.A, t.B, z, port, xs, ys);
                TryCrossing(t.B, t.C, z, port, xs, ys);
                TryCrossing(t.C, t.A, z, port, xs, ys);
            }
            x = 0f; y = 0f;
            if (xs.Count == 0) return false;

            float edgeX = xs[0];
            for (int i = 1; i < xs.Count; i++)
                if (port ? xs[i] < edgeX : xs[i] > edgeX) edgeX = xs[i];

            float bestY = float.NegativeInfinity; float bestX = edgeX; bool found = false;
            for (int i = 0; i < xs.Count; i++)
            {
                if (Mathf.Abs(xs[i] - edgeX) > beamTol) continue;
                if (!found || ys[i] > bestY) { bestY = ys[i]; bestX = xs[i]; found = true; }
            }
            x = bestX; y = bestY;
            return found;
        }

        static void TryCrossing(Vector3 p0, Vector3 p1, float z, bool port, List<float> xs, List<float> ys)
        {
            float z0 = p0.z, z1 = p1.z;
            if ((z0 - z) * (z1 - z) > 0f) return;   // both endpoints on the same side: this edge doesn't cross z
            float dz = z1 - z0;
            float t = Mathf.Abs(dz) > 1e-9f ? (z - z0) / dz : 0f;
            float x = Mathf.Lerp(p0.x, p1.x, t);
            if (port ? x > 0f : x <= 0f) return;    // the other side
            xs.Add(x); ys.Add(Mathf.Lerp(p0.y, p1.y, t));
        }

        /// <summary>
        /// One port and one starboard point per damage-model section (Sections = 5, so 10 points) in shipT's local space, each at that
        /// section's own centre z (the hull's length split evenly: the game gives only the count and the order of the sections), each the
        /// deck corner of that side at that z, found exactly on the hull collider's mesh (DeckCornerAtMesh) and moved InwardShift closer to the
        /// centreline. There is no approximate fallback: a ship whose hull meshes are not readable (Read/Write off) or where a corner is not
        /// found has no points (returns false) and keeps the old list/trim rules.
        /// </summary>
        static bool LocalPoints(Transform shipT, List<Component> hull, out Vector3[] points, out float width)
        {
            points = null; width = 0.01f;
            var pieces = PieceBoxes(shipT, hull);
            if (pieces.Count == 0) return false;
            Vector3 bMin = pieces[0].Min, bMax = pieces[0].Max;
            foreach (var p in pieces) { bMin = Vector3.Min(bMin, p.Min); bMax = Vector3.Max(bMax, p.Max); }
            float zBow = bMin.z, zStern = bMax.z;
            // the lateral tolerance for "still at the true edge, not a separate, taller feature further inboard": a slice of the ship's beam
            float beamTol = Mathf.Max(1e-4f, (bMax.x - bMin.x) * 0.03f);

            List<Tri> tris;
            if (!TryHullTriangles(shipT, hull, out tris)) return false;

            var list = new List<Vector3>(PointCount);
            for (int i = 0; i < Sections; i++)
            {
                float z = zBow + (zStern - zBow) * (i + 0.5f) / Sections;
                float xp, yp, xs, ys;
                if (!DeckCornerAtMesh(tris, z, true, beamTol, out xp, out yp) || !DeckCornerAtMesh(tris, z, false, beamTol, out xs, out ys)) return false;
                list.Add(new Vector3(xp * (1f - InwardShift), yp, z));
                list.Add(new Vector3(xs * (1f - InwardShift), ys, z));
            }
            points = list.ToArray();
            width = Mathf.Clamp((bMax - bMin).magnitude * 0.004f, 0.001f, 0.05f);
            return true;
        }

        static float _sea = float.NaN; static bool _seaTried;
        /// <summary>The flat sea level (no waves): Ocean.Instance.level (the Ceto ocean's own level) if the type has it, else 0.
        /// Written to the trace once, with how it was found.</summary>
        public static float SeaLevel()
        {
            if (!float.IsNaN(_sea)) return _sea;
            if (_seaTried) return 0f;
            _seaTried = true;
            try
            {
                object inst = _pOceanInstance == null ? null : _pOceanInstance.GetValue(null, null);
                if (inst != null)
                {
                    const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    foreach (var name in new[] { "level", "Level", "SeaLevel", "seaLevel" })
                    {
                        var f = inst.GetType().GetField(name, all); var pr = inst.GetType().GetProperty(name, all);
                        object v = f != null ? f.GetValue(inst) : pr != null && pr.GetIndexParameters().Length == 0 ? pr.GetValue(inst, null) : null;
                        if (v is float) { _sea = (float)v; Trace("Sea level (flat) from Ocean." + name + " = " + _sea.ToString("0.###")); return _sea; }
                    }
                    Trace("Sea level: Ocean has no level member, taken as 0");
                }
            }
            catch (Exception e) { Trace("Sea level: " + e.Message); }
            _sea = 0f;
            return _sea;
        }

        public static float QueryWave(float x, float z)
        {
            if (_pOceanInstance == null || _mQueryWaves == null) return float.NaN;
            try
            {
                object inst = _pOceanInstance.GetValue(null, null);
                if (inst == null) return float.NaN;
                return (float)_mQueryWaves.Invoke(inst, new object[] { x, z, true });
            }
            catch { return float.NaN; }
        }

        // ------------------------------------------------------------ drawing: wave-to-deck lines

        static GameObject _root;
        static LineRenderer[] _lines;
        /// <summary>The selected ship's points come from the full (mesh) algorithm; false: unreadable collider meshes - no points.</summary>
        public static bool PointsExact;   // set by Select
        static readonly Color CCollExact = new Color(0.2f, 0.85f, 1f, 0.2f), CCollFallback = new Color(1f, 0.55f, 0.1f, 0.2f);
        static readonly Color CDry = new Color(0.3f, 1f, 0.4f), CWet = new Color(1f, 0.25f, 0.2f);

        /// <summary>(Re)builds the pool of per-point LineRenderers to match count, which is always PointCount (10)
        /// in practice, since the point layout is a fixed constant now - called once per ship selection, right
        /// after LocalPoints, destroying whatever the previous ship's pool held and making a fresh one. Cheap (a
        /// handful of GameObjects, once per selection, not per frame).</summary>
        static void RebuildVisual(int count)
        {
            if (_root == null)
            {
                _root = new GameObject("ADM_DeckCorners");
                UnityEngine.Object.DontDestroyOnLoad(_root);
            }
            for (int i = _root.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_root.transform.GetChild(i).gameObject);
            Material mat = OnTopMaterial();
            _lines = new LineRenderer[count];
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("point" + i);
                go.transform.SetParent(_root.transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.positionCount = 2;
                lr.receiveShadows = false;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (mat != null) lr.material = mat;
                _lines[i] = lr;
            }
            // the level labels: one mesh of line segments (digits drawn as 7-segment glyphs), rebuilt every tick
            _labelMesh = new Mesh(); _labelMesh.MarkDynamic();
            var lgo = new GameObject("levels");
            lgo.transform.SetParent(_root.transform, false);
            lgo.AddComponent<MeshFilter>().sharedMesh = _labelMesh;
            var lmr = lgo.AddComponent<MeshRenderer>();
            lmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; lmr.receiveShadows = false;
            if (mat != null) lmr.sharedMaterial = mat;
            _labelMesh.bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1e6f));
        }

        static Mesh _labelMesh;

        /// <summary>Vertex-coloured, no depth test (seen through the sea surface), drawn after everything else.</summary>
        static Material OnTopMaterial()
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) { shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color"); return shader == null ? null : new Material(shader); }
            var m = new Material(shader);
            m.hideFlags = HideFlags.HideAndDontSave;
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            m.SetInt("_ZWrite", 0);
            m.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            m.renderQueue = 4000;
            return m;
        }

        // 7-segment glyphs: bits a (top), b (top right), c (bottom right), d (bottom), e (bottom left), f (top left), g (middle)
        static readonly int[] DigitSegs = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F };
        const float GlyphW = 0.5f;
        static readonly float[][] SegXY = {
            new[] { 0f, 1f, GlyphW, 1f }, new[] { GlyphW, 1f, GlyphW, .5f }, new[] { GlyphW, .5f, GlyphW, 0f }, new[] { 0f, 0f, GlyphW, 0f },
            new[] { 0f, 0f, 0f, .5f }, new[] { 0f, .5f, 0f, 1f }, new[] { 0f, .5f, GlyphW, .5f } };

        static float GlyphAdvance(char c) { return c == '.' ? 0.35f : GlyphW + 0.35f; }

        static void AddSeg(List<Vector3> v, List<Color> col, List<int> idx, Vector3 o, Vector3 r, Vector3 u, float s, float x0, float y0, float x1, float y1, Color c)
        {
            idx.Add(v.Count); v.Add(o + r * (x0 * s) + u * (y0 * s));
            idx.Add(v.Count); v.Add(o + r * (x1 * s) + u * (y1 * s));
            col.Add(c); col.Add(c);
        }

        static void AddGlyph(List<Vector3> v, List<Color> col, List<int> idx, char ch, Vector3 o, Vector3 r, Vector3 u, float s, Color c)
        {
            if (ch >= '0' && ch <= '9')
            {
                int m = DigitSegs[ch - '0'];
                for (int k = 0; k < 7; k++) if ((m & (1 << k)) != 0) { var q = SegXY[k]; AddSeg(v, col, idx, o, r, u, s, q[0], q[1], q[2], q[3], c); }
            }
            else if (ch == '-') AddSeg(v, col, idx, o, r, u, s, 0f, .5f, GlyphW, .5f, c);
            else if (ch == '+')
            {
                AddSeg(v, col, idx, o, r, u, s, 0f, .5f, GlyphW, .5f, c);
                AddSeg(v, col, idx, o, r, u, s, GlyphW / 2f, .25f, GlyphW / 2f, .75f, c);
            }
            else if (ch == '.') AddSeg(v, col, idx, o, r, u, s, 0f, 0f, 0f, .08f, c);
        }

        /// <summary>Small level numbers (metres of the deck point above the sea: +, below: -) next to the points, facing the camera.</summary>
        static void UpdateLabels(Vector3[] at, float[] meters)
        {
            if (_labelMesh == null) return;
            var cam = Camera.main;
            if (cam == null || at == null || meters == null) { _labelMesh.Clear(); return; }
            var v = new List<Vector3>(); var col = new List<Color>(); var idx = new List<int>();
            Vector3 right = cam.transform.right, up = cam.transform.up, camPos = cam.transform.position;
            for (int i = 0; i < at.Length; i++)
            {
                float m = meters[i];
                if (float.IsNaN(m)) continue;
                float s = Vector3.Distance(camPos, at[i]) * 0.006f;
                string text = (m >= 0f ? "+" : "-") + Math.Abs(m).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                float w = 0f; foreach (char ch in text) w += GlyphAdvance(ch);
                // above the point; for a point under water (its line goes up from the point to the wave) - below it, not across the line
                Vector3 o = at[i] + up * (m >= 0f ? s * 0.6f : -s * 1.6f) - right * (w * s * 0.5f);
                Color c = m >= 0f ? CDry : CWet;
                foreach (char ch in text) { AddGlyph(v, col, idx, ch, o, right, up, s, c); o += right * (GlyphAdvance(ch) * s); }
            }
            _labelMesh.Clear();
            if (v.Count == 0) return;
            _labelMesh.SetVertices(v); _labelMesh.SetColors(col); _labelMesh.SetIndices(idx.ToArray(), MeshTopology.Lines, 0);
        }

        static void SetActive(bool on)
        {
            if (_root == null) return;   // nothing built yet (no ship ever selected) - RebuildVisual makes it, not this
            if (_root.activeSelf != on) _root.SetActive(on);
        }

        // ------------------------------------------------------------ drawing: translucent hull overlay + wireframe

        static readonly List<GameObject> _colliderOverlays = new List<GameObject>();
        static Material _colliderMat;
        static Material _wireMat, _wireTopMat;

        static void ClearColliderOverlays()
        {
            foreach (var go in _colliderOverlays) if (go != null) UnityEngine.Object.Destroy(go);
            _colliderOverlays.Clear();
        }

        static void BuildColliderOverlays()
        {
            ClearColliderOverlays();
            if (_hullPieces == null || _shipTransform == null) return;
            if (_colliderMat == null)
            {
                Shader sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Hidden/Internal-Colored");
                _colliderMat = sh != null ? new Material(sh) : null;
                _wireMat = sh != null ? new Material(sh) : null;
                if (_wireMat != null) _wireMat.color = new Color(1f, 1f, 0.15f, 0.95f);   // bright yellow, near-opaque
                _wireTopMat = OnTopMaterial();   // the same wireframe again with no depth test, faint: seen through the sea surface
                if (_wireTopMat != null) _wireTopMat.color = new Color(1f, 1f, 0.15f, 0.15f);
            }
            if (_colliderMat != null) _colliderMat.color = PointsExact ? CCollExact : CCollFallback;   // orange: no exact points for this ship
            foreach (var mc in _hullPieces)
            {
                Mesh mesh = SharedMesh(mc);
                if (mesh == null) continue;
                try
                {
                    // NOT parented onto mc.transform: with UseKinematicCollision on (every ship checked so far), the
                    // non-kinematic coll_hull* pieces' own GameObject hierarchy is deactivated after load (the game
                    // keeps only the kinematic collider active) - a child of an inactive parent never renders, even
                    // with its own MeshRenderer enabled, which is exactly why this was invisible. _shipTransform
                    // (Compartments._compartmentsTransform) is the ship's own, always-active root, so the overlay is
                    // parented there instead and simply given the collider's current world transform once; from then
                    // on it rides along rigidly as the ship moves, the same as every other active child of that root.
                    var go = new GameObject("hull");
                    go.transform.SetParent(_shipTransform, false);
                    Transform mt = mc.transform;
                    go.transform.SetPositionAndRotation(mt.position, mt.rotation);
                    go.transform.localScale = mt.lossyScale;
                    var mf = go.AddComponent<MeshFilter>();
                    mf.sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    if (_colliderMat != null) mr.sharedMaterial = _colliderMat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                    _colliderOverlays.Add(go);

                    // Wireframe sibling, same transform, same mesh's actual triangle edges - the translucent fill
                    // alone (above) makes it hard to tell a coarse, low-poly collider (long, shallow triangles -
                    // wp_ptg_komar was the original example) from a smooth one just by eye; seeing the real edges
                    // directly answers that, and shows exactly where a point lands relative to them. Needs a CPU
                    // read of mesh.vertices/triangles, which - unlike the fill above (GPU-only, via sharedMesh) -
                    // does throw on a mesh with Read/Write disabled in its import settings; BuildWireframeMesh
                    // catches that itself and returns null, so that one piece simply has no wireframe (the fill
                    // still draws fine) rather than losing the whole overlay.
                    Mesh wireMesh = BuildWireframeMesh(mesh);
                    if (wireMesh != null)
                    {
                        var wgo = new GameObject("hull_wire");
                        wgo.transform.SetParent(_shipTransform, false);
                        wgo.transform.SetPositionAndRotation(mt.position, mt.rotation);
                        wgo.transform.localScale = mt.lossyScale;
                        var wmf = wgo.AddComponent<MeshFilter>();
                        wmf.sharedMesh = wireMesh;
                        var wmr = wgo.AddComponent<MeshRenderer>();
                        // two materials on the one submesh: the second (extra) one draws the same lines again, without the depth test
                        if (_wireMat != null && _wireTopMat != null) wmr.sharedMaterials = new[] { _wireMat, _wireTopMat };
                        else if (_wireMat != null) wmr.sharedMaterial = _wireMat;
                        wmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        wmr.receiveShadows = false;
                        _colliderOverlays.Add(wgo);
                    }
                }
                catch (Exception e) { Trace("Deck corners hull overlay: " + e.Message); }
            }
            if (_colliderOverlays.Count == 0) Warn("Deck corners: hull overlay built 0 meshes.");
            else Trace("Deck corners: hull overlay built " + _colliderOverlays.Count + " mesh(es) (including wireframe).");
        }

        /// <summary>A UnityEngine.MeshTopology.Lines mesh of every unique triangle edge of src, in src's own local
        /// space (so it can be given the exact same transform as the translucent fill above and line up with it
        /// pixel-for-pixel) - built once per hull piece per ship selection, not per frame. Requires mesh.vertices/
        /// triangles, which throws on a mesh with Read/Write disabled in its import settings - caught here, so a
        /// piece that throws just has no wireframe (null) rather than taking the rest of the overlay down with it.</summary>
        static Mesh BuildWireframeMesh(Mesh src)
        {
            Vector3[] v = null; int[] t = null;
            if (src.isReadable)
            {
                try { v = src.vertices; t = src.triangles; }
                catch (Exception e) { Trace("Deck corners wireframe (mesh read failed): " + e.Message); return null; }
            }
            else if (!TryGpuMesh(src, out v, out t))
            {
                if (_warnedOnce.Add("nowire:" + src.name)) Warn("Deck corners: no wireframe for '" + src.name + "' (mesh not readable, GPU read-back failed).");
                return null;
            }
            if (v == null || t == null) return null;
            if (v.Length == 0 || t.Length == 0) return null;

            var seen = new HashSet<long>();
            var indices = new List<int>(t.Length);
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                AddEdge(t[i], t[i + 1], seen, indices);
                AddEdge(t[i + 1], t[i + 2], seen, indices);
                AddEdge(t[i + 2], t[i], seen, indices);
            }
            var mesh = new Mesh();
            mesh.indexFormat = v.Length > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = v;
            mesh.SetIndices(indices.ToArray(), MeshTopology.Lines, 0);
            return mesh;
        }

        static void AddEdge(int a, int b, HashSet<long> seen, List<int> indices)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (!seen.Add(key)) return;
            indices.Add(a); indices.Add(b);
        }

        public static void Tick(float now)
        {
            if (!_ok) return;
            bool alive = (_unit as UnityEngine.Object) != null;

            bool showLines = Model && alive && _points != null && _shipTransform != null && _lines != null && _lines.Length == _points.Length;
            SetActive(showLines);
            if (showLines)
            {
                Matrix4x4 ltw = _shipTransform.localToWorldMatrix;
                var labelAt = new Vector3[_points.Length]; var labelM = new float[_points.Length];
                for (int i = 0; i < _points.Length; i++)
                {
                    Vector3 deck = ltw.MultiplyPoint3x4(_points[i]);
                    float waveY = QueryWave(deck.x, deck.z);
                    if (float.IsNaN(waveY)) waveY = deck.y;
                    labelAt[i] = deck; labelM[i] = (deck.y - waveY) * 67.200066f;
                    var lr = _lines[i];
                    lr.startWidth = lr.endWidth = _lineWidth;
                    lr.SetPosition(0, new Vector3(deck.x, waveY, deck.z));
                    lr.SetPosition(1, deck);
                    Color c = deck.y < waveY ? CWet : CDry;   // red: this point is under the current wave; green: clear of it
                    lr.startColor = lr.endColor = c;
                }
                UpdateLabels(labelAt, labelM);
            }

            bool showHull = ShowCollider && alive && _hullPieces != null && _hullPieces.Count > 0;
            if (showHull && _colliderOverlays.Count == 0) BuildColliderOverlays();
            if (!showHull && _colliderOverlays.Count > 0) ClearColliderOverlays();

            ApplyHideModel(alive);
        }
    }
}
