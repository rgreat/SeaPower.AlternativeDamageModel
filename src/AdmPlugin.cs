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
        static bool _patched, _patchTried;

        // settings, cached for the hooks (updated on SettingChanged)
        static float _mWar = 2f, _mCiv = 2f, _mSub = 1f, _shellVuln = 1f, _fireVuln = 1f, _floodVuln = 1f;
        static float _flameUp = 1f, _extinguish = 1f, _floodSpeed = 1f, _pumpOut = 1f, _fireHull = 1f;
        ConfigEntry<Factor> _flameUpEntry, _extinguishEntry, _floodSpeedEntry, _pumpOutEntry, _fireHullEntry;
        ConfigEntry<Factor> _teamEffEntry, _transferSpeedEntry, _musterEntry, _healEntry, _healSpreadEntry, _listLimitEntry, _trimLimitEntry, _trimLengthEntry, _levelLimitEntry;
        ConfigEntry<int> _knockDeathEntry;
        ConfigEntry<string> _profileEntry;
        ConfigEntry<string> _warTeamsEntry, _subTeamsEntry, _civTeamsEntry;
        static bool _logShips, _logFires;
        static bool _trimFloodHooked;
        static FieldInfo _fCompCount;
        static Dictionary<string, float> _perShip = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        ConfigEntry<bool> _debugEntry, _logShipsEntry, _logFiresEntry, _uiEntry;
        static bool _dumpXaml;
        static string _xamlDir;
        ConfigEntry<Factor> _mWarEntry, _mCivEntry, _mSubEntry, _shellVulnEntry, _fireVulnEntry, _floodVulnEntry,
                            _uiFloodOne, _uiFloodTwo, _uiFireOne, _uiFireTwoUp, _uiFireTwoDown;
        static bool _uiOn, _uiReady;
        const string PerShipSection = "HullIntegrityPerShip";
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
        }
        static readonly ConditionalWeakTable<object, HullState> Hulls = new ConditionalWeakTable<object, HullState>();
        static readonly List<WeakReference> LiveHulls = new List<WeakReference>(); // Compartments of ships in the current mission

        // live reload of the .cfg
        static bool _reloading;
        DateTime _cfgStamp; long _cfgSize;
        float _nextCfgCheck, _cfgChangedAt = -1f;

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

        void Awake()
        {
            _inst = this;

            _profileEntry = Config.Bind("General", "Profile", "Advanced",
                "Settings profile, chosen in the game: Unit Status -> Damage Control -> the gear next to (i). Base Game and Advanced come with the mod " +
                "(Profiles folder of the mod, read-only); your own are in BepInEx/config/AlternativeDamageModel/Profiles. " +
                "Values changed (in this file or in the game) under a built-in profile make it Custom (saved as a profile of your own); " +
                "under a profile of your own they are saved into it. [General] and [UI] are not part of a profile.");
            _debugEntry = Config.Bind("General", "Debug", false,
                "Enables verbose logging and BepInEx/AlternativeDamageModel.trace.txt.");
            _logFiresEntry = Config.Bind("General", "LogFires", false,
                "Diagnostics: writes every fire started by a hit and every change of a fire's stage to the BepInEx log " +
                "(ship, compartment, mission time, severity and growth in % of the compartment's maximum, damage control teams there). " +
                "Works also while the mod is off in the Mods menu, to compare with the game.");
            _logShipsEntry = Config.Bind("General", "LogShipIntegrity", false,
                "Writes a line to the BepInEx log for every ship that enters a mission: its hull integrity in the game and with the mod.");

            Factor.Register();
            _mWarEntry = Config.Bind("HullIntegrity", "Warships", new Factor(2f),
                "How many times stronger the hull of surface warships is than in the game. 1.0 = as in the game, 2.0 = twice as strong. " +
                "Single ships get their own value in section [HullIntegrityPerShip], one line per ship: ini file name = multiplier (e.g. usn_bb_iowa = 3.0).");
            _mCivEntry = Config.Bind("HullIntegrity", "CivilianShips", new Factor(2f),
                "How many times stronger the hull of civilian ships (ini files civ_*) is than in the game. 1.0 = as in the game.");
            _mSubEntry = Config.Bind("HullIntegrity", "Submarines", new Factor(1f),
                "How many times stronger the hull of submarines is than in the game. 1.0 = as in the game.");

            _shellVulnEntry = Config.Bind("SideEffects", "HullShellVulnerability", new Factor(1f),
                "Vulnerability of the hull to gun shells (missiles, torpedoes and bombs are not affected). " +
                "Gun shells take a share of a ship's sinking limit that depends on the size of the hull. " +
                "1.0 = each shell takes as much as in the game; the stronger hull just survives more hits. " +
                "2.0 = shells hit as if the hull were half as big; 0.5 = as if it were twice as big (medium shells may stop hurting big ships).");
            _fireVulnEntry = Config.Bind("SideEffects", "HullFireVulnerability", new Factor(1f),
                "Vulnerability of the ship to fires started by hits (all weapon types). " +
                "The stronger hull also holds fires back in the same proportion: with Warships = 5.0 a hit starts a fire 5 times weaker " +
                "(relative to the compartment) than in the game. 1.0 = this normal behaviour of the mod. " +
                "2.0 = fires twice as strong and grow twice as fast; 0.5 = half as strong. 1.0 with every hull multiplier 1.0 = as in the game.");
            _floodVulnEntry = Config.Bind("SideEffects", "HullFloodingVulnerability", new Factor(1f),
                "Vulnerability of the hull to flooding through holes made by hits (all weapon types). " +
                "1.0 = each hole lets in as much water as in the game, and holes above the waterline start leaking after the same damage " +
                "as in the game (the stronger hull does not hold the water back). 2.0 = twice as much water; 0.5 = half.");


            _flameUpEntry = Config.Bind("FireAndFlooding", "FlameUpSpeed", new Factor(1f),
                "How fast fires grow by themselves, between hits. 1.0 = as in the game, 2.0 = twice as fast, 0.5 = half as fast.");
            _fireHullEntry = Config.Bind("FireAndFlooding", "FireHullDamageSpeed", new Factor(1f),
                "How fast a fire eats the hull of its section (integrity of both its compartments). 1.0 = as in the game. " +
                "Damage to the systems in the section is not changed.");
            _extinguishEntry = Config.Bind("FireAndFlooding", "ExtinguishSpeed", new Factor(1f),
                "How fast damage control teams put fires out. 1.0 = as in the game.");
            _floodSpeedEntry = Config.Bind("FireAndFlooding", "FloodingSpeed", new Factor(1f),
                "How fast water comes in through the holes of a compartment. 1.0 = as in the game.");
            _pumpOutEntry = Config.Bind("FireAndFlooding", "PumpOutSpeed", new Factor(1f),
                "How fast damage control teams pump water out. 1.0 = as in the game.");

            _warTeamsEntry = Config.Bind("DamageControlTeams", "WarshipTeams", "0=1, 500=2, 1500=3, 5000=4, 20000=5",
                "Damage control teams of a surface warship by displacement: tons=teams, from that many tons up. " +
                "NumberOfDCTeams in a ship's ini still sets its own number.");
            _subTeamsEntry = Config.Bind("DamageControlTeams", "SubmarineTeams", "0=1, 1000=2, 5000=3, 8000=4",
                "Damage control teams of a submarine by displacement: tons=teams.");
            _civTeamsEntry = Config.Bind("DamageControlTeams", "CivilianTeams", "0=1, 20000=2",
                "Damage control teams of a civilian ship by displacement: tons=teams.");
            _teamEffEntry = Config.Bind("DamageControlTeams", "DCTeamEffectiveness", new Factor(0.5f),
                "How much one team gets done (plugging, fire fighting, pumping, hull and system repairs) compared with the game. 1.0 = as in the game.");
            _transferSpeedEntry = Config.Bind("DamageControlTeams", "TransferSpeed", new Factor(50f),
                "How fast a team moves through the ship, metres per minute. A transfer = MusterTime + the walk between its start and its goal " +
                "(sections, or the reserve in the middle of the ship) at this speed, rounded to 10 s. A team on the move takes no orders. " +
                "0 = no walking time; TransferSpeed and MusterTime both 0 = teams move at once and Auto DC works as in the game. While transfers take time, " +
                "Auto DC keeps the walking down: a team does not leave a section with fire or flooding still to fight (only fire with DC Priority Fire, " +
                "only flooding with Flooding); every job gets the nearest free team, up to 3 where fire or water is bad or rising fast; " +
                "a team leaves hull or system repairs only for fire or flooding, after a minute there; idle teams wait where they are.");
            _musterEntry = Config.Bind("DamageControlTeams", "MusterTime", new Factor(60f),
                "Seconds a team needs to get ready before every transfer. Cancelling a transfer while it is still getting ready costs no time.");
            _healEntry = Config.Bind("DamageControlTeams", "HealTime", new Factor(90f),
                "Seconds a knocked-out team needs to recover; then it is back in the reserve. Its transfer from the section to the reserve " +
                "(MusterTime + the walk) is added.");
            _healSpreadEntry = Config.Bind("DamageControlTeams", "HealTimeSpread", new Factor(0f),
                "Random spread of the recovery time, seconds either way: HealTime 90 with 30 = 60 to 120 s, as in the game " +
                "(there teams recover one after another; here each on its own). 0 = always HealTime.");
            _knockDeathEntry = Config.Bind("DamageControlTeams", "KnockoutsToDeath", 2,
                "A team knocked out this many times is dead and does not come back. 0 = never (as in the game).");

            _listLimitEntry = Config.Bind("Sinking", "ListLimit", new Factor(Limits.ListFull),
                "List, degrees, from which water comes over the deck edge and floods every compartment of the low side fast (game: 45). " +
                "It does not sink the ship by itself; capsizing at 70 degrees does, as in the game. Also the full scale of the LIST gauge.");
            _trimLimitEntry = Config.Bind("Sinking", "TrimLimit", new Factor(Limits.TrimFull),
                "Trim, degrees, for a ship TrimReferenceLength long. Past it the end that goes down floods fast: on each side its outermost compartment, " +
                "when that is full the next one, and so on. The ship does not sink at once, as it does in the game (8 degrees with a flooded section). " +
                "Also the full scale of the TRIM gauge.");
            _trimLengthEntry = Config.Bind("Sinking", "TrimReferenceLength", new Factor(Limits.TrimRefLength),
                "Metres. The trim limit depends on the ship's length: the longer the ship, the sooner its bow or stern goes under at the same angle " +
                "(tan limit = tan TrimLimit x (TrimReferenceLength / length)^0.442; with 5.2 at 100: 30 m 8.8, 60 m 6.5, 150 m 4.4, 300 m 3.2 degrees). 0 = TrimLimit for every ship (as in the game).");
            _levelLimitEntry = Config.Bind("Sinking", "FloodLimit", new Factor(Limits.LevelFull),
                "Water in the whole ship, % (the average of its compartments), from which it sinks, as in the game (game: 40; submarines keep the game's 50). " +
                "Also the full scale of the LEVEL gauge.");

            _uiEntry = Config.Bind("UI", "DetailedDamageControl", true,
                "Unit Status -> Damage Control: hull integrity per side next to each section's name, an icon per damage control team " +
                "(its task; tasks and moves by hand when Auto DC is off), water and fire in % with trend arrows, DC Priority, " +
                "the team cards, the LIST / TRIM / LEVEL gauges with the flooding plan, tooltips. Also the (i) legend and the gear of " +
                "this settings window: when off, they are gone too - turn it on again here in the .cfg.");
            _uiFloodOne = Config.Bind("UI", "FloodingOneArrowAt", new Factor(0.02f),
                "Flooding row and flooding plan: one arrow when the water level changes faster than this, in % of the compartment per second.");
            _uiFloodTwo = Config.Bind("UI", "FloodingTwoArrowsAt", new Factor(0.25f),
                "Flooding row and flooding plan: two arrows when the water level changes faster than this, in % of the compartment per second.");
            _uiFireOne = Config.Bind("UI", "FireOneArrowAt", new Factor(0.05f),
                "Fire row: one arrow when the fire changes faster than this, in % of its maximum per second.");
            _uiFireTwoUp = Config.Bind("UI", "FireTwoArrowsUpAt", new Factor(1f),
                "Fire row: two arrows up when the fire grows faster than this, in % of its maximum per second.");
            _uiFireTwoDown = Config.Bind("UI", "FireTwoArrowsDownAt", new Factor(0.25f),
                "Fire row: two arrows down when the fire is put out faster than this, in % of its maximum per second.");

            BindPerShip();

            _debug = _debugEntry.Value;
            _traceFile = Path.Combine(Paths.BepInExRootPath, "AlternativeDamageModel.trace.txt");
            if (_debug) { try { File.WriteAllText(_traceFile, ""); } catch { } }
            Trace(PluginName + " " + PluginVersion + " build " + BuildId);

            ReadSettings();
            Config.SaveOnConfigSet = false; // saved by SaveConfig (keeps the [HullIntegrityPerShip] comment)
            // a change made in game (e.g. Configuration Manager); changes made in the file are picked up by WatchConfigFile
            Config.SettingChanged += (s, e) => { if (_reloading) return; ReadSettings(); SaveConfig(); RescaleLiveHulls(); SyncProfile(); };
            SaveConfig();
            CollectEntries();
            // Advanced changed (FloodLimit 28 -> 33.6 -> 40, TrimLimit 7 -> 5.2): a .cfg still on Advanced with old values follows it
            // (instead of becoming "Custom")
            if (string.Equals(_profileEntry.Value, AdvancedName, StringComparison.OrdinalIgnoreCase))
            {
                bool changed = false;
                _reloading = true;
                try
                {
                    float lv = _levelLimitEntry.Value.V, tr = _trimLimitEntry.Value.V;
                    if (Math.Abs(lv - 28f) < 0.01f || Math.Abs(lv - 33.6f) < 0.01f) { _levelLimitEntry.Value = new Factor(40f); changed = true; }
                    if (Math.Abs(tr - 7f) < 0.01f) { _trimLimitEntry.Value = new Factor(5.2f); changed = true; }
                }
                finally { _reloading = false; }
                if (changed) { ReadSettings(); SaveConfig(); }
            }
            SyncProfile();

            RefreshEnabled();
            TryPatch();
            Logger.LogInfo(PluginName + " " + PluginVersion + " (build " + BuildId + ") loaded" +
                           (ModState.Controlled ? "; follows its tick in the game's Mods menu." : "."));
        }

        void Update()
        {
            if (!_patchTried) TryPatch();
            WatchConfigFile();
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
            if (_uiReady)
            {
                try { UiTick(); }
                catch (Exception e) { _uiReady = false; Warn("Damage Control view failed and is off: " + e.Message); Trace(e.ToString()); }
            }
        }

        /// <summary>
        /// Checks the .cfg once a second; when it has been changed outside the game (and stays unchanged for half a second,
        /// i.e. the editor has finished writing), its values are read again and applied at once.
        /// </summary>
        void WatchConfigFile()
        {
            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextCfgCheck) return;
            _nextCfgCheck = now + (_cfgChangedAt >= 0f ? 0.25f : 1f);
            try
            {
                var fi = new FileInfo(Config.ConfigFilePath);
                if (!fi.Exists) return;
                if (fi.LastWriteTimeUtc == _cfgStamp && fi.Length == _cfgSize)
                {
                    if (_cfgChangedAt >= 0f && now - _cfgChangedAt >= 0.5f) { _cfgChangedAt = -1f; ReloadConfig(); }
                    return;
                }
                _cfgStamp = fi.LastWriteTimeUtc; _cfgSize = fi.Length;
                _cfgChangedAt = now; // wait until it stops changing
            }
            catch (Exception e) { Trace("WatchConfigFile: " + e.Message); }
        }

        void RememberCfgStamp()
        {
            try { var fi = new FileInfo(Config.ConfigFilePath); _cfgStamp = fi.LastWriteTimeUtc; _cfgSize = fi.Length; } catch { }
        }

        void ReloadConfig()
        {
            _reloading = true;
            try { Config.Reload(); }
            catch (Exception e) { Logger.LogWarning("Reading the changed settings failed (" + e.Message + "); will try again."); _cfgStamp = default(DateTime); return; }
            finally { _reloading = false; }
            ReadSettings();
            Logger.LogInfo("Settings re-read from the .cfg: warships x" + F(_mWar) + ", civilian x" + F(_mCiv) + ", submarines x" + F(_mSub) +
                           ", per-ship " + _perShip.Count + ", shell " + F(_shellVuln) + ", fire " + F(_fireVuln) + ", flooding " + F(_floodVuln) +
                           (Enabled ? "" : " (the mod is off in the Mods menu)") + ".");
            RescaleLiveHulls();
            SyncProfile();
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
                    string group;
                    float m = Multiplier(st.Ini, st.IsSub, st.IsCiv, out group);
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
            _fireHull = Math.Max(0f, _fireHullEntry.Value.V);
            DcTeams.WarTable = DcTeams.Parse(_warTeamsEntry.Value);
            DcTeams.SubTable = DcTeams.Parse(_subTeamsEntry.Value);
            DcTeams.CivTable = DcTeams.Parse(_civTeamsEntry.Value);
            DcTeams.Effectiveness = Math.Max(0f, _teamEffEntry.Value.V);
            DcTeams.Speed = Math.Max(0f, _transferSpeedEntry.Value.V);
            DcTeams.Muster = Math.Max(0f, _musterEntry.Value.V);
            DcTeams.Heal = Math.Max(0f, _healEntry.Value.V);
            DcTeams.HealSpread = Math.Max(0f, _healSpreadEntry.Value.V);
            DcTeams.KnocksToDie = Math.Max(0, _knockDeathEntry.Value);
            Limits.ListFull = Clamp(_listLimitEntry.Value.V, 5f, 69f);
            Limits.TrimFull = Clamp(_trimLimitEntry.Value.V, 1f, 30f);
            Limits.TrimRefLength = Math.Max(0f, _trimLengthEntry.Value.V);
            Limits.LevelFull = Clamp(_levelLimitEntry.Value.V, 5f, 100f);
            _logFires = _logFiresEntry.Value;
            _dumpXaml = HiddenBool("General", "DumpXaml");
            if (_dumpXaml) RequestXamlExport();
            _uiOn = _uiEntry.Value;
            try { ApplyUiSettings(); } catch (Exception e) { Trace("ApplyUiSettings: " + e.Message); }

            var map = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            ReadPerShip(map);
            _perShip = map;

            Trace("settings: warships " + F(_mWar) + ", civilian " + F(_mCiv) + ", submarines " + F(_mSub) + ", perShip " + map.Count +
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
        /// [HullIntegrityPerShip]: one plain line per ship, "ini file name = multiplier". The names are not known in advance, so the lines
        /// are not bound as settings: BepInEx keeps such lines as they are (orphans) and writes them back without comments.
        /// They are read from there, written back with a decimal point ("3" -> "3.0"); an empty section gets one example line.
        /// </summary>
        void BindPerShip()
        {
            var orphans = PerShipLines();
            if (orphans == null) { Logger.LogWarning("[HullIntegrityPerShip] cannot be read (BepInEx changed?); per-ship values are not used."); return; }

            // the first test build kept them in one line: [HullIntegrity] PerShip = name=x, name=y
            var oldDef = new ConfigDefinition("HullIntegrity", "PerShip");
            string oldList;
            if (orphans.TryGetValue(oldDef, out oldList))
            {
                orphans.Remove(oldDef);
                foreach (var part in (oldList ?? "").Split(','))
                {
                    int eq = part.IndexOf('=');
                    Factor v;
                    if (eq > 0 && Factor.TryParse(part.Substring(eq + 1), out v) && part.Substring(0, eq).Trim().Length > 0)
                        orphans[new ConfigDefinition(PerShipSection, part.Substring(0, eq).Trim())] = v.ToString();
                }
            }

            // hidden setting: [General] DumpXaml stays in the file only while it is true
            var dumpDef = new ConfigDefinition("General", "DumpXaml");
            string dumpVal;
            if (orphans.TryGetValue(dumpDef, out dumpVal))
            {
                bool on;
                if (!bool.TryParse((dumpVal ?? "").Trim(), out on) || !on) orphans.Remove(dumpDef);
            }

            orphans.Remove(new ConfigDefinition("DamageControlTeams", "OptimizedAutoDC"));
            orphans.Remove(new ConfigDefinition("Sinking", "TrimHullFactor"));   // test builds: trim limit from the hull, removed   // now automatic: on while transfers take time

            // settings that no longer exist: IntegrityOverride ships are always multiplied, point values are never changed
            orphans.Remove(new ConfigDefinition("HullIntegrity", "IncludeShipsWithFixedIntegrity"));
            orphans.Remove(new ConfigDefinition("PointValue", "PointValueUsesNewIntegrity"));

            // test builds: section [PerShip] -> [HullIntegrityPerShip]
            foreach (var kv in new List<KeyValuePair<ConfigDefinition, string>>(orphans))
            {
                if (!string.Equals(kv.Key.Section, OldPerShipSection, StringComparison.OrdinalIgnoreCase)) continue;
                orphans.Remove(kv.Key);
                var nd = new ConfigDefinition(PerShipSection, kv.Key.Key);
                if (!orphans.ContainsKey(nd)) orphans[nd] = kv.Value;
            }

            foreach (var kv in new List<KeyValuePair<ConfigDefinition, string>>(orphans))
            {
                if (!string.Equals(kv.Key.Section, PerShipSection, StringComparison.OrdinalIgnoreCase)) continue;
                if (kv.Key.Key == PerShipExample) { orphans.Remove(kv.Key); continue; }
                Factor v;
                if (Factor.TryParse(kv.Value, out v)) orphans[kv.Key] = v.ToString(); // "3" -> "3.0"
            }
        }

        /// <summary>Saves the .cfg and puts the comment under [HullIntegrityPerShip] (adds the section, empty, when it has no lines).</summary>
        void SaveConfig()
        {
            try { Config.Save(); }
            catch (Exception e) { Logger.LogWarning("Saving the settings failed: " + e.Message); return; }
            try
            {
                var path = Config.ConfigFilePath;
                var lines = new List<string>(File.ReadAllLines(path));
                int at = lines.FindIndex(l => string.Equals(l.Trim(), "[" + PerShipSection + "]", StringComparison.OrdinalIgnoreCase));
                if (at < 0)
                {
                    while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
                    lines.Add("");
                    lines.Add("[" + PerShipSection + "]");
                    lines.Add("");
                    lines.AddRange(PerShipComment);
                    lines.Add("");
                    lines.Add("");
                }
                else
                {
                    lines.InsertRange(at + 1, new[] { "" });
                    lines.InsertRange(at + 2, PerShipComment);
                }
                File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception e) { Trace("SaveConfig comment: " + e.Message); }
            RememberCfgStamp(); // our own write is not a change to re-read
        }

        /// <summary>
        /// A setting that is not written to the .cfg by default (diagnostics): "Key = true" added by hand under [Section] turns it on.
        /// BepInEx keeps such a line as it is; it is read from there (also after the file is re-read).
        /// </summary>
        bool HiddenBool(string section, string key)
        {
            var orphans = PerShipLines();
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
            try { return Orphans(Config); } catch (Exception e) { Trace("Orphans: " + e.Message); return null; }
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
                Patch(harmony, tComp.GetMethod("RepairFlooding", all), null, null, "PumpOutTranspiler", "Compartments.RepairFlooding", ref ok, ref failed);
            }

            // 10. damage control teams: number by displacement, transfers, knock-outs, effectiveness
            DcTeams.IsEnabled = () => Enabled;
            DcTeams.Trace = Trace;
            if (tComp != null && DcTeams.Init(asm))
            {
                DcAuto.Trace = Trace; SettingsWindow.Trace = Trace; TrimFlooding.Trace = Trace; TrimFlooding.IsEnabled = () => Enabled; SettingsWindow.Warn = Warn; SettingsWindow.Info = Say;
                if (!DcAuto.Init(asm)) Warn("Optimised Auto DC: game members not found; the game's Auto DC is used.");
                DcControl.StayWhenIdle = c => DcTeams.TransfersOn && DcTeams.Active(c);
                Patch(harmony, tComp.GetMethod("SendDCTeamToCompartment", all), "TeamSendPrefix", null, null, "Compartments.SendDCTeamToCompartment", ref ok, ref failed);
                Patch(harmony, tComp.GetMethod("ReturnDCTeamToPool", all), "TeamReturnPrefix", null, null, "Compartments.ReturnDCTeamToPool", ref ok, ref failed);
                Patch(harmony, tComp.GetMethod("RecallAllTeams", all), "TeamRecallPrefix", null, null, "Compartments.RecallAllTeams", ref ok, ref failed);
                int okBefore = ok;
                Patch(harmony, tComp.GetMethod("OnFixedUpdate", all), "TeamTickPrefix", null, null, "Compartments.OnFixedUpdate (teams)", ref ok, ref failed);
                _trimFloodHooked = ok > okBefore;   // the game's trim sinking is replaced only when the flooding that replaces it runs
                foreach (var name in new[] { "RepairFloodingRate", "RepairFire", "RepairFlooding", "RepairStructure", "RepairSystems" })
                    Patch(harmony, tComp.GetMethod(name, all), null, null, "TeamEffTranspiler", "Compartments." + name + " (effectiveness)", ref ok, ref failed);
            }
            else { Warn("Damage control team members not found: team numbers, transfers and knock-outs are the game's."); failed++; }

            // 9. faster flooding sooner: the game's own thresholds for heavy listing and for sinking, lowered
            if (tComp != null)
                Patch(harmony, tComp.GetMethod("OnFixedUpdate", all), null, null, "SinkThresholdsTranspiler", "Compartments.OnFixedUpdate", ref ok, ref failed);

            // 7. damage control: ship-wide priority (Auto / Fire / Flooding) and tasks given by hand to single teams
            DcControl.IsEnabled = () => Enabled;
            DcControl.Trace = Trace;
            if (tComp != null && DcControl.Init(asm))
            {
                Patch(harmony, tComp.GetMethod("CheckDamageControl", all), "DcCheckPrefix", null, null, "Compartments.CheckDamageControl", ref ok, ref failed);
                Patch(harmony, tComp.GetMethod("CheckDCTeamsAllocation", all), "DcAllocPrefix", null, null, "Compartments.CheckDCTeamsAllocation", ref ok, ref failed);
                // kept in saved games: an extra key in the unit's own section (the game ignores keys it does not know)
                if (tIni != null)
                {
                    _mIniWriteStr = tIni.GetMethod("writeValue", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(string), typeof(string) }, null);
                    _mIniReadStr = tIni.GetMethod("readValue", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(string), typeof(string), typeof(bool) }, null);
                }
                if (_mIniWriteStr != null && _mIniReadStr != null)
                {
                    Patch(harmony, tComp.GetMethod("SaveStateToFile", all), null, "DcSavePostfix", null, "Compartments.SaveStateToFile", ref ok, ref failed);
                    Patch(harmony, tComp.GetMethod("LoadStateFromFile", all), null, "DcLoadPostfix", null, "Compartments.LoadStateFromFile", ref ok, ref failed);
                }
            }
            else { Warn("Damage control members not found: priorities and tasks by hand are off."); failed++; }

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
                try
                {
                    float dispT = _fCompDisp == null ? 0f : (float)_fCompDisp.GetValue(comp);
                    DcTeams.SetCount(comp, dispT, isSub, isCiv, TeamsOverrideOf(obp));
                }
                catch (Exception e) { Trace("team count: " + e.Message); }
                string ini = _fObpIniName == null ? null : _fObpIniName.GetValue(obp) as string;
                bool fixedByIni = OverrideOf(obp) > 0f;

                string group;
                float m = Multiplier(ini, isSub, isCiv, out group);
                float x0 = (float)_fCompMaxInt.GetValue(comp);
                float x = x0 * m;
                if (m != 1f) _fCompMaxInt.SetValue(comp, x);
                Hulls.Remove(comp);
                Hulls.Add(comp, new HullState { Mult = m, OriginalX = x0, Ini = ini, IsSub = isSub, IsCiv = isCiv, FixedByIni = fixedByIni });
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
            if (!Enabled) return maxLimit;
            return maxLimit / (MultOf(comp) * _shellVuln);
        }

        // ------------------------------------------------------------ fire ignition by hits

        /// <summary>OnHitUnit: "GetWeaponDamage(hit) / compartments._maxIntegrity" (fire in the hit compartment).</summary>
        static IEnumerable<CodeInstruction> OnHitUnitTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
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
            if (!Enabled) return x;
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
            if (!Enabled) return 100f * cur / max; // the game's own getter
            object comp = null;
            try { comp = _fFcComp.GetValue(fc); } catch { }
            float reference = max / (MultOf(comp) * _floodVuln);
            float pct = 100f - 100f * (max - cur) / reference;
            return pct < 0f ? 0f : (pct > 100f ? 100f : pct);
        }

        public static float FireIntegrity(object comp, float x)
        {
            if (!Enabled) return x;
            return x / _fireVuln; // the ship's own (multiplied) integrity: a stronger hull burns proportionally weaker
        }

        public static float FireIntegrityFc(object fc, float x)
        {
            if (!Enabled) return x;
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
            DamageControlUi.Tick(UnityEngine.Time.unscaledTime, Enabled && _uiOn);
        }

        static bool UiActive { get { return _uiReady && _uiOn && Enabled; } }

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

        public static float SinkLevel(float game) { return Enabled ? Limits.LevelFull : game; }       // 40: ship sinks above this flooding %
        // 8: trim (with a flooded section) sinks the ship in the game - off while the mod is on (TrimFlooding floods the end going down
        // instead); life rafts (1 compartment) keep it: the same check sets them level again
        public static float SinkTrim(float game, object comp)
        {
            if (!Enabled || !_trimFloodHooked) return game;
            try { if (_fCompCount != null && (int)_fCompCount.GetValue(comp) < 2) return game; } catch { }
            return float.MaxValue;
        }
        public static float HeavyList(float game) { return Enabled ? Limits.ListFull : game; }        // 45: water over the deck edge
        public static float HeavyListNeg(float game) { return Enabled ? -Limits.ListFull : game; }    // -45

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
                if (m == "SinkTrim")
                {
                    // per ship: SinkTrim(8, this)
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

        public static float FlameUpMul(float v) { return Enabled ? v * _flameUp : v; }
        public static float ExtinguishMul(float v) { return Enabled ? v * _extinguish : v; }
        public static float FloodingMul(float v) { return Enabled ? v * _floodSpeed : v; }
        public static float PumpOutMul(float v) { return Enabled ? v * _pumpOut : v; }
        public static float FireHullMul(float v) { return Enabled ? v * _fireHull : v; }

        // SystemCompartment.OnFixedUpdate: hull damage of a fire = Globals._shipFireCoefficient * FireSeverity * dt * _shipFireSpeedMultiplier
        static IEnumerable<CodeInstruction> FireHullTranspiler(IEnumerable<CodeInstruction> ins)
        {
            return MulAfter(ins, (l, i) => Field(l[i], "Globals", "_shipFireCoefficient"), "FireHullMul", 1, "FireHullDamageSpeed");
        }

        /// <summary>Inserts "call mul" after every instruction that matches; warns when the count is not the expected one.</summary>
        static IEnumerable<CodeInstruction> MulAfter(IEnumerable<CodeInstruction> instructions, Func<List<CodeInstruction>, int, bool> match,
                                                     string mul, int expected, string label)
        {
            var list = new List<CodeInstruction>(instructions);
            var m = typeof(AdmPlugin).GetMethod(mul, BindingFlags.Static | BindingFlags.Public);
            int n = 0;
            for (int i = 0; i < list.Count; i++)
                if (match(list, i)) { list.Insert(i + 1, new CodeInstruction(OpCodes.Call, m)); i++; n++; }
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
            return MulAfter(ins, (l, i) => Field(l[i], "SystemCompartment", "_fireGrowRate") && i + 1 < l.Count && FixedDt(l[i + 1]), "FlameUpMul", 1, "FlameUpSpeed");
        }

        // FloodingCompartment.OnFixedUpdate: _currentFlooding += _shipFloodingCoefficient * _floodingRate * ... (normal and sinking)
        static IEnumerable<CodeInstruction> FloodingSpeedTranspiler(IEnumerable<CodeInstruction> ins)
        {
            return MulAfter(ins, (l, i) => Field(l[i], "FloodingCompartment", "_floodingRate") && i >= 2 && Field(l[i - 2], "Globals", "_shipFloodingCoefficient"), "FloodingMul", 2, "FloodingSpeed");
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
        static bool TeamRecallPrefix() { return DcTeams.RecallPrefix(); }
        static void TeamTickPrefix(object __instance) { DcTeams.Tick(__instance); TrimFlooding.Tick(__instance); }

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
        const string DcTeamsSaveKey = "AlternativeDamageModel_Teams";

        static void DcSavePostfix(object __instance, object ini, string sectionName, bool isCampaignStateSave)
        {
            try
            {
                if (!Enabled || isCampaignStateSave || ini == null) return;
                var text = DcControl.Save(__instance);
                if (text != null) _mIniWriteStr.Invoke(ini, new object[] { sectionName, DcSaveKey, text });
                var teams = DcTeams.Save(__instance);
                if (teams != null) _mIniWriteStr.Invoke(ini, new object[] { sectionName, DcTeamsSaveKey, teams });
            }
            catch (Exception e) { Trace("DcSavePostfix: " + e.Message); }
        }

        static void DcLoadPostfix(object __instance, object ini, string sectionName)
        {
            try
            {
                if (!Enabled || ini == null) return;
                var text = _mIniReadStr.Invoke(ini, new object[] { sectionName, DcSaveKey, "", false }) as string;
                if (!string.IsNullOrEmpty(text)) DcControl.Load(__instance, text);
                var teams = _mIniReadStr.Invoke(ini, new object[] { sectionName, DcTeamsSaveKey, "", false }) as string;
                if (!string.IsNullOrEmpty(teams)) DcTeams.Load(__instance, teams);
            }
            catch (Exception e) { Trace("DcLoadPostfix: " + e.Message); }
        }
        static bool DcAllocPrefix(object __instance) { return DcControl.AllocPrefix(__instance); }

        /// <summary>Skips setting an unchanged DamageIntensity (see PatchUi). Only while the detailed view is on.</summary>
        static bool DcIntensityPrefix(int value, int ____damageIntensity)
        {
            return !(UiActive && value == ____damageIntensity);
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

        public override string ToString() { return V.ToString("0.0##", CultureInfo.InvariantCulture); }

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
