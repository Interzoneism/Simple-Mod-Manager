using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using VintageStoryModManager.ViewModels;

namespace VintageStoryModManager.Services;

/// <summary>
/// Stores simple user configuration values for the mod manager, such as the selected directories.
/// </summary>
public sealed class UserConfigurationService
{
    private const string ConfigurationFileName = "SimpleVSManagerConfiguration.json";
    private const string ModConfigPathsFileName = "SimpleVSManagerModConfigPaths.json";
    private static readonly string CurrentModManagerVersion = ResolveCurrentVersion();
    private static readonly string CurrentConfigurationVersion = CurrentModManagerVersion;
    private const int DefaultModDatabaseSearchResultLimit = 30;
    private const int DefaultModDatabaseNewModsRecentMonths = 3;
    private const int MaxModDatabaseNewModsRecentMonths = 24;

    private readonly string _configurationPath;
    private readonly string _modConfigPathsPath;
    private readonly Dictionary<string, string> _modConfigPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModConfigPathEntry> _storedModConfigPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedPresetName;
    private string _configurationVersion = CurrentConfigurationVersion;
    private string _modManagerVersion = CurrentModManagerVersion;
    private bool _isCompactView;
    private bool _useModDbDesignView = true;
    private ModDatabaseAutoLoadMode _modDatabaseAutoLoadMode = ModDatabaseAutoLoadMode.TotalDownloads;
    private bool _excludeInstalledModDatabaseResults;
    private bool _cacheAllVersionsLocally = true;
    private bool _disableInternetAccess;
    private bool _enableDebugLogging;
    private bool _suppressModlistSavePrompt;
    private ModlistAutoLoadBehavior _modlistAutoLoadBehavior = ModlistAutoLoadBehavior.Prompt;
    private int _modDatabaseSearchResultLimit = DefaultModDatabaseSearchResultLimit;
    private int _modDatabaseNewModsRecentMonths = DefaultModDatabaseNewModsRecentMonths;
    private string? _modsSortMemberPath;
    private ListSortDirection _modsSortDirection = ListSortDirection.Ascending;
    private double? _windowWidth;
    private double? _windowHeight;
    private string? _customShortcutPath;
    private string? _cloudUploaderName;
    private bool _isPersistenceEnabled;
    private bool _hasPendingSave;
    private bool _hasPendingModConfigPathSave;

    public UserConfigurationService()
    {
        _configurationPath = DetermineConfigurationPath();
        _modConfigPathsPath = DetermineModConfigPathsPath(ModConfigPathsFileName);
        Load();

        _hasPendingSave |= !File.Exists(_configurationPath);
        _hasPendingModConfigPathSave = false;
    }

    public string? DataDirectory { get; private set; }

    public string? GameDirectory { get; private set; }

    public string ConfigurationVersion => _configurationVersion;

    public string ModManagerVersion => _modManagerVersion;

    public bool IsCompactView => _isCompactView;

    public bool UseModDbDesignView => _useModDbDesignView;

    public bool CacheAllVersionsLocally => _cacheAllVersionsLocally;

    public bool ExcludeInstalledModDatabaseResults => _excludeInstalledModDatabaseResults;

    public bool DisableInternetAccess => _disableInternetAccess;

    public bool EnableDebugLogging => _enableDebugLogging;

    public bool SuppressModlistSavePrompt => _suppressModlistSavePrompt;

    public ModlistAutoLoadBehavior ModlistAutoLoadBehavior => _modlistAutoLoadBehavior;

    public int ModDatabaseSearchResultLimit => _modDatabaseSearchResultLimit;

    public int ModDatabaseNewModsRecentMonths => _modDatabaseNewModsRecentMonths;

    public ModDatabaseAutoLoadMode ModDatabaseAutoLoadMode => _modDatabaseAutoLoadMode;

    public double? WindowWidth => _windowWidth;

