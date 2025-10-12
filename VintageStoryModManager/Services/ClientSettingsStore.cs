
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VintageStoryModManager.Services;

/// <summary>
/// Provides read/write access to the Vintage Story clientsettings.json file.
/// </summary>
public sealed class ClientSettingsStore
{
    private readonly string _settingsPath;
    private readonly string _tempPath;
    private readonly string _backupPath;
    private readonly JsonObject _root;
    private readonly JsonObject _stringListSettings;
    private readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };
    private readonly List<string> _disabledMods;
    private readonly HashSet<string> _disabledLookup;
    private readonly List<string> _modPaths;
    private readonly IReadOnlyList<string> _searchBases;
    private readonly object _syncRoot = new();

    public ClientSettingsStore(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(dataDirectory));
        }

        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);

        _settingsPath = Path.Combine(DataDirectory, "clientsettings.json");
        _tempPath = Path.Combine(DataDirectory, "clientsettings.tmp");
        _backupPath = Path.Combine(DataDirectory, "clientsettings.bkp");

        _root = LoadOrCreateRoot();
        _stringListSettings = GetOrCreateObject(_root, "stringListSettings");
        _disabledMods = ExtractStringList(_stringListSettings, "disabledMods");
        _disabledLookup = new HashSet<string>(_disabledMods, StringComparer.OrdinalIgnoreCase);
        _modPaths = ExtractStringList(_stringListSettings, "modPaths");

        if (_modPaths.Count == 0)
        {
            _modPaths.Add("Mods");
            _modPaths.Add(Path.Combine(DataDirectory, "Mods"));
            Persist();
        }

        _searchBases = BuildSearchBases().ToList();
    }

    public string DataDirectory { get; }

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

    public IReadOnlyList<string> GetDisabledEntriesSnapshot()
    {
        lock (_syncRoot)
        {
            return _disabledMods.ToArray();
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

    /// <summary>
    /// Directories that are used as bases for resolving relative mod paths.
    /// </summary>
    public IReadOnlyList<string> SearchBaseCandidates => _searchBases;

    public bool IsDisabled(string modId, string? version)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            return false;
        }

        lock (_syncRoot)
        {
            if (_disabledLookup.Contains(modId))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(version))
            {
                return _disabledLookup.Contains($"{modId}@{version}");
            }
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

            bool changed = false;

            if (isActive)
            {
                if (keyWithVersion != null)
                {
                    changed |= RemoveDisabledEntry(keyWithVersion);
                }

                changed |= RemoveDisabledEntry(normalizedModId);
            }
            else
            {
                string keyToAdd = keyWithVersion ?? normalizedModId;
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
            {
                foreach (string entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry))
                    {
                        continue;
                    }

                    string trimmed = entry.Trim();
                    if (seen.Add(trimmed))
                    {
                        sanitized.Add(trimmed);
                    }
                }
            }

            bool changed = sanitized.Count != _disabledMods.Count
                || !sanitized.SequenceEqual(_disabledMods, StringComparer.OrdinalIgnoreCase);

            if (!changed)
            {
                error = null;
                return true;
            }

            _disabledMods.Clear();
            _disabledLookup.Clear();

            foreach (string value in sanitized)
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

    public bool TryUpdateDisabledEntry(string modId, string? previousVersion, string? newVersion, bool shouldDisable, out string? error)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            error = "Mod ID is missing.";
            return false;
        }

        string normalizedModId = modId.Trim();
        string? previousKey = ComposeVersionKey(normalizedModId, previousVersion);
        string? newKey = ComposeVersionKey(normalizedModId, newVersion);

        lock (_syncRoot)
        {
            bool hadGeneralEntry = _disabledLookup.Contains(normalizedModId);
            bool changed = false;

            if (previousKey != null)
            {
                changed |= RemoveDisabledEntry(previousKey);
            }

            if (!shouldDisable)
            {
                changed |= RemoveDisabledEntry(normalizedModId);
                if (newKey != null)
                {
                    changed |= RemoveDisabledEntry(newKey);
                }
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
                    string keyToAdd = newKey ?? normalizedModId;
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
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return string.Concat(modId, '@', version.Trim());
    }

    private bool RemoveDisabledEntry(string key)
    {
        if (!_disabledLookup.Remove(key))
        {
            return false;
        }

        _disabledMods.RemoveAll(value => string.Equals(value, key, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    private JsonObject LoadOrCreateRoot()
    {
        if (!File.Exists(_settingsPath))
        {
            return CreateDefaultSettings();
        }

        try
        {
            using FileStream stream = File.OpenRead(_settingsPath);
            return (JsonNode.Parse(stream)?.AsObject()) ?? CreateDefaultSettings();
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
        {
            if (string.Equals(pair.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                if (pair.Value is JsonObject existing)
                {
                    return existing;
                }

                var created = new JsonObject();
                owner[propertyName] = created;
                return created;
            }
        }

        var result = new JsonObject();
        owner[propertyName] = result;
        return result;
    }

    private static List<string> ExtractStringList(JsonObject container, string propertyName)
    {
        foreach (var pair in container)
        {
            if (string.Equals(pair.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                if (pair.Value is JsonArray array)
                {
                    return array.Select(node => node?.GetValue<string>() ?? string.Empty).ToList();
                }

                break;
            }
        }

        var list = new List<string>();
        container[propertyName] = new JsonArray();
        return list;
    }

    private IEnumerable<string> BuildSearchBases()
    {
        var bases = new List<string>
        {
            DataDirectory,
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        string? envPath = Environment.GetEnvironmentVariable("VINTAGE_STORY");
        if (!string.IsNullOrWhiteSpace(envPath) && Directory.Exists(envPath))
        {
            bases.Add(Path.GetFullPath(envPath));
        }

        string? vsFolder = Environment.GetEnvironmentVariable("VSFOLDER");
        if (!string.IsNullOrWhiteSpace(vsFolder) && Directory.Exists(vsFolder))
        {
            bases.Add(Path.GetFullPath(vsFolder));
        }

        string? binaryHint = Environment.GetEnvironmentVariable("VINTAGE_STORY_BIN");
        if (!string.IsNullOrWhiteSpace(binaryHint) && Directory.Exists(binaryHint))
        {
            bases.Add(Path.GetFullPath(binaryHint));
        }

        return bases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void Persist()
    {
        lock (_syncRoot)
        {
            _stringListSettings["disabledMods"] = new JsonArray(_disabledMods.Select(value => JsonValue.Create(value)).ToArray());
            _stringListSettings["modPaths"] = new JsonArray(_modPaths.Select(path => JsonValue.Create(path)).ToArray());

            var json = _root.ToJsonString(_serializerOptions);

            File.WriteAllText(_tempPath, json);
            if (File.Exists(_settingsPath))
            {
                File.Replace(_tempPath, _settingsPath, _backupPath);
            }
            else
            {
                File.Move(_tempPath, _settingsPath);
            }
        }
    }
}
