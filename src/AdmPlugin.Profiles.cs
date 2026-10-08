using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;

namespace AlternativeDamageModel
{
    /// <summary>
    /// A set of game-play settings: every setting of the profile sections plus the [HullIntegrityPerShip] lines.
    /// Keys are "Section/Key"; values are the .cfg's text.
    /// </summary>
    public sealed class Profile
    {
        public string Name;
        public bool BuiltIn;
        public SortedDictionary<string, string> Values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public SortedDictionary<string, string> Ships = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Profile Copy(string name)
        {
            var p = new Profile { Name = name, BuiltIn = false };
            foreach (var kv in Values) p.Values[kv.Key] = kv.Value;
            foreach (var kv in Ships) p.Ships[kv.Key] = kv.Value;
            return p;
        }

        /// <summary>Same values (numbers compared as numbers, lists without their spaces).</summary>
        public bool SameAs(Profile o)
        {
            if (o == null) return false;
            foreach (var kv in Values)
            {
                string v;
                if (!o.Values.TryGetValue(kv.Key, out v) || !AdmPlugin.SameValue(kv.Value, v)) return false;
            }
            foreach (var kv in o.Values) if (!Values.ContainsKey(kv.Key)) return false;
            if (Ships.Count != o.Ships.Count) return false;
            foreach (var kv in Ships)
            {
                string v;
                if (!o.Ships.TryGetValue(kv.Key, out v) || !AdmPlugin.SameValue(kv.Value, v)) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Settings profiles. The .cfg holds the values in use and [General] Profile = the name of the profile they came from.
    ///  - Base Game and Advanced: built in, read-only. Files Profiles/Base Game.cfg and Profiles/Advanced.cfg next to the dll (a value
    ///    missing there comes from the code); without the files the code's values are used.
    ///  - The player's own: BepInEx/config/AlternativeDamageModel/Profiles/name.cfg.
    /// When the values in use no longer match a built-in profile (changed in the game's settings window, in the .cfg or by a
    /// configuration manager), they are kept as the profile "Custom"; a player's own profile is simply saved with them.
    /// </summary>
    public partial class AdmPlugin
    {
        public const string BaseGameName = "Base Game", AdvancedName = "Advanced", CustomName = "Custom";
        static readonly string[] ProfileSections = { "HullIntegrity", "SideEffects", "FireAndFlooding", "DamageControlTeams", "Sinking" };
        readonly List<ConfigEntryBase> _profileEntries = new List<ConfigEntryBase>();
        readonly List<ConfigEntryBase> _globalEntries = new List<ConfigEntryBase>();

        /// <summary>The values of Base Game; anything not listed is the same in both built-in profiles (1.0).</summary>
        static readonly Dictionary<string, string> BaseGameValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "HullIntegrity/Warships", "1.0" }, { "HullIntegrity/CivilianShips", "1.0" }, { "HullIntegrity/Submarines", "1.0" },
            { "SideEffects/HullShellVulnerability", "1.0" }, { "SideEffects/HullFireVulnerability", "1.0" }, { "SideEffects/HullFloodingVulnerability", "1.0" },
            { "FireAndFlooding/FlameUpSpeed", "1.0" }, { "FireAndFlooding/FireHullDamageSpeed", "1.0" }, { "FireAndFlooding/ExtinguishSpeed", "1.0" },
            { "FireAndFlooding/FloodingSpeed", "1.0" }, { "FireAndFlooding/PumpOutSpeed", "1.0" },
            { "DamageControlTeams/WarshipTeams", "0=1, 7000=2, 24000=3" }, { "DamageControlTeams/SubmarineTeams", "0=1, 5000=2" },
            { "DamageControlTeams/CivilianTeams", "0=1" }, { "DamageControlTeams/DCTeamEffectiveness", "1.0" },
            { "DamageControlTeams/TransferSpeed", "0.0" }, { "DamageControlTeams/MusterTime", "0.0" },
            { "DamageControlTeams/HealTime", "90.0" }, { "DamageControlTeams/HealTimeSpread", "30.0" }, { "DamageControlTeams/KnockoutsToDeath", "0" },
            { "Sinking/ListLimit", "45.0" }, { "Sinking/TrimLimit", "8.0" }, { "Sinking/TrimReferenceLength", "0.0" }, { "Sinking/FloodLimit", "40.0" },
        };

        static string Key(ConfigDefinition d) { return d.Section + "/" + d.Key; }

        public static bool SameValue(string a, string b)
        {
            a = (a ?? "").Trim(); b = (b ?? "").Trim();
            float x, y;
            if (float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out x) && float.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                return Math.Abs(x - y) < 1e-4f;
            return string.Equals(a.Replace(" ", ""), b.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Sorts the bound settings into profile ones and global ones (UI, logging). Called once, after binding.</summary>
        void CollectEntries()
        {
            _profileEntries.Clear(); _globalEntries.Clear();
            foreach (var kv in Config)
            {
                if (Array.IndexOf(ProfileSections, kv.Key.Section) >= 0) _profileEntries.Add(kv.Value);
                else if (kv.Value != _profileEntry) _globalEntries.Add(kv.Value);
            }
        }

        // ------------------------------------------------------------ files

        static string ModProfilesDir
        {
            get
            {
                try { return Path.Combine(Path.GetDirectoryName(typeof(AdmPlugin).Assembly.Location) ?? "", "Profiles"); }
                catch { return null; }
            }
        }

        static string UserProfilesDir { get { return Path.Combine(Path.Combine(Paths.ConfigPath, "AlternativeDamageModel"), "Profiles"); } }

        public static bool IsBuiltIn(string name)
        {
            return string.Equals(name, BaseGameName, StringComparison.OrdinalIgnoreCase) || string.Equals(name, AdvancedName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Why a name cannot be used for a profile of the player's own, or null when it can.</summary>
        public static string BadName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "Type a name.";
            if (name.Length > 40) return "40 characters at most.";
            if (IsBuiltIn(name)) return "\"" + name + "\" is a built-in profile.";
            foreach (char ch in Path.GetInvalidFileNameChars()) if (name.IndexOf(ch) >= 0) return "Not allowed in a name: " + ch;
            if (name.IndexOf(';') >= 0 || name.IndexOf('#') >= 0 || name.IndexOf('[') >= 0 || name.IndexOf(']') >= 0) return "Not allowed in a name: ; # [ ]";
            return null;
        }

        static string UserFile(string name) { return Path.Combine(UserProfilesDir, name.Trim() + ".cfg"); }

        /// <summary>The names to choose from: the two built-in ones, then the player's own (alphabetical).</summary>
        public static List<string> ProfileNames()
        {
            var list = new List<string> { BaseGameName, AdvancedName };
            var own = new List<string>();
            try
            {
                if (Directory.Exists(UserProfilesDir))
                    foreach (var f in Directory.GetFiles(UserProfilesDir, "*.cfg"))
                    {
                        string n = Path.GetFileNameWithoutExtension(f);
                        if (!IsBuiltIn(n)) own.Add(n);
                    }
            }
            catch (Exception e) { Trace("ProfileNames: " + e.Message); }
            own.Sort(StringComparer.OrdinalIgnoreCase);
            list.AddRange(own);
            return list;
        }

        /// <summary>A profile by name (built-in: file over the code's values); null when there is no such profile.</summary>
        public Profile LoadProfile(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (IsBuiltIn(name))
            {
                bool basic = string.Equals(name, BaseGameName, StringComparison.OrdinalIgnoreCase);
                var p = new Profile { Name = basic ? BaseGameName : AdvancedName, BuiltIn = true };
                foreach (var e in _profileEntries)
                {
                    string k = Key(e.Definition), v;
                    p.Values[k] = basic && BaseGameValues.TryGetValue(k, out v) ? v : TomlTypeConverter.ConvertToString(e.DefaultValue, e.SettingType);
                }
                string dir = ModProfilesDir;
                if (dir != null) ReadProfileFile(Path.Combine(dir, p.Name + ".cfg"), p);
                return p;
            }
            string file = UserFile(name);
            if (!File.Exists(file)) return null;
            var own = new Profile { Name = Path.GetFileNameWithoutExtension(file), BuiltIn = false };
            foreach (var e in _profileEntries) own.Values[Key(e.Definition)] = TomlTypeConverter.ConvertToString(e.DefaultValue, e.SettingType);
            ReadProfileFile(file, own);
            return own;
        }

        /// <summary>A plain ini: [Section] / Key = Value, '#' comments; [HullIntegrityPerShip] lines are the per-ship values.</summary>
        void ReadProfileFile(string file, Profile p)
        {
            try
            {
                if (!File.Exists(file)) return;
                string section = "";
                bool shipsSeen = false;
                foreach (var raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2).Trim(); continue; }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    if (string.Equals(section, PerShipSection, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!shipsSeen) { p.Ships.Clear(); shipsSeen = true; }
                        Factor f;
                        if (k.Length > 0 && Factor.TryParse(v, out f) && f.V > 0f) p.Ships[k] = f.ToString();
                        continue;
                    }
                    string key = section + "/" + k;
                    if (p.Values.ContainsKey(key)) p.Values[key] = v;   // only settings that exist
                }
            }
            catch (Exception e) { Warn("Profile " + file + " could not be read: " + e.Message); }
        }

        void WriteProfileFile(string file, Profile p)
        {
            var sb = new StringBuilder();
            sb.Append("## Alternative Damage Model - settings profile \"").Append(p.Name).Append("\"").AppendLine();
            sb.Append("## Chosen in the game: Unit Status -> Damage Control -> the gear next to (i). Same keys as the mod's .cfg.").AppendLine();
            foreach (var sec in ProfileSections)
            {
                sb.AppendLine().Append('[').Append(sec).Append(']').AppendLine();
                foreach (var e in _profileEntries)
                {
                    if (e.Definition.Section != sec) continue;
                    string v;
                    if (p.Values.TryGetValue(Key(e.Definition), out v)) sb.Append(e.Definition.Key).Append(" = ").Append(v).AppendLine();
                }
            }
            sb.AppendLine().Append('[').Append(PerShipSection).Append(']').AppendLine();
            foreach (var kv in p.Ships) sb.Append(kv.Key).Append(" = ").Append(kv.Value).AppendLine();
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        }

        // ------------------------------------------------------------ the values in use

        /// <summary>The values in use (the .cfg) as a profile named after [General] Profile.</summary>
        public Profile CurrentValues()
        {
            var p = new Profile { Name = _profileEntry.Value, BuiltIn = IsBuiltIn(_profileEntry.Value) };
            foreach (var e in _profileEntries) p.Values[Key(e.Definition)] = e.GetSerializedValue();
            var orphans = PerShipLines();
            if (orphans != null)
                foreach (var kv in orphans)
                    if (string.Equals(kv.Key.Section, PerShipSection, StringComparison.OrdinalIgnoreCase) && kv.Key.Key != PerShipExample)
                    {
                        Factor f;
                        if (Factor.TryParse(kv.Value, out f) && f.V > 0f) p.Ships[kv.Key.Key.Trim()] = f.ToString();
                    }
            return p;
        }

        public string ActiveProfileName { get { return _profileEntry.Value; } }

        /// <summary>Puts a profile's values into use (the .cfg) under that name, at once (ships at sea too).</summary>
        void ApplyValues(Profile p, string name)
        {
            _reloading = true;
            try
            {
                foreach (var e in _profileEntries)
                {
                    string v;
                    if (!p.Values.TryGetValue(Key(e.Definition), out v)) continue;
                    try { e.SetSerializedValue(v); }
                    catch (Exception ex) { Warn("Profile " + p.Name + ": " + Key(e.Definition) + " = '" + v + "' ignored (" + ex.Message + ")."); }
                }
                var orphans = PerShipLines();
                if (orphans != null)
                {
                    foreach (var kv in new List<KeyValuePair<ConfigDefinition, string>>(orphans))
                        if (string.Equals(kv.Key.Section, PerShipSection, StringComparison.OrdinalIgnoreCase)) orphans.Remove(kv.Key);
                    foreach (var kv in p.Ships) orphans[new ConfigDefinition(PerShipSection, kv.Key)] = kv.Value;
                }
                _profileEntry.Value = name;
            }
            finally { _reloading = false; }
            ReadSettings();
            SaveConfig();
            RescaleLiveHulls();
        }

        /// <summary>
        /// The values in use against their profile, after they were changed outside the settings window (start, .cfg edited, a
        /// configuration manager): a built-in profile that no longer matches becomes Custom (saved); a profile of the player's own
        /// is saved with the new values.
        /// </summary>
        void SyncProfile()
        {
            try
            {
                var cur = CurrentValues();
                string name = _profileEntry.Value;
                if (string.IsNullOrEmpty(name) || BadName(name) != null && !IsBuiltIn(name)) name = CustomName;
                var prof = LoadProfile(name);
                if (prof != null && prof.SameAs(cur)) return;
                if (prof == null || prof.BuiltIn)
                {
                    // a built-in profile with other values: they are the player's own now
                    string own = prof == null ? name : CustomName;
                    WriteProfileFile(UserFile(own), cur.Copy(own));
                    if (_profileEntry.Value != own) { _reloading = true; try { _profileEntry.Value = own; } finally { _reloading = false; } SaveConfig(); }
                    Say("Settings differ from profile " + (prof == null ? "?" : prof.Name) + ": kept as profile \"" + own + "\".");
                }
                else WriteProfileFile(UserFile(prof.Name), cur.Copy(prof.Name));
            }
            catch (Exception e) { Warn("Profile check failed: " + e.Message); }
        }

        // ------------------------------------------------------------ for the settings window

        public static AdmPlugin Instance { get { return _inst; } }
        public IList<ConfigEntryBase> ProfileEntries { get { return _profileEntries; } }
        public IList<ConfigEntryBase> GlobalEntries { get { return _globalEntries; } }

        /// <summary>
        /// The window's OK / Apply: 'values' come into use. Picked from a built-in profile and unchanged: that profile. Changed, or
        /// from a built-in one: saved as 'saveAs' (Custom when null). Returns the name of the profile now in use.
        /// </summary>
        public string Commit(string selected, Profile values, Dictionary<ConfigEntryBase, string> globals)
        {
            string name = selected;
            var prof = LoadProfile(selected);
            if (prof == null || !prof.SameAs(values))
            {
                if (prof == null) name = BadName(selected) == null ? selected.Trim() : CustomName;
                else if (prof.BuiltIn) name = CustomName;
                WriteProfileFile(UserFile(name), values.Copy(name));
            }
            if (globals != null)
            {
                _reloading = true;
                try
                {
                    foreach (var kv in globals)
                        try { kv.Key.SetSerializedValue(kv.Value); } catch (Exception e) { Warn(Key(kv.Key.Definition) + ": " + e.Message); }
                }
                finally { _reloading = false; }
            }
            ApplyValues(values, name);
            Say("Settings profile \"" + name + "\" in use.");
            return name;
        }

        /// <summary>Saves the values as a profile of the player's own (Save as).</summary>
        public string SaveProfileAs(string name, Profile values)
        {
            string bad = BadName(name);
            if (bad != null) return bad;
            try { WriteProfileFile(UserFile(name.Trim()), values.Copy(name.Trim())); return null; }
            catch (Exception e) { return "Could not save: " + e.Message; }
        }

        public string RenameProfile(string from, string to)
        {
            if (IsBuiltIn(from)) return "Built-in profiles cannot be renamed.";
            string bad = BadName(to);
            if (bad != null) return bad;
            to = to.Trim();
            try
            {
                string a = UserFile(from), b = UserFile(to);
                if (!File.Exists(a)) return "Profile not found.";
                if (File.Exists(b) && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return "A profile with this name exists.";
                var p = LoadProfile(from);
                File.Delete(a);
                WriteProfileFile(b, p.Copy(to));
                if (string.Equals(_profileEntry.Value, from, StringComparison.OrdinalIgnoreCase))
                {
                    _reloading = true; try { _profileEntry.Value = to; } finally { _reloading = false; }
                    SaveConfig();
                }
                return null;
            }
            catch (Exception e) { return "Could not rename: " + e.Message; }
        }

        public string DeleteProfile(string name)
        {
            if (IsBuiltIn(name)) return "Built-in profiles cannot be deleted.";
            try
            {
                string f = UserFile(name);
                if (File.Exists(f)) File.Delete(f);
                if (string.Equals(_profileEntry.Value, name, StringComparison.OrdinalIgnoreCase))
                {
                    var adv = LoadProfile(AdvancedName);
                    ApplyValues(adv, AdvancedName);
                }
                return null;
            }
            catch (Exception e) { return "Could not delete: " + e.Message; }
        }

        public static string ProfileFolder { get { return UserProfilesDir; } }
    }
}
