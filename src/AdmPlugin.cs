using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Alternative Damage Model - Sea Power hull integrity mod.
    ///
    ///  - Hull integrity (SeaPower.Compartments.Init): the game's final value (displacement formula, civilian / submarine
    ///    halving, armour bonus, IntegrityOverride from the unit's ini) is multiplied by a factor from [HullIntegrity]
    ///    (Warships / CivilianShips / Submarines, or [HullIntegrityPerShip] by ini file name). Life rafts and sea mines are left alone.
    ///    Everything derived from it (compartments, total and limit integrity, fire scale) follows automatically.
    ///  - Gun shell damage tiers (Blastzone.CheckForInstakill): the tier is chosen by
    ///    new limit / (multiplier * HullShellVulnerability), i.e. by the game's own limit at 1.0.
    ///  - Fire ignition by hits (Blastzone.OnHitUnit, Blastzone.ProcessCompartmentHit): relative to
    ///    new integrity / HullFireVulnerability: a stronger hull burns proportionally weaker at 1.0.
    ///  - Flooding through holes and the leak thresholds above the waterline (Blastzone.ProcessCompartmentHit): relative to
    ///    new integrity / (multiplier * HullFloodingVulnerability), i.e. as in the game at 1.0.
    ///  - Point value is not changed (it stays as the game computes it).
    ///
    /// Follows its tick in the game's Mods menu (Anchor Chain starts every mod folder): while it is off nothing is changed.
    /// No compile-time dependency on the game's assemblies: types and fields are looked up by name.
    /// Debug = true in the .cfg enables verbose logging and BepInEx/AlternativeDamageModel.trace.txt.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public partial class AdmPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.rgreat.seapower.alternativedamagemodel";
        public const string PluginName = "Alternative Damage Model";
        public const string PluginVersion = "1.0.0";

        /// <summary>Build number (UTC build time, yyyyMMdd.HHmm) stamped into the dll by the csproj; shows which dll is running.</summary>
        public static readonly string BuildId = ReadBuildId();

        static AdmPlugin _inst;
        static bool _debug;
        static string _traceFile;

        // ------------------------------------------------------------ state

        /// <summary>Ticked in the game's Mods menu (always true for a BepInEx/plugins copy).</summary>
        static volatile bool Enabled;
        /// <summary>[General] AlternativeDamageControl: the damage model and the damage control logic (the UI stays with Enabled).</summary>
        static volatile bool _admOn = true;
        static bool Model { get { return Enabled && _admOn; } }
        static bool _patched, _patchTried;

        // settings, cached for the hooks (updated on SettingChanged)
        static float _mWar = 2f, _mCiv = 2f, _mSub = 1f, _shellVuln = 1f, _fireVuln = 1f, _floodVuln = 1f;
        static bool _teamBlocksSpread = true;
        static float _flameUp = 1f, _extinguish = 1f, _floodSpeed = 1f, _pumpOut = 1f, _fireHull = 1f;
        ConfigEntry<Factor> _flameUpEntry, _extinguishEntry, _floodSpeedEntry, _pumpOutEntry, _subFireEntry, _counterRateEntry, _counterLimitEntry, _leakPumpEntry, _fireHullEntry;
        ConfigEntry<Factor> _teamEffEntry, _transferSpeedEntry, _musterEntry, _healthEntry, _regenEntry, _fireWoundEntry, _hitWoundEntry, _woundChanceEntry, _fatigueEntry, _exhaustEntry, _speedRestedEntry, _speedTiredEntry, _speedExhaustEntry, _fatigueRecoverEntry, _listLimitEntry, _trimLimitEntry, _trimLengthEntry, _levelLimitEntry, _deckFloodSpeedEntry, _deckFloodDelayEntry, _wreckDragEntry, _bubblesEntry, _imbalanceEntry;
        ConfigEntry<string> _profileEntry;
        ConfigEntry<string> _speedEffectEntry;
        ConfigEntry<Factor> _maxPlugEntry;
        ConfigEntry<string> _warTeamsEntry, _subTeamsEntry, _civTeamsEntry;
        static bool _logShips, _logFires;
        static bool _trimFloodHooked;
        static FieldInfo _fCompCount;
        static Dictionary<string, float> _perShip = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        ConfigEntry<bool> _enemyDmgEntry, _debugEntry, _logShipsEntry, _logFiresEntry, _logHeelEntry, _uiEntry, _weaponColorEntry, _admEntry, _deckCornersEntry, _showHullEntry, _fixTrimTcEntry;
        ConfigEntry<string> _shipViewEntry;
        static bool _fixTrimTc = true;
        ConfigEntry<Factor> _fireSpreadEntry;
        static float _fireSpread = GameFireSpreadPct;   // % per check
        ConfigEntry<bool> _anyIntegrityEntry, _reserveHitsEntry, _healthOnEntry, _fatigueOnEntry, _smallAiEntry, _unattendedEntry, _keepComEntry, _teamBlocksSpreadEntry;
        ConfigEntry<Factor> _unattendedTimeEntry, _unattendedRateEntry, _unattendedMaxEntry;
        static bool _unattended; static float _unattendedTime = 60f, _unattendedRate = 0.25f, _unattendedMax = 0.5f;
        ConfigEntry<bool> _smokeEntry, _densityEntry; ConfigEntry<Factor> _densityMinEntry, _densityEndEntry, _smokeModEntry, _smokeSevEntry;
        static bool _density = true;
        static bool _smoke = true;
        static bool _dumpXaml;
        static string _xamlDir;
        ConfigEntry<string> _noDeckEntry;
        ConfigEntry<Factor> _reopenEntry, _reopenLimitEntry, _fullFireEntry, _fireEffEntry;
        ConfigEntry<bool> _fullSystemsEntry, _spillEntry;
        static HashSet<string> _noDeck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ConfigEntry<Factor> _mWarEntry, _mCivEntry, _mSubEntry, _shellVulnEntry, _fireVulnEntry, _floodVulnEntry,
                            _uiFloodOne, _uiFloodTwo, _uiFireOne, _uiFireTwoUp, _uiFireTwoDown;
        static bool _uiOn, _uiReady;
        static int _uiFails;
        const string PerShipSection = "HullIntegrityPerShip";
        const string PerModuleSection = "SystemTypeIntegrity";   // module type (modules.ini section) = its Integrity, set explicitly
        const string OldPerModuleSection = "SystemsPerType";     // test builds: module type = integrity multiplier
        /// <summary>The game's chance of a fire to spread, in % per fire check: SystemCompartment.CheckSpreadFire, 1 - 0.9998666644096375.</summary>
        public const float GameFireSpreadPct = 0.01333356f;
        ConfigEntry<Factor> _sysIntEntry;
        static float _sysInt = 1f;
        static Dictionary<string, float> _perModule = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        const string OldPerShipSection = "PerShip"; // 1.0.0 test builds
        const string PerShipExample = "example_ini_file_name"; // example line of a 1.0.0 test build: removed

        /// <summary>Comment under the [HullIntegrityPerShip] header (BepInEx has no section comments: added after every save).</summary>
        static readonly string[] PerShipComment =
        {
            "## Own hull multiplier for single ships, one line per ship: ini file name = multiplier.",
            "## How many times stronger the hull is than in the game; replaces the value from [HullIntegrity]",
            "## (it is not multiplied by it). Ini file names: Sea Power_Data/StreamingAssets/original/vessels.",
            "## Example:",
            "## usn_bb_iowa = 3.0",
            "## wp_pt_libelle = 1.0",
        };

        /// <summary>What the mod did to one ship's Compartments (kept for the damage hooks).</summary>
        sealed class HullState
        {
            public float Mult = 1f, OriginalX;
            public string Ini; public bool IsSub, IsCiv, FixedByIni; // to work out the multiplier again when the settings change
            public float Disp; public int TeamsIni = -1;              // and the number of damage control teams
        }
        static readonly ConditionalWeakTable<object, HullState> Hulls = new ConditionalWeakTable<object, HullState>();
        static readonly List<WeakReference> LiveHulls = new List<WeakReference>(); // Compartments of ships in the current mission

        // live reload of the .cfg
        static bool _reloading;
        float _nextCfgCheck;

        // fields rescaled when a hull multiplier changes during a mission
        static FieldInfo _fCompFireMult, _fCompMaxTotal, _fCompLimit, _fCompPort, _fCompStar, _fCompSys,
                         _fFcLowest, _fScMaxGrow, _fScMinGrow;

        // game members (looked up by name)
        static FieldInfo _fFcCur, _fFcMaxInt, _fScComp, _fScNum, _fScGrow, _fScMaxSev;
        static PropertyInfo _pScSeverity, _pScState, _pGameTime, _pCompDcTeams;
        static MethodInfo _mUidName;
        static FieldInfo _fCompObp, _fCompBase, _fCompMaxInt, _fCompMaxLimit, _fCompDisp, _fFcComp,
                         _fObpRoles, _fObpIniName, _fObpArmor, _fSopIni, _fSopIniName, _fSopCiv, _fSopRoles;
        static PropertyInfo _pObpCivilian;
        static MethodInfo _mIniReadFloat;
        static Type _tSubmarine;
        static readonly Dictionary<Type, FieldInfo> OverrideFields = new Dictionary<Type, FieldInfo>();
        static bool _warnedHull;

        // ------------------------------------------------------------ helpers

        static string ReadBuildId()
        {
            try
            {
                foreach (AssemblyMetadataAttribute a in typeof(AdmPlugin).Assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false))
                    if (a.Key == "Build") return a.Value;
            }
            catch { }
            return "unknown";
        }

        static void Trace(string msg)
        {
            if (!_debug) return;
            try { File.AppendAllText(_traceFile, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine); }
            catch { }
        }

        static void Say(string msg) { if (_inst != null) _inst.Logger.LogInfo(msg); }
        static void Warn(string msg) { if (_inst != null) _inst.Logger.LogWarning(msg); Trace("WARN " + msg); }

        static string F(float v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        // ------------------------------------------------------------ setup

        /// <summary>
        /// The settings file: Sea Power/Sea Power_Data/StreamingAssets/user/AdvancedDamageControl/AlternativeDamageModel.cfg
        /// (with the player's own profiles in Profiles next to it), like other Sea Power mods keep their user settings.
        /// The old BepInEx/config file is moved there once.
        /// </summary>
        ConfigFile Cfg;
        /// <summary>
        /// The values of the profile in use (the profile sections, [HullIntegrityPerShip], [SystemTypeIntegrity]). Only in memory: never
        /// saved to a file of its own - they come from the profile's file and changes are written to a profile of the player's own.
        /// </summary>
        ConfigFile Pcfg;
        public static string UserDir { get; private set; }

        void OpenProfileValues()
        {
            Pcfg = new ConfigFile(Path.Combine(UserDir ?? Paths.ConfigPath, "~profile-in-memory.cfg"), false);
            Pcfg.SaveOnConfigSet = false;
        }

        void OpenSettingsFile()
        {
            try
            {
                UserDir = Path.Combine(Path.Combine(UnityEngine.Application.streamingAssetsPath, "user"), "AdvancedDamageControl");
                Directory.CreateDirectory(UserDir);
                string path = Path.Combine(UserDir, "AlternativeDamageModel.cfg");
                string old = Config.ConfigFilePath;
                try { if (File.Exists(old + ".moved")) File.Delete(old + ".moved"); } catch { }   // left by test builds
                if (!File.Exists(path) && File.Exists(old))
                {
                    File.Move(old, path);
                    Logger.LogInfo("Settings moved to " + path);
                }
                Cfg = new ConfigFile(path, true);
                Cfg.SaveOnConfigSet = false;   // saved once, without comments, by SaveConfig
            }
            catch (Exception e)
            {
                Logger.LogWarning("Settings in StreamingAssets/user not available (" + e.Message + "); BepInEx/config is used.");
                Cfg = Config;
                UserDir = Path.Combine(Paths.ConfigPath, "AlternativeDamageModel");
            }
        }

        void Awake()
        {
            _inst = this;
            OpenSettingsFile();

            _profileEntry = Cfg.Bind("General", "Profile", "Advanced",
                "Settings profile in use, chosen in the game: Unit Status -> Damage Control -> the gear next to (i). This file holds only the general " +
                "settings ([General], [UI]); the damage model's values are in the profile's file: built-in ones in the Profiles folder of the mod " +
                "(read-only), your own in StreamingAssets/user/AdvancedDamageControl/Profiles. Changing a built-in profile in the game saves it as " +
                "Custom. Edits to the file of the profile in use, or to this name, are picked up while the game runs.");
            _admEntry = Cfg.Bind("General", "AlternativeDamageControl", true,
                "The mod's damage model and damage control: hull and systems strength, side effects, fire and flooding speeds, fire spread, damage " +
                "control teams (numbers, effectiveness, walking transfers, knock-outs, the optimised Auto DC, moving single teams), list / trim / " +
                "sinking limits. DC Priority and tasks given by hand belong to the detailed view and stay. " +
                "Off: the game's own damage and damage control (the Damage Control tab view stays). Can be switched at any time: ships at sea " +
                "follow at once (hull strength and team numbers back to the game's or the mod's; teams on the move arrive, knocked-out ones recover).");
            _debugEntry = Cfg.Bind("General", "Debug", false,
                "Enables verbose logging and BepInEx/AlternativeDamageModel.trace.txt.");
            _logFiresEntry = Cfg.Bind("General", "LogFires", false,
                "Diagnostics: writes every fire started by a hit and every change of a fire's stage to the BepInEx log " +
                "(ship, compartment, mission time, severity and growth in % of the compartment's maximum, damage control teams there). " +
                "Works also while the mod is off in the Mods menu, to compare with the game.");
            _logHeelEntry = Cfg.Bind("General", "LogHeel", false,
                "Diagnostics: while a ship lists or trims noticeably, writes to the BepInEx log every 2 seconds: list, trim, height, " +
                "buoyancy against weight, submerged volume, water in the ship and whether it is sinking / capsized.");
            _logShipsEntry = Cfg.Bind("General", "LogShipIntegrity", false,
                "Writes a line to the BepInEx log for every ship that enters a mission: its hull integrity in the game and with the mod.");
            _deckCornersEntry = Cfg.Bind("General", "ShowDeckCorners", false,
                "Debug: for the ship currently selected in the Unit Status panel, draws a line from the current wave to the " +
                "estimated deck height at the hull's bow and stern centreline, and at a port/starboard pair per damage-model " +
                "section (its own centre point along the length) - hull colliders only, not turrets, masts or any other " +
                "sub-model. Green: that point clears the wave; red: the wave is over it.");
            _showHullEntry = Cfg.Bind("General", "ShowHullCollider", false,
                "Debug: draws the selected ship's hull-only collider meshes (coll_hull*) as a translucent overlay plus " +
                "a bright wireframe of their actual triangles, to compare against the visible 3D model directly.");
            _shipViewEntry = Cfg.Bind("General", "ShipModelView", "Show",
                "Debug: how the selected ship's own 3D model (submodels too) is drawn, so ShowDeckCorners/ShowHullCollider can be seen: " +
                "Show (as in the game), Translucent (70% transparent) or Hide.");
            _fixTrimTcEntry = Cfg.Bind("General", "FixHighTimeCompressionTrimError", true,
                "Fixes an error of the game itself: above 10x time compression a ship's trim grows with the time compression " +
                "(a 25 m boat at 47 kn: 2.6 degrees up to 10x, 9 at 100x) - enough to send a short, fast, undamaged ship " +
                "under. The game moves a ship by a step per physics tick, and above 10x that step is stretched while the " +
                "buoyancy torque is still taken about the centre of mass from before the step. On: that extra step is left " +
                "out, so the trim stays what it is at 10x at any time compression; up to 10x nothing changes. " +
                "Surface ships only. Off: the game's own behaviour.");

            Factor.Register();
            OpenProfileValues();
            _mWarEntry = Pcfg.Bind("HullIntegrity", "Warships", new Factor(2f),
                "How many times stronger the hull of surface warships is than in the game. 1.0 = as in the game, 2.0 = twice as strong. " +
                "Single ships get their own value in section [HullIntegrityPerShip], one line per ship: ini file name = multiplier (e.g. usn_bb_iowa = 3.0).");
            _reopenEntry = Pcfg.Bind("FireAndFlooding", "FireReopenLeaks", new Factor(0.5f),
                "A burning section opens its plugged leaks again: % of the largest leak the section has had, per second at fire 100% (less at a smaller fire). 0 = off.");
            _reopenLimitEntry = Pcfg.Bind("FireAndFlooding", "FireReopenLimit", new Factor(50f),
                "A leak reopened by fire grows back up to this % of the largest leak the section has had.");
            _spillEntry = Pcfg.Bind("HullIntegrity", "DamageSpill", true,
                "A hit on a compartment that is already destroyed is not lost: half of the damage goes to the neighbouring compartment (the same side, towards the middle of the ship); " +
                "if that one is destroyed too, a quarter goes to the one after it. The surplus of the hit that destroys a compartment spills the same way.");
            _fullSystemsEntry = Pcfg.Bind("FireAndFlooding", "FullFloodSystems", true,
                "A fully flooded section (water above 75%): its systems are brought down to severe damage, made inoperable and cannot be repaired while it stays flooded.");
            _fullFireEntry = Pcfg.Bind("FireAndFlooding", "FullFloodFireDecay", new Factor(10f),
                "A fully flooded section puts its fire out: % of the maximum fire per second. 0 = off.");
            _fireEffEntry = Pcfg.Bind("FireAndFlooding", "FireWorkEfficiency", new Factor(75f),
                "Damage control teams in a section with a fire that are not fighting it work at this % of their effectiveness. 100 = off.");
            _noDeckEntry = Pcfg.Bind("FireAndFlooding", "NoDeckPointsShips", "",
                "Ships (ini file names, comma separated; edited in the settings window, Flooding tab) that get no Deck Points: they are flooded by the old, simpler rules, as a ship without a hull collider.");
            _mCivEntry = Pcfg.Bind("HullIntegrity", "CivilianShips", new Factor(2f),
                "How many times stronger the hull of civilian ships (ini files civ_*) is than in the game. 1.0 = as in the game.");
            _mSubEntry = Pcfg.Bind("HullIntegrity", "Submarines", new Factor(1f),
                "How many times stronger the hull of submarines is than in the game. 1.0 = as in the game.");

            _shellVulnEntry = Pcfg.Bind("SideEffects", "HullShellVulnerability", new Factor(1f),
                "Vulnerability of the hull to gun shells (missiles, torpedoes and bombs are not affected). " +
                "Gun shells take a share of a ship's sinking limit that depends on the size of the hull. " +
                "1.0 = each shell takes as much as in the game; the stronger hull just survives more hits. " +
                "2.0 = shells hit as if the hull were half as big; 0.5 = as if it were twice as big (medium shells may stop hurting big ships).");
            _fireVulnEntry = Pcfg.Bind("SideEffects", "HullFireVulnerability", new Factor(1f),
                "Vulnerability of the ship to fires started by hits (all weapon types). " +
                "The stronger hull also holds fires back in the same proportion: with Warships = 5.0 a hit starts a fire 5 times weaker " +
                "(relative to the compartment) than in the game. 1.0 = this normal behaviour of the mod. " +
                "2.0 = fires twice as strong and grow twice as fast; 0.5 = half as strong. 1.0 with every hull multiplier 1.0 = as in the game.");
            _floodVulnEntry = Pcfg.Bind("SideEffects", "HullFloodingVulnerability", new Factor(1f),
                "Vulnerability of the hull to flooding through holes made by hits (all weapon types). " +
                "1.0 = each hole lets in as much water as in the game, and holes above the waterline start leaking after the same damage " +
                "as in the game (the stronger hull does not hold the water back). 2.0 = twice as much water; 0.5 = half.");


            _flameUpEntry = Pcfg.Bind("FireAndFlooding", "FlameUpSpeed", new Factor(1f),
                "How fast fires grow by themselves, between hits. 1.0 = as in the game, 2.0 = twice as fast, 0.5 = half as fast.");
            _fireHullEntry = Pcfg.Bind("FireAndFlooding", "FireHullDamageSpeed", new Factor(1f),
                "How fast a fire eats the hull of its section (integrity of both its compartments). 1.0 = as in the game. " +
                "Damage to the systems in the section is not changed.");
            _sysIntEntry = Pcfg.Bind("Systems", "Integrity", new Factor(1f),
                "How many times stronger the systems of ships and submarines are than in the game (weapons, sensors, engines, magazines... - " +
                "modules.ini Integrity, after the game's own adjustments). 1.0 = as in the game. Single module types can get their own Integrity " +
                "instead, in section [" + PerModuleSection + "]: one line per type, ModuleType = integrity (e.g. MediumTurret = 80).");
            _fireSpreadEntry = Pcfg.Bind("FireAndFlooding", "FireSpreadPercent", new Factor(GameFireSpreadPct),
                "Chance of a severe fire (from 50% of its maximum) to spread to the next sections, in % per fire check: the game checks every " +
                "FireCheckTime (10 s) with a chance of 0.0133% - hardly ever. 0.0133 = as in the game; 1.0 = 1% per check (about once in " +
                "17 minutes of severe fire), 0 = never.");
            _teamBlocksSpreadEntry = Pcfg.Bind("FireAndFlooding", "TeamBlocksFireSpread", true,
                "A fire does not spread into a compartment where a damage control team is (it stops the fire at the door). Off = as in the game.");
            _unattendedEntry = Pcfg.Bind("FireAndFlooding", "UnattendedFireGrowth", false,
                "A fire no damage control team has fought for a while grows faster and faster: every fire check (FireCheckTime, 10 s) its " +
                "growth rate rises as if the section were hit again. Off = as in the game (a fire grows only from hits).");
            _unattendedTimeEntry = Pcfg.Bind("FireAndFlooding", "UnattendedFireTime", new Factor(60f),
                "Unattended fire: seconds without a team fighting the fire (from the time it started, or the last time a team fought it) " +
                "before it starts to grow faster.");
            _unattendedRateEntry = Pcfg.Bind("FireAndFlooding", "UnattendedFireGrowthRate", new Factor(0.25f),
                "Unattended fire: how much the growth rate rises every fire check, in % of the fire's maximum growth rate " +
                "(an ordinary hit adds 0.1-0.8%).");
            _unattendedMaxEntry = Pcfg.Bind("FireAndFlooding", "UnattendedFireMaxGrowth", new Factor(0.5f),
                "Unattended fire: its growth rate is raised only up to this, in % of the fire's maximum a second.");
            _extinguishEntry = Pcfg.Bind("FireAndFlooding", "ExtinguishSpeed", new Factor(1f),
                "How fast damage control teams put fires out. 1.0 = as in the game.");
            _floodSpeedEntry = Pcfg.Bind("FireAndFlooding", "FloodingSpeed", new Factor(1f),
                "How fast water comes in through the holes of a compartment. 1.0 = as in the game.");
            _pumpOutEntry = Pcfg.Bind("FireAndFlooding", "PumpOutSpeed", new Factor(1f),
                "How fast damage control teams pump water out. 1.0 = as in the game.");
            _subFireEntry = Pcfg.Bind("FireAndFlooding", "SubmergedFireDecay", new Factor(1f),
                "A section under the sea puts its fire out: the fire loses this % of its maximum per second for every metre the section's deck edge is " +
                "under water (1 = as many % per second as metres). Needs the ship's deck points (as flooding over the deck). 0 = off.");
            _counterRateEntry = Pcfg.Bind("FireAndFlooding", "CounterFloodSpeed", new Factor(2f),
                "Counterflooding (a team's task given by hand): how fast water is let into a compartment, times the pumping out speed. " +
                "The water goes into the side opposite to the heel, or into the side with less water when the ship is upright.");
            _leakPumpEntry = Pcfg.Bind("FireAndFlooding", "LeakPumpShare", new Factor(0.5f),
                "While a team plugs the holes of a compartment (the Flooding Rate task) it pumps water out of it at the same time: this share of the pumping out speed " +
                "(0.5 = half, 0 = off). A team ordered to counterflood pumps only from the side where that does not worsen the list and trim.");
            _counterLimitEntry = Pcfg.Bind("FireAndFlooding", "CounterFloodLimit", new Factor(50f),
                "Counterflooding lets water into one side of a compartment only up to this % (the game's own limit of a compartment also holds).");

            _speedEffectEntry = Pcfg.Bind("FireAndFlooding", "SpeedEffect", "0=1/1, 50=1/1",
                "How the ship's speed changes the growth of fires and the water coming in (through holes and over the deck edge): knots=flooding/fire multiplier, " +
                "a broken line through the points. The speed is taken as an absolute value; below the first point and above the last the end multipliers hold.");
            _smallAiEntry = Pcfg.Bind("FireAndFlooding", "SmallAiNoSpeedLimit", false,
                "AI ships (not the player's own) shorter than 50 m do not slow down from flooding: the game's cut of speed by holes and water is off for them " +
                "(they keep going, like kamikazes). The \"Limit Speed\" button of your own ships is separate.");
            _maxPlugEntry = Pcfg.Bind("DamageControlTeams", "MaxPlugSpeed", new Factor(0f),
                "A team cannot plug holes while the ship is faster than this, knots. 0 = no limit.");
            _warTeamsEntry = Pcfg.Bind("DamageControlTeams", "WarshipTeams", "0=1, 500=2, 1500=3, 5000=4, 20000=5",
                "Damage control teams of a surface warship by displacement: tons=teams, from that many tons up. " +
                "NumberOfDCTeams in a ship's ini still sets its own number.");
            _subTeamsEntry = Pcfg.Bind("DamageControlTeams", "SubmarineTeams", "0=1, 1000=2, 5000=3, 8000=4",
                "Damage control teams of a submarine by displacement: tons=teams.");
            _civTeamsEntry = Pcfg.Bind("DamageControlTeams", "CivilianTeams", "0=1, 20000=2",
                "Damage control teams of a civilian ship by displacement: tons=teams.");
            _teamEffEntry = Pcfg.Bind("DamageControlTeams", "DCTeamEffectiveness", new Factor(0.5f),
                "How much one team gets done (plugging, fire fighting, pumping, hull and system repairs) compared with the game. 1.0 = as in the game.");
            _transferSpeedEntry = Pcfg.Bind("DamageControlTeams", "TransferSpeed", new Factor(50f),
                "How fast a team walks through the ship, metres per minute (the reserve is in the middle). A transfer = MusterTime + " +
                "the walk, rounded to 10 s. 0 = no walking time; with MusterTime 0 too, teams move at once and Auto DC is the game's.");
            _musterEntry = Pcfg.Bind("DamageControlTeams", "MusterTime", new Factor(60f),
                "Seconds a team needs to get ready before every transfer. Cancelling a transfer while it is still getting ready costs no time.");
            _healthOnEntry = Pcfg.Bind("DamageControlTeams", "TeamHealthEnabled", true,
                "Team health on: wounds from hits and fires, recovery in the reserve, wounded teams work slower. Off = the game's rules " +
                "(a hit takes a team out for a while, no wounds); the other health settings are hidden.");
            _anyIntegrityEntry = Pcfg.Bind("DamageControlTeams", "WoundsAnyIntegrity", true,
                "A hit on a section can wound its teams at any integrity. Off = as in the game: only a hit section with integrity under 50% can take a team " +
                "out, with the chance damage x (1 - integrity), at most 20%. On = every hit on a section with teams gets the wound roll (see Wound chance); " +
                "a wounded team walks to the reserve.");
            _reserveHitsEntry = Pcfg.Bind("DamageControlTeams", "HitsInReserve", true,
                "The teams rest in the central compartment: a hit on it can wound a team waiting in the reserve (the chance is the game's for a " +
                "team knocked out in a hit section: damage x (1 - integrity), only under 50% integrity). A wounded resting team stays in the reserve.");
            _healthEntry = Pcfg.Bind("DamageControlTeams", "TeamHealth", new Factor(1f),
                "Health of a damage control team, a multiplier of its maximum, which is 1..100 points by the ship's displacement (100 = a crew of 1000 and more, about 30000 t; " +
                "1000 t = 24, 10000 t = 101). Shown in the team's hint as a part of the full. A team at 0 is dead. Wounded teams work slower " +
                "(as much as their health, not below 25%). 0 = teams are never wounded (a hit still takes a team out of its section).");
            _regenEntry = Pcfg.Bind("DamageControlTeams", "HealthRegen", new Factor(0.5f),
                "A team in the reserve recovers this much of its full health a second.");
            _fireWoundEntry = Pcfg.Bind("DamageControlTeams", "FireWound", new Factor(1f),
                "A team in a burning section that is not fighting the fire loses this much of its full health a second at fire 100% " +
                "(less at a lower fire level).");
            _hitWoundEntry = Pcfg.Bind("DamageControlTeams", "HitWound", new Factor(1f),
                "Multiplier of the strength of a wound: at most the warhead power of the ammunition (0..196, half of the game's ammunition under 25) x this, " +
                "in points of the team's health (1..100 by displacement). A wounded team walks to the reserve.");
            _woundChanceEntry = Pcfg.Bind("DamageControlTeams", "WoundChance", new Factor(1f),
                "Multiplier of the chance that a hit wounds a team (0.5 = half as likely, 2 = twice; the result is at most 100%). Step 1 (only while Wounds at any hull integrity is off) - the game decides whether a hit takes a team out of its section at all " +
                "(only a hit section with integrity under 50%: damage x (1 - integrity), at most 20%). Step 2 - the mod decides the wound: the chance grows steeply with the warhead power (about power^1.8) and falls with the team's health (about health^1.2) - " +
                "warhead 2.2 against health 2: about 10%, against 100: 0.1%; warhead 82 against 100: about 75%; far above that over 100%. It is multiplied by this multiplier, then limited to 100% " +
                "(with Wounds at any hull integrity off a team that was not wounded still leaves the section). " +
                "A stronger warhead raises the chance. A bigger ship lowers it: its teams are tougher (more health), and its compartments are bigger, so a hit is less likely to land where the team is. " +
                "Even the weakest hit has a small chance. Example, warhead 25: against 12 health about 100%, against 29 about 37%, against 100 about 9%. 0 = never wounded.");
            _fatigueOnEntry = Pcfg.Bind("DamageControlTeams", "FatigueEnabled", true,
                "Team fatigue on: working builds it up, the reserve takes it away, a tired team works at half speed. Off = no fatigue; its settings are hidden.");
            _fatigueEntry = Pcfg.Bind("DamageControlTeams", "FatigueMinutes", new Factor(60f),
                "Minutes of work in a section until a team is tired (first level); its speed falls as set by the Speed settings. The reserve takes fatigue away as fast as " +
                "work builds it up. Shown in the team's hint; a tired team's icon is highlighted. 0 = no fatigue.");
            _exhaustEntry = Pcfg.Bind("DamageControlTeams", "ExhaustedMinutes", new Factor(120f),
                "Minutes of work until a team is exhausted, the second level of fatigue (at most: fatigue stops growing there). Not above 'Tired after' = no second level.");
            _speedRestedEntry = Pcfg.Bind("DamageControlTeams", "SpeedRested", new Factor(100f),
                "Work speed of a rested team, % (the starting point of the fatigue curve). The speed falls in a straight line from here to the tired level.");
            _speedTiredEntry = Pcfg.Bind("DamageControlTeams", "SpeedTired", new Factor(75f),
                "Work speed of a team that has just become tired, %. Between the tired and the exhausted level the speed falls in a straight line.");
            _speedExhaustEntry = Pcfg.Bind("DamageControlTeams", "SpeedExhausted", new Factor(25f),
                "Work speed of an exhausted team, %; it stays there while the team keeps working.");
            _fatigueRecoverEntry = Pcfg.Bind("DamageControlTeams", "FatigueRecovery", new Factor(100f),
                "How fast a team in the reserve gets rid of fatigue, % of the speed it builds up at while working. 100 = as fast; 50 = rest takes twice as long as the work.");

            _listLimitEntry = Pcfg.Bind("Sinking", "ListLimit", new Factor(Limits.ListFull),
                "List, degrees: the full scale of the LIST gauge. From its red zone (75% of it) water comes over the deck edge and floods every " +
                "compartment of the low side fast (game: at 45). It does not sink the ship by itself; capsizing at 70 degrees does, as in the game.");
            _trimLimitEntry = Pcfg.Bind("Sinking", "TrimLimit", new Factor(Limits.TrimFull),
                "Trim, degrees, for a ship TrimReferenceLength long: the full scale of the TRIM gauge. From its red zone (75% of it) the end that goes " +
                "down floods fast: on each side its outermost compartment, when that is full the next one, and so on. The ship does not sink " +
                "at once, as it does in the game (8 degrees with a flooded section).");
            _trimLengthEntry = Pcfg.Bind("Sinking", "TrimReferenceLength", new Factor(Limits.TrimRefLength),
                "Metres. The trim limit depends on the ship's length: the longer the ship, the sooner its bow or stern goes under at the same angle " +
                "(tan limit = tan TrimLimit x (TrimReferenceLength / length)^0.442; with 5.2 at 100: 30 m 8.8, 60 m 6.5, 150 m 4.4, 300 m 3.2 degrees). 0 = TrimLimit for every ship (as in the game).");
            _imbalanceEntry = Pcfg.Bind("Sinking", "FloodImbalanceEffect", new Factor(1f),
                "How strongly the list follows uneven flooding. The difference in water between a section's port and starboard sides " +
                "(which heels the ship) is multiplied by this; the section's average is kept, so the draught and the trim do not change from it. " +
                "1 = as in the game, 2 = twice the heel from the same difference.");
            _levelLimitEntry = Pcfg.Bind("Sinking", "FloodLimit", new Factor(Limits.LevelFull),
                "Water in the whole ship, % (the average of its compartments), from which it sinks, as in the game (game: 40; submarines keep the game's 50). " +
                "Also the full scale of the LEVEL gauge.");
            _keepComEntry = Cfg.Bind("Effects", "KeepCentreOfMass", true,
                "When a ship starts to sink the game puts its centre of mass at the hull origin ((0, 1, 0) if it has capsized): the centre of mass jumps, and with it " +
                "every torque on the hull. On = the centre of mass stays where it was. Off = as in the game.");
            _wreckDragEntry = Cfg.Bind("Effects", "WreckDragFactor", new Factor(3f),
                "Resistance to the HORIZONTAL movement of a destroyed ship, times the game's linear drag (25 for nearly every ship): the wreck loses its way sooner. " +
                "1 = as in the game. Sinking (vertical movement) and turning (capsizing) are not slowed.");
            _bubblesEntry = Cfg.Bind("Effects", "BubblesStopMinutes", new Factor(10f),
                "Minutes (game time, so time compression counts) after a ship starts to sink, when its sinking-bubbles effect stops emitting " +
                "(the game never stops it while the wreck exists). Saves resources in long missions. 0 = never, as in the game.");
            _deckFloodDelayEntry = Pcfg.Bind("Sinking", "DeckFloodingDelay", new Factor(3f),
                "Seconds (game time, so time compression counts) a compartment's deck point must stay under the sea in a row before water over the deck " +
                "starts to come in (see DeckFloodingSpeed). A surfacing point resets the timer. Default 3.");
            _deckFloodSpeedEntry = Pcfg.Bind("Sinking", "DeckFloodingSpeed", new Factor(1f),
                "Water over the deck edge, a multiplier. For ships whose deck points are known (the full hull-collider algorithm): when a compartment's " +
                "deck point has been under the sea for DeckFloodingDelay seconds in a row (game time, so time compression counts), the compartment takes on water " +
                "that damage control cannot stop - 1.0 = 0.1% of its volume per second per metre of submersion (deeper = faster); a full compartment " +
                "passes the water to the lowest-lying not-full neighbour, and on along the chain. For other ships: how fast the end that goes down " +
                "(from trim past its limit) or the low side (from list past its limit) takes on water over the deck edge - the mod's own " +
                "mechanic, not in the game. 2.0 = twice as fast, 0.5 = half, 0 = off.");

            _weaponColorEntry = Cfg.Bind("UI", "ColorWeaponStatus", true,
                "Colours the weapon buttons of the selected unit's bottom panel by the state of their weapon systems: " +
                "all working - the game's colour, some of that type inoperable - yellow, all of them inoperable - red.");
            _enemyDmgEntry = Cfg.Bind("UI", "ShowEnemyDamage", true,
                "Four small icons under a ship's icon on the map (yours too): fire (the fires' total strength), water, hull (as classed on the Damage Control panel) " +
                "and engine; yellow / orange / red by severity, and a greyer map icon when the ship is going to sink (within 20 minutes by the flooding / hull trend). " +
                "A contact: fire, water, hull and engine only in direct sight; by sonar - the engine only (yellow: part of it lost, red: none works); " +
                "by radar - a guess for a ship hit in the last 10 minutes, marked '?' (yellow: 10 knots or less, red: under 3); nothing by ESM or without a contact, and nothing for a sunk ship. " +
                "The Fires line of the Damage Control tab uses the same total strength. The Unit Status of a contact shows a Damage State tab with the same information in words.");
            _uiEntry = Cfg.Bind("UI", "DetailedView", true,
                "The detailed view of Unit Status -> Damage Control (the mod's display): hull integrity per side next to each section's " +
                "name, an icon per damage control team (its task; tasks and moves by hand when Auto DC is off), water and fire in % with " +
                "trend arrows, DC Priority, the team cards, the LIST / TRIM / LEVEL gauges with the flooding plan, tooltips, the (i) " +
                "legend. Off: the game's own view (the gear of the settings window stays).");
            // the key's old name (builds up to 1.0.0): taken over once, then dropped from the .cfg
            try
            {
                var orph = Orphans(Cfg);
                string oldUi; bool v;
                var oldDef = new ConfigDefinition("UI", "DetailedDamageControl");
                if (orph != null && orph.TryGetValue(oldDef, out oldUi))
                {
                    if (bool.TryParse((oldUi ?? "").Trim(), out v)) _uiEntry.Value = v;
                    orph.Remove(oldDef);
                }
            }
            catch (Exception e) { Trace("DetailedDamageControl -> DetailedView: " + e.Message); }
            _uiFloodOne = Cfg.Bind("UI", "FloodingOneArrowAt", new Factor(0.001f),
                "Flooding row and flooding plan: one arrow when the water level changes faster than this, in % of the compartment per second.");
            _uiFloodTwo = Cfg.Bind("UI", "FloodingTwoArrowsAt", new Factor(0.25f),
                "Flooding row and flooding plan: two arrows when the water level changes faster than this, in % of the compartment per second.");
            _uiFireOne = Cfg.Bind("UI", "FireOneArrowAt", new Factor(0.001f),
                "Fire row: one arrow when the fire changes faster than this, in % of its maximum per second.");
            _uiFireTwoUp = Cfg.Bind("UI", "FireTwoArrowsUpAt", new Factor(0.25f),
                "Fire row: two arrows up when the fire grows faster than this, in % of its maximum per second.");
            _uiFireTwoDown = Cfg.Bind("UI", "FireTwoArrowsDownAt", new Factor(0.25f),
                "Fire row: two arrows down when the fire is put out faster than this, in % of its maximum per second.");
            _smokeEntry = Cfg.Bind("Effects", "ExtraSmokeEmitters", true,
                "Number of fire and smoke points by ship length instead of the game's three: under 25 m 1, under 50 m 2, under 75 m 3, from 75 m 5 " +
                "(one per section, each showing its own section's fire). Ships spawned from now on.");
            _densityEntry = Cfg.Bind("Effects", "SmokeBySeverity", true,
                "Fire looks by the fire's strength (share of its maximum) instead of the game's stages: small, moderate from " +
                "SmokeModerateFrom, severe from SmokeSevereFrom (the game: 25% / 50%); the small fire's smoke grows from " +
                "SmokeStartOpacity to SmokeEndOpacity. Only the look - damage and spreading stay the game's. Off = as in the game.");
            _densityMinEntry = Cfg.Bind("Effects", "SmokeStartOpacity", new Factor(10f),
                "Small fire: smoke opacity of new particles at 0% of the fire's maximum, % of the game's.");
            _densityEndEntry = Cfg.Bind("Effects", "SmokeEndOpacity", new Factor(100f),
                "Small fire: smoke opacity of new particles at SmokeModerateFrom, % of the game's; between - evenly by the fire's strength.");
            _smokeModEntry = Cfg.Bind("Effects", "SmokeModerateFrom", new Factor(50f),
                "The moderate fire's look from this strength, % of the fire's maximum (the game: 25%).");
            _smokeSevEntry = Cfg.Bind("Effects", "SmokeSevereFrom", new Factor(90f),
                "The severe fire's look from this strength, % of the fire's maximum (the game: 50%).");

            _debug = _debugEntry.Value;
            _traceFile = Path.Combine(Paths.BepInExRootPath, "AlternativeDamageModel.trace.txt");
            if (_debug) { try { File.WriteAllText(_traceFile, ""); } catch { } }
            Trace(PluginName + " " + PluginVersion + " build " + BuildId);

            Cfg.SaveOnConfigSet = false; // saved by SaveConfig
            // a change made in game (settings window, Configuration Manager); changes made in the files are picked up by WatchConfigFile
            Cfg.SettingChanged += (s, e) => { if (_reloading) return; ReadSettings(); SaveConfig(); RescaleLiveHulls(); };
            Pcfg.SettingChanged += (s, e) => { if (_reloading) return; ReadSettings(); RescaleLiveHulls(); SyncProfile(); };
            CollectEntries();
            MoveOldProfiles();
            StartProfile(TakeOldProfileValues());   // reads the settings and saves the .cfg

            RefreshEnabled();
            TryPatch();
            Logger.LogInfo(PluginName + " " + PluginVersion + " (build " + BuildId + ") loaded" +
                           (ModState.Controlled ? "; follows its tick in the game's Mods menu." : "."));
        }

        void Update()
        {
            if (!_patchTried) TryPatch();
            WatchConfigFile();
            try { DeckCorners.Tick(UnityEngine.Time.unscaledTime); } catch (Exception e) { Trace("DeckCorners.Tick: " + e.Message); }
            if (_dumpXaml && _xamlExportAt >= 0f && _patchTried)
            {
                // a second after the request: the tab's XAML (and the dictionaries it uses) are loaded by then
                if (_xamlExportAt == 0f) _xamlExportAt = UnityEngine.Time.unscaledTime + 1f;
                else if (UnityEngine.Time.unscaledTime >= _xamlExportAt)
                {
                    _xamlExportAt = -1f;
                    try { ExportXamlNow(); } catch (Exception e) { Warn("XAML export failed: " + e.Message); Trace(e.ToString()); }
                }
            }
            if (_patchTried)
            {
                try { WeaponStatusColors.Tick(UnityEngine.Time.unscaledTime, Enabled && _weaponColor); }
                catch (Exception e) { Trace("WeaponStatusColors.Tick: " + e.Message); }
                try { ContactIntel.Tick(UnityEngine.Time.unscaledTime, WeaponStatusColors.Root(), Enabled && _enemyDmg); }
                catch (Exception e) { Trace("ContactIntel.Tick: " + e.Message); }
                try { EnemyStatus.Tick(UnityEngine.Time.unscaledTime); } catch (Exception e) { Trace("EnemyStatus.Tick: " + e.Message); }
            }
            if (_uiReady)
            {
                // one failure does not switch the view off for good (it could not restore the game's view then); many in a row do
                try { UiTick(); _uiFails = 0; }
                catch (Exception e)
                {
                    Trace("UiTick: " + e);
                    if (++_uiFails == 1) Warn("Damage Control view: " + e.Message);
                    if (_uiFails >= 50) { _uiReady = false; Warn("Damage Control view failed repeatedly and is off: " + e.Message); }
                }
            }
        }

        /// <summary>
        /// Checks the .cfg once a second; when it has been changed outside the game (and stays unchanged for half a second,
        /// i.e. the editor has finished writing), its values are read again and applied at once.
        /// </summary>
        /// <summary>A file changed outside the game: reported once it has stopped changing for half a second.</summary>
        sealed class FileWatch
        {
            public string Path; DateTime _stamp; long _size; float _changedAt = -1f;
            public void Remember()
            {
                try { var fi = new FileInfo(Path); _stamp = fi.Exists ? fi.LastWriteTimeUtc : default(DateTime); _size = fi.Exists ? fi.Length : -1; }
                catch { }
                _changedAt = -1f;
            }
            public void Set(string path) { Path = path; Remember(); }
            public void Forget() { _stamp = default(DateTime); }
            public bool Busy { get { return _changedAt >= 0f; } }
            public bool Poll(float now)
            {
                if (string.IsNullOrEmpty(Path)) return false;
                var fi = new FileInfo(Path);
                if (!fi.Exists) return false;
                if (fi.LastWriteTimeUtc == _stamp && fi.Length == _size)
                {
                    if (_changedAt >= 0f && now - _changedAt >= 0.5f) { _changedAt = -1f; return true; }
                    return false;
                }
                _stamp = fi.LastWriteTimeUtc; _size = fi.Length;
                _changedAt = now; // wait until it stops changing
                return false;
            }
        }
        readonly FileWatch _cfgWatch = new FileWatch(), _profWatch = new FileWatch();

        void WatchConfigFile()
        {
            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextCfgCheck) return;
            _nextCfgCheck = now + (_cfgWatch.Busy || _profWatch.Busy ? 0.25f : 1f);
            try { if (_cfgWatch.Poll(now)) ReloadConfig(); } catch (Exception e) { Trace("WatchConfigFile: " + e.Message); }
            try { if (_profWatch.Poll(now)) ReloadProfile(); } catch (Exception e) { Trace("WatchProfileFile: " + e.Message); }
        }

        void RememberCfgStamp() { _cfgWatch.Path = Cfg.ConfigFilePath; _cfgWatch.Remember(); }

        /// <summary>The .cfg (general settings) changed: re-read; another profile named in it comes into use.</summary>
        void ReloadConfig()
        {
            string before = _profileEntry.Value;
            _reloading = true;
            try { Cfg.Reload(); }
            catch (Exception e) { Logger.LogWarning("Reading the changed settings failed (" + e.Message + "); will try again."); _cfgWatch.Forget(); return; }
            finally { _reloading = false; }
            bool cleaned = CleanCfg();   // lines that do not belong here (profile values typed in by hand): dropped from the file too
            string name = _profileEntry.Value;
            if (!string.Equals(before, name, StringComparison.OrdinalIgnoreCase))
            {
                var prof = LoadProfile(name);
                if (prof != null) { ApplyValues(prof, prof.Name); Say("Settings profile \"" + prof.Name + "\" in use (.cfg)."); }
                else
                {
                    Warn("Profile \"" + name + "\" (.cfg) not found; \"" + before + "\" stays in use.");
                    _reloading = true; try { _profileEntry.Value = before; } finally { _reloading = false; }
                    ReadSettings(); SaveConfig();
                }
            }
            else { ReadSettings(); if (cleaned) SaveConfig(); }
            if (cleaned) Warn("Lines that are not general settings were removed from the .cfg; the damage model's values are in the profile files.");
            Logger.LogInfo("Settings re-read from the .cfg" + (Enabled ? "" : " (the mod is off in the Mods menu)") + ".");
            RescaleLiveHulls();
            SettingsWindow.Refresh();
        }

        /// <summary>The file of the profile in use changed (edited by hand): its values come into use.</summary>
        void ReloadProfile()
        {
            var prof = LoadProfile(_profileEntry.Value);
            if (prof == null) return;   // deleted: the values in use stay
            if (prof.SameAs(CurrentValues())) return;
            ApplyValues(prof, prof.Name);
            Logger.LogInfo("Settings profile \"" + prof.Name + "\" re-read from its file: warships x" + F(_mWar) + ", civilian x" + F(_mCiv) +
                           ", submarines x" + F(_mSub) + ", per-ship " + _perShip.Count + ".");
            SettingsWindow.Refresh();
        }

        /// <summary>
        /// A hull multiplier changed while ships are at sea: every ship the mod changed is brought to its new multiplier at once.
        /// Everything that depends on hull integrity is scaled by the same factor, so damage, flooding and fires keep their state
        /// in percent (a ship at 60% stays at 60%).
        /// </summary>
        static void RescaleLiveHulls()
        {
            if (!Enabled || _fCompMaxInt == null) return;
            int n = 0;
            lock (LiveHulls)
            {
                for (int i = LiveHulls.Count - 1; i >= 0; i--)
                {
                    var comp = LiveHulls[i].Target;
                    HullState st;
                    if (comp == null || !Hulls.TryGetValue(comp, out st)) { LiveHulls.RemoveAt(i); continue; }
                    string group = "Alternative Damage Control off";
                    float m = Model ? Multiplier(st.Ini, st.IsSub, st.IsCiv, out group) : 1f;
                    try { DcTeams.Retarget(comp, Model ? DcTeams.ModCount(st.Disp, st.IsSub, st.IsCiv, st.TeamsIni) : DcTeams.GameCount(st.Disp, st.IsSub, st.IsCiv, st.TeamsIni), st.Ini); }
                    catch (Exception e) { Trace("team numbers: " + e.Message); }
                    try { var obpL = _fCompObp.GetValue(comp); if (obpL != null) ScaleSystems(obpL); } catch (Exception e) { Trace("systems: " + e.Message); }
                    if (Math.Abs(m - st.Mult) < 0.0001f) continue;
                    try
                    {
                        Rescale(comp, m / st.Mult);
                        if (_logShips) Say("Hull: " + (st.Ini ?? "?") + " (" + group + ") changed during the mission: integrity " +
                                           F(st.OriginalX * st.Mult) + " -> " + F(st.OriginalX * m) + " (x" + F(m) + ")");
                        st.Mult = m;
                        n++;
                    }
                    catch (Exception e) { Warn("Changing the hull of " + (st.Ini ?? "?") + " failed: " + e.Message); }
                }
            }
            if (n > 0) Say("Hull integrity changed for " + n + " ship(s) at sea.");
        }

        // ------------------------------------------------------------ systems (modules)

        sealed class SysState { public float Orig, Mult = 1f; }
        static readonly ConditionalWeakTable<object, SysState> Systems = new ConditionalWeakTable<object, SysState>();
        static FieldInfo _fObpSystems, _fSysMax, _fSysModule;
        static PropertyInfo _pSysCurrent;

        /// <summary>
        /// The integrity of every system of a ship: x [Systems] Integrity, or its module type's own Integrity ([SystemTypeIntegrity],
        /// in place of modules.ini's: the game's adjustments - displacement, the ship's own value - keep their proportion);
        /// x1 while Alternative Damage Control is off. Idempotent (from the game's value, kept per system); the current integrity
        /// keeps its percent, so damage states stay.
        /// </summary>
        static bool SysFields(object sys)
        {
            if (_fSysMax == null)
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                for (var t = sys.GetType(); t != null && _fSysMax == null; t = t.BaseType)
                {
                    _fSysMax = t.GetField("_maxIntegrity", all); _fSysModule = t.GetField("_moduleName", all); _pSysCurrent = t.GetProperty("CurrentIntegrity", all);
                }
            }
            return _fSysMax != null && _pSysCurrent != null;
        }

        static void ScaleSystems(object obp)
        {
            if (_fObpSystems == null)
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                _fObpSystems = obp.GetType().GetField("_systems", all);
                if (_fObpSystems == null) for (var t = obp.GetType(); t != null && _fObpSystems == null; t = t.BaseType) _fObpSystems = t.GetField("_systems", all);
            }
            var list = _fObpSystems == null ? null : _fObpSystems.GetValue(obp) as IEnumerable;
            if (list == null) return;
            foreach (var sys in list)
            {
                if (sys == null) continue;
                if (!SysFields(sys)) return;
                SysState st;
                if (!Systems.TryGetValue(sys, out st)) { st = new SysState { Orig = (float)_fSysMax.GetValue(sys), Mult = 1f }; Systems.Add(sys, st); }
                float m = 1f;
                if (Model)
                {
                    string mod = _fSysModule == null ? null : _fSysModule.GetValue(sys) as string;
                    float v;
                    float gi = string.IsNullOrEmpty(mod) ? 0f : ModuleNames.GameIntegrity(mod);
                    m = gi > 0f && _perModule.TryGetValue(mod, out v) ? v / gi : _sysInt;   // its own Integrity instead of modules.ini's
                }
                if (Math.Abs(m - st.Mult) < 0.0001f || st.Orig <= 0f) continue;
                float max = st.Orig * m;
                float cur = Convert.ToSingle(_pSysCurrent.GetValue(sys, null));
                float oldMax = st.Orig * st.Mult;
                float frac = oldMax > 0f ? cur / oldMax : 1f;
                _fSysMax.SetValue(sys, max);
                if (_pSysCurrent.CanWrite) _pSysCurrent.SetValue(sys, max * frac, null);
                st.Mult = m;
            }
        }

        /// <summary>Alternative Damage Control switched off: every ship's teams on the move arrive, knocked-out and lost ones are back.</summary>
        static void ReleaseTeams()
        {
            lock (LiveHulls)
                foreach (var w in LiveHulls)
                {
                    var comp = w.Target;
                    if (comp != null) try { DcTeams.ReleaseAll(comp); } catch (Exception e) { Trace("ReleaseTeams: " + e.Message); }
                }
        }

        // SystemCompartment.CheckSpreadFire: spreads unless NextDouble() < 0.9998666644096375 - the chance from FireSpreadPercent
        public static double FireSpreadThreshold(double game)
        {
            if (!Model || Math.Abs(_fireSpread - GameFireSpreadPct) < 1e-5f) return game;
            return Math.Max(0.0, Math.Min(1.0, 1.0 - _fireSpread / 100.0));
        }

        static IEnumerable<CodeInstruction> FireSpreadTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Ldc_R8 || !(list[i].operand is double) || Math.Abs((double)list[i].operand - 0.9998666644096375) > 1e-12) continue;
                list.Insert(i + 1, new CodeInstruction(OpCodes.Call, typeof(AdmPlugin).GetMethod("FireSpreadThreshold", BindingFlags.Static | BindingFlags.Public)));
                i++; n++;
            }
            if (n != 1) Warn("SystemCompartment.CheckSpreadFire: spread chance patched " + n + " times, 1 expected (game updated?).");
            Trace("Fire spread patched: " + n);
            return list;
        }

        static void Scale(object o, FieldInfo f, float k)
        {
            if (o == null || f == null) return;
            float v = (float)f.GetValue(o);
            if (v == float.MaxValue || v == float.MinValue) return; // "not set yet" markers
            f.SetValue(o, v * k);
        }

        static void Rescale(object comp, float k)
        {
            Scale(comp, _fCompMaxInt, k);
            Scale(comp, _fCompFireMult, k);
            Scale(comp, _fCompMaxTotal, k);
            Scale(comp, _fCompMaxLimit, k);
            Scale(comp, _fCompLimit, k);
            foreach (var arrField in new[] { _fCompPort, _fCompStar })
            {
                var arr = arrField == null ? null : arrField.GetValue(comp) as Array;
                if (arr == null) continue;
                foreach (var fc in arr)
                {
                    Scale(fc, _fFcMaxInt, k);
                    Scale(fc, _fFcCur, k);
                    Scale(fc, _fFcLowest, k);
                }
            }
            var sys = _fCompSys == null ? null : _fCompSys.GetValue(comp) as Array;
            if (sys != null)
                foreach (var sc in sys)
                {
                    if (sc == null) continue;
                    Scale(sc, _fScMaxSev, k);
                    Scale(sc, _fScMaxGrow, k);
                    Scale(sc, _fScMinGrow, k);
                    Scale(sc, _fScGrow, k);
                    if (_pScSeverity != null && _pScSeverity.CanWrite)
                        _pScSeverity.SetValue(sc, (float)_pScSeverity.GetValue(sc, null) * k, null);
                }
        }

        void ReadSettings()
        {
            _debug = _debugEntry.Value;
            _logShips = _logShipsEntry.Value;
            _mWar = Pos(_mWarEntry.Value.V);
            _mCiv = Pos(_mCivEntry.Value.V);
            _mSub = Pos(_mSubEntry.Value.V);
            _shellVuln = Pos(_shellVulnEntry.Value.V);
            _fireVuln = Pos(_fireVulnEntry.Value.V);
            _floodVuln = Pos(_floodVulnEntry.Value.V);
            _flameUp = Math.Max(0f, _flameUpEntry.Value.V);
            _extinguish = Math.Max(0f, _extinguishEntry.Value.V);
            _floodSpeed = Math.Max(0f, _floodSpeedEntry.Value.V);
            _pumpOut = Math.Max(0f, _pumpOutEntry.Value.V);
            _teamBlocksSpread = _teamBlocksSpreadEntry.Value;
            SubmergedFlooding.FireDecay = Math.Max(0f, _subFireEntry.Value.V);
            DcControl.CounterRate = Math.Max(0f, _counterRateEntry.Value.V);
            DcControl.LeakPump = Math.Max(0f, _leakPumpEntry.Value.V);
            SpeedEffect.Set(_speedEffectEntry.Value);
            SpeedEffect.SmallAiFree = _smallAiEntry.Value;
            SectionEffects.ReopenRate = Math.Max(0f, _reopenEntry.Value.V);
            SectionEffects.ReopenLimit = Math.Max(0f, Math.Min(100f, _reopenLimitEntry.Value.V));
            SectionEffects.SystemsOut = _fullSystemsEntry.Value;
            DamageSpill.Enabled = _spillEntry.Value;
            SectionEffects.FireDecay = Math.Max(0f, _fullFireEntry.Value.V);
            DcTeams.FireWorkEff = Math.Max(0f, Math.Min(100f, _fireEffEntry.Value.V)) / 100f;
            var nd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in (_noDeckEntry.Value ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)) { var t = n.Trim(); if (t.Length > 0) nd.Add(t); }
            _noDeck = nd;
            DeckCorners.NoPoints = c => { HullState hs; return _noDeck.Count > 0 && c != null && Hulls.TryGetValue(c, out hs) && hs.Ini != null && _noDeck.Contains(hs.Ini); };
            SpeedEffect.MaxPlugKnots = Math.Max(0f, _maxPlugEntry.Value.V);
            DcControl.CounterLimit = Math.Max(0f, _counterLimitEntry.Value.V);
            _fireHull = Math.Max(0f, _fireHullEntry.Value.V);
            _fireSpread = Clamp(_fireSpreadEntry.Value.V, 0f, 100f);
            _unattended = _unattendedEntry.Value;
            _smoke = _smokeEntry.Value;
            _density = _densityEntry.Value;
            FireSmoke.StartOpacity = Clamp(_densityMinEntry.Value.V, 0f, 100f) / 100f;
            FireSmoke.EndOpacity = Clamp(_densityEndEntry.Value.V, 0f, 100f) / 100f;
            FireSmoke.ModerateFrom = Clamp(_smokeModEntry.Value.V, 1f, 100f) / 100f;
            FireSmoke.SevereFrom = Math.Max(FireSmoke.ModerateFrom, Clamp(_smokeSevEntry.Value.V, 1f, 100f) / 100f);
            _unattendedTime = Math.Max(0f, _unattendedTimeEntry.Value.V);
            _unattendedRate = Clamp(_unattendedRateEntry.Value.V, 0f, 100f);
            _unattendedMax = Clamp(_unattendedMaxEntry.Value.V, 0f, 100f);
            _sysInt = Pos(_sysIntEntry.Value.V);
            bool adm = _admEntry.Value;
            if (adm != _admOn)
            {
                _admOn = adm;
                Say("Alternative Damage Control " + (adm ? "on" : "off") + (Enabled ? " - ships at sea follow now." : "."));
                if (!adm) ReleaseTeams();
            }
            DcTeams.WarTable = DcTeams.Parse(_warTeamsEntry.Value);
            DcTeams.SubTable = DcTeams.Parse(_subTeamsEntry.Value);
            DcTeams.CivTable = DcTeams.Parse(_civTeamsEntry.Value);
            DcTeams.Effectiveness = Math.Max(0f, _teamEffEntry.Value.V);
            DcTeams.Speed = Math.Max(0f, _transferSpeedEntry.Value.V);
            DcTeams.Muster = Math.Max(0f, _musterEntry.Value.V);
            DcTeams.AnyIntegrity = _anyIntegrityEntry.Value;
            DcTeams.ReserveHits = ReserveHits.Enabled = _reserveHitsEntry.Value;
            DcTeams.HealthMul = _healthOnEntry.Value ? Math.Max(0f, _healthEntry.Value.V) : 0f;
            DcTeams.RegenRate = Math.Max(0f, _regenEntry.Value.V);
            DcTeams.FireWound = Math.Max(0f, _fireWoundEntry.Value.V);
            DcTeams.WoundChance = Math.Max(0f, _woundChanceEntry.Value.V);
            DcTeams.HitWound = Math.Max(0f, _hitWoundEntry.Value.V);
            DcTeams.FatigueRecover = Math.Max(0f, _fatigueRecoverEntry.Value.V);
            DcTeams.ExhaustedMin = Math.Max(Math.Max(0f, _fatigueEntry.Value.V), Math.Max(0f, _exhaustEntry.Value.V));   // never below 'Tired after'
            DcTeams.SpeedRested = Math.Max(0f, _speedRestedEntry.Value.V) / 100f;
            DcTeams.SpeedTired = Math.Max(0f, _speedTiredEntry.Value.V) / 100f;
            DcTeams.SpeedExhausted = Math.Max(0f, _speedExhaustEntry.Value.V) / 100f;
            DcTeams.FatigueMin = _fatigueOnEntry.Value ? Math.Max(0f, _fatigueEntry.Value.V) : 0f;
            Limits.ListFull = Clamp(_listLimitEntry.Value.V, 5f, 69f);
            Limits.TrimFull = Clamp(_trimLimitEntry.Value.V, 1f, 30f);
            Limits.TrimRefLength = Math.Max(0f, _trimLengthEntry.Value.V);
            Limits.LevelFull = Clamp(_levelLimitEntry.Value.V, 5f, 100f);
            TrimFlooding.Speed = Math.Max(0f, _deckFloodSpeedEntry.Value.V);
            SubmergedFlooding.Delay = Math.Max(0f, _deckFloodDelayEntry.Value.V);
            BubblesOff.Minutes = Math.Max(0f, _bubblesEntry.Value.V);
            CapsizeFix.KeepCom = _keepComEntry.Value;
            WreckDrag.Factor = Math.Max(0f, _wreckDragEntry.Value.V);
            SubmergedFlooding.Rate = 0.1f * Math.Max(0f, _deckFloodSpeedEntry.Value.V);   // 1.0 = 0.1 % per second per metre
            TrimFlooding.Imbalance = 1f;   // FloodImbalanceEffect is switched off (hidden in the settings) until reworked; the key is still read so old profiles load
            _logFires = _logFiresEntry.Value;
            TrimFlooding.LogHeel = _logHeelEntry.Value;
            DeckCorners.Model = _deckCornersEntry.Value;
            DeckCorners.ShowCollider = _showHullEntry.Value;
            DeckCorners.ModelView = ParseShipView(_shipViewEntry.Value);
            _fixTrimTc = _fixTrimTcEntry.Value;
            _dumpXaml = HiddenBool("General", "DumpXaml");
            if (_dumpXaml) RequestXamlExport();
            _uiOn = _uiEntry.Value;
            _weaponColor = _weaponColorEntry.Value;
            _enemyDmg = _enemyDmgEntry.Value;
            try { ApplyUiSettings(); } catch (Exception e) { Trace("ApplyUiSettings: " + e.Message); }

            var map = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            ReadPerShip(map);
            _perShip = map;
            var mods = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            ReadSection(PerModuleSection, mods);
            _perModule = mods;

            Trace("settings: detailed view " + (_uiOn ? "on" : "off") + ", damage model " + (_admOn ? "on" : "off") + ", warships " + F(_mWar) + ", civilian " + F(_mCiv) + ", submarines " + F(_mSub) + ", perShip " + map.Count +
                  ", shellVuln " + F(_shellVuln) + ", fireVuln " + F(_fireVuln) + ", floodVuln " + F(_floodVuln));
        }

        static float Pos(float v) { return v > 0.01f ? v : 0.01f; }
        static float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }

        static Dictionary<ConfigDefinition, string> Orphans(ConfigFile cfg)
        {
            var prop = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return prop == null ? null : prop.GetValue(cfg, null) as Dictionary<ConfigDefinition, string>;
        }

        /// <summary>
        /// The .cfg holds only the general settings ([General], [UI]). Anything else in it is an orphan: removed, except the hidden
        /// [General] DumpXaml while it is true.
        /// </summary>
        bool CleanCfg()
        {
            var orphans = Orphans(Cfg);
            if (orphans == null) return false;
            bool removed = false;
            foreach (var kv in new List<KeyValuePair<ConfigDefinition, string>>(orphans))
            {
                bool on;
                if (string.Equals(kv.Key.Section, "General", StringComparison.OrdinalIgnoreCase) && string.Equals(kv.Key.Key, "DumpXaml", StringComparison.OrdinalIgnoreCase) &&
                    bool.TryParse((kv.Value ?? "").Trim(), out on) && on) continue;
                orphans.Remove(kv.Key);
                removed = true;
            }
            return removed;
        }

        /// <summary>
        /// Builds up to this version the .cfg held the profile's values too (and the per-ship lines). They are taken out of it: the
        /// values in use then, as a profile (over the active profile's values), or null when the .cfg had none.
        /// </summary>
        Profile TakeOldProfileValues()
        {
            Profile p = null;
            bool sawShips = false, sawMods = false;
            try
            {
                var orphans = Orphans(Cfg);
                if (orphans == null) { Logger.LogWarning("The .cfg cannot be cleaned up (BepInEx changed?)."); return null; }
                foreach (var kv in orphans)
                {
                    string sec = kv.Key.Section, key = kv.Key.Key.Trim();
                    bool ship = string.Equals(sec, PerShipSection, StringComparison.OrdinalIgnoreCase) || string.Equals(sec, OldPerShipSection, StringComparison.OrdinalIgnoreCase);
                    bool mod = string.Equals(sec, PerModuleSection, StringComparison.OrdinalIgnoreCase);
                    bool oldList = string.Equals(sec, "HullIntegrity", StringComparison.OrdinalIgnoreCase) && string.Equals(key, "PerShip", StringComparison.OrdinalIgnoreCase);
                    string full = null;
                    foreach (var e in _profileEntries) if (string.Equals(Key(e.Definition), sec + "/" + key, StringComparison.OrdinalIgnoreCase)) full = Key(e.Definition);
                    bool old = IsOld(sec, key);
                    if (!ship && !mod && !oldList && !old && full == null) continue;
                    if (p == null)
                    {
                        var basis = LoadProfile(_profileEntry.Value) ?? LoadProfile(AdvancedName);
                        p = basis.Copy(basis.Name);
                        p.BuiltIn = basis.BuiltIn;
                    }
                    // the .cfg's own lines replace the profile's list (only when it has any: a list it does not mention stays)
                    if ((ship || oldList) && !sawShips) { p.Ships.Clear(); sawShips = true; }
                    if (mod && !sawMods) { p.Modules.Clear(); sawMods = true; }
                    Factor f;
                    if (old && string.Equals(sec, OldPerModuleSection, StringComparison.OrdinalIgnoreCase) && !sawMods) { p.Modules.Clear(); sawMods = true; }
                    if (old) ConvertOld(p, sec, key, kv.Value);
                    else if (full != null) p.Values[full] = kv.Value;
                    else if (oldList)
                    {
                        // the first test build: [HullIntegrity] PerShip = name=x, name=y
                        foreach (var part in (kv.Value ?? "").Split(','))
                        {
                            int eq = part.IndexOf('=');
                            if (eq > 0 && part.Substring(0, eq).Trim().Length > 0 && Factor.TryParse(part.Substring(eq + 1), out f) && f.V > 0f)
                                p.Ships[part.Substring(0, eq).Trim()] = f.ToString();
                        }
                    }
                    else if (key.Length > 0 && key != PerShipExample && Factor.TryParse(kv.Value, out f) && f.V > 0f)
                    {
                        if (ship) p.Ships[key] = f.ToString(); else p.Modules[key] = f.ToString();
                    }
                }
            }
            catch (Exception e) { Warn("Old settings in the .cfg could not be read: " + e.Message); }
            CleanCfg();
            return p;
        }

        /// <summary>
        /// Start: the profile named in the .cfg comes into use (its file; a missing one: Advanced). Values from an old .cfg that differ
        /// from it are kept as a profile of the player's own (Custom when the profile was a built-in one).
        /// </summary>
        void StartProfile(Profile old)
        {
            string name = _profileEntry.Value;
            var prof = LoadProfile(name);
            if (prof == null)
            {
                if (old != null && BadName(name) == null) { WriteProfileFile(UserFile(name), old.Copy(name.Trim())); prof = LoadProfile(name); }
                if (prof == null)
                {
                    if (!string.IsNullOrEmpty(name)) Warn("Settings profile \"" + name + "\" not found; Advanced is used.");
                    prof = LoadProfile(AdvancedName);
                }
            }
            if (old != null && !old.SameAs(prof))
            {
                string own = prof.BuiltIn ? CustomName : prof.Name;
                var oc = old.Copy(own); oc.Parent = prof.BuiltIn ? prof.Name : prof.Parent;
                WriteProfileFile(UserFile(own), oc);
                prof = LoadProfile(own) ?? prof;
                Say("Settings from the .cfg kept as profile \"" + own + "\".");
            }
            ApplyValues(prof, prof.Name);
        }

        /// <summary>Saves the .cfg (general settings only).</summary>
        void SaveConfig()
        {
            try { Cfg.Save(); }
            catch (Exception e) { Logger.LogWarning("Saving the settings failed: " + e.Message); return; }
            try
            {
                // without BepInEx's comments (descriptions, types, defaults): everything is explained in the settings window
                var path = Cfg.ConfigFilePath;
                var lines = new List<string>();
                foreach (var raw in File.ReadAllLines(path))
                {
                    string t = raw.Trim();
                    // no blank lines inside a section (BepInEx puts one after every entry); one blank line before each section
                    if (t.StartsWith("#") || t.Length == 0) continue;
                    if (t.StartsWith("[") && lines.Count > 0) lines.Add("");
                    lines.Add(t);
                }
                    File.WriteAllText(path, string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception e) { Trace("SaveConfig: " + e.Message); }
            RememberCfgStamp(); // our own write is not a change to re-read
        }

        /// <summary>
        /// A setting that is not written to the .cfg by default (diagnostics): "Key = true" added by hand under [Section] turns it on.
        /// BepInEx keeps such a line as it is; it is read from there (also after the file is re-read).
        /// </summary>
        bool HiddenBool(string section, string key)
        {
            Dictionary<ConfigDefinition, string> orphans = null;
            try { orphans = Orphans(Cfg); } catch { }
            if (orphans == null) return false;
            foreach (var kv in orphans)
                if (string.Equals(kv.Key.Section, section, StringComparison.OrdinalIgnoreCase) && string.Equals(kv.Key.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    bool on;
                    return bool.TryParse((kv.Value ?? "").Trim(), out on) && on;
                }
            return false;
        }

        Dictionary<ConfigDefinition, string> PerShipLines()
        {
            try { return Orphans(Pcfg); } catch (Exception e) { Trace("Orphans: " + e.Message); return null; }
        }

        void ReadSection(string section, Dictionary<string, float> map)
        {
            var orphans = PerShipLines();
            if (orphans == null) return;
            foreach (var kv in orphans)
            {
                if (!string.Equals(kv.Key.Section, section, StringComparison.OrdinalIgnoreCase)) continue;
                Factor v;
                if (Factor.TryParse(kv.Value, out v) && v.V > 0f) map[kv.Key.Key.Trim()] = Pos(v.V);
                else Logger.LogWarning("[" + section + "] " + kv.Key.Key + " = '" + kv.Value + "' is not a positive number (e.g. 2.0); ignored.");
            }
        }

        void ReadPerShip(Dictionary<string, float> map)
        {
            var orphans = PerShipLines();
            if (orphans == null) return;
            foreach (var kv in orphans)
            {
                if (!string.Equals(kv.Key.Section, PerShipSection, StringComparison.OrdinalIgnoreCase)) continue;
                var name = kv.Key.Key.Trim();
                if (name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                if (name.Length == 0 || name == PerShipExample) continue;
                Factor v;
                if (!Factor.TryParse(kv.Value, out v) || v.V <= 0f)
                {
                    Logger.LogWarning("[HullIntegrityPerShip] " + kv.Key.Key + " = '" + kv.Value + "' is not a positive number (e.g. 3.0); ignored.");
                    continue;
                }
                map[name] = Pos(v.V);
            }
        }

        /// <summary>Re-reads the tick of the game's Mods menu.</summary>
        static void RefreshEnabled()
        {
            bool? state = ModState.Read();
            if (state == null && ModState.Controlled) Warn("Could not read the game's mod list; " + PluginName + " stays on.");
            bool now = state != false;
            if (now == Enabled) return;
            Enabled = now;
            if (ModState.Controlled)
                Say(now ? "Enabled in the game's Mods menu (" + ModState.ModId + ")." : "Disabled in the game's Mods menu (" + ModState.ModId + "); the game's damage model is used.");
        }

        static Assembly GameAssembly()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "Seapower-Scripts") return a;
            return null;
        }

        static void ReserveHitPostfix(object __instance, object[] __args) { ReserveHits.Postfix(__instance, __args); HitLog.Postfix(__instance, __args); }
        static void SpillPrefix(object __instance, object[] __args) { DamageSpill.Prefix(__instance, __args); }

        static HarmonyMethod Hm(string name)
        {
            return new HarmonyMethod(typeof(AdmPlugin).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
        }

        // ------------------------------------------------------------ patching

        void TryPatch()
        {
            var asm = GameAssembly();
            if (asm == null) return; // not loaded yet; try again next frame
            _patchTried = true;

            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var harmony = new Harmony(PluginGuid);
            int ok = 0, failed = 0;

            Type tComp = asm.GetType("SeaPower.Compartments");
            Type tFc = asm.GetType("SeaPower.FloodingCompartment");
            Type tObp = asm.GetType("SeaPower.ObjectBaseParameters");
            Type tSop = asm.GetType("SeaPower.SimpleObjectParameters");
            Type tBlast = asm.GetType("SeaPower.Blastzone");
            _tSubmarine = asm.GetType("SeaPower.Submarine");
            Type tIni = asm.GetType("SeaPower.IniHandler");

            if (tComp != null)
            {
                _fCompObp = tComp.GetField("_obp", all);
                _fCompBase = tComp.GetField("_baseObject", all);
                _fCompMaxInt = tComp.GetField("_maxIntegrity", all);
                _fCompMaxLimit = tComp.GetField("_maxLimitIntegrity", all);
                _fCompDisp = tComp.GetField("_displacement", all);
                _fCompFireMult = tComp.GetField("_fireMult", all);
                _fCompMaxTotal = tComp.GetField("_maxTotalIntegrity", all);
                _fCompLimit = tComp.GetField("_limitIntegrity", all);
                _fCompPort = tComp.GetField("_portCompartments", all);
                _fCompStar = tComp.GetField("_starboardCompartments", all);
                _fCompSys = tComp.GetField("_systemCompartments", all);
                _fCompCount = tComp.GetField("_compartmentsCount", all);
            }
            if (tFc != null)
            {
                _fFcComp = tFc.GetField("_compartments", all);
                _fFcCur = tFc.GetField("_currentIntegrity", all);
                _fFcMaxInt = tFc.GetField("_maxIntegrity", all);
                _fFcLowest = tFc.GetField("_lowestIntegrity", all);
            }
            if (tObp != null)
            {
                _fObpRoles = tObp.GetField("_unitRoles", all);
                _fObpIniName = tObp.GetField("_objectIniName", all);
                _fObpArmor = tObp.GetField("_armorType", all);
                _pObpCivilian = tObp.GetProperty("_isCivilian", all);
            }
            if (tSop != null)
            {
                _fSopIni = tSop.GetField("_ini", all);
                _fSopIniName = tSop.GetField("_objectIniName", all);
                _fSopCiv = tSop.GetField("_isCivilian", all);
                _fSopRoles = tSop.GetField("_unitRoles", all);
            }
            if (tIni != null)
                _mIniReadFloat = tIni.GetMethod("readValue", BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(string), typeof(string), typeof(float), typeof(bool) }, null);

            // 1. hull integrity
            if (tComp != null && _fCompObp != null && _fCompBase != null && _fCompMaxInt != null && _fObpRoles != null && _pObpCivilian != null)
                Patch(harmony, tComp.GetMethod("Init", all), null, null, "InitTranspiler", "Compartments.Init", ref ok, ref failed);
            else { Warn("SeaPower.Compartments (or its fields) not found: hull integrity is NOT changed."); failed++; }

            // [Effects] five fire / smoke points
            FireSmoke.Trace = Trace;
            FireSmoke.Verbose = () => _debug;
            FireSmoke.IsEnabled = () => Enabled && _smoke;
            FireSmoke.DensityOn = () => Enabled && _density;
            if (tComp != null && FireSmoke.Init(asm))
            {
                Patch(harmony, tComp.GetMethod("Init", all), null, "SmokeInitPostfix", null, "Compartments.Init (smoke points)", ref ok, ref failed);
                Patch(harmony, tComp.GetMethod("UpdateFireState", all), null, "SmokeFireStatePostfix", null, "Compartments.UpdateFireState", ref ok, ref failed);
                Patch(harmony, tComp.GetMethod("CreateFireEffect", all), null, "SmokeCreatePostfix", "SmokeCreateTranspiler", "Compartments.CreateFireEffect", ref ok, ref failed);
            }
            else Warn("Extra smoke points: game members not found; the game's three are used.");

            // debug visualisation: deck-corner heights vs. the current wave, for the player's selected ship
            DeckCorners.Trace = Trace;
            DeckCorners.Warn = Warn;
            if (DeckCorners.Init(harmony, asm)) ok++; else failed++;

            // 2. gun shell damage tiers
            if (tBlast != null && _fCompMaxLimit != null)
                Patch(harmony, tBlast.GetMethod("CheckForInstakill", all), null, null, "InstakillTranspiler", "Blastzone.CheckForInstakill", ref ok, ref failed);
            else { Warn("Blastzone.CheckForInstakill not found: HullShellVulnerability has no effect."); failed++; }

            // 3. fire ignition by hits
            if (tBlast != null && _fCompMaxInt != null)
                Patch(harmony, tBlast.GetMethod("OnHitUnit", all), null, null, "OnHitUnitTranspiler", "Blastzone.OnHitUnit", ref ok, ref failed);
            // + flooding through holes (same method)
            if (tBlast != null && tFc != null && _fFcComp != null && _fFcCur != null && _fFcMaxInt != null)
                Patch(harmony, tBlast.GetMethod("ProcessCompartmentHit", all), null, null, "CompartmentHitTranspiler", "Blastzone.ProcessCompartmentHit", ref ok, ref failed);

            // wounds of the teams resting in the central compartment (Hits in reserve)
            if (tBlast != null)
            {
                ReserveHits.Trace = Trace;
                DamageSpill.Trace = Trace;
                Patch(harmony, tBlast.GetMethod("ProcessCompartmentHit", all), "SpillPrefix", "ReserveHitPostfix", null, "Blastzone.ProcessCompartmentHit (reserve hits, damage spill)", ref ok, ref failed);
            }

            // diagnostics: LogFires
            var tSc = asm.GetType("SeaPower.SystemCompartment");
            var tGt = asm.GetType("SeaPower.GameTime");
            var tOb = asm.GetType("SeaPower.ObjectBase");
            if (tSc != null)
            {
                _fScComp = tSc.GetField("_compartments", all);
                _fScNum = tSc.GetField("_compartmentNumber", all);
                _fScGrow = tSc.GetField("_fireGrowRate", all);
                _fScMaxSev = tSc.GetField("_maxFireSeverity", all);
                _pScSeverity = tSc.GetProperty("FireSeverity", all);
                _pScState = tSc.GetProperty("FireState", all);
                _fScMaxGrow = tSc.GetField("_maxfireGrowRate", all);
                _fScMinGrow = tSc.GetField("_minfireGrowRate", all);
                if (tGt != null) _pGameTime = tGt.GetProperty("missionElapsedTime", all);
                if (tOb != null) _mUidName = tOb.GetMethod("getUIDAndName", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (tComp != null) _pCompDcTeams = tComp.GetProperty("DamageControlTeamsNumbers", all);
                if (_fScComp != null && _pScSeverity != null && _pScState != null && _fScMaxSev != null)
                {
                    Patch(harmony, tSc.GetMethod("SetFireState", all), null, "FireStatePostfix", null, "SystemCompartment.SetFireState", ref ok, ref failed);
                    Patch(harmony, tSc.GetMethod("IgniteByNeighbour", all), "IgniteByNeighbourPrefix", null, null, "SystemCompartment.IgniteByNeighbour", ref ok, ref failed);
                    Patch(harmony, tSc.GetMethod("AddCompartmentFire", all), "AddFirePrefix", "AddFirePostfix", null, "SystemCompartment.AddCompartmentFire", ref ok, ref failed);
                }
            }


            // 8. speeds of fire and water: a multiplier right after the value each formula is built on
            var tScx = asm.GetType("SeaPower.SystemCompartment");
            var tFcx = asm.GetType("SeaPower.FloodingCompartment");
            if (tScx != null) Patch(harmony, tScx.GetMethod("OnFixedUpdate", all), null, null, "FlameUpTranspiler", "SystemCompartment.OnFixedUpdate", ref ok, ref failed);
            if (tScx != null) Patch(harmony, tScx.GetMethod("OnFixedUpdate", all), null, null, "FireHullTranspiler", "SystemCompartment.OnFixedUpdate (hull)", ref ok, ref failed);
            if (tFcx != null) Patch(harmony, tFcx.GetMethod("OnFixedUpdate", all), null, null, "FloodingSpeedTranspiler", "FloodingCompartment.OnFixedUpdate", ref ok, ref failed);
            if (tComp != null)
            {
                Patch(harmony, tComp.GetMethod("RepairFire", all), null, null, "ExtinguishTranspiler", "Compartments.RepairFire", ref ok, ref failed);
                Patch(harmony, tComp.GetMethod("UpdateWantedVelocityInKnots", all), null, null, "SpeedLimitTranspiler", "Compartments.UpdateWantedVelocityInKnots", ref ok, ref failed);
                // unattended fires: when teams last fought each fire, and the growth every fire check
                Patch(harmony, tComp.GetMethod("RepairFire", all), null, "FireFoughtPostfix", null, "Compartments.RepairFire (unattended)", ref ok, ref failed);
                Patch(harmony, tComp.GetMethod("CheckFire", all), null, "CheckFirePostfix", null, "Compartments.CheckFire (unattended)", ref ok, ref failed);
                Patch(harmony, tComp.GetMethod("RepairFlooding", all), null, null, "PumpOutTranspiler", "Compartments.RepairFlooding", ref ok, ref failed);
            }

            // 10. damage control teams: number by displacement, transfers, knock-outs, effectiveness
            DcTeams.IsEnabled = () => Model;
            DcTeams.IsTracking = () => Enabled;
            DcTeams.Info = Say;
            DcTeams.Trace = Trace;
            SpeedEffect.Trace = Trace;
            if (tComp != null && DcTeams.Init(asm))
            {
                DcAuto.Trace = Trace; SettingsWindow.Trace = Trace; TrimFlooding.Trace = Trace; TrimFlooding.Log = m => Logger.LogInfo(m); TrimFlooding.IsEnabled = () => Model; SubmergedFlooding.Trace = Trace; SubmergedFlooding.IsEnabled = () => Model; SectionEffects.Trace = Trace; SectionEffects.IsEnabled = () => Model; CapsizeFix.Trace = Trace; CapsizeFix.IsEnabled = () => Model; BubblesOff.Trace = Trace; BubblesOff.IsEnabled = () => Model; WreckDrag.Trace = Trace; WreckDrag.IsEnabled = () => Model;
                SettingsWindow.Warn = Warn; SettingsWindow.Info = Say;
                if (!DcAuto.Init(asm)) Warn("Optimised Auto DC: game members not found; the game's Auto DC is used.");
                DcControl.StayWhenIdle = c => DcAuto.Ok && DcTeams.TransfersOn && DcTeams.Active(c) && DcAuto.AnyWork(c);
                Patch(harmony, tComp.GetMethod("SendDCTeamToCompartment", all), "TeamSendPrefix", null, null, "Compartments.SendDCTeamToCompartment", ref ok, ref failed);
                Patch(harmony, tComp.GetMethod("ReturnDCTeamToPool", all), "TeamReturnPrefix", null, null, "Compartments.ReturnDCTeamToPool", ref ok, ref failed);
                Patch(harmony, tComp.GetMethod("RecallAllTeams", all), "TeamRecallPrefix", null, null, "Compartments.RecallAllTeams", ref ok, ref failed);
                int okBefore = ok;
                Patch(harmony, tComp.GetMethod("OnFixedUpdate", all), "TeamTickPrefix", null, null, "Compartments.OnFixedUpdate (teams)", ref ok, ref failed);
                Patch(harmony, tComp.GetMethod("Sink", all), "SinkComPrefix", "SinkComPostfix", null, "Compartments.Sink (centre of mass)", ref ok, ref failed);
                Patch(harmony, tComp.GetMethod("OnFixedUpdate", all), null, "WreckDragPostfix", null, "Compartments.OnFixedUpdate (wreck drag)", ref ok, ref failed);
                _trimFloodHooked = ok > okBefore;   // the game's trim sinking is replaced only when the flooding that replaces it runs
                foreach (var name in new[] { "RepairFloodingRate", "RepairFire", "RepairFlooding", "RepairStructure", "RepairSystems" })
                    Patch(harmony, tComp.GetMethod(name, all), null, null, "TeamEffTranspiler", "Compartments." + name + " (effectiveness)", ref ok, ref failed);
            }
            else { Warn("Damage control team members not found: team numbers, transfers and knock-outs are the game's."); failed++; }

            // 10. fire spread chance
            var tSys = asm.GetType("SeaPower.SystemCompartment");
            if (tSys != null) Patch(harmony, tSys.GetMethod("CheckSpreadFire", all), null, null, "FireSpreadTranspiler", "SystemCompartment.CheckSpreadFire", ref ok, ref failed);

            // 9. faster flooding sooner: the game's own thresholds for heavy listing and for sinking, lowered
            if (tComp != null)
                Patch(harmony, tComp.GetMethod("OnFixedUpdate", all), null, null, "SinkThresholdsTranspiler", "Compartments.OnFixedUpdate", ref ok, ref failed);

            // 9b. the game's own trim error above 10x time compression (stale centre of mass in the buoyancy torque)
            TimeCompressionFix.IsEnabled = () => Enabled && _fixTrimTc;
            TimeCompressionFix.Trace = Trace; TimeCompressionFix.Warn = Warn;
            if (TimeCompressionFix.Init(harmony, asm)) ok++; else failed++;
            WeaponStatusColors.Trace = Trace; WeaponStatusColors.Init(asm);
            ContactIntel.Trace = Trace; ContactIntel.IsEnabled = () => Enabled && _enemyDmg;
            if (ContactIntel.Init(harmony, asm)) ok++; else failed++;
            EnemyStatus.Trace = Trace; EnemyStatus.IsEnabled = () => Enabled && _enemyDmg;
            if (EnemyStatus.Init(harmony, asm)) ok++; else failed++;
            if (TrimFlooding.InitList(harmony, asm)) ok++; else failed++;

            // 7. damage control: ship-wide priority (Auto / Fire / Flooding) and tasks given by hand to single teams
            DcControl.IsEnabled = () => Enabled && _uiOn;   // DC Priority and tasks by hand: with the detailed view, whatever the damage model
            DcControl.AutoOn = () => Model;                 // the optimised Auto DC: with the damage model, whatever the view
            DamageControlUi.ModelOn = () => Model; ContactIntel.ModelOn = () => Model;
            DcControl.Trace = Trace;
            if (tComp != null && DcControl.Init(asm))
            {
                Patch(harmony, tComp.GetMethod("CheckDamageControl", all), "DcCheckPrefix", null, null, "Compartments.CheckDamageControl", ref ok, ref failed);
                Patch(harmony, tComp.GetMethod("CheckDCTeamsAllocation", all), "DcAllocPrefix", null, null, "Compartments.CheckDCTeamsAllocation", ref ok, ref failed);
            }
            else { Warn("Damage control members not found: priorities and tasks by hand are off."); failed++; }

            // kept in saved games: extra keys in the unit's own section (the game ignores keys it does not know) - the hull and
            // systems multipliers they were saved with, the teams and DC Priority; independent of the members above
            if (tComp != null && tIni != null)
            {
                _mIniWriteStr = tIni.GetMethod("writeValue", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(string), typeof(string) }, null);
                _mIniReadStr = tIni.GetMethod("readValue", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(string), typeof(string), typeof(bool) }, null);
            }
            if (tComp != null && _mIniWriteStr != null && _mIniReadStr != null)
            {
                Patch(harmony, tComp.GetMethod("SaveStateToFile", all), null, "DcSavePostfix", null, "Compartments.SaveStateToFile", ref ok, ref failed);
                Patch(harmony, tComp.GetMethod("LoadStateFromFile", all), null, "DcLoadPostfix", null, "Compartments.LoadStateFromFile", ref ok, ref failed);
                var tBs = asm.GetType("SeaPower.BaseSystem");
                if (tBs != null)
                {
                    Patch(harmony, tBs.GetMethod("SaveStateToFile", all), null, "SysSavePostfix", null, "BaseSystem.SaveStateToFile", ref ok, ref failed);
                    Patch(harmony, tBs.GetMethod("LoadStateFromFile", all), null, "SysLoadPostfix", null, "BaseSystem.LoadStateFromFile", ref ok, ref failed);
                }
            }
            else { Warn("Saved-game members not found: hull multipliers, teams and DC Priority are not kept in saved games."); failed++; }

            // 6. Unit Status -> Damage Control tab
            try { PatchUi(harmony, asm, tComp, ref ok, ref failed); }
            catch (Exception e) { Warn("Damage Control view not available: " + e.Message); Trace(e.ToString()); }

            // 5. following the Mods menu tick: Apply in the Mods menu, and the start of every load screen (before ships spawn)
            var fm = asm.GetType("SeaPower.FileManager");
            if (fm != null) Patch(harmony, fm.GetMethod("SaveDirectories", all), null, "ModsSavedPostfix", null, "FileManager.SaveDirectories", ref ok, ref failed);
            var ls = asm.GetType("SeaPower.LoadScreen");
            if (ls != null)
                foreach (var nt in ls.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                    if (nt.Name.StartsWith("<ExecuteLoadScreen>", StringComparison.Ordinal))
                    {
                        Patch(harmony, nt.GetMethod("MoveNext", all), "LoadScreenPrefix", null, null, "LoadScreen.ExecuteLoadScreen", ref ok, ref failed);
                        break;
                    }

            _patched = failed == 0;
            Logger.LogInfo("Hooks installed: " + ok + (failed > 0 ? ", failed: " + failed + " (see warnings above; game updated?)" : "") + ".");
        }

        void Patch(Harmony h, MethodInfo target, string prefix, string postfix, string transpiler, string label, ref int ok, ref int failed)
        {
            if (target == null) { Warn(label + " not found (game updated?)."); failed++; return; }
            try
            {
                h.Patch(target, prefix: prefix == null ? null : Hm(prefix), postfix: postfix == null ? null : Hm(postfix),
                        transpiler: transpiler == null ? null : Hm(transpiler));
                ok++;
                Trace("patched " + label);
            }
            catch (Exception e) { Warn("Patching " + label + " failed: " + e.Message); Trace(e.ToString()); failed++; }
        }

        // ------------------------------------------------------------ Mods menu tick

        static WeakReference _loadIter = new WeakReference(null);

        // the first MoveNext of a new load-screen coroutine: a new scene (mission) is being built - re-read the tick first
        static void LoadScreenPrefix(object __instance)
        {
            if (__instance == null || ReferenceEquals(_loadIter.Target, __instance)) return;
            _loadIter = new WeakReference(__instance);
            try { RefreshEnabled(); } catch (Exception e) { Trace("LoadScreenPrefix: " + e.Message); }
        }

        // the game has just written the Mods menu's list (Apply)
        static void ModsSavedPostfix()
        {
            try { RefreshEnabled(); } catch (Exception e) { Trace("ModsSavedPostfix: " + e.Message); }
        }

        // ------------------------------------------------------------ hull integrity (Compartments.Init)

        /// <summary>
        /// Inserts "ApplyHull(this)" right before "_fireMult = _maxIntegrity / 1800f": at that point _maxIntegrity holds the
        /// game's final value (formula, civilian / submarine halving, armour, IntegrityOverride) and nothing has used it yet.
        /// </summary>
        static IEnumerable<CodeInstruction> InitTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            int at = -1;
            for (int i = 5; i < list.Count; i++)
            {
                var fi = list[i].operand as FieldInfo;
                if (list[i].opcode == OpCodes.Stfld && fi != null && fi.Name == "_fireMult"
                    && list[i - 5].opcode == OpCodes.Ldarg_0 && list[i - 4].opcode == OpCodes.Ldarg_0 && list[i - 3].LoadsField(_fCompMaxInt))
                { at = i - 5; break; }
            }
            if (at < 0)
            {
                Warn("Compartments.Init: insertion point not found (game updated?); hull integrity is NOT changed.");
                return list;
            }
            var call = new CodeInstruction(OpCodes.Ldarg_0);
            call.MoveLabelsFrom(list[at]); // the IntegrityOverride branch jumps here
            list.InsertRange(at, new[] { call, new CodeInstruction(OpCodes.Call, typeof(AdmPlugin).GetMethod("ApplyHull", BindingFlags.Static | BindingFlags.Public)) });
            Trace("Compartments.Init: ApplyHull inserted at " + at);
            return list;
        }

        public static void ApplyHull(object comp)
        {
            try
            {
                if (!Enabled || comp == null) return;
                var obp = _fCompObp.GetValue(comp);
                if (obp == null) return;

                var roles = _fObpRoles.GetValue(obp) as IEnumerable;
                if (HasRole(roles, "LifeRaft") || HasRole(roles, "SeaMine")) return; // the game sets 0.1 for them anyway

                var bo = _fCompBase.GetValue(comp);
                bool isSub = bo != null && _tSubmarine != null && _tSubmarine.IsInstanceOfType(bo);
                bool isCiv = (bool)_pObpCivilian.GetValue(obp, null);
                if (Model && !isCiv && !isSub && !HasWeapons(obp)) isCiv = true;   // no weapons: not a warship
                float dispT = 0f; int teamsIni = -1;
                try
                {
                    dispT = _fCompDisp == null ? 0f : (float)_fCompDisp.GetValue(comp);
                    teamsIni = TeamsOverrideOf(obp);
                    if (Model) DcTeams.SetCount(comp, dispT, isSub, isCiv, teamsIni);   // off: the game's own count stays
                }
                catch (Exception e) { Trace("team count: " + e.Message); }
                string ini = _fObpIniName == null ? null : _fObpIniName.GetValue(obp) as string;
                bool fixedByIni = OverrideOf(obp) > 0f;

                string group = "Alternative Damage Control off";
                float m = Model ? Multiplier(ini, isSub, isCiv, out group) : 1f;
                float x0 = (float)_fCompMaxInt.GetValue(comp);
                float x = x0 * m;
                if (m != 1f) _fCompMaxInt.SetValue(comp, x);
                try { ScaleSystems(obp); } catch (Exception e) { Trace("systems: " + e.Message); }
                Hulls.Remove(comp);
                Hulls.Add(comp, new HullState { Mult = m, OriginalX = x0, Ini = ini, IsSub = isSub, IsCiv = isCiv, FixedByIni = fixedByIni, Disp = dispT, TeamsIni = teamsIni });
                lock (LiveHulls)
                {
                    if (LiveHulls.Count > 64) LiveHulls.RemoveAll(w => !w.IsAlive);
                    LiveHulls.Add(new WeakReference(comp));
                }

                if (_logShips || _debug)
                {
                    string disp = _fCompDisp == null ? "?" : F((float)_fCompDisp.GetValue(comp));
                    string armor = _fObpArmor == null ? "?" : Convert.ToString(_fObpArmor.GetValue(obp));
                    string line = "Hull: " + (ini ?? "?") + " (" + group + (fixedByIni ? ", IntegrityOverride" : "") + "), " + disp + " t, armor " + armor +
                                  ": integrity " + F(x0) + " -> " + F(x) + " (x" + F(m) + "), sinking limit " + F(x0 * 5f) + " -> " + F(x * 5f);
                    if (_logShips) Say(line); else Trace(line);
                }
            }
            catch (Exception e)
            {
                if (!_warnedHull) { _warnedHull = true; Warn("ApplyHull failed: " + e.Message); }
                Trace("ApplyHull: " + e);
            }
        }

        /// <summary>A gun, launcher, CIWS or hardpoint among the ship's systems (chaff is not a weapon). True when the list cannot be read.</summary>
        static bool HasWeapons(object obp)
        {
            try
            {
                if (_fObpSystems == null) _fObpSystems = obp.GetType().GetField("_systems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var list = _fObpSystems == null ? null : _fObpSystems.GetValue(obp) as IEnumerable;
                if (list == null) return true;
                foreach (var sys in list)
                    for (var t = sys == null ? null : sys.GetType(); t != null; t = t.BaseType)
                    {
                        string n = t.Name;
                        if (n == "WeaponSystemGun" || n == "WeaponSystemLauncher" || n == "WeaponSystemCIWS" || n == "WeaponSystemHardpoint") return true;
                    }
                return false;
            }
            catch { return true; }
        }

        static bool HasRole(IEnumerable roles, string name)
        {
            if (roles == null) return false;
            foreach (var r in roles) if (r != null && r.ToString() == name) return true;
            return false;
        }

        static readonly Dictionary<Type, FieldInfo> TeamsOverrideFields = new Dictionary<Type, FieldInfo>();

        /// <summary>NumberOfDCTeams from the ship's ini (VesselParameters / SubmarineParameters._numberOfDCTeamsOverride), -1 = not set.</summary>
        static int TeamsOverrideOf(object obp)
        {
            var t = obp.GetType();
            FieldInfo f;
            lock (TeamsOverrideFields)
            {
                if (!TeamsOverrideFields.TryGetValue(t, out f))
                {
                    f = t.GetField("_numberOfDCTeamsOverride", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    TeamsOverrideFields[t] = f;
                }
            }
            if (f == null) return -1;
            try { return Convert.ToInt32(f.GetValue(obp)); } catch { return -1; }
        }

        static float OverrideOf(object obp)
        {
            var t = obp.GetType();
            FieldInfo f;
            lock (OverrideFields)
            {
                if (!OverrideFields.TryGetValue(t, out f))
                {
                    f = t.GetField("_integrityOverride", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    OverrideFields[t] = f;
                }
            }
            if (f == null) return -1f;
            try { return Convert.ToSingle(f.GetValue(obp), CultureInfo.InvariantCulture); } catch { return -1f; }
        }

        /// <summary>The hull multiplier for one unit (1 = as in the game).</summary>
        static float Multiplier(string iniName, bool isSub, bool isCiv, out string group)
        {
            float v;
            if (!string.IsNullOrEmpty(iniName) && _perShip.TryGetValue(iniName, out v)) { group = "HullIntegrityPerShip"; return v; }
            if (isSub) { group = "Submarines"; return _mSub; }
            if (isCiv) { group = "CivilianShips"; return _mCiv; }
            group = "Warships";
            return _mWar;
        }

        static float MultOf(object comp)
        {
            HullState s;
            return comp != null && Hulls.TryGetValue(comp, out s) ? s.Mult : 1f;
        }

        // ------------------------------------------------------------ gun shell damage tiers (Blastzone.CheckForInstakill)

        /// <summary>
        /// Every read of Compartments._maxLimitIntegrity in CheckForInstakill chooses the gun shell damage tier:
        /// "ldfld _maxLimitIntegrity" becomes "dup; ldfld _maxLimitIntegrity; call ShellTierLimit(object, float)".
        /// </summary>
        static IEnumerable<CodeInstruction> InstakillTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var m = typeof(AdmPlugin).GetMethod("ShellTierLimit", BindingFlags.Static | BindingFlags.Public);
            int n = 0;
            foreach (var ci in instructions)
            {
                if (ci.LoadsField(_fCompMaxLimit))
                {
                    var dup = new CodeInstruction(OpCodes.Dup);
                    dup.MoveLabelsFrom(ci);
                    dup.MoveBlocksFrom(ci);
                    yield return dup;
                    yield return ci;
                    yield return new CodeInstruction(OpCodes.Call, m);
                    n++;
                }
                else yield return ci;
            }
            if (n != 30) Warn("Blastzone.CheckForInstakill: " + n + " tier reads patched, 30 expected (game updated?).");
            Trace("CheckForInstakill: " + n + " reads patched");
        }

        public static float ShellTierLimit(object comp, float maxLimit)
        {
            if (!Model) return maxLimit;
            return maxLimit / (MultOf(comp) * _shellVuln);
        }

        // ------------------------------------------------------------ fire ignition by hits

        /// <summary>OnHitUnit: "GetWeaponDamage(hit) / compartments._maxIntegrity" (fire in the hit compartment).</summary>
        static void SmokeInitPostfix(object __instance) { FireSmoke.AfterInit(__instance); }
        static void SmokeFireStatePostfix(object __instance) { FireSmoke.AfterUpdateFireState(__instance); }
        static void SmokeCreatePostfix(object fireEffect) { if (_debug) FireSmoke.DumpEffect(fireEffect); }
        static IEnumerable<CodeInstruction> SmokeCreateTranspiler(IEnumerable<CodeInstruction> ins) { return FireSmoke.CreateFireEffectTranspiler(ins); }

        // ------------------------------------------------------------ unattended fires ([FireAndFlooding] UnattendedFireGrowth)

        sealed class FireWatch { public double[] Since = { -1, -1, -1, -1, -1 }, Fought = { -1, -1, -1, -1, -1 }; }
        static readonly ConditionalWeakTable<object, FireWatch> FireWatches = new ConditionalWeakTable<object, FireWatch>();

        static double GameNow() { try { return _pGameTime == null ? 0 : Convert.ToDouble(_pGameTime.GetValue(null, null)); } catch { return 0; } }

        /// <summary>Postfix of Compartments.RepairFire(index): true = teams fought the fire there this tick.</summary>
        static void FireFoughtPostfix(object __instance, int index, bool __result)
        {
            if (!__result || index < 0 || index > 4) return;
            FireWatches.GetOrCreateValue(__instance).Fought[index] = GameNow();
        }

        /// <summary>
        /// Postfix of Compartments.CheckFire (every FireCheckTime): a fire nobody has fought for UnattendedFireTime gets its growth
        /// rate raised by UnattendedFireGrowthRate % of its maximum - as a hit would.
        /// </summary>
        static void CheckFirePostfix(object __instance)
        {
            try
            {
                if (!Model || __instance == null || _fScMaxSev == null || _fCompSys == null || _pScSeverity == null || _fScGrow == null || _fScMaxGrow == null) return;
                var sys = _fCompSys.GetValue(__instance) as Array;
                if (sys == null) return;
                var w = FireWatches.GetOrCreateValue(__instance);
                double now = GameNow();
                for (int i = 0; i < sys.Length && i < 5; i++)
                {
                    var sc = sys.GetValue(i);
                    if (sc == null) continue;
                    bool burning = (float)_pScSeverity.GetValue(sc, null) > 0f || (float)_fScGrow.GetValue(sc) > 0f;
                    if (!burning) { w.Since[i] = -1; continue; }
                    if (w.Since[i] < 0) w.Since[i] = now;
                    if (!_unattended || _unattendedRate <= 0f) continue;
                    if (now - Math.Max(w.Since[i], w.Fought[i]) < _unattendedTime) continue;
                    float max = (float)_fScMaxGrow.GetValue(sc), cap = _unattendedMax / 100f * (float)_fScMaxSev.GetValue(sc);
                    float g = (float)_fScGrow.GetValue(sc);
                    if (g >= cap) continue;   // UnattendedFireMaxGrowth: a share of the fire's maximum a second
                    _fScGrow.SetValue(sc, Math.Min(cap, g + _unattendedRate / 100f * max));
                }
            }
            catch (Exception e) { Trace("CheckFirePostfix: " + e.Message); }
        }

        /// <summary>For the fire tooltip: seconds the fire in section i has been unattended past the limit (growing faster), or -1.</summary>
        public static double UnattendedFor(object comp, int i)
        {
            FireWatch w;
            if (!Model || !_unattended || comp == null || i < 0 || i > 4 || !FireWatches.TryGetValue(comp, out w) || w.Since[i] < 0) return -1;
            double t = GameNow() - Math.Max(w.Since[i], w.Fought[i]);
            return t >= _unattendedTime ? t : -1;
        }

        /// <summary>
        /// An incendiary hit (_ap._addFire 1..3) starts a fire of a fixed share of the compartment's maximum severity (0.125 / 0.25 /
        /// 0.5). The maximum grows with the hull multiplier, so the same share would be a fire m times stronger than in the game:
        /// the share / m, x HullFireVulnerability - as for every other hit.
        /// </summary>
        public static float IncendiaryFrac(float v)
        {
            if (!Model) return v;
            float m = HullMultOf(_lastFireComp);
            return m > 0f ? v * _fireVuln / m : v;
        }

        static IEnumerable<CodeInstruction> OnHitUnitTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            // incendiary shares: "if (_ap._addFire == k) num7 = 0.5f / 0.25f / 0.125f"
            var mi = typeof(AdmPlugin).GetMethod("IncendiaryFrac", BindingFlags.Static | BindingFlags.Public);
            int inc = 0;
            for (int i = 0; i < list.Count - 1; i++)
            {
                if (list[i].opcode != OpCodes.Ldc_R4 || !(list[i].operand is float)) continue;
                float v = (float)list[i].operand;
                if (Math.Abs(v - 0.5f) > 1e-6f && Math.Abs(v - 0.25f) > 1e-6f && Math.Abs(v - 0.125f) > 1e-6f) continue;
                var op = list[i + 1].opcode;
                if (op != OpCodes.Stloc && op != OpCodes.Stloc_S && op != OpCodes.Stloc_0 && op != OpCodes.Stloc_1 && op != OpCodes.Stloc_2 && op != OpCodes.Stloc_3) continue;
                bool afterAddFire = false;
                for (int j = Math.Max(0, i - 6); j < i; j++) if (list[j].operand is FieldInfo && ((FieldInfo)list[j].operand).Name == "_addFire") afterAddFire = true;
                if (!afterAddFire) continue;
                list.Insert(i + 1, new CodeInstruction(OpCodes.Call, mi));
                i++; inc++;
            }
            if (inc != 3) Warn("Blastzone.OnHitUnit: " + inc + " incendiary fires patched, 3 expected (game updated?).");
            Trace("OnHitUnit: " + inc + " incendiary fires patched");
            var m = typeof(AdmPlugin).GetMethod("FireIntegrity", BindingFlags.Static | BindingFlags.Public);
            int n = 0;
            for (int i = 0; i < list.Count - 1; i++)
            {
                if (!list[i].LoadsField(_fCompMaxInt) || list[i + 1].opcode != OpCodes.Div) continue;
                var dup = new CodeInstruction(OpCodes.Dup);
                dup.MoveLabelsFrom(list[i]);
                dup.MoveBlocksFrom(list[i]);
                list.Insert(i, dup);
                list.Insert(i + 2, new CodeInstruction(OpCodes.Call, m));
                i += 2;
                n++;
            }
            if (n != 1) Warn("Blastzone.OnHitUnit: " + n + " fire reads patched, 1 expected (game updated?).");
            Trace("OnHitUnit: " + n + " reads patched");
            return list;
        }

        /// <summary>
        /// ProcessCompartmentHit:
        ///  - fire: "num /= floodingCompartment._maxIntegrity" followed by "num / 2", "num / 20" -> FireIntegrityFc;
        ///  - flooding: "damage / floodingCompartment._maxIntegrity" (the other such read) -> FloodIntegrityFc;
        ///  - every "floodingCompartment.CurrentIntegrityPercent" (thresholds for leaking above the waterline 50% / 25%,
        ///    and the chance to knock out a damage control team) -> FloodPercent: the percentage the same damage would leave
        ///    of the game's own integrity.
        /// </summary>
        static IEnumerable<CodeInstruction> CompartmentHitTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var mFire = typeof(AdmPlugin).GetMethod("FireIntegrityFc", BindingFlags.Static | BindingFlags.Public);
            var mFlood = typeof(AdmPlugin).GetMethod("FloodIntegrityFc", BindingFlags.Static | BindingFlags.Public);
            var mPct = typeof(AdmPlugin).GetMethod("FloodPercent", BindingFlags.Static | BindingFlags.Public);
            int nFire = 0, nFlood = 0, nPct = 0;
            for (int i = 0; i < list.Count - 1; i++)
            {
                var mi = list[i].operand as MethodInfo;
                if ((list[i].opcode == OpCodes.Callvirt || list[i].opcode == OpCodes.Call) && mi != null &&
                    mi.Name == "get_CurrentIntegrityPercent" && mi.DeclaringType != null && mi.DeclaringType.Name == "FloodingCompartment")
                {
                    list[i].opcode = OpCodes.Call; // labels and blocks stay on the instruction
                    list[i].operand = mPct;
                    nPct++;
                    continue;
                }
                var fi = list[i].operand as FieldInfo;
                if (list[i].opcode != OpCodes.Ldfld || fi == null || fi.Name != "_maxIntegrity" || fi.DeclaringType == null ||
                    fi.DeclaringType.Name != "FloodingCompartment" || list[i + 1].opcode != OpCodes.Div) continue;
                bool fire = false;
                for (int k = i + 2; k < list.Count && k <= i + 6; k++)
                    if (list[k].opcode == OpCodes.Ldc_R4 && list[k].operand is float && (float)list[k].operand == 2f) { fire = true; break; }
                var dup = new CodeInstruction(OpCodes.Dup);
                dup.MoveLabelsFrom(list[i]);
                dup.MoveBlocksFrom(list[i]);
                list.Insert(i, dup);
                list.Insert(i + 2, new CodeInstruction(OpCodes.Call, fire ? mFire : mFlood));
                i += 2;
                if (fire) nFire++; else nFlood++;
            }
            if (nFire != 1 || nFlood != 1 || nPct != 4)
                Warn("Blastzone.ProcessCompartmentHit: patched fire " + nFire + ", flooding " + nFlood + ", percent " + nPct + "; expected 1, 1, 4 (game updated?).");
            Trace("ProcessCompartmentHit: fire " + nFire + ", flooding " + nFlood + ", percent " + nPct);
            return list;
        }

        /// <summary>Flooding rate of a hole: damage / (integrity / (multiplier * HullFloodingVulnerability)).</summary>
        public static float FloodIntegrityFc(object fc, float x)
        {
            if (!Model) return x;
            object comp = null;
            try { comp = fc == null ? null : _fFcComp.GetValue(fc); } catch { }
            return x / (MultOf(comp) * _floodVuln); // the game's own integrity: a hole lets in as much water as in the game
        }

        /// <summary>
        /// floodingCompartment.CurrentIntegrityPercent as the game would see it: the damage the compartment has taken, as a
        /// percentage of integrity / (multiplier * HullFloodingVulnerability) - with 1.0 the game's own integrity.
        /// </summary>
        public static float FloodPercent(object fc)
        {
            float cur = (float)_fFcCur.GetValue(fc), max = (float)_fFcMaxInt.GetValue(fc);
            if (max <= 0f) return 100f;
            if (!Model) return 100f * cur / max; // the game's own getter
            object comp = null;
            try { comp = _fFcComp.GetValue(fc); } catch { }
            float reference = max / (MultOf(comp) * _floodVuln);
            float pct = 100f - 100f * (max - cur) / reference;
            return pct < 0f ? 0f : (pct > 100f ? 100f : pct);
        }

        [ThreadStatic] static object _lastFireComp;   // the unit of the hit being processed (Blastzone.OnHitUnit): for IncendiaryFrac

        public static float FireIntegrity(object comp, float x)
        {
            _lastFireComp = comp;
            if (!Model) return x;
            return x / _fireVuln; // the ship's own (multiplied) integrity: a stronger hull burns proportionally weaker
        }

        public static float FireIntegrityFc(object fc, float x)
        {
            if (!Model) return x;
            object comp = null;
            try { comp = fc == null ? null : _fFcComp.GetValue(fc); } catch { }
            return x / _fireVuln; // the ship's own (multiplied) integrity: a stronger hull burns proportionally weaker
        }

        // ------------------------------------------------------------ Damage Control tab (DamageControlUi; Noesis types only there)

        // separate methods: DamageControlUi (and Noesis) is loaded only when they run
        [MethodImpl(MethodImplOptions.NoInlining)]
        void PatchUi(Harmony harmony, Assembly asm, Type tComp, ref int ok, ref int failed)
        {
            DamageControlUi.Trace = Trace;
            DamageControlUi.Warn = Warn;
            DamageControlUi.Verbose = _debug;
            DamageControlUi.DumpFile = System.IO.Path.Combine(Paths.BepInExRootPath, "AlternativeDamageModel.dctab.txt");
            if (!DamageControlUi.Init(asm)) return;
            ApplyUiSettings();
            var tView = asm.GetType("SeapowerUI.DamageControlView");
            var ctor = tView == null ? null : tView.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (ctor == null) { Warn("SeapowerUI.DamageControlView not found: the detailed Damage Control view is off."); failed++; return; }
            try { harmony.Patch(ctor, postfix: Hm("DcViewCreated")); ok++; }
            catch (Exception e) { Warn("Patching DamageControlView failed: " + e.Message); failed++; return; }
            // the Unit Status window: its Damage Control tab button gets (i) and the gear before the tab is opened
            var tStatus = asm.GetType("SeapowerUI.UnitStatusView");
            var sctor = tStatus == null ? null : tStatus.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (sctor != null)
            {
                try { harmony.Patch(sctor, postfix: Hm("StatusViewCreated")); ok++; }
                catch (Exception e) { Warn("Patching UnitStatusView failed: " + e.Message); failed++; }
            }
            else Trace("SeapowerUI.UnitStatusView not found: (i) and the gear appear once the Damage Control tab is opened");
            // the tab view models: the Damage Control one is known as soon as the game makes it
            Type tTab = null;
            foreach (var t in asm.GetTypes()) if (t.Name == "UnitStatusTabViewModel") { tTab = t; break; }
            if (tTab != null)
                foreach (var c in tTab.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try { harmony.Patch(c, postfix: Hm("TabCreated")); ok++; }
                    catch (Exception e) { Warn("Patching UnitStatusTabViewModel failed: " + e.Message); failed++; }
                }
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            // the game sets DamageIntensity of the Flooding rows every frame; every set notifies, and the list recreates that row's
            // item each time. Skipping unchanged values keeps the items (and what we draw into them) - and saves the UI work.
            var tSum = asm.GetType("SeapowerUI.ViewModels.DamageSummary");
            var setter = tSum == null ? null : tSum.GetProperty("DamageIntensity", all);
            int before = ok;
            if (setter != null && tSum.GetField("_damageIntensity", all) != null)
                Patch(harmony, setter.GetSetMethod(true), "DcIntensityPrefix", null, null, "DamageSummary.DamageIntensity", ref ok, ref failed);
            DamageControlUi.StableRows = ok > before;
            if (!DamageControlUi.StableRows) Warn("DamageSummary.DamageIntensity not patched: the Flooding / Fire rows use plain + / - marks.");
            _uiReady = true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void ApplyUiSettings()
        {
            if (_uiFloodOne == null || !_uiReadyOrInit()) return;
            DamageControlUi.FloodOne = _uiFloodOne.Value.V;
            DamageControlUi.FloodTwo = _uiFloodTwo.Value.V;
            DamageControlUi.FireOne = _uiFireOne.Value.V;
            DamageControlUi.FireTwoUp = _uiFireTwoUp.Value.V;
            DamageControlUi.FireTwoDown = _uiFireTwoDown.Value.V;
            DamageControlUi.Verbose = _debug;
        }

        bool _uiReadyOrInit() { return _uiReady || _patchTried; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void UiTick()
        {
            DamageControlUi.Tick(UnityEngine.Time.unscaledTime, Enabled && _uiOn, Enabled);
        }

        static bool _weaponColor, _enemyDmg;
        static int ParseShipView(string v)
        {
            v = (v ?? "").Trim();
            if (v.StartsWith("T", StringComparison.OrdinalIgnoreCase)) return 1;
            if (v.StartsWith("H", StringComparison.OrdinalIgnoreCase)) return 2;
            return 0;
        }
        static bool UiActive { get { return _uiReady && _uiOn && Enabled; } }

        static void TabCreated(object __instance)
        {
            try { DamageControlUi.RegisterTab(__instance); } catch (Exception e) { Trace("TabCreated: " + e.Message); }
        }

        static void StatusViewCreated(object __instance)
        {
            try { DamageControlUi.RegisterStatusView(__instance); } catch (Exception e) { Trace("StatusViewCreated: " + e.Message); }
        }

        static void DcViewCreated(object __instance)
        {
            try { DamageControlUi.RegisterView(__instance); } catch (Exception e) { Trace("DcViewCreated: " + e.Message); }
            if (_dumpXaml) RequestXamlExport();
        }

        static float _xamlExportAt = -1f;
        static void RequestXamlExport() { _xamlExportAt = 0f; } // done in Update (main thread), once the UI has loaded

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ExportXamlNow()
        {
            if (_xamlDir == null) _xamlDir = System.IO.Path.Combine(Paths.BepInExRootPath, "AlternativeDamageModel_xaml");
            int n = DamageControlUi.ExportXaml(_xamlDir);
            if (n > 0) Say("XAML: " + n + " file(s) written to " + _xamlDir);
        }

        // ------------------------------------------------------------ thresholds of faster flooding (Compartments.OnFixedUpdate)

        // 40: ship sinks above this flooding %; for ships with exact deck points also only while the decks are under water enough
        // (SubmergedFlooding.SinkAllowed): below that the threshold is out of reach
        public static float SinkLevel(float game, object comp)
        {
            if (!Model) return game;
            return SubmergedFlooding.SinkAllowed(comp) ? Limits.LevelFull : float.MaxValue;
        }
        // 8: trim (with a flooded section) sinks the ship in the game - off while the mod is on (TrimFlooding floods the end going down
        // instead); life rafts (1 compartment) keep it: the same check sets them level again
        sealed class CountBox { public int N; }
        static readonly ConditionalWeakTable<object, CountBox> CompCounts = new ConditionalWeakTable<object, CountBox>();   // per physics tick: no reflection

        public static float SinkTrim(float game, object comp)
        {
            if (!Model || !_trimFloodHooked) return game;
            CountBox b;
            if (!CompCounts.TryGetValue(comp, out b))
            {
                b = new CountBox();
                try { b.N = _fCompCount == null ? 5 : (int)_fCompCount.GetValue(comp); } catch { b.N = 5; }
                CompCounts.Add(comp, b);
            }
            return b.N < 2 ? game : float.MaxValue;
        }
        public static float HeavyList(float game) { return Model ? float.MaxValue : game; }        // 45: the game's water over the deck edge is off; TrimFlooding.FloodList does it
        public static float HeavyListNeg(float game) { return Model ? float.MinValue : game; }    // -45

        static IEnumerable<CodeInstruction> SinkThresholdsTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var map = new Dictionary<float, string> { { 40f, "SinkLevel" }, { 8f, "SinkTrim" }, { 45f, "HeavyList" }, { -45f, "HeavyListNeg" } };
            var done = new Dictionary<float, int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Ldc_R4 || !(list[i].operand is float)) continue;
                float v = (float)list[i].operand;
                string m;
                if (!map.TryGetValue(v, out m)) continue;
                if (m == "SinkTrim" || m == "SinkLevel")
                {
                    // per ship: SinkTrim(8, this) / SinkLevel(40, this)
                    list.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
                    list.Insert(i + 2, new CodeInstruction(OpCodes.Call, typeof(AdmPlugin).GetMethod(m, BindingFlags.Static | BindingFlags.Public)));
                    i += 2;
                }
                else
                {
                    list.Insert(i + 1, new CodeInstruction(OpCodes.Call, typeof(AdmPlugin).GetMethod(m, BindingFlags.Static | BindingFlags.Public)));
                    i++;
                }
                int c; done.TryGetValue(v, out c); done[v] = c + 1;
            }
            foreach (var v in map.Keys)
            {
                int c; done.TryGetValue(v, out c);
                if (c != 1) Warn("Compartments.OnFixedUpdate: threshold " + v + " patched " + c + " times, 1 expected (game updated?).");
            }
            Trace("Sink thresholds patched");
            return list;
        }

        // ------------------------------------------------------------ speeds of fire and water

        public static float FlameUpMul(float v, object sc) { return Model ? v * _flameUp * SpeedEffect.Fire(sc) : v; }
        public static float ExtinguishMul(float v) { return Model ? v * _extinguish : v; }
        public static float FloodingMul(float v, object fc) { return Model ? v * _floodSpeed * SpeedEffect.Flood(fc) : v; }
        /// <summary>Multiplier of the inflow through holes for the tooltips (the flooding speed and the ship's speed).</summary>
        public static float FloodingMul(float v) { return Model ? v * _floodSpeed : v; }
        public static float PumpOutMul(float v) { return Model ? v * _pumpOut : v; }
        public static float FireHullMul(float v) { return Model ? v * _fireHull : v; }

        // SystemCompartment.OnFixedUpdate: hull damage of a fire = Globals._shipFireCoefficient * FireSeverity * dt * _shipFireSpeedMultiplier
        static IEnumerable<CodeInstruction> FireHullTranspiler(IEnumerable<CodeInstruction> ins)
        {
            return MulAfter(ins, (l, i) => Field(l[i], "Globals", "_shipFireCoefficient"), "FireHullMul", 1, "FireHullDamageSpeed");
        }

        /// <summary>Inserts "call mul" after every instruction that matches; warns when the count is not the expected one.</summary>
        static IEnumerable<CodeInstruction> MulAfter(IEnumerable<CodeInstruction> instructions, Func<List<CodeInstruction>, int, bool> match,
                                                     string mul, int expected, string label, bool withThis = false)
        {
            var list = new List<CodeInstruction>(instructions);
            var m = typeof(AdmPlugin).GetMethod(mul, BindingFlags.Static | BindingFlags.Public, null, withThis ? new[] { typeof(float), typeof(object) } : new[] { typeof(float) }, null);
            int n = 0;
            for (int i = 0; i < list.Count; i++)
                if (match(list, i)) { list.Insert(i + 1, new CodeInstruction(OpCodes.Call, m)); if (withThis) list.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0)); i += withThis ? 2 : 1; n++; }
            if (n != expected) Warn(label + ": " + n + " places patched, " + expected + " expected (game updated?).");
            Trace(label + ": " + n + " places patched");
            return list;
        }

        static bool Field(CodeInstruction ci, string declaring, string name)
        {
            var fi = ci.operand as FieldInfo;
            return (ci.opcode == OpCodes.Ldfld || ci.opcode == OpCodes.Ldsfld) && fi != null && fi.Name == name && fi.DeclaringType != null && fi.DeclaringType.Name == declaring;
        }

        static bool FixedDt(CodeInstruction ci)
        {
            var mi = ci.operand as MethodInfo; var fi = ci.operand as FieldInfo;
            return (mi != null && mi.Name == "get_fixedDeltaTime") || (fi != null && fi.Name == "fixedDeltaTime");
        }

        // SystemCompartment.OnFixedUpdate: FireSeverity += _fireGrowRate * fixedDeltaTime
        static IEnumerable<CodeInstruction> FlameUpTranspiler(IEnumerable<CodeInstruction> ins)
        {
            return MulAfter(ins, (l, i) => Field(l[i], "SystemCompartment", "_fireGrowRate") && i + 1 < l.Count && FixedDt(l[i + 1]), "FlameUpMul", 1, "FlameUpSpeed", true);
        }

        // FloodingCompartment.OnFixedUpdate: _currentFlooding += _shipFloodingCoefficient * _floodingRate * ... (normal and sinking)
        static IEnumerable<CodeInstruction> FloodingSpeedTranspiler(IEnumerable<CodeInstruction> ins)
        {
            // only "_shipFloodingCoefficient * _floodingRate * 0.5f * dt" (water through holes); the sinking branch
            // ("... * dt * _floodingMultiplicatorWhenSinking") stays the game's - FloodingSpeed 0 must not keep a sinking ship afloat
            return MulAfter(ins, (l, i) => Field(l[i], "FloodingCompartment", "_floodingRate") && i >= 2 && Field(l[i - 2], "Globals", "_shipFloodingCoefficient") &&
                                           i + 2 < l.Count && l[i + 1].opcode == OpCodes.Mul &&   // IL: ldfld _floodingRate, mul, ldc.r4 0.5
                                           l[i + 2].opcode == OpCodes.Ldc_R4 && l[i + 2].operand is float && Math.Abs((float)l[i + 2].operand - 0.5f) < 1e-6f,
                            "FloodingMul", 1, "FloodingSpeed", true);
        }

        public static bool SpeedLimitOff(bool flag, object comp) { return flag || (Model && !DcControl.LimitSpeed(comp)); }

        // Compartments.UpdateWantedVelocityInKnots: "if (WantedVelocityInKnots > 10f && !flag2)" - the cut of the speed by flooding (10-25 kn);
        // the "Limit Speed" button off = treated like the submarine's emergency surfacing: no cut
        static IEnumerable<CodeInstruction> SpeedLimitTranspiler(IEnumerable<CodeInstruction> ins)
        {
            var list = new List<CodeInstruction>(ins);
            var m = typeof(AdmPlugin).GetMethod("SpeedLimitOff", BindingFlags.Static | BindingFlags.Public);
            int n = 0;
            for (int i = 0; i + 3 < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_R4 && list[i].operand is float && Math.Abs((float)list[i].operand - 10f) < 1e-6f &&
                    (list[i + 1].opcode == OpCodes.Ble || list[i + 1].opcode == OpCodes.Ble_S || list[i + 1].opcode == OpCodes.Ble_Un || list[i + 1].opcode == OpCodes.Ble_Un_S) &&
                    list[i + 2].opcode.Name.StartsWith("ldloc", StringComparison.Ordinal) &&
                    (list[i + 3].opcode == OpCodes.Brtrue || list[i + 3].opcode == OpCodes.Brtrue_S))
                {
                    list.Insert(i + 3, new CodeInstruction(OpCodes.Call, m));
                    list.Insert(i + 3, new CodeInstruction(OpCodes.Ldarg_0));
                    n++; i += 4;
                }
            }
            if (n != 1) Warn("SpeedLimit: " + n + " places patched, 1 expected (game updated?).");
            Trace("SpeedLimit: " + n + " places patched");
            return list;
        }

        // Compartments.RepairFire: teams * _crew._fireSeveretyRepair * _fireMult * dt
        static IEnumerable<CodeInstruction> ExtinguishTranspiler(IEnumerable<CodeInstruction> ins)
        {
            return MulAfter(ins, (l, i) => Field(l[i], "Crew", "_fireSeveretyRepair"), "ExtinguishMul", 1, "ExtinguishSpeed");
        }

        // Compartments.RepairFlooding: teams * _crew._floodingRepair * dt
        static IEnumerable<CodeInstruction> PumpOutTranspiler(IEnumerable<CodeInstruction> ins)
        {
            return MulAfter(ins, (l, i) => Field(l[i], "Crew", "_floodingRepair"), "PumpOutMul", 1, "PumpOutSpeed");
        }

        static bool DcCheckPrefix(object __instance, int i) { return DcControl.CheckPrefix(__instance, i); }

        static bool TeamSendPrefix(object __instance, int index) { return DcTeams.SendPrefix(__instance, index); }
        static bool TeamReturnPrefix(object __instance, int index) { return DcTeams.ReturnPrefix(__instance, index); }
        static bool TeamRecallPrefix(object __instance) { return DcTeams.RecallPrefix(__instance); }
        /// <summary>A fire does not spread into a compartment where a damage control team is.</summary>
        static bool IgniteByNeighbourPrefix(object __instance)
        {
            try
            {
                if (!_teamBlocksSpread || !Model || _fScComp == null || _fScNum == null || _pCompDcTeams == null) return true;
                var comp = _fScComp.GetValue(__instance);
                int i = (int)_fScNum.GetValue(__instance) - 1;   // compartment numbers are 1-based, the teams' counts 0-based
                var counts = comp == null ? null : _pCompDcTeams.GetValue(comp, null) as int[];
                return !(counts != null && i >= 0 && i < 5 && i < counts.Length && counts[i] > 0);
            }
            catch { return true; }
        }

        static void WreckDragPostfix(object __instance) { WreckDrag.Tick(__instance); }
        static void SinkComPrefix(object __instance, out object __state) { __state = CapsizeFix.BeforeSink(__instance); }
        static void SinkComPostfix(object __instance, object __state) { CapsizeFix.AfterSink(__instance, __state); }
        static void TeamTickPrefix(object __instance) { DcTeams.Tick(__instance); TrimFlooding.Tick(__instance); SubmergedFlooding.Tick(__instance); SectionEffects.Tick(__instance); BubblesOff.Tick(__instance); if (Enabled) FireSmoke.Tick(__instance); }

        public static float TeamEffMul(float v) { return DcTeams.Eff(v); }

        // every crew work rate read in the Repair* methods (Crew._floodingRateRepair, _fireSeveretyRepair, _floodingRepair,
        // _structureRepair, _systemRepair) is multiplied by DCTeamEffectiveness
        static IEnumerable<CodeInstruction> TeamEffTranspiler(IEnumerable<CodeInstruction> ins, MethodBase original)
        {
            var names = new HashSet<string> { "_floodingRateRepair", "_fireSeveretyRepair", "_floodingRepair", "_structureRepair", "_systemRepair" };
            int expected = original != null && original.Name == "RepairSystems" ? 2 : 1;
            return MulAfter(ins, (l, i) =>
            {
                var fi = l[i].operand as FieldInfo;
                return l[i].opcode == OpCodes.Ldfld && fi != null && fi.DeclaringType != null && fi.DeclaringType.Name == "Crew" && names.Contains(fi.Name);
            }, "TeamEffMul", expected, "DCTeamEffectiveness " + (original != null ? original.Name : "?"));
        }

        static MethodInfo _mIniWriteStr, _mIniReadStr;
        const string DcSaveKey = "AlternativeDamageModel_DC";
        const string DcTeamsSaveKey = "AlternativeDamageModel_Teams", HitsSaveKey = "AlternativeDamageModel_Hits", TrendSaveKey = "AlternativeDamageModel_Trend";

        const string HullSaveKey = "AlternativeDamageModel_HullMult";
        const string SysSaveKey = "AlternativeDamageModel_IntegrityMult";

        static float HullMultOf(object comp) { HullState st; return comp != null && Hulls.TryGetValue(comp, out st) ? st.Mult : 1f; }
        static float SysMultOf(object sys) { SysState st; return sys != null && Systems.TryGetValue(sys, out st) ? st.Mult : 1f; }

        static string ReadKey(object ini, string section, string key)
        {
            return _mIniReadStr.Invoke(ini, new object[] { section, key, "", false }) as string;
        }

        static bool TryReadMult(object ini, string section, string key, out float m)
        {
            m = 1f;
            var s = ReadKey(ini, section, key);
            return !string.IsNullOrEmpty(s) && float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out m) && m > 0f;
        }

        static FieldInfo _fBoCampaignRepair, _fSysBase;
        static bool CampaignRepair(object baseObject)
        {
            if (baseObject == null) return false;
            if (_fBoCampaignRepair == null)
                for (var t = baseObject.GetType(); t != null && _fBoCampaignRepair == null; t = t.BaseType)
                    _fBoCampaignRepair = t.GetField("_campaignRepair", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return _fBoCampaignRepair != null && (bool)_fBoCampaignRepair.GetValue(baseObject);
        }

        /// <summary>
        /// Saved games hold absolute integrities (hull sections, fires, systems). The multiplier they were saved with is kept next to
        /// them, so a game loaded with other settings (another profile, Alternative Damage Control or the mod off) keeps the damage in
        /// percent instead of the absolute numbers. Saves without the key (older builds, no mod) are loaded as they are.
        /// </summary>
        static void HullLoaded(object comp, object ini, string sectionName)
        {
            if (CampaignRepair(_fCompBase == null ? null : _fCompBase.GetValue(comp))) { TakePendingSystems(comp, false); return; }   // the game did not load the state
            if (string.IsNullOrEmpty(ReadKey(ini, sectionName, "FloodingCompartments")))
            {
                // no hull state (e.g. a campaign state of an undamaged hull): its systems as below
                bool hullKey = !string.IsNullOrEmpty(ReadKey(ini, sectionName, HullSaveKey));
                bool oldMod = !hullKey && (!string.IsNullOrEmpty(ReadKey(ini, sectionName, DcTeamsSaveKey)) || !string.IsNullOrEmpty(ReadKey(ini, sectionName, DcSaveKey)));
                TakePendingSystems(comp, !oldMod);
                return;
            }
            float saved;
            if (!TryReadMult(ini, sectionName, HullSaveKey, out saved))
            {
                // no multiplier: a game saved without the mod (x1) - unless an older build of the mod saved it (its teams or DC
                // state are there: the multiplier was the one in use then, taken as now)
                bool oldMod = !string.IsNullOrEmpty(ReadKey(ini, sectionName, DcTeamsSaveKey)) || !string.IsNullOrEmpty(ReadKey(ini, sectionName, DcSaveKey));
                if (oldMod) { TakePendingSystems(comp, false); return; }
                saved = 1f;
                TakePendingSystems(comp, true);
            }
            // saved by this build: its systems without a key were not scaled by the mod then (the mod off, or never on) - x1
            else TakePendingSystems(comp, true);
            float k = HullMultOf(comp) / saved;
            if (Math.Abs(k - 1f) < 0.0001f) return;
            foreach (var arrField in new[] { _fCompPort, _fCompStar })
            {
                var arr = arrField == null ? null : arrField.GetValue(comp) as Array;
                if (arr == null) continue;
                foreach (var fc in arr) { Scale(fc, _fFcCur, k); Scale(fc, _fFcLowest, k); }
            }
            var sys = _fCompSys == null ? null : _fCompSys.GetValue(comp) as Array;
            if (sys != null)
                foreach (var sc in sys)
                {
                    if (sc == null) continue;
                    Scale(sc, _fScGrow, k);
                    if (_pScSeverity != null && _pScSeverity.CanWrite) _pScSeverity.SetValue(sc, (float)_pScSeverity.GetValue(sc, null) * k, null);
                }
            Trace("Loaded hull state rescaled x" + F(k) + " (saved with x" + F(saved) + ")");
        }

        /// <summary>Only for systems the mod scales (ships' systems) and only where the game wrote CurrentIntegrity.</summary>
        static void SysSavePostfix(object __instance, object ini, string sectionName)
        {
            try
            {
                SysState st;
                if (ini == null || _mIniWriteStr == null || __instance == null || !Systems.TryGetValue(__instance, out st)) return;
                if (string.IsNullOrEmpty(ReadKey(ini, sectionName, "CurrentIntegrity"))) return;
                _mIniWriteStr.Invoke(ini, new object[] { sectionName, SysSaveKey, st.Mult.ToString("0.0####", CultureInfo.InvariantCulture) });
            }
            catch (Exception e) { Trace("SysSavePostfix: " + e.Message); }
        }

        /// <summary>Systems loaded without a multiplier, per unit: decided when the unit's hull is loaded (right after them).</summary>
        static readonly ConditionalWeakTable<object, List<object>> PendingSys = new ConditionalWeakTable<object, List<object>>();

        /// <summary>
        /// unscaled: the systems' integrity in the save was the game's (x1) - saved without the mod, or by this build while the mod had
        /// not scaled them (off in the Mods menu); else (an older build of the mod, which wrote no keys) they are taken as they are.
        /// </summary>
        static void TakePendingSystems(object comp, bool vanilla)
        {
            var bo = _fCompBase == null ? null : _fCompBase.GetValue(comp);
            List<object> list;
            if (bo == null || !PendingSys.TryGetValue(bo, out list)) return;
            PendingSys.Remove(bo);
            if (!vanilla) return;
            foreach (var sys in list) RescaleLoadedSystem(sys, SysMultOf(sys));
        }

        static void RescaleLoadedSystem(object sys, float k)
        {
            if (Math.Abs(k - 1f) < 0.0001f) return;
            float max = Convert.ToSingle(_fSysMax.GetValue(sys));
            float cur = Convert.ToSingle(_pSysCurrent.GetValue(sys, null)) * k;
            _pSysCurrent.SetValue(sys, Math.Min(max, cur), null);
        }

        static void SysLoadPostfix(object __instance, object ini, string sectionName, bool isCampaignStateSave)
        {
            try
            {
                if (ini == null || _mIniReadStr == null || !SysFields(__instance) || !_pSysCurrent.CanWrite) return;
                if (_fSysBase == null)
                    for (var t = __instance.GetType(); t != null && _fSysBase == null; t = t.BaseType)
                        _fSysBase = t.GetField("_baseObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (isCampaignStateSave && CampaignRepair(_fSysBase == null ? null : _fSysBase.GetValue(__instance))) return;
                if (string.IsNullOrEmpty(ReadKey(ini, sectionName, "CurrentIntegrity"))) return;
                float saved;
                if (!TryReadMult(ini, sectionName, SysSaveKey, out saved))
                {
                    // no multiplier: decided with the unit's hull (saved without the mod, or by an older build)
                    SysState st;
                    var bo = _fSysBase == null ? null : _fSysBase.GetValue(__instance);
                    if (bo != null && Systems.TryGetValue(__instance, out st)) PendingSys.GetOrCreateValue(bo).Add(__instance);
                    return;
                }
                RescaleLoadedSystem(__instance, SysMultOf(__instance) / saved);
            }
            catch (Exception e) { Trace("SysLoadPostfix: " + e.Message); }
        }

        static void DcSavePostfix(object __instance, object ini, string sectionName, bool isCampaignStateSave)
        {
            try
            {
                if (ini == null) return;
                _mIniWriteStr.Invoke(ini, new object[] { sectionName, HullSaveKey, HullMultOf(__instance).ToString("0.0####", CultureInfo.InvariantCulture) });
                if (!Enabled || isCampaignStateSave) return;
                var text = _uiOn ? DcControl.Save(__instance) : null;
                if (text != null) _mIniWriteStr.Invoke(ini, new object[] { sectionName, DcSaveKey, text });
                var teams = Model ? DcTeams.Save(__instance) : null;
                if (teams != null) _mIniWriteStr.Invoke(ini, new object[] { sectionName, DcTeamsSaveKey, teams });
                var hits = HitLog.Save(__instance);
                if (hits != null) _mIniWriteStr.Invoke(ini, new object[] { sectionName, HitsSaveKey, hits });
                var trend = ContactIntel.SaveTrend(__instance);
                if (trend != null) _mIniWriteStr.Invoke(ini, new object[] { sectionName, TrendSaveKey, trend });
            }
            catch (Exception e) { Trace("DcSavePostfix: " + e.Message); }
        }

        static void DcLoadPostfix(object __instance, object ini, string sectionName)
        {
            try { if (ini != null) HullLoaded(__instance, ini, sectionName); } catch (Exception e) { Trace("HullLoaded: " + e.Message); }
            try
            {
                if (!Enabled || ini == null) return;
                var text = _mIniReadStr.Invoke(ini, new object[] { sectionName, DcSaveKey, "", false }) as string;
                if (_uiOn && !string.IsNullOrEmpty(text)) DcControl.Load(__instance, text);
                var teams = _mIniReadStr.Invoke(ini, new object[] { sectionName, DcTeamsSaveKey, "", false }) as string;
                if (Model && !string.IsNullOrEmpty(teams)) DcTeams.Load(__instance, teams);
                var hits = _mIniReadStr.Invoke(ini, new object[] { sectionName, HitsSaveKey, "", false }) as string;
                if (!string.IsNullOrEmpty(hits)) HitLog.Load(__instance, hits);
                var trend = _mIniReadStr.Invoke(ini, new object[] { sectionName, TrendSaveKey, "", false }) as string;
                if (!string.IsNullOrEmpty(trend)) ContactIntel.LoadTrend(__instance, trend);
            }
            catch (Exception e) { Trace("DcLoadPostfix: " + e.Message); }
        }
        static bool DcAllocPrefix(object __instance) { return DcControl.AllocPrefix(__instance); }

        /// <summary>Skips setting an unchanged DamageIntensity (see PatchUi), and a change between the stages of a Flooding row. Only while the detailed view is on.</summary>
        static bool DcIntensityPrefix(object __instance, int value, int ____damageIntensity)
        {
            if (!UiActive) return true;
            if (value == ____damageIntensity) return false;
            // the Flooding row: a change between the stages (Minor / Moderate / Severe: the water crossing 33% or 66%) is not needed - the row
            // draws its own colours - and each change makes the game make the row's item anew (the arrows and their hints with it, and
            // with the water hovering around a threshold, many times a second). Only the change from / to "none" passes.
            if (____damageIntensity > 0 && value > 0 && DamageControlUi.IsFloodSummary(__instance)) return false;
            return true;
        }


        // ------------------------------------------------------------ diagnostics: LogFires

        sealed class FireLog { public int State; }
        static readonly ConditionalWeakTable<object, FireLog> FireLogs = new ConditionalWeakTable<object, FireLog>();

        static string FireLine(object sc, string what)
        {
            var comp = _fScComp.GetValue(sc);
            string ship = "?";
            try
            {
                var bo = comp == null ? null : _fCompBase.GetValue(comp);
                if (bo != null && _mUidName != null) ship = _mUidName.Invoke(bo, null) as string ?? "?";
            }
            catch { }
            int n = _fScNum == null ? 0 : (int)_fScNum.GetValue(sc);
            float sev = (float)_pScSeverity.GetValue(sc, null), max = (float)_fScMaxSev.GetValue(sc);
            float grow = _fScGrow == null ? 0f : (float)_fScGrow.GetValue(sc);
            string t = "?";
            try { if (_pGameTime != null) t = Convert.ToDouble(_pGameTime.GetValue(null, null)).ToString("0.0", CultureInfo.InvariantCulture); } catch { }
            string dc = "?";
            try
            {
                var arr = comp == null || _pCompDcTeams == null ? null : _pCompDcTeams.GetValue(comp, null) as int[];
                if (arr != null && n >= 1 && n - 1 < arr.Length) dc = arr[n - 1].ToString(CultureInfo.InvariantCulture);
            }
            catch { }
            float pct = max > 0f ? 100f * sev / max : 0f, gpct = max > 0f ? 100f * grow / max : 0f;
            return "Fire t=" + t + " " + ship + " #" + n + " " + what + ": severity " + pct.ToString("0.#", CultureInfo.InvariantCulture) +
                   "% of max, growth " + gpct.ToString("0.###", CultureInfo.InvariantCulture) + "%/s, DC teams " + dc +
                   ", hull x" + F(MultOf(comp)) + (Enabled ? "" : " (mod off)");
        }

        static void FireStatePostfix(object __instance)
        {
            if (!_logFires || __instance == null) return;
            try
            {
                int st = Convert.ToInt32(_pScState.GetValue(__instance, null));
                FireLog fl;
                if (!FireLogs.TryGetValue(__instance, out fl)) { fl = new FireLog(); FireLogs.Add(__instance, fl); }
                if (fl.State == st) return;
                fl.State = st;
                Say(FireLine(__instance, "-> " + _pScState.GetValue(__instance, null)));
            }
            catch (Exception e) { Trace("FireStatePostfix: " + e.Message); }
        }

        static void AddFirePrefix(object __instance, out float __state)
        {
            __state = 0f;
            if (!_logFires || __instance == null) return;
            try { __state = (float)_pScSeverity.GetValue(__instance, null); } catch { }
        }

        static void AddFirePostfix(object __instance, float relativeSeverity, float relativeGrowRate, float __state)
        {
            if (!_logFires || __instance == null) return;
            try
            {
                Say(FireLine(__instance, "hit (+" + (100f * relativeSeverity).ToString("0.#", CultureInfo.InvariantCulture) + "% severity, +" +
                                         (100f * relativeGrowRate).ToString("0.#", CultureInfo.InvariantCulture) + "% of max growth)"));
            }
            catch (Exception e) { Trace("AddFirePostfix: " + e.Message); }
        }

    }

    /// <summary>
    /// A multiplier in the .cfg, always written with a decimal point ("2.0", "0.5", "1.25") so it does not look like a whole number.
    /// Its own TOML converter: the converter for float (shared by all plugins) stays untouched.
    /// </summary>
    public struct Factor
    {
        public readonly float V;
        public Factor(float v) { V = v; }

        public override string ToString() { return V.ToString("0.0####", CultureInfo.InvariantCulture); }

        public static bool TryParse(string s, out Factor f)
        {
            float v;
            bool ok = float.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                      && !float.IsNaN(v) && !float.IsInfinity(v);
            f = new Factor(ok ? v : 1f);
            return ok;
        }

        static bool _registered;
        public static void Register()
        {
            if (_registered || TomlTypeConverter.CanConvert(typeof(Factor))) { _registered = true; return; }
            _registered = true;
            TomlTypeConverter.AddConverter(typeof(Factor), new TypeConverter
            {
                ConvertToString = (o, t) => ((Factor)o).ToString(),
                ConvertToObject = (str, t) =>
                {
                    Factor f;
                    if (!TryParse(str, out f)) throw new FormatException("'" + str + "' is not a number (e.g. 2.0)");
                    return f;
                }
            });
        }
    }
}