    public double? WindowHeight => _windowHeight;

    public string? CustomShortcutPath => _customShortcutPath;

    public string? CloudUploaderName => _cloudUploaderName;

    public (string? SortMemberPath, ListSortDirection Direction) GetModListSortPreference()
    {
        return (_modsSortMemberPath, _modsSortDirection);
    }

    public string GetConfigurationDirectory()
    {
        string directory = Path.GetDirectoryName(_configurationPath)
            ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);
        return directory;
    }

    public void EnablePersistence()
    {
        if (_isPersistenceEnabled)
        {
            return;
        }

        _isPersistenceEnabled = true;

        if (_hasPendingSave)
        {
            PersistConfiguration();
        }

        if (_hasPendingModConfigPathSave)
        {
            PersistModConfigPathHistory();
        }
    }

    public string? GetLastSelectedPresetName() => _selectedPresetName;

    public void SetLastSelectedPresetName(string? name)
    {
        string? normalized = NormalizePresetName(name);

        if (string.Equals(_selectedPresetName, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _selectedPresetName = normalized;

        Save();
    }

    public bool TryGetModConfigPath(string? modId, out string? path)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            path = null;
            return false;
        }

        string key = modId.Trim();
        if (_modConfigPaths.TryGetValue(key, out path))
        {
            return true;
        }

        if (_storedModConfigPaths.TryGetValue(key, out ModConfigPathEntry? entry) && entry is not null)
        {
            string? combined = BuildFullConfigPath(entry);
            if (!string.IsNullOrWhiteSpace(combined))
            {
                string combinedPath = combined!;
                _modConfigPaths[key] = combinedPath;
                path = combinedPath;
                return true;
            }
        }

        path = null;
        return false;
    }

    public void SetModConfigPath(string modId, string path)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            throw new ArgumentException("Mod ID cannot be empty.", nameof(modId));
        }

        string? normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("The configuration path is invalid.", nameof(path));
        }

        string key = modId.Trim();
        _modConfigPaths[key] = normalized;
        UpdatePersistentModConfigPath(key, normalized);
        Save();
    }

    public void RemoveModConfigPath(string? modId, bool preserveHistory = false)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            return;
        }

        string key = modId.Trim();
        bool removed = _modConfigPaths.Remove(key);
        bool historyChanged = false;

        if (!preserveHistory)
        {
            historyChanged = _storedModConfigPaths.Remove(key);
        }

        if (removed)
        {
            Save();
        }

        if (historyChanged)
        {
            SaveModConfigPathHistory();
        }
    }

    public void SetCompactViewMode(bool isCompact)
    {
        if (_isCompactView == isCompact)
        {
            return;
        }

        _isCompactView = isCompact;
        Save();
    }

    public void SetModDbDesignViewMode(bool useModDbDesignView)
    {
        if (_useModDbDesignView == useModDbDesignView)
        {
            return;
        }

        _useModDbDesignView = useModDbDesignView;
        Save();
    }

    public void SetEnableDebugLogging(bool enableDebugLogging)
    {
        if (_enableDebugLogging == enableDebugLogging)
        {
            return;
        }

        _enableDebugLogging = enableDebugLogging;
        Save();
    }

    public void SetModlistAutoLoadBehavior(ModlistAutoLoadBehavior behavior)
    {
        if (_modlistAutoLoadBehavior == behavior)
        {
            return;
        }

        _modlistAutoLoadBehavior = behavior;
        Save();
    }

    public void SetCacheAllVersionsLocally(bool cacheAllVersionsLocally)
    {
        if (_cacheAllVersionsLocally == cacheAllVersionsLocally)
        {
            return;
        }

        _cacheAllVersionsLocally = cacheAllVersionsLocally;
        Save();
    }

    public void SetModDatabaseAutoLoadMode(ModDatabaseAutoLoadMode mode)
    {
        if (_modDatabaseAutoLoadMode == mode)
        {
            return;
        }

        _modDatabaseAutoLoadMode = mode;
        Save();
    }

    public void SetModListSortPreference(string? sortMemberPath, ListSortDirection direction)
    {
        string? normalized = string.IsNullOrWhiteSpace(sortMemberPath)
            ? null
            : sortMemberPath.Trim();

        if (string.Equals(_modsSortMemberPath, normalized, StringComparison.Ordinal)
            && _modsSortDirection == direction)
        {
            return;
        }

        _modsSortMemberPath = normalized;
        _modsSortDirection = direction;
        Save();
    }

    public void SetWindowDimensions(double width, double height)
    {
        double? normalizedWidth = NormalizeWindowDimension(width);
        double? normalizedHeight = NormalizeWindowDimension(height);

        if (!normalizedWidth.HasValue || !normalizedHeight.HasValue)
        {
            return;
        }

        bool hasWidthChanged = !_windowWidth.HasValue || Math.Abs(_windowWidth.Value - normalizedWidth.Value) > 0.1;
        bool hasHeightChanged = !_windowHeight.HasValue || Math.Abs(_windowHeight.Value - normalizedHeight.Value) > 0.1;

        if (!hasWidthChanged && !hasHeightChanged)
        {
            return;
        }

        _windowWidth = normalizedWidth;
        _windowHeight = normalizedHeight;
        Save();
    }

    public void SetDataDirectory(string path)
    {
        DataDirectory = NormalizePath(path);
        Save();
    }

    public void ClearDataDirectory()
    {
        if (DataDirectory is null)
        {
            return;
        }

        DataDirectory = null;
        Save();
    }

    public void SetGameDirectory(string path)
    {
        GameDirectory = NormalizePath(path);
        Save();
    }

    public void ClearGameDirectory()
    {
        if (GameDirectory is null)
        {
            return;
        }

        GameDirectory = null;
        Save();
    }

    public void SetCustomShortcutPath(string path)
    {
        string? normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("The shortcut path is invalid.", nameof(path));
        }

        if (string.Equals(_customShortcutPath, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _customShortcutPath = normalized;
        Save();
    }

    public void ClearCustomShortcutPath()
    {
        if (_customShortcutPath is null)
        {
            return;
        }

        _customShortcutPath = null;
        Save();
    }

    public void SetDisableInternetAccess(bool disable)
    {
        if (_disableInternetAccess == disable)
        {
            return;
        }

        _disableInternetAccess = disable;
        Save();
    }

    public void SetSuppressModlistSavePrompt(bool suppress)
    {
        if (_suppressModlistSavePrompt == suppress)
        {
            return;
        }

        _suppressModlistSavePrompt = suppress;
        Save();
    }

    public void SetCloudUploaderName(string? name)
    {
        string? normalized = NormalizeUploaderName(name);

        if (string.Equals(_cloudUploaderName, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _cloudUploaderName = normalized;
        Save();
    }

    public void SetExcludeInstalledModDatabaseResults(bool exclude)
    {
        if (_excludeInstalledModDatabaseResults == exclude)
        {
            return;
        }

        _excludeInstalledModDatabaseResults = exclude;
        Save();
    }

    private void Load()
    {
        _modConfigPaths.Clear();
        _selectedPresetName = null;

        try
        {
            if (!File.Exists(_configurationPath))
            {
                return;
            }

            using FileStream stream = File.OpenRead(_configurationPath);
            JsonNode? node = JsonNode.Parse(stream);
            if (node is not JsonObject obj)
            {
                return;
            }

            string? originalConfigurationVersion = NormalizeVersion(GetOptionalString(obj["configurationVersion"]));
            string? originalModManagerVersion = NormalizeVersion(GetOptionalString(obj["modManagerVersion"]));
            InitializeVersionMetadata(originalConfigurationVersion, originalModManagerVersion);

            DataDirectory = NormalizePath(GetOptionalString(obj["dataDirectory"]));
            GameDirectory = NormalizePath(GetOptionalString(obj["gameDirectory"]));
            _isCompactView = obj["isCompactView"]?.GetValue<bool?>() ?? false;
            _useModDbDesignView = obj["useModDbDesignView"]?.GetValue<bool?>() ?? true;
            _cacheAllVersionsLocally = obj["cacheAllVersionsLocally"]?.GetValue<bool?>() ?? true;
            _disableInternetAccess = obj["disableInternetAccess"]?.GetValue<bool?>() ?? false;
            _enableDebugLogging = obj["enableDebugLogging"]?.GetValue<bool?>() ?? false;
            _suppressModlistSavePrompt = obj["suppressModlistSavePrompt"]?.GetValue<bool?>() ?? false;
            _modlistAutoLoadBehavior = ParseModlistAutoLoadBehavior(GetOptionalString(obj["modlistAutoLoadBehavior"]));
            _modsSortMemberPath = NormalizeSortMemberPath(GetOptionalString(obj["modsSortMemberPath"]));
            _modsSortDirection = ParseSortDirection(GetOptionalString(obj["modsSortDirection"]));
            _modDatabaseSearchResultLimit = NormalizeModDatabaseSearchResultLimit(obj["modDatabaseSearchResultLimit"]?.GetValue<int?>());
            _modDatabaseNewModsRecentMonths = NormalizeModDatabaseNewModsRecentMonths(
                obj["modDatabaseNewModsRecentMonths"]?.GetValue<int?>());
            _modDatabaseAutoLoadMode = ParseModDatabaseAutoLoadMode(GetOptionalString(obj["modDatabaseAutoLoadMode"]));
            _excludeInstalledModDatabaseResults = obj["excludeInstalledModDatabaseResults"]?.GetValue<bool?>() ?? false;
            _windowWidth = NormalizeWindowDimension(obj["windowWidth"]?.GetValue<double?>());
            _windowHeight = NormalizeWindowDimension(obj["windowHeight"]?.GetValue<double?>());
            LoadModConfigPaths(obj["modConfigPaths"]);
            _selectedPresetName = NormalizePresetName(GetOptionalString(obj["selectedPreset"]));
            _customShortcutPath = NormalizePath(GetOptionalString(obj["customShortcutPath"]));
            _cloudUploaderName = NormalizeUploaderName(GetOptionalString(obj["cloudUploaderName"]));

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            DataDirectory = null;
            GameDirectory = null;
            _modConfigPaths.Clear();
            _configurationVersion = CurrentConfigurationVersion;
            _modManagerVersion = CurrentModManagerVersion;
            _isCompactView = false;
            _useModDbDesignView = true;
            _cacheAllVersionsLocally = true;
            _disableInternetAccess = false;
            _enableDebugLogging = false;
            _suppressModlistSavePrompt = false;
            _modlistAutoLoadBehavior = ModlistAutoLoadBehavior.Prompt;
            _modsSortMemberPath = null;
            _modsSortDirection = ListSortDirection.Ascending;
            _selectedPresetName = null;
            _modDatabaseSearchResultLimit = DefaultModDatabaseSearchResultLimit;
            _modDatabaseNewModsRecentMonths = DefaultModDatabaseNewModsRecentMonths;
            _modDatabaseAutoLoadMode = ModDatabaseAutoLoadMode.TotalDownloads;
            _excludeInstalledModDatabaseResults = false;
            _windowWidth = null;
            _windowHeight = null;
            _customShortcutPath = null;
            _cloudUploaderName = null;
        }

        LoadPersistentModConfigPaths();
    }

    private void Save()
    {
        _hasPendingSave = true;

        if (!_isPersistenceEnabled)
        {
            return;
        }

        PersistConfiguration();
    }

    private void PersistConfiguration()
    {
        try
        {
            string directory = Path.GetDirectoryName(_configurationPath)!;
            Directory.CreateDirectory(directory);

            var obj = new JsonObject
            {
                ["configurationVersion"] = _configurationVersion,
                ["modManagerVersion"] = _modManagerVersion,
                ["dataDirectory"] = DataDirectory,
                ["gameDirectory"] = GameDirectory,
                ["isCompactView"] = _isCompactView,
                ["useModDbDesignView"] = _useModDbDesignView,
                ["cacheAllVersionsLocally"] = _cacheAllVersionsLocally,
                ["disableInternetAccess"] = _disableInternetAccess,
                ["enableDebugLogging"] = _enableDebugLogging,
                ["suppressModlistSavePrompt"] = _suppressModlistSavePrompt,
                ["modlistAutoLoadBehavior"] = _modlistAutoLoadBehavior.ToString(),
                ["modsSortMemberPath"] = _modsSortMemberPath,
                ["modsSortDirection"] = _modsSortDirection.ToString(),
                ["modDatabaseSearchResultLimit"] = _modDatabaseSearchResultLimit,
                ["modDatabaseNewModsRecentMonths"] = _modDatabaseNewModsRecentMonths,
                ["modDatabaseAutoLoadMode"] = _modDatabaseAutoLoadMode.ToString(),
                ["excludeInstalledModDatabaseResults"] = _excludeInstalledModDatabaseResults,
                ["windowWidth"] = _windowWidth,
                ["windowHeight"] = _windowHeight,
                ["modConfigPaths"] = BuildModConfigPathsJson(),
                ["selectedPreset"] = _selectedPresetName,
                ["customShortcutPath"] = _customShortcutPath,
                ["cloudUploaderName"] = _cloudUploaderName
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            File.WriteAllText(_configurationPath, obj.ToJsonString(options));
            _hasPendingSave = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persisting the configuration is a best-effort attempt. Ignore failures silently.
        }
    }

    private void InitializeVersionMetadata(string? configurationVersion, string? modManagerVersion)
    {
        bool requiresSave = false;

        string resolvedConfigurationVersion = string.IsNullOrWhiteSpace(configurationVersion)
            ? CurrentConfigurationVersion
            : configurationVersion!;

        string resolvedModManagerVersion = string.IsNullOrWhiteSpace(modManagerVersion)
            ? CurrentModManagerVersion
            : modManagerVersion!;

        if (!string.Equals(resolvedConfigurationVersion, CurrentConfigurationVersion, StringComparison.OrdinalIgnoreCase))
        {
            resolvedConfigurationVersion = CurrentConfigurationVersion;
            requiresSave = true;
        }

        if (!string.Equals(resolvedModManagerVersion, CurrentModManagerVersion, StringComparison.OrdinalIgnoreCase))
        {
            resolvedModManagerVersion = CurrentModManagerVersion;
            requiresSave = true;
        }

        _configurationVersion = resolvedConfigurationVersion;
        _modManagerVersion = resolvedModManagerVersion;

        if (requiresSave)
        {
            _hasPendingSave = true;
        }
    }

    private static int NormalizeModDatabaseSearchResultLimit(int? value)
    {
        if (!value.HasValue)
        {
            return DefaultModDatabaseSearchResultLimit;
        }

        int normalized = value.Value;
        if (normalized <= 0)
        {
            return DefaultModDatabaseSearchResultLimit;
        }

        return Math.Max(normalized, 1);
    }

    private static int NormalizeModDatabaseNewModsRecentMonths(int? value)
    {
        if (!value.HasValue)
        {
            return DefaultModDatabaseNewModsRecentMonths;
        }

        int normalized = value.Value;
        if (normalized <= 0)
        {
            return DefaultModDatabaseNewModsRecentMonths;
        }

        return Math.Clamp(normalized, 1, MaxModDatabaseNewModsRecentMonths);
    }

    private static ModlistAutoLoadBehavior ParseModlistAutoLoadBehavior(string? value)
    {
        if (Enum.TryParse(value, ignoreCase: true, out ModlistAutoLoadBehavior behavior))
        {
            return behavior;
        }

        return ModlistAutoLoadBehavior.Prompt;
    }

    private static ModDatabaseAutoLoadMode ParseModDatabaseAutoLoadMode(string? value)
    {
        if (Enum.TryParse(value, ignoreCase: true, out ModDatabaseAutoLoadMode mode))
        {
            return mode;
        }

        return ModDatabaseAutoLoadMode.TotalDownloads;
    }

    private static double? NormalizeWindowDimension(double? dimension)
    {
        if (!dimension.HasValue)
        {
            return null;
        }

        double value = dimension.Value;
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return null;
        }

        return value;
    }

    private static string DetermineConfigurationPath()
    {
        string preferredDirectory = GetPreferredConfigurationDirectory();
        return Path.Combine(preferredDirectory, ConfigurationFileName);
    }

    private static string DetermineModConfigPathsPath(string fileName)
    {
        string preferredDirectory = GetPreferredConfigurationDirectory();
        return Path.Combine(preferredDirectory, fileName);
    }

    private JsonObject BuildModConfigPathsJson()
    {
        var result = new JsonObject();

        foreach (var pair in _modConfigPaths.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private void LoadModConfigPaths(JsonNode? node)
    {
        _modConfigPaths.Clear();

        if (node is not JsonObject obj)
        {
            return;
        }

        foreach (var pair in obj)
        {
            string modId = pair.Key;
            if (string.IsNullOrWhiteSpace(modId))
            {
                continue;
            }

            string? path = GetOptionalString(pair.Value);
            string? normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            _modConfigPaths[modId.Trim()] = normalized;
        }
    }

    private void LoadPersistentModConfigPaths()
    {
        _storedModConfigPaths.Clear();

        try
        {
            if (!File.Exists(_modConfigPathsPath))
            {
                return;
            }

            using (FileStream stream = File.OpenRead(_modConfigPathsPath))
            {
                JsonNode? node = JsonNode.Parse(stream);
                if (node is not JsonObject obj)
                {
                    return;
                }

                foreach (var pair in obj)
                {
                    string modId = pair.Key;
                    if (string.IsNullOrWhiteSpace(modId) || pair.Value is not JsonObject entryObj)
                    {
                        continue;
                    }

                    string? directoryPath = NormalizePath(GetOptionalString(entryObj["directoryPath"]));
                    string? fileName = GetOptionalString(entryObj["fileName"])?.Trim();

                    if (string.IsNullOrWhiteSpace(directoryPath) || string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    var entry = new ModConfigPathEntry(directoryPath, fileName);
                    string key = modId.Trim();
                    _storedModConfigPaths[key] = entry;

                    string? combinedPath = BuildFullConfigPath(entry);
                    if (!string.IsNullOrWhiteSpace(combinedPath))
                    {
                        _modConfigPaths[key] = combinedPath;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            _storedModConfigPaths.Clear();
        }
    }

    private void UpdatePersistentModConfigPath(string modId, string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        string? directoryPath = ExtractDirectoryPath(normalizedPath);
        string? fileName = ExtractFileName(normalizedPath);

        if (string.IsNullOrWhiteSpace(directoryPath) || string.IsNullOrWhiteSpace(fileName))
        {
            if (_storedModConfigPaths.Remove(modId))
            {
                SaveModConfigPathHistory();
            }

            return;
        }

        string directory = directoryPath!;
        string file = fileName!;
        var entry = new ModConfigPathEntry(directory, file);

        if (_storedModConfigPaths.TryGetValue(modId, out ModConfigPathEntry? existing)
            && existing is not null
            && string.Equals(existing.DirectoryPath, entry.DirectoryPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.FileName, entry.FileName, StringComparison.Ordinal))
        {
            return;
        }

        _storedModConfigPaths[modId] = entry;
        SaveModConfigPathHistory();
    }

    private void SaveModConfigPathHistory()
    {
        _hasPendingModConfigPathSave = true;

        if (!_isPersistenceEnabled)
        {
            return;
        }

        PersistModConfigPathHistory();
    }

    private void PersistModConfigPathHistory()
    {
        try
        {
            string directory = Path.GetDirectoryName(_modConfigPathsPath)!;
            Directory.CreateDirectory(directory);

            var root = new JsonObject();

            foreach (var pair in _storedModConfigPaths.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                var entry = pair.Value;
                root[pair.Key] = new JsonObject
                {
                    ["directoryPath"] = entry.DirectoryPath,
                    ["fileName"] = entry.FileName
                };
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            File.WriteAllText(_modConfigPathsPath, root.ToJsonString(options));
            _hasPendingModConfigPathSave = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Persisting the configuration is a best-effort attempt. Ignore failures silently.
        }
    }

    private static string? BuildFullConfigPath(ModConfigPathEntry entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.DirectoryPath) || string.IsNullOrWhiteSpace(entry.FileName))
        {
            return null;
        }

        string combined = Path.Combine(entry.DirectoryPath, entry.FileName);
        return NormalizePath(combined) ?? combined;
    }

    private static string? ExtractDirectoryPath(string normalizedPath)
    {
        string? directory = Path.GetDirectoryName(normalizedPath);

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.GetPathRoot(normalizedPath);
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        return NormalizePath(directory);
    }

    private static string? ExtractFileName(string normalizedPath)
    {
        string trimmed = normalizedPath.Trim();
        string fileName = Path.GetFileName(trimmed);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = Path.GetFileName(trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        return fileName;
    }

    private sealed class ModConfigPathEntry
    {
        public ModConfigPathEntry(string directoryPath, string fileName)
        {
            DirectoryPath = directoryPath;
            FileName = fileName;
        }

        public string DirectoryPath { get; }

        public string FileName { get; }
    }

    private static string GetPreferredConfigurationDirectory()
    {
        string? documents = GetFolder(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents))
        {
            return Path.Combine(documents!, "Simple VS Manager");
        }

        string? personal = GetFolder(Environment.SpecialFolder.Personal);
        if (!string.IsNullOrWhiteSpace(personal))
        {
            return Path.Combine(personal!, "Simple VS Manager");
        }

        string? appData = GetFolder(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            return Path.Combine(appData!, "Simple VS Manager");
        }

        string? userProfile = GetFolder(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return Path.Combine(userProfile!, ".simple-vs-manager");
        }

        return Path.Combine(AppContext.BaseDirectory, "Simple VS Manager");
    }

    private static string? GetFolder(Environment.SpecialFolder folder)
    {
        try
        {
            string? path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }

        return null;
    }

    private static string? GetOptionalString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string?>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string ResolveCurrentVersion()
    {
        Assembly assembly = typeof(UserConfigurationService).Assembly;

        string? informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion!;
        }

        string? fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(fileVersion))
        {
            return fileVersion!;
        }

        Version? version = assembly.GetName().Version;
        return version?.ToString() ?? "0.0.0";
    }

    private static string? NormalizeVersion(string? version)
    {
        return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? NormalizeSortMemberPath(string? sortMemberPath)
    {
        if (string.IsNullOrWhiteSpace(sortMemberPath))
        {
            return null;
        }

        return sortMemberPath.Trim();
    }

    private static ListSortDirection ParseSortDirection(string? value)
    {
        if (Enum.TryParse(value, ignoreCase: true, out ListSortDirection direction))
        {
            return direction;
        }

        return ListSortDirection.Ascending;
    }

    private static string? NormalizePresetName(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private static string? NormalizeUploaderName(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }
}
