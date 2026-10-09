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
        public SortedDictionary<string, string> Modules = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // [SystemTypeIntegrity]: module type = its Integrity

        public Profile Copy(string name)
        {
            var p = new Profile { Name = name, BuiltIn = false };
            foreach (var kv in Values) p.Values[kv.Key] = kv.Value;
            foreach (var kv in Ships) p.Ships[kv.Key] = kv.Value;
            foreach (var kv in Modules) p.Modules[kv.Key] = kv.Value;
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
            return SameMap(Ships, o.Ships) && SameMap(Modules, o.Modules);
        }

        static bool SameMap(SortedDictionary<string, string> a, SortedDictionary<string, string> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kv in a)
            {
                string v;
                if (!b.TryGetValue(kv.Key, out v) || !AdmPlugin.SameValue(kv.Value, v)) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Settings profiles. The .cfg holds the values in use and [General] Profile = the name of the profile they came from.
    ///  - System (built-in, read-only): every Profiles/*.cfg next to the dll; Base Game and Advanced always exist. These files are
    ///    the defaults (edit them for a release); the code's Bind defaults are only a fallback for a missing file or key. While a
    ///    built-in profile is in use, the .cfg follows its file at every start (a new release with changed defaults is taken over).
    ///  - The player's own: StreamingAssets/user/AdvancedDamageControl/Profiles/name.cfg.
    /// When the values in use no longer match a built-in profile (changed in the game's settings window, in the .cfg or by a
    /// configuration manager), they are kept as the profile "Custom"; a player's own profile is simply saved with them.
    /// </summary>
    public partial class AdmPlugin
    {
        public const string BaseGameName = "Base Game", AdvancedName = "Advanced", CustomName = "Custom";
        static readonly string[] ProfileSections = { "HullIntegrity", "SideEffects", "Systems", "FireAndFlooding", "DamageControlTeams", "Sinking" };
        readonly List<ConfigEntryBase> _profileEntries = new List<ConfigEntryBase>();
        readonly List<ConfigEntryBase> _globalEntries = new List<ConfigEntryBase>();

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
            foreach (var kv in Pcfg) _profileEntries.Add(kv.Value);
            foreach (var kv in Cfg) if (kv.Value != _profileEntry) _globalEntries.Add(kv.Value);
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

        static string UserProfilesDir { get { return Path.Combine(UserDir ?? Path.Combine(Paths.ConfigPath, "AlternativeDamageModel"), "Profiles"); } }

        /// <summary>Profiles of the player's own from the old place (BepInEx/config/AlternativeDamageModel/Profiles), moved once.</summary>
        static void MoveOldProfiles()
        {
            try
            {
                string oldRoot = Path.Combine(Paths.ConfigPath, "AlternativeDamageModel"), old = Path.Combine(oldRoot, "Profiles");
                if (!Directory.Exists(old) || string.Equals(Path.GetFullPath(old), Path.GetFullPath(UserProfilesDir), StringComparison.OrdinalIgnoreCase)) return;
                Directory.CreateDirectory(UserProfilesDir);
                foreach (var f in Directory.GetFiles(old, "*.cfg"))
                {
                    string to = Path.Combine(UserProfilesDir, Path.GetFileName(f));
                    if (!File.Exists(to)) File.Move(f, to); else File.Delete(f);
                }
                try { Directory.Delete(old); Directory.Delete(oldRoot); } catch { }   // only when empty
            }
            catch (Exception e) { Trace("MoveOldProfiles: " + e.Message); }
        }

        /// <summary>
        /// The built-in (system) profiles: every Profiles/*.cfg next to the dll, plus Base Game and Advanced (from the code when
        /// their files are missing). Alphabetical.
        /// </summary>
        public static List<string> SystemProfiles()
        {
            var list = new List<string>();
            try
            {
                string dir = ModProfilesDir;
                if (dir != null && Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "*.cfg")) list.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch (Exception e) { Trace("SystemProfiles: " + e.Message); }
            foreach (var n in new[] { BaseGameName, AdvancedName })
                if (!list.Exists(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) list.Add(n);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            _system = list;
            return new List<string>(list);   // a copy: callers add the player's own to it
        }
        static List<string> _system;

        /// <summary>The name as the system profile spells it, or null when 'name' is not a system profile.</summary>
        static string SystemName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var list = _system ?? SystemProfiles();
            foreach (var n in list) if (string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase)) return n;
            return null;
        }

        /// <summary>
        /// Why a name cannot be a key of [HullIntegrityPerShip] / [SystemTypeIntegrity] (BepInEx refuses these characters in a key:
        /// = \n \t \ " ' [ ]), or null when it can.
        /// </summary>
        public static string BadKey(string key)
        {
            key = key ?? "";
            if (key.Trim().Length == 0) return "empty";
            foreach (char ch in "=\n\t\\\"'[]") if (key.IndexOf(ch) >= 0) return "not allowed: " + (ch == '\n' ? "line break" : ch == '\t' ? "tab" : ch.ToString());
            return null;
        }

        /// <summary>A per-ship / per-type line into the values in use; a key BepInEx refuses is left out (never stops the rest).</summary>
        void PutLine(Dictionary<ConfigDefinition, string> orphans, string section, string key, string value)
        {
            if (BadKey(key) != null) { Warn("[" + section + "] '" + key + "' left out (" + BadKey(key) + ")."); return; }
            try { orphans[new ConfigDefinition(section, key)] = value; }
            catch (Exception e) { Warn("[" + section + "] '" + key + "' left out (" + e.Message + ")."); }
        }

        public static bool IsBuiltIn(string name) { return SystemName(name) != null; }

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

        /// <summary>The names to choose from: the system profiles (alphabetical), then the player's own (alphabetical).</summary>
        public static List<string> ProfileNames()
        {
            var list = SystemProfiles();
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

        static readonly HashSet<string> _missingWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A profile by name; null when there is no such profile. Built-in ones come from Profiles/name.cfg next to the dll (the
        /// files are the defaults); a value missing there (or no file) falls back to the code's default, with a warning in the log.
        /// </summary>
        public Profile LoadProfile(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (IsBuiltIn(name))
            {
                var p = new Profile { Name = SystemName(name), BuiltIn = true };
                var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string dir = ModProfilesDir, bf = dir == null ? null : Path.Combine(dir, p.Name + ".cfg");
                foreach (var e in _profileEntries) p.Values[Key(e.Definition)] = TomlTypeConverter.ConvertToString(e.DefaultValue, e.SettingType);
                if (bf != null) ReadProfileFile(bf, p, read);
                var missing = new List<string>();
                foreach (var e in _profileEntries) if (!read.Contains(Key(e.Definition))) missing.Add(Key(e.Definition));
                if (missing.Count > 0 && _missingWarned.Add(p.Name))
                    Warn(bf == null || !File.Exists(bf)
                        ? "Built-in profile file missing: " + (bf ?? p.Name + ".cfg") + " - the code's defaults are used."
                        : "Built-in profile " + p.Name + ".cfg has no " + string.Join(", ", missing.ToArray()) + " - the code's defaults are used for them.");
                return p;
            }
            string file = UserFile(name);
            if (!File.Exists(file)) return null;
            var own = new Profile { Name = Path.GetFileNameWithoutExtension(file), BuiltIn = false };
            foreach (var e in _profileEntries) own.Values[Key(e.Definition)] = TomlTypeConverter.ConvertToString(e.DefaultValue, e.SettingType);
            ReadProfileFile(file, own);
            return own;
        }

        /// <summary>A setting in a format of an older build (converted by ConvertOld).</summary>
        static bool IsOld(string section, string key)
        {
            return string.Equals(section, OldPerModuleSection, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(section, "FireAndFlooding", StringComparison.OrdinalIgnoreCase) && string.Equals(key, "FireSpreadChance", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Older formats into the current one: [SystemsPerType] multipliers become [SystemTypeIntegrity] integrities (x the game's
        /// modules.ini value); FireSpreadChance (x the game's) becomes FireSpreadPercent. Returns the key it set, or null.
        /// </summary>
        static string ConvertOld(Profile p, string section, string key, string value)
        {
            Factor f;
            if (!Factor.TryParse(value, out f) || f.V < 0f) return null;
            if (string.Equals(section, OldPerModuleSection, StringComparison.OrdinalIgnoreCase))
            {
                float gi = ModuleNames.GameIntegrity(key);
                if (gi <= 0f || f.V <= 0f) return null;
                p.Modules[ModuleNames.ToKey(key)] = new Factor(f.V * gi).ToString();
                return PerModuleSection + "/" + key;
            }
            const string fire = "FireAndFlooding/FireSpreadPercent";
            if (!p.Values.ContainsKey(fire)) return null;
            p.Values[fire] = new Factor(Math.Min(100f, f.V * GameFireSpreadPct)).ToString();
            return fire;
        }

        /// <summary>A plain ini: [Section] / Key = Value, '#' comments; [HullIntegrityPerShip] lines are the per-ship values.</summary>
        void ReadProfileFile(string file, Profile p, HashSet<string> read = null)
        {
            try
            {
                if (!File.Exists(file)) return;
                string section = "";
                bool shipsSeen = false, modsSeen = false;
                foreach (var raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2).Trim(); continue; }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    if (IsOld(section, k))
                    {
                        if (string.Equals(section, OldPerModuleSection, StringComparison.OrdinalIgnoreCase) && !modsSeen) { p.Modules.Clear(); modsSeen = true; }
                        string conv = ConvertOld(p, section, k, v);
                        if (conv != null && read != null) read.Add(conv);
                        continue;
                    }
                    if (string.Equals(section, PerShipSection, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!shipsSeen) { p.Ships.Clear(); shipsSeen = true; }
                        Factor f;
                        if (BadKey(k) != null) { Trace("Profile " + file + ": [" + PerShipSection + "] '" + k + "' ignored (" + BadKey(k) + ")."); continue; }
                        if (Factor.TryParse(v, out f) && f.V > 0f) p.Ships[k] = f.ToString();
                        continue;
                    }
                    if (string.Equals(section, PerModuleSection, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!modsSeen) { p.Modules.Clear(); modsSeen = true; }
                        Factor f;
                        if (BadKey(k) != null) { Trace("Profile " + file + ": [" + PerModuleSection + "] '" + k + "' ignored (" + BadKey(k) + ")."); continue; }
                        if (Factor.TryParse(v, out f) && f.V > 0f) p.Modules[k] = f.ToString();
                        continue;
                    }
                    string key = section + "/" + k;
                    if (p.Values.ContainsKey(key)) { p.Values[key] = v; if (read != null) read.Add(key); }   // only settings that exist
                }
            }
            catch (Exception e) { Warn("Profile " + file + " could not be read: " + e.Message); }
        }

        void WriteProfileFile(string file, Profile p)
        {
            var sb = new StringBuilder();
            sb.Append("## Alternative Damage Model - settings profile \"").Append(p.Name).Append("\"").AppendLine();
            sb.Append("## Chosen in the game: Unit Status -> Damage Control -> the gear next to (i), or [General] Profile in AlternativeDamageModel.cfg.").AppendLine();
            sb.Append("## Edits made here while the game runs are picked up at once when this profile is in use.").AppendLine();
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
            foreach (var c in PerShipComment) sb.Append(c).AppendLine();
            foreach (var kv in p.Ships) sb.Append(kv.Key).Append(" = ").Append(kv.Value).AppendLine();
            sb.AppendLine().Append('[').Append(PerModuleSection).Append(']').AppendLine();
            foreach (var kv in p.Modules) sb.Append(kv.Key).Append(" = ").Append(kv.Value).AppendLine();
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            if (string.Equals(file, _profWatch.Path, StringComparison.OrdinalIgnoreCase)) _profWatch.Remember();   // our own write
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
                    else if (string.Equals(kv.Key.Section, PerModuleSection, StringComparison.OrdinalIgnoreCase))
                    {
                        Factor f;
                        if (Factor.TryParse(kv.Value, out f) && f.V > 0f) p.Modules[kv.Key.Key.Trim()] = f.ToString();
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
                        if (string.Equals(kv.Key.Section, PerShipSection, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(kv.Key.Section, PerModuleSection, StringComparison.OrdinalIgnoreCase)) orphans.Remove(kv.Key);
                    foreach (var kv in p.Ships) PutLine(orphans, PerShipSection, kv.Key, kv.Value);
                    foreach (var kv in p.Modules) PutLine(orphans, PerModuleSection, kv.Key, kv.Value);
                }
                _profileEntry.Value = name;
            }
            finally { _reloading = false; }
            _profWatch.Set(ProfilePath(name));
            ReadSettings();
            SaveConfig();
            RescaleLiveHulls();
        }

        /// <summary>The file of a profile: next to the dll for a built-in one, else in the player's folder.</summary>
        static string ProfilePath(string name)
        {
            if (IsBuiltIn(name)) { string dir = ModProfilesDir; return dir == null ? null : Path.Combine(dir, SystemName(name) + ".cfg"); }
            return UserFile(name);
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
                    _profWatch.Set(UserFile(own));
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
            name = name.Trim();
            try { WriteProfileFile(UserFile(name), values.Copy(name)); }
            catch (Exception e) { return "Could not save: " + e.Message; }
            // saved over the profile in use: these values are in use now (the file and the values in use stay the same)
            if (string.Equals(name, _profileEntry.Value, StringComparison.OrdinalIgnoreCase))
            {
                var p = LoadProfile(name);
                if (p != null) ApplyValues(p, p.Name);
            }
            return null;
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
                    _profWatch.Set(b);
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
