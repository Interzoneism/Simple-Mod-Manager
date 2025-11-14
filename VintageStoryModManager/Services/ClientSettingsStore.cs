using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VintageStoryModManager.Services;

/// <summary>
///     Provides read/write access to the Vintage Story clientsettings.json file.
/// </summary>
public sealed class ClientSettingsStore
{
    private readonly string _backupPath;
    private readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };
    private readonly object _syncRoot = new();
    private readonly string _tempPath;
    private HashSet<string> _disabledLookup = null!;
    private List<string> _disabledMods = null!;
    private List<string> _modPaths = null!;
    private JsonObject _root = null!;
    private JsonObject _stringListSettings = null!;
    private JsonObject _stringSettings = null!;

    public ClientSettingsStore(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(dataDirectory));

        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);

        SettingsPath = Path.Combine(DataDirectory, "clientsettings.json");
        _tempPath = Path.Combine(DataDirectory, "clientsettings.tmp");
        _backupPath = Path.Combine(DataDirectory, "clientsettings.bkp");

        InitializeFromRoot(LoadOrCreateRoot());
        EnsureDefaultModPaths();

        SearchBaseCandidates = BuildSearchBases().ToList();
    }

    public string DataDirectory { get; }

    public string SettingsPath { get; }

    public ReadOnlyCollection<string> DisabledEntries
    {
        get
        {
            lock (_syncRoot)
            {
                return new ReadOnlyCollection<string>(_disabledMods.ToList());
            }
        }
    }

    public ReadOnlyCollection<string> ModPaths
    {
        get
        {
            lock (_syncRoot)
            {
                return new ReadOnlyCollection<string>(_modPaths.ToList());
            }
        }
    }

    public string? PlayerUid => TryGetStringSettingValue("playeruid");

    public string? PlayerName => TryGetStringSettingValue("playername");

    /// <summary>
    ///     Directories that are used as bases for resolving relative mod paths.
    /// </summary>
    public IReadOnlyList<string> SearchBaseCandidates { get; }

    public IReadOnlyList<string> GetDisabledEntriesSnapshot()
    {
        lock (_syncRoot)
        {
            return _disabledMods.ToArray();
        }
    }

    public bool TryReload(out string? error)
    {
        lock (_syncRoot)
        {
            try
            {
                InitializeFromRoot(LoadOrCreateRoot());
                EnsureDefaultModPaths();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    public bool IsDisabled(string modId, string? version)
    {
        if (string.IsNullOrWhiteSpace(modId)) return false;

        lock (_syncRoot)
        {
            if (_disabledLookup.Contains(modId)) return true;

            if (!string.IsNullOrWhiteSpace(version)) return _disabledLookup.Contains($"{modId}@{version}");
        }

        return false;
    }

    public bool TrySetActive(string modId, string? version, bool isActive, out string? error)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            error = "Mod ID is missing.";
            return false;
        }

        lock (_syncRoot)
        {
            var normalizedModId = modId.Trim();
            var keyWithVersion = !string.IsNullOrWhiteSpace(version)
                ? $"{normalizedModId}@{version!.Trim()}"
                : null;

            var changed = false;

            if (isActive)
            {
                if (keyWithVersion != null) changed |= RemoveDisabledEntry(keyWithVersion);

                changed |= RemoveDisabledEntry(normalizedModId);
            }
            else
            {
                var keyToAdd = keyWithVersion ?? normalizedModId;
                if (!_disabledLookup.Contains(keyToAdd))
                {
                    _disabledLookup.Add(keyToAdd);
                    _disabledMods.Add(keyToAdd);
                    changed = true;
                }
            }

            if (!changed)
            {
                error = null;
                return true;
            }

            try
            {
                Persist();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    public bool TryApplyDisabledEntries(IEnumerable<string> entries, out string? error)
    {
        lock (_syncRoot)
        {
            var sanitized = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (entries != null)
                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;

                    var trimmed = entry.Trim();
                    if (seen.Add(trimmed)) sanitized.Add(trimmed);
                }

            var changed = sanitized.Count != _disabledMods.Count
                          || !sanitized.SequenceEqual(_disabledMods, StringComparer.OrdinalIgnoreCase);

            if (!changed)
            {
                error = null;
                return true;
            }

            _disabledMods.Clear();
            _disabledLookup.Clear();

            foreach (var value in sanitized)
            {
                _disabledMods.Add(value);
                _disabledLookup.Add(value);
            }

            try
            {
                Persist();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    public bool TryUpdateDisabledEntry(string modId, string? previousVersion, string? newVersion, bool shouldDisable,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            error = "Mod ID is missing.";
            return false;
        }

        var normalizedModId = modId.Trim();
        var previousKey = ComposeVersionKey(normalizedModId, previousVersion);
        var newKey = ComposeVersionKey(normalizedModId, newVersion);

        lock (_syncRoot)
        {
            var hadGeneralEntry = _disabledLookup.Contains(normalizedModId);
            var changed = false;

            if (previousKey != null) changed |= RemoveDisabledEntry(previousKey);

            if (!shouldDisable)
            {
                changed |= RemoveDisabledEntry(normalizedModId);
                if (newKey != null) changed |= RemoveDisabledEntry(newKey);
            }
            else
            {
                if (hadGeneralEntry)
                {
                    if (!_disabledLookup.Contains(normalizedModId))
                    {
                        _disabledLookup.Add(normalizedModId);
                        _disabledMods.Add(normalizedModId);
                        changed = true;
                    }
                }
                else
                {
                    var keyToAdd = newKey ?? normalizedModId;
                    if (!_disabledLookup.Contains(keyToAdd))
                    {
                        _disabledLookup.Add(keyToAdd);
                        _disabledMods.Add(keyToAdd);
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                error = null;
                return true;
            }

            try
            {
                Persist();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    private static string? ComposeVersionKey(string modId, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        return string.Concat(modId, '@', version.Trim());
    }

    private bool RemoveDisabledEntry(string key)
    {
        if (!_disabledLookup.Remove(key)) return false;

        _disabledMods.RemoveAll(value => string.Equals(value, key, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    private JsonObject LoadOrCreateRoot()
    {
        if (!File.Exists(SettingsPath)) return CreateDefaultSettings();

        try
        {
            using var stream = File.OpenRead(SettingsPath);
            return JsonNode.Parse(stream)?.AsObject() ?? CreateDefaultSettings();
        }
        catch (JsonException)
        {
            return CreateDefaultSettings();
        }
    }

    private JsonObject CreateDefaultSettings()
    {
        return new JsonObject
        {
            ["boolSettings"] = new JsonObject(),
            ["intSettings"] = new JsonObject(),
            ["floatSettings"] = new JsonObject(),
            ["stringSettings"] = new JsonObject(),
            ["stringListSettings"] = new JsonObject
            {
                ["disabledMods"] = new JsonArray(),
                ["modPaths"] = new JsonArray
                {
                    "Mods",
                    Path.Combine(DataDirectory, "Mods")
                }
            }
        };
    }

    private static JsonObject GetOrCreateObject(JsonObject owner, string propertyName)
    {
        foreach (var pair in owner)
            if (string.Equals(pair.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                if (pair.Value is JsonObject existing) return existing;

                var created = new JsonObject();
                owner[propertyName] = created;
                return created;
            }

        var result = new JsonObject();
        owner[propertyName] = result;
        return result;
    }

    private static List<string> ExtractStringList(JsonObject container, string propertyName)
    {
        foreach (var pair in container)
            if (string.Equals(pair.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                if (pair.Value is JsonArray array)
                    return array.Select(node => node?.GetValue<string>() ?? string.Empty).ToList();

                break;
            }

        var list = new List<string>();
        container[propertyName] = new JsonArray();
        return list;
    }

    private void InitializeFromRoot(JsonObject root)
    {
        _root = root;
        _stringListSettings = GetOrCreateObject(_root, "stringListSettings");
        _stringSettings = GetOrCreateObject(_root, "stringSettings");

        _disabledMods = ExtractStringList(_stringListSettings, "disabledMods");
        _disabledLookup = new HashSet<string>(_disabledMods, StringComparer.OrdinalIgnoreCase);
        _modPaths = ExtractStringList(_stringListSettings, "modPaths");
    }

    private void EnsureDefaultModPaths()
    {
        lock (_syncRoot)
        {
            var expectedDataModsPath = Path.Combine(DataDirectory, "Mods");
            var changed = false;

            if (_modPaths.Count == 0)
            {
                _modPaths.Add("Mods");
                _modPaths.Add(expectedDataModsPath);
                changed = true;
            }
            else
            {
                if (!string.Equals(_modPaths[0], "Mods", StringComparison.OrdinalIgnoreCase))
                {
                    var existingModsIndex = _modPaths.FindIndex(path =>
                        string.Equals(path, "Mods", StringComparison.OrdinalIgnoreCase));
                    if (existingModsIndex >= 0) _modPaths.RemoveAt(existingModsIndex);

                    _modPaths.Insert(0, "Mods");
                    changed = true;
                }

                if (_modPaths.Count < 2)
                {
                    _modPaths.Insert(1, expectedDataModsPath);
                    changed = true;
                }
                else if (!PathsEqual(_modPaths[1], expectedDataModsPath))
                {
                    var existingIndex = _modPaths.FindIndex(path => PathsEqual(path, expectedDataModsPath));
                    if (existingIndex >= 0) _modPaths.RemoveAt(existingIndex);

                    _modPaths[1] = expectedDataModsPath;
                    changed = true;
                }
            }

            if (changed) Persist();
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

        var normalizedLeft = NormalizePath(left);
        var normalizedRight = NormalizePath(right);

        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path.Trim();
        }
    }

    private IEnumerable<string> BuildSearchBases()
    {
        var bases = new List<string>
        {
            DataDirectory,
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        return bases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string? TryGetStringSettingValue(string settingName)
    {
        if (string.IsNullOrWhiteSpace(settingName)) return null;

        lock (_syncRoot)
        {
            foreach (var pair in _stringSettings)
            {
                if (!string.Equals(pair.Key, settingName, StringComparison.OrdinalIgnoreCase)) continue;

                if (pair.Value is null) return null;

                string? rawValue;
                try
                {
                    rawValue = pair.Value.GetValue<string?>();
                }
                catch (InvalidOperationException)
                {
                    return null;
                }

                return string.IsNullOrWhiteSpace(rawValue) ? null : rawValue.Trim();
            }

            return null;
        }
    }

    private void Persist()
    {
        lock (_syncRoot)
        {
            _stringListSettings["disabledMods"] =
                new JsonArray(_disabledMods.Select(value => JsonValue.Create(value)).ToArray());
            _stringListSettings["modPaths"] = new JsonArray(_modPaths.Select(path => JsonValue.Create(path)).ToArray());

            var json = _root.ToJsonString(_serializerOptions);

            File.WriteAllText(_tempPath, json);
            if (File.Exists(SettingsPath))
                File.Replace(_tempPath, SettingsPath, _backupPath);
            else
                File.Move(_tempPath, SettingsPath);
        }
    }
}