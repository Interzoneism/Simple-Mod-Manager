using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VintageStoryModManager.Services;

/// <summary>
/// Stores simple user configuration values for the mod manager, such as the selected directories.
/// </summary>
public sealed class UserConfigurationService
{
    private const string ConfigurationFileName = "SimpleVSManagerConfig.json";
    private const int DefaultModDatabaseSearchResultLimit = 30;
    private const int DefaultModDatabaseNewModsRecentMonths = 3;
    private const int MaxModDatabaseNewModsRecentMonths = 24;

    private readonly string _configurationPath;
    private readonly Dictionary<string, string> _modConfigPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedPresetName;
    private bool _isCompactView;
    private bool _useModDbDesignView = true;
    private bool _cacheAllVersionsLocally;
    private bool _disableInternetAccess;
    private bool _enableDebugLogging;
    private bool _suppressModlistSavePrompt;
    private int _modDatabaseSearchResultLimit = DefaultModDatabaseSearchResultLimit;
    private int _modDatabaseNewModsRecentMonths = DefaultModDatabaseNewModsRecentMonths;
    private string? _modsSortMemberPath;
    private ListSortDirection _modsSortDirection = ListSortDirection.Ascending;
    private double? _windowWidth;
    private double? _windowHeight;

    public UserConfigurationService()
    {
        _configurationPath = DetermineConfigurationPath();
        Load();

        if (!File.Exists(_configurationPath))
        {
            Save();
        }
    }

    public string? DataDirectory { get; private set; }

    public string? GameDirectory { get; private set; }

    public bool IsCompactView => _isCompactView;

    public bool UseModDbDesignView => _useModDbDesignView;

    public bool CacheAllVersionsLocally => _cacheAllVersionsLocally;

    public bool DisableInternetAccess => _disableInternetAccess;

    public bool EnableDebugLogging => _enableDebugLogging;

    public bool SuppressModlistSavePrompt => _suppressModlistSavePrompt;

    public int ModDatabaseSearchResultLimit => _modDatabaseSearchResultLimit;

    public int ModDatabaseNewModsRecentMonths => _modDatabaseNewModsRecentMonths;

    public double? WindowWidth => _windowWidth;

    public double? WindowHeight => _windowHeight;

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

        return _modConfigPaths.TryGetValue(modId.Trim(), out path);
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

        _modConfigPaths[modId.Trim()] = normalized;
        Save();
    }

    public void RemoveModConfigPath(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            return;
        }

        if (_modConfigPaths.Remove(modId.Trim()))
        {
            Save();
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

    public void SetCacheAllVersionsLocally(bool cacheAllVersionsLocally)
    {
        if (_cacheAllVersionsLocally == cacheAllVersionsLocally)
        {
            return;
        }

        _cacheAllVersionsLocally = cacheAllVersionsLocally;
        Save();
    }

    public void SetModDatabaseSearchResultLimit(int limit)
    {
        int normalized = NormalizeModDatabaseSearchResultLimit(limit);
        if (_modDatabaseSearchResultLimit == normalized)
        {
            return;
        }

        _modDatabaseSearchResultLimit = normalized;
        Save();
    }

    public void SetModDatabaseNewModsRecentMonths(int months)
    {
        int normalized = NormalizeModDatabaseNewModsRecentMonths(months);
        if (_modDatabaseNewModsRecentMonths == normalized)
        {
            return;
        }

        _modDatabaseNewModsRecentMonths = normalized;
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

    public void SetGameDirectory(string path)
    {
        GameDirectory = NormalizePath(path);
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

            DataDirectory = NormalizePath(obj["dataDirectory"]?.GetValue<string?>());
            GameDirectory = NormalizePath(obj["gameDirectory"]?.GetValue<string?>());
            _isCompactView = obj["isCompactView"]?.GetValue<bool?>() ?? false;
            _useModDbDesignView = obj["useModDbDesignView"]?.GetValue<bool?>() ?? true;
            _cacheAllVersionsLocally = obj["cacheAllVersionsLocally"]?.GetValue<bool?>() ?? false;
            _disableInternetAccess = obj["disableInternetAccess"]?.GetValue<bool?>() ?? false;
            _enableDebugLogging = obj["enableDebugLogging"]?.GetValue<bool?>() ?? false;
            _suppressModlistSavePrompt = obj["suppressModlistSavePrompt"]?.GetValue<bool?>() ?? false;
            _modsSortMemberPath = NormalizeSortMemberPath(obj["modsSortMemberPath"]?.GetValue<string?>());
            _modsSortDirection = ParseSortDirection(obj["modsSortDirection"]?.GetValue<string?>());
            _modDatabaseSearchResultLimit = NormalizeModDatabaseSearchResultLimit(obj["modDatabaseSearchResultLimit"]?.GetValue<int?>());
            _modDatabaseNewModsRecentMonths = NormalizeModDatabaseNewModsRecentMonths(
                obj["modDatabaseNewModsRecentMonths"]?.GetValue<int?>());
            _windowWidth = NormalizeWindowDimension(obj["windowWidth"]?.GetValue<double?>());
            _windowHeight = NormalizeWindowDimension(obj["windowHeight"]?.GetValue<double?>());
            LoadModConfigPaths(obj["modConfigPaths"]);
            _selectedPresetName = NormalizePresetName(obj["selectedPreset"]?.GetValue<string?>());

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            DataDirectory = null;
            GameDirectory = null;
            _modConfigPaths.Clear();
            _isCompactView = false;
            _useModDbDesignView = true;
            _cacheAllVersionsLocally = false;
            _disableInternetAccess = false;
            _enableDebugLogging = false;
            _suppressModlistSavePrompt = false;
            _modsSortMemberPath = null;
            _modsSortDirection = ListSortDirection.Ascending;
            _selectedPresetName = null;
            _modDatabaseSearchResultLimit = DefaultModDatabaseSearchResultLimit;
            _modDatabaseNewModsRecentMonths = DefaultModDatabaseNewModsRecentMonths;
            _windowWidth = null;
            _windowHeight = null;
        }
    }

    private void Save()
    {
        try
        {
            string directory = Path.GetDirectoryName(_configurationPath)!;
            Directory.CreateDirectory(directory);

            var obj = new JsonObject
            {
                ["dataDirectory"] = DataDirectory,
                ["gameDirectory"] = GameDirectory,
                ["isCompactView"] = _isCompactView,
                ["useModDbDesignView"] = _useModDbDesignView,
                ["cacheAllVersionsLocally"] = _cacheAllVersionsLocally,
                ["disableInternetAccess"] = _disableInternetAccess,
                ["enableDebugLogging"] = _enableDebugLogging,
                ["suppressModlistSavePrompt"] = _suppressModlistSavePrompt,
                ["modsSortMemberPath"] = _modsSortMemberPath,
                ["modsSortDirection"] = _modsSortDirection.ToString(),
                ["modDatabaseSearchResultLimit"] = _modDatabaseSearchResultLimit,
                ["modDatabaseNewModsRecentMonths"] = _modDatabaseNewModsRecentMonths,
                ["windowWidth"] = _windowWidth,
                ["windowHeight"] = _windowHeight,
                ["modConfigPaths"] = BuildModConfigPathsJson(),
                ["selectedPreset"] = _selectedPresetName
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            File.WriteAllText(_configurationPath, obj.ToJsonString(options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persisting the configuration is a best-effort attempt. Ignore failures silently.
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

        return Math.Clamp(normalized, 1, 100);
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
        Directory.CreateDirectory(preferredDirectory);
        return Path.Combine(preferredDirectory, ConfigurationFileName);
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

            string? path = pair.Value?.GetValue<string?>();
            string? normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            _modConfigPaths[modId.Trim()] = normalized;
        }
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

        string? home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home!, ".simple-vs-manager");
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
}
