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
    /// its own port and starboard compartment - so 12 points total), each at that section's own centre z, as
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
    /// Height and the port/starboard edge are found directly from the hull colliders' own mesh data whenever it
    /// can be read (TryHullTriangles) - this puts a point exactly on the collider's own real edge, not merely near
    /// it. Some ships' hull meshes turned out to have Read/Write disabled in their import settings (isReadable ==
    /// false), which throws the moment anything touches Mesh.vertices/triangles; for those (confirmed so far only
    /// for civ_ms_andizhan), every point on that ship instead falls back to physics - raycasting against the
    /// collider (Collider.Raycast) needs none of that mesh data, working directly off whatever collision geometry
    /// the engine already built for it, but can only ever land within one sweep step of the real edge, not exactly
    /// on it. For a centreline point that's the exact mesh height at x = 0 (HeightAtMesh), or one ray straight down
    /// at x = 0 taking its first hit (CentreHeightAtRay) when falling back to physics.
    ///
    /// For a port/starboard point, the right edge isn't "the widest point of the cross-section there" (that can
    /// be a bulge below the waterline, reading too low) and isn't "the highest point of the cross-section"
    /// either (deck camber, or any other unevenness, can pull that inboard toward the centreline instead of the
    /// true edge). It is the corner where the two meet, found in two passes either way: exactly, from the mesh
    /// itself (DeckCornerAtMesh) - first the true outboard edge at this z, from every triangle edge that actually
    /// crosses the z = query plane (an exact point by linear interpolation, never a triangle's raw vertex); or
    /// approximately, from physics (DeckCornerAtRay) - a row of purely HORIZONTAL rays cast from outside the
    /// hull's whole beam straight toward the centreline at heights stepped from well above the hull to well below
    /// it - each ray starts outside everything, so whatever it meets first is, by construction, the real outer
    /// skin, never a
    /// structure set back from it; the most outboard of all those hits is the edge. Second pass: among only the
    /// hits within a thin strip of that edge, the highest is the corner where the wall meets the deck. (A single
    /// first-hit raycast per sample, all tilted the same fixed angle, tried first and keeping whichever projected
    /// furthest "outward and up" - does find the right corner on a plain hull, but a user screenshot showed it
    /// losing to a taller secondary feature set back from the true side, such as a raised forecastle or a
    /// well-deck break, since that's still taller even though it's further inboard; an even earlier idea, keeping
    /// whichever vertical ray crosses the collider "exactly twice", foundered the same way a wide, bulging hull
    /// wall gives the same crossing count as a sharp corner. Two passes - lateral extremity decided first, height
    /// only among points already at that extreme - can't be fooled by either: nothing off the true outer skin
    /// ever reaches the second pass at all.)
    ///
    /// Collider/Ray/RaycastHit are resolved lazily, from an actual hull MeshCollider instance (EnsureRaycastReflection,
    /// on the first ship ever selected) rather than by guessing an assembly name at Init time: nothing has loaded
    /// UnityEngine.PhysicsModule into the AppDomain yet when Init runs (straight off BepInEx startup, before any
    /// ship or collider exists), so a name-based lookup there always came back empty - confirmed in testing, every
    /// point silently fell back to TopAt with a "Collider.Raycast not found by reflection" warning in the log.
    /// RaycastHit.point itself turned out to be a C# property, not a field (Unity wraps its native-interop members
    /// that way) - a second confirmed-in-testing miss, fixed by trying both and using whichever is actually there.
    /// Collider.Raycast itself always works in WORLD space, while every z/x/y used while building the ship's points is
    /// in shipT's own local frame (the frame LocalPoints returns them in, and the one the ship's current heading/
    /// roll/pitch/position get reapplied to every frame in Tick) - a third bug, this one silent (no exception, no
    /// warning, every single cast just legitimately missed, since "local-frame coordinates fed straight in as if
    /// they were world space" lands nowhere near the real, moving ship). DeckCornerAt/CentreHeightAt now transform
    /// each ray's origin and direction out to world via shipT right before the cast, and transform the hit straight
    /// back via shipT's inverse before using it, so everything the rest of the code sees is local as before.
    ///
    /// Collider.Raycast only reports a hit while the collider's GameObject (and every ancestor) is active and the
    /// Collider component itself is enabled - and the hull's non-kinematic coll_hull* pieces are usually neither,
    /// for the same reason the translucent overlay once needed reparenting (UseKinematicCollision deactivates
    /// their whole hierarchy once a separate kinematic collider takes over). ActivateForRaycast forces exactly
    /// what's inactive/disabled on, just for the duration of the one-off sweep that builds the ship's points, then
    /// RestoreAfterRaycast puts all of it straight back - nothing is left changed once a ship's points are built.
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
        // Collider.Raycast, and the Ray/RaycastHit types it takes, are resolved by EnsureRaycastReflection the
        // first time a hull is actually selected (see there) rather than up front in Init - .NET only lists an
        // assembly in AppDomain.GetAssemblies() once something has already loaded it, and at Init time (started
        // from BepInEx, before any ship or its colliders exist) nothing has touched UnityEngine.PhysicsModule yet,
        // so a name-based lookup for Collider/Ray/RaycastHit here always came back empty (confirmed in testing -
        // "Collider.Raycast not found by reflection" every single time). If resolution still fails for some other
        // reason once a real collider instance is in hand, the feature falls back to the bounds-only TopAt for
        // every point rather than refusing to initialise at all.
        static bool _raycastResolveAttempted;
        static ConstructorInfo _cRay;
        static MethodInfo _mColliderRaycast;
        static FieldInfo _fHitPoint;        // RaycastHit.point as a field, if it turns out to be one (older Unity)
        static PropertyInfo _pHitPoint;     // ...or as a property (confirmed: this is the one actually hit - Unity's
                                             // RaycastHit wraps its native-interop fields in C# properties, point included)
        static PropertyInfo _pColliderEnabled;

        /// <summary>Resolves Collider.Raycast (and the Ray/RaycastHit types it takes) from an actual, already-loaded
        /// hull MeshCollider instance rather than by guessing an assembly name up front: hull[0]'s own runtime type
        /// (whatever Unity version/module actually put it in) is walked up its base-type chain to "Collider" -
        /// guaranteed loaded, since hull[0] already exists as one - then its "Raycast(Ray, out RaycastHit, float)"
        /// overload is found by shape (3 params, middle one by-ref, last one float) rather than by guessing exact
        /// parameter types, and Ray/RaycastHit are read straight off that method's own parameter types. Tried once
        /// per game session (on whichever ship is selected first), never again after - a failure here isn't going
        /// to start succeeding later, since the relevant assembly is already loaded by the time this runs at all.</summary>
        static void EnsureRaycastReflection(List<Component> hull)
        {
            if (_raycastResolveAttempted) return;
            _raycastResolveAttempted = true;
            if (hull == null || hull.Count == 0) return;
            try
            {
                Type tCollider = hull[0].GetType();
                while (tCollider != null && tCollider.Name != "Collider") tCollider = tCollider.BaseType;
                if (tCollider == null)
                { Warn("Deck corners: 'Collider' base type not found on a live hull collider; port/starboard points will fall back to bounds-only estimates."); return; }

                MethodInfo raycast = null;
                foreach (var m in tCollider.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (m.Name != "Raycast") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 3 && ps[1].ParameterType.IsByRef && ps[2].ParameterType == typeof(float))
                    { raycast = m; break; }
                }
                if (raycast == null)
                { Warn("Deck corners: Collider.Raycast(Ray, out RaycastHit, float) not found; port/starboard points will fall back to bounds-only estimates."); return; }

                var pars = raycast.GetParameters();
                Type tRay = pars[0].ParameterType;
                Type tRaycastHit = pars[1].ParameterType.GetElementType();
                _cRay = tRay.GetConstructor(new[] { typeof(Vector3), typeof(Vector3) });
                // RaycastHit.point turned out to be a C# property, not a field (confirmed in testing: the field
                // lookup always came back null) - Unity wraps RaycastHit's native-interop members in properties,
                // point included, so both are tried and whichever is actually there is used.
                _fHitPoint = tRaycastHit?.GetField("point", BindingFlags.Instance | BindingFlags.Public);
                if (_fHitPoint == null) _pHitPoint = tRaycastHit?.GetProperty("point", BindingFlags.Instance | BindingFlags.Public);
                _pColliderEnabled = tCollider.GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
                if (_cRay != null && (_fHitPoint != null || _pHitPoint != null)) _mColliderRaycast = raycast;
                else Warn("Deck corners: Ray constructor or RaycastHit.point not found; port/starboard points will fall back to bounds-only estimates.");
            }
            catch (Exception e)
            { Warn("Deck corners: raycast reflection setup failed (" + e.Message + "); port/starboard points will fall back to bounds-only estimates."); Trace(e.ToString()); }
        }

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

        static void Select(object unit)
        {
            ClearColliderOverlays();
            RestoreShipModel();
            _unit = null; _points = null; _shipTransform = null; _hullPieces = null;
            if (unit == null) return;
            try
            {
                object comp = _pCompartments.GetValue(unit, null);
                if (comp == null) return;          // not a ship with compartments (aircraft, weapon, ...)
                object obp = _fObp.GetValue(unit);
                if (obp == null) return;
                var hull = HullColliders(obp);
                if (hull.Count == 0)
                {
                    string name = SafeName(unit);
                    if (_warnedOnce.Add(name)) Warn("Deck corners: '" + name + "' has no coll_hull* collider; its lines are off.");
                    return;
                }
                Transform shipT = _fCompTransform.GetValue(comp) as Transform;
                if (shipT == null) return;
                Vector3[] points;
                if (!LocalPoints(shipT, hull, out points, out _lineWidth)) return;
                _unit = unit; _shipTransform = shipT; _points = points; _hullPieces = hull;
                RebuildVisual(points.Length);
            }
            catch (Exception e) { Trace("Deck corners Select: " + e.Message); }
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
        const int PointCount = 2 + Sections * 2;   // bow centre + stern centre + one port/starboard pair per section = 12

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
                if (mc == null) continue;
                Mesh mesh = SharedMesh(mc);
                if (mesh != null) result.Add(mc);
            }
            return result;
        }

        static Mesh SharedMesh(Component mc)
        {
            try { return _pSharedMesh.GetValue(mc, null) as Mesh; }
            catch { return null; }
        }

        struct PieceBox { public Vector3 Min, Max; }

        /// <summary>Every hull piece's own bounds, in shipT's local space (kept separate - TopAt needs them per piece,
        /// not combined - and as the fallback when a point falls in a gap no triangle covers).</summary>
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

        /// <summary>The hull's own top at longitudinal position z: only from the piece(s) whose own length actually
        /// reaches z (or, if none does - a gap between pieces - the nearest one's), never a single ship-wide max.
        /// Used only as the last-resort fallback, when the collider can't be raycast at all (reflection failed to
        /// resolve Collider.Raycast) or a particular cast happens to miss everything.</summary>
        static float TopAt(List<PieceBox> pieces, float z)
        {
            float minDist = float.PositiveInfinity;
            foreach (var b in pieces)
            {
                float d = z < b.Min.z ? b.Min.z - z : (z > b.Max.z ? z - b.Max.z : 0f);
                if (d < minDist) minDist = d;
            }
            float top = float.NegativeInfinity;
            foreach (var b in pieces)
            {
                float d = z < b.Min.z ? b.Min.z - z : (z > b.Max.z ? z - b.Max.z : 0f);
                if (d <= minDist + 1e-4f) top = Math.Max(top, b.Max.y);
            }
            return top;
        }

        static readonly List<Transform> _reactivatedTransforms = new List<Transform>();
        static readonly List<Component> _reenabledColliders = new List<Component>();

        /// <summary>Collider.Raycast only reports a hit while the collider's own GameObject - and every one of its
        /// ancestors - is active, and the Collider component itself is enabled: exactly the condition the hull's
        /// non-kinematic coll_hull* pieces usually fail. With UseKinematicCollision on (every ship checked so far),
        /// the game deactivates their whole hierarchy once a separate kinematic collider takes over collision
        /// duties - the same deactivation that made the ShowHullCollider overlay invisible until it was reparented
        /// onto _shipTransform instead. Left alone, every raycast in the sweep misses everything silently and
        /// LocalPoints falls straight through to TopAt for every single point - which is exactly what this looked
        /// like in practice. Called once per ship selection (not per frame), right before the sweep: walks every
        /// ancestor of each hull collider up to the scene root, force-activates any that are inactive, force-enables
        /// the collider itself if it isn't, and remembers only what it actually changed - RestoreAfterRaycast puts
        /// all of it straight back after the sweep, so nothing is left different from how the game had it.</summary>
        static void ActivateForRaycast(List<Component> hull)
        {
            _reactivatedTransforms.Clear();
            _reenabledColliders.Clear();
            foreach (var mc in hull)
            {
                for (Transform t = mc.transform; t != null; t = t.parent)
                {
                    if (!t.gameObject.activeSelf)
                    {
                        _reactivatedTransforms.Add(t);
                        t.gameObject.SetActive(true);
                    }
                }
                if (_pColliderEnabled != null)
                {
                    try
                    {
                        if (!(bool)_pColliderEnabled.GetValue(mc, null))
                        {
                            _reenabledColliders.Add(mc);
                            _pColliderEnabled.SetValue(mc, true, null);
                        }
                    }
                    catch (Exception e) { Trace("Deck corners ActivateForRaycast: " + e.Message); }
                }
            }
        }

        static void RestoreAfterRaycast()
        {
            foreach (var mc in _reenabledColliders)
            {
                try { _pColliderEnabled.SetValue(mc, false, null); }
                catch (Exception e) { Trace("Deck corners RestoreAfterRaycast: " + e.Message); }
            }
            foreach (var t in _reactivatedTransforms) if (t != null) t.gameObject.SetActive(false);
            _reenabledColliders.Clear();
            _reactivatedTransforms.Clear();
        }

        /// <summary>Casts one ray (origin, dir, up to maxDist) against every hull collider and returns the nearest
        /// hit point across all of them (Collider.Raycast is a per-collider query; the hull can be several pieces).
        /// Built entirely through reflection (see ResolveType) since Collider.Raycast needs none of Mesh.vertices/
        /// triangles, which is exactly what fails on a hull mesh with Read/Write disabled in its import settings.
        /// Returns false (and leaves hitPoint at Vector3.zero) if reflection never resolved, or every cast missed.</summary>
        static bool RaycastFirst(List<Component> hull, Vector3 origin, Vector3 dir, float maxDist, out Vector3 hitPoint)
        {
            hitPoint = Vector3.zero;
            if (_cRay == null || _mColliderRaycast == null || (_fHitPoint == null && _pHitPoint == null)) return false;
            bool found = false; float bestDist = float.PositiveInfinity;
            object ray;
            try { ray = _cRay.Invoke(new object[] { origin, dir.normalized }); }
            catch (Exception e) { Trace("Deck corners RaycastFirst (Ray ctor): " + e.Message); return false; }
            foreach (var mc in hull)
            {
                try
                {
                    object[] args = { ray, null, maxDist };
                    bool hit = (bool)_mColliderRaycast.Invoke(mc, args);
                    if (!hit) continue;
                    Vector3 p = (Vector3)(_fHitPoint != null ? _fHitPoint.GetValue(args[1]) : _pHitPoint.GetValue(args[1], null));
                    float d = Vector3.Distance(origin, p);
                    if (!found || d < bestDist) { bestDist = d; hitPoint = p; found = true; }
                }
                catch (Exception e) { Trace("Deck corners RaycastFirst: " + e.Message); }
            }
            return found;
        }

        /// <summary>The centreline height at z, approximated by physics: a single ray straight down from above the
        /// hull at x = 0, taking its first hit. Only used when the mesh can't be read (HeightAtMesh unavailable -
        /// see TryHullTriangles); falls back further to TopAt (bounds-only) if the cast misses or reflection never
        /// resolved. Collider.Raycast works in WORLD space - origin/z here are in shipT's local frame (the frame
        /// every point LocalPoints returns is in), so the origin and direction are transformed out to world via
        /// shipT before the cast, and the hit transformed straight back via shipT's inverse before returning it.</summary>
        static bool CentreHeightAtRay(Transform shipT, List<Component> hull, float z, float topY, float rayLen, out float y)
        {
            Vector3 originWorld = shipT.TransformPoint(new Vector3(0f, topY, z));
            Vector3 dirWorld = shipT.TransformDirection(Vector3.down);
            Vector3 hitWorld;
            if (RaycastFirst(hull, originWorld, dirWorld, rayLen, out hitWorld)) { y = shipT.InverseTransformPoint(hitWorld).y; return true; }
            y = 0f; return false;
        }

        // scratch lists reused by DeckCornerAtRay - this runs only once per ship selection, not per frame
        static readonly List<float> _hcX = new List<float>();
        static readonly List<float> _hcY = new List<float>();

        /// <summary>The port (or starboard) deck corner at longitudinal position z, approximated by physics: only
        /// used when the mesh can't be read (DeckCornerAtMesh unavailable - see TryHullTriangles). A sample point
        /// sits wherever a ray happened to land on a mesh face, so this can only ever get within one sweep step of
        /// the real edge - good enough to tell a coarse collider from a smooth one, not pixel-exact; DeckCornerAtMesh
        /// (the real edge, found analytically wherever the mesh is readable) is what actually answers "exactly on
        /// the edge". Found in two passes: first, sweep a row of HORIZONTAL rays, from outside the hull's overall
        /// beam straight toward the centreline, at heights stepped from well above the hull down to well below it -
        /// each one starts outside everything and stops at the very first surface it meets, which is by construction
        /// the true outer skin at that height, never anything set back from it (an inner structure - a forecastle/
        /// well-deck break, say - only intrudes on this sweep if it pokes out PAST the hull's own side, which a
        /// recessed step never does). Across every height sampled this way, the most outboard hit X is the true
        /// edge of the hull at this z (edgeX). Second pass: among only the samples within a thin strip of edgeX,
        /// the highest Y is the corner where that wall meets the deck.
        ///
        /// A straight single-direction sweep (cast one ray per column, all tilted the same fixed angle, keep
        /// whichever hit projects furthest "outward and up") was tried first and does find the right corner on a
        /// plain hull - but a ship with a taller secondary feature set back from the true side (a raised forecastle
        /// or a well-deck break, confirmed by a user screenshot) can have a point there outweigh the true corner on
        /// that single combined up+out scale, even at a shallow tilt, since it's taller even though it's further
        /// inboard. Two passes - lateral extremity decided first, height only among points already at that extreme
        /// - can't be fooled that way: nothing not on the true outer skin ever enters the second pass at all.
        ///
        /// Collider.Raycast works in WORLD space - z/outerX/yTop/yBottom here are all in shipT's local frame (the
        /// frame every point LocalPoints returns is in), so each sample's origin and the shared ray direction are
        /// transformed out to world via shipT before the cast, and every hit transformed straight back via shipT's
        /// inverse so edgeX/the returned x/y stay local. Returns false only if every cast in the sweep missed.</summary>
        static bool DeckCornerAtRay(Transform shipT, List<Component> hull, float z, bool port, float outerX, float yTop, float yBottom, float rayLen, float beamTol, out float x, out float y)
        {
            x = 0f; y = 0f;
            float side = port ? -1f : 1f;
            Vector3 dirWorld = shipT.TransformDirection(new Vector3(-side, 0f, 0f));   // straight across, toward the centreline
            _hcX.Clear(); _hcY.Clear();
            float edgeX = 0f; bool haveEdge = false;
            const int steps = 40;
            for (int i = 0; i <= steps; i++)
            {
                float sy = Mathf.Lerp(yTop, yBottom, (float)i / steps);
                Vector3 originWorld = shipT.TransformPoint(new Vector3(outerX * side, sy, z));
                Vector3 hitWorld;
                if (!RaycastFirst(hull, originWorld, dirWorld, rayLen, out hitWorld)) continue;
                Vector3 hitLocal = shipT.InverseTransformPoint(hitWorld);
                _hcX.Add(hitLocal.x); _hcY.Add(hitLocal.y);
                if (!haveEdge || (port ? hitLocal.x < edgeX : hitLocal.x > edgeX)) { edgeX = hitLocal.x; haveEdge = true; }
            }
            if (!haveEdge) return false;

            float bestY = float.NegativeInfinity; float bestX = edgeX; bool found = false;
            for (int i = 0; i < _hcX.Count; i++)
            {
                if (Mathf.Abs(_hcX[i] - edgeX) > beamTol) continue;
                if (!found || _hcY[i] > bestY) { bestY = _hcY[i]; bestX = _hcX[i]; found = true; }
            }
            x = bestX; y = bestY;
            return found;
        }

        // ------------------------------------------------------------ exact (mesh-based) point placement

        struct Tri { public Vector3 A, B, C; }

        /// <summary>Every hull piece's own triangles, transformed once into shipT's local space - true, exact mesh
        /// geometry, used whenever it's available since DeckCornerAtMesh/HeightAtMesh can then put a point exactly
        /// on the collider's own real edge, rather than within one physics-sweep step of it (DeckCornerAtRay/
        /// CentreHeightAtRay). Needs mesh.vertices/mesh.triangles, which throws on a mesh with Read/Write disabled
        /// in its import settings (confirmed for civ_ms_andizhan's hull collider) - so this is all-or-nothing for
        /// the whole ship: the moment any one hull piece throws, the exact path is abandoned for every point on
        /// this ship (not just that one piece), since a part-exact, part-approximate hull would be an inconsistent,
        /// confusing mix to look at, and the physics sweep alone is already a perfectly good fallback for the
        /// (so far, rare) ships this happens on.</summary>
        static bool TryHullTriangles(Transform shipT, List<Component> hull, out List<Tri> tris)
        {
            tris = new List<Tri>();
            Matrix4x4 worldToShip = shipT.worldToLocalMatrix;
            foreach (var mc in hull)
            {
                Mesh mesh = SharedMesh(mc);
                if (mesh == null) continue;
                // Unity does not throw on an unreadable mesh: it writes an ERROR to the game's own log and hands back
                // an empty array - so ask first (isReadable), keep the game's log clean, and log it only here.
                if (!mesh.isReadable)
                {
                    if (_warnedOnce.Add("unreadable:" + mesh.name))
                        Warn("Deck corners: hull mesh '" + mesh.name + "' is not readable (Read/Write off); raycast fallback for this ship.");
                    tris = null; return false;
                }
                Vector3[] v; int[] t;
                try { v = mesh.vertices; t = mesh.triangles; }
                catch (Exception e) { Trace("Deck corners TryHullTriangles (mesh not readable): " + e.Message); tris = null; return false; }
                if (v.Length == 0 || t.Length == 0) { tris = null; return false; }
                Matrix4x4 m = worldToShip * mc.transform.localToWorldMatrix;
                var lv = new Vector3[v.Length];
                for (int i = 0; i < v.Length; i++) lv[i] = m.MultiplyPoint3x4(v[i]);
                for (int i = 0; i + 2 < t.Length; i += 3)
                    tris.Add(new Tri { A = lv[t[i]], B = lv[t[i + 1]], C = lv[t[i + 2]] });
            }
            return true;
        }

        /// <summary>True local hull height at (x, z): the highest point, among every hull triangle whose XZ
        /// footprint covers (x, z), of that triangle's own plane there (barycentric) - used only for the exact
        /// centreline point (x = 0), where there is no "edge" to find, just the deck's own real height. Falls back
        /// to the nearest point on the mesh's own surface (NearestHeightMesh) if no triangle's footprint covers
        /// (x, z) exactly (a seam, or a point right at the edge of the mesh), then to TopAt (bounds-only) as the
        /// last resort.</summary>
        static float HeightAtMesh(List<Tri> tris, List<PieceBox> pieces, float x, float z)
        {
            float best = float.NegativeInfinity; bool found = false;
            for (int i = 0; i < tris.Count; i++)
            {
                var t = tris[i];
                float x1 = t.A.x, z1 = t.A.z, x2 = t.B.x, z2 = t.B.z, x3 = t.C.x, z3 = t.C.z;
                float d = (x1 - x3) * (z2 - z3) - (x2 - x3) * (z1 - z3);
                if (d > -1e-9f && d < 1e-9f) continue;    // degenerate triangle
                float a = ((x - x3) * (z2 - z3) - (x2 - x3) * (z - z3)) / d;
                float b = ((x1 - x3) * (z - z3) - (x - x3) * (z1 - z3)) / d;
                float c = 1f - a - b;
                const float eps = -1e-3f;
                if (a < eps || b < eps || c < eps) continue;   // outside this triangle's footprint
                float y = a * t.A.y + b * t.B.y + c * t.C.y;
                if (!found || y > best) { best = y; found = true; }
            }
            if (found) return best;
            float nearest;
            return NearestHeightMesh(tris, x, z, out nearest) ? nearest : TopAt(pieces, z);
        }

        static bool NearestHeightMesh(List<Tri> tris, float x, float z, out float y)
        {
            float bestDistSq = float.PositiveInfinity; float bestY = 0f; bool found = false;
            for (int i = 0; i < tris.Count; i++)
            {
                var t = tris[i];
                TryEdgeMesh(t.A, t.B, x, z, ref bestDistSq, ref bestY, ref found);
                TryEdgeMesh(t.B, t.C, x, z, ref bestDistSq, ref bestY, ref found);
                TryEdgeMesh(t.C, t.A, x, z, ref bestDistSq, ref bestY, ref found);
            }
            y = bestY;
            return found;
        }

        static void TryEdgeMesh(Vector3 p0, Vector3 p1, float x, float z, ref float bestDistSq, ref float bestY, ref bool found)
        {
            float dx = p1.x - p0.x, dz = p1.z - p0.z;
            float len2 = dx * dx + dz * dz;
            float t = len2 > 1e-12f ? ((x - p0.x) * dx + (z - p0.z) * dz) / len2 : 0f;
            t = Mathf.Clamp01(t);
            float cx = p0.x + dx * t, cz = p0.z + dz * t;
            float ex = x - cx, ez = z - cz;
            float distSq = ex * ex + ez * ez;
            if (!found || distSq < bestDistSq) { bestDistSq = distSq; bestY = Mathf.Lerp(p0.y, p1.y, t); found = true; }
        }

        // scratch lists reused by DeckCornerAtMesh - this runs only once per ship selection, not per frame
        static readonly List<float> _crossX = new List<float>();
        static readonly List<float> _crossY = new List<float>();

        /// <summary>The port (or starboard) deck corner at longitudinal position z, EXACT: every triangle edge that
        /// actually crosses the z = query plane gives a precise (x, y) there, by linear interpolation along that
        /// edge - so every candidate is a real point on the collider's own surface, not a sample a physics sweep
        /// happened to land near. Same two-pass logic as DeckCornerAtRay, for the same reason (taking the single
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

        /// <summary>2 + 2*sections points in shipT's local space: a centreline point at the bow and at the stern
        /// (for trim/pitch - DeckCornerAt isn't involved at all there, just HeightAtMesh/CentreHeightAtRay at
        /// x = 0), then one port and one starboard point per damage-model section (Sections - normally 5, so 10
        /// points), each at that section's own centre z (the hull's own length evenly divided into that many
        /// slices - the game doesn't expose each section's individual 3D extent, only their count and order, bow
        /// to stern, so an even split of the physical length is the closest available match to "centre of each
        /// section"). These replace the original 28-point layout (bow/stern corners, a midships pair and 10
        /// arbitrary evenly-spaced pairs) at the user's request, now that each side point lines up with an actual
        /// damage-model section instead of an arbitrary station, ready to compare each section's own point against
        /// the wave to tell whether that section is taking water over the side. Every port/starboard point is that
        /// side's deck corner at that z, exact (DeckCornerAtMesh) whenever the hull mesh is readable, or physics-
        /// approximate (DeckCornerAtRay) when it isn't, falling back to TopAt (bounds-only) if even that finds
        /// nothing there; the centreline points use HeightAtMesh/CentreHeightAtRay the same way.</summary>
        static bool LocalPoints(Transform shipT, List<Component> hull, out Vector3[] points, out float width)
        {
            points = null; width = 0.01f;
            EnsureRaycastReflection(hull);
            var pieces = PieceBoxes(shipT, hull);
            if (pieces.Count == 0) return false;
            Vector3 bMin = pieces[0].Min, bMax = pieces[0].Max;
            foreach (var p in pieces) { bMin = Vector3.Min(bMin, p.Min); bMax = Vector3.Max(bMax, p.Max); }
            float zBow = bMin.z, zStern = bMax.z;
            // margin so the ray sweeps start safely outside/above/below the hull's own bounding box, and cast far
            // enough to pass clean through it - all scaled off the hull's own size rather than a fixed distance,
            // so this works sensibly across wildly different ship sizes (a patrol boat vs. a cruiser).
            float margin = Mathf.Max(0.5f, (bMax - bMin).magnitude * 0.05f);
            float outerX = Mathf.Max(Mathf.Abs(bMin.x), Mathf.Abs(bMax.x)) + margin;
            float topY = bMax.y + margin;
            float bottomY = bMin.y - margin;
            float vertRayLen = (bMax - bMin).magnitude + margin * 4f;      // for the centreline's straight-down cast
            float horizRayLen = outerX * 2f + margin * 2f;                 // for DeckCornerAt's side-to-side casts
            // the lateral tolerance for "still at the true edge, not a separate, taller feature further inboard"
            // in DeckCornerAt's second pass - a small slice of the ship's own beam, not a fixed distance, so it
            // scales sensibly across wildly different ship sizes.
            float beamTol = Mathf.Max(1e-4f, (bMax.x - bMin.x) * 0.03f);

            List<Tri> tris;
            bool haveMesh = TryHullTriangles(shipT, hull, out tris);

            Func<float, Vector3> port = z =>
            {
                float x, y;
                if (haveMesh && DeckCornerAtMesh(tris, z, true, beamTol, out x, out y)) return new Vector3(x, y, z);
                if (DeckCornerAtRay(shipT, hull, z, true, outerX, topY, bottomY, horizRayLen, beamTol, out x, out y)) return new Vector3(x, y, z);
                return new Vector3(bMin.x, TopAt(pieces, z), z);
            };
            Func<float, Vector3> starboard = z =>
            {
                float x, y;
                if (haveMesh && DeckCornerAtMesh(tris, z, false, beamTol, out x, out y)) return new Vector3(x, y, z);
                if (DeckCornerAtRay(shipT, hull, z, false, outerX, topY, bottomY, horizRayLen, beamTol, out x, out y)) return new Vector3(x, y, z);
                return new Vector3(bMax.x, TopAt(pieces, z), z);
            };
            Func<float, Vector3> centre = z =>
            {
                if (haveMesh) return new Vector3(0f, HeightAtMesh(tris, pieces, 0f, z), z);
                float y;
                if (CentreHeightAtRay(shipT, hull, z, topY, vertRayLen, out y)) return new Vector3(0f, y, z);
                return new Vector3(0f, TopAt(pieces, z), z);
            };

            List<Vector3> list;
            ActivateForRaycast(hull);     // see ActivateForRaycast: the hull colliders are normally inactive/disabled
            try
            {
                list = new List<Vector3>(PointCount) { centre(zBow), centre(zStern) };
                for (int i = 0; i < Sections; i++)                          // one port/starboard pair per section, at its own centre z
                {
                    float z = zBow + (zStern - zBow) * (i + 0.5f) / Sections;
                    list.Add(port(z)); list.Add(starboard(z));
                }
            }
            finally { RestoreAfterRaycast(); }
            points = list.ToArray();
            width = Mathf.Clamp((bMax - bMin).magnitude * 0.004f, 0.001f, 0.05f);
            return true;
        }

        static float QueryWave(float x, float z)
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
        static readonly Color CDry = new Color(0.3f, 1f, 0.4f), CWet = new Color(1f, 0.25f, 0.2f);

        /// <summary>(Re)builds the pool of per-point LineRenderers to match count, which is always PointCount (12)
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
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Hidden/Internal-Colored");
            Material mat = shader != null ? new Material(shader) : null;
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
        }

        static void SetActive(bool on)
        {
            if (_root == null) return;   // nothing built yet (no ship ever selected) - RebuildVisual makes it, not this
            if (_root.activeSelf != on) _root.SetActive(on);
        }

        // ------------------------------------------------------------ drawing: translucent hull overlay + wireframe

        static readonly List<GameObject> _colliderOverlays = new List<GameObject>();
        static Material _colliderMat;
        static Material _wireMat;

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
                if (_colliderMat != null) _colliderMat.color = new Color(0.2f, 0.85f, 1f, 0.35f);
                _wireMat = sh != null ? new Material(sh) : null;
                if (_wireMat != null) _wireMat.color = new Color(1f, 1f, 0.15f, 0.95f);   // bright yellow, near-opaque
            }
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
                        if (_wireMat != null) wmr.sharedMaterial = _wireMat;
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
            if (!src.isReadable)
            {
                if (_warnedOnce.Add("nowire:" + src.name)) Warn("Deck corners: no wireframe for '" + src.name + "' (mesh not readable).");
                return null;
            }
            Vector3[] v; int[] t;
            try { v = src.vertices; t = src.triangles; }
            catch (Exception e) { Trace("Deck corners wireframe (mesh not readable?): " + e.Message); return null; }
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
                for (int i = 0; i < _points.Length; i++)
                {
                    Vector3 deck = ltw.MultiplyPoint3x4(_points[i]);
                    float waveY = QueryWave(deck.x, deck.z);
                    if (float.IsNaN(waveY)) waveY = deck.y;
                    var lr = _lines[i];
                    lr.startWidth = lr.endWidth = _lineWidth;
                    lr.SetPosition(0, new Vector3(deck.x, waveY, deck.z));
                    lr.SetPosition(1, deck);
                    Color c = deck.y < waveY ? CWet : CDry;   // red: this point is under the current wave; green: clear of it
                    lr.startColor = lr.endColor = c;
                }
            }

            bool showHull = ShowCollider && alive && _hullPieces != null && _hullPieces.Count > 0;
            if (showHull && _colliderOverlays.Count == 0) BuildColliderOverlays();
            if (!showHull && _colliderOverlays.Count > 0) ClearColliderOverlays();

            ApplyHideModel(alive);
        }
    }
}
