using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using VintageStoryModManager.ViewModels;

namespace VintageStoryModManager.Services;

/// <summary>
///     Stores simple user configuration values for the mod manager, such as the selected directories.
/// </summary>
public sealed class UserConfigurationService
{
    private const string ModConfigDirectoryName = "ModConfig";
    private const string ModConfigPathHistoryVersionPropertyName = "version";
    private const string ModConfigPathHistoryEntriesPropertyName = "modConfigPaths";
    private const string ModConfigPathHistoryDirectoryPropertyName = "directoryPath";
    private const string ModConfigPathHistoryFileNamePropertyName = "fileName";
    private const string ModConfigPathHistoryConfigNamePropertyName = "configName";
    private const string DefaultGameProfileName = "Default";
    public const string DefaultProfileName = DefaultGameProfileName;
    private static readonly string ConfigurationFileName = DevConfig.ConfigurationFileName;
    private static readonly string ModConfigPathsFileName = DevConfig.ModConfigPathsFileName;

    private static readonly char[] DirectorySeparators =
    {
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
    };

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static readonly string CurrentModManagerVersion = ResolveCurrentVersion();
    private static readonly string CurrentConfigurationVersion = CurrentModManagerVersion;

    private static readonly IReadOnlyDictionary<string, string> VintageStoryPaletteColors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Palette.BaseSurface.Shadowed"] = "#FF403529",
            ["Palette.BaseSurface.Raised"] = "#FF4D3D2D",
            ["Palette.BaseSurface.HoverGlow"] = "#FF5A4530",
            ["Palette.Interactive.Surface"] = "#FF453525",
            ["Palette.Accent.Primary"] = "#FF479BBE",
            ["Palette.Interactive.DisabledSurface"] = "#FF332A21",
            ["Palette.Text.Primary"] = "#FFC8BCAE",
            ["Palette.Text.Link"] = "#FF479BBE",
            ["Palette.Bevel.Highlight"] = "#80FFFFFF",
            ["Palette.Bevel.Shadow"] = "#40000000",
            ["Palette.Overlay.HoverTint"] = "#10FFFFFF"
        };

    private static readonly IReadOnlyDictionary<string, string> DarkPaletteColors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Palette.Accent.Primary"] = "#FF0078D4",
            ["Palette.BaseSurface.Shadowed"] = "#FF202020",
            ["Palette.BaseSurface.HoverGlow"] = "#FF323232",
            ["Palette.BaseSurface.Raised"] = "#FF2B2B2B",
            ["Palette.Bevel.Shadow"] = "#26000000",
            ["Palette.Bevel.Highlight"] = "#21FFFFFF",
            ["Palette.Interactive.DisabledSurface"] = "#FF2A2A2A",
            ["Palette.Interactive.Surface"] = "#FF2E2E2E",
            ["Palette.Overlay.HoverTint"] = "#14FFFFFF",
            ["Palette.Text.Link"] = "#FF0F6CBD",
            ["Palette.Text.Primary"] = "#FFEDEDED"
        };

    private static readonly IReadOnlyDictionary<string, string> LightPaletteColors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Palette.Accent.Primary"] = "#FF0078D4",
            ["Palette.BaseSurface.HoverGlow"] = "#FFE0EAF5",
            ["Palette.BaseSurface.Raised"] = "#FFFFFFFF",
            ["Palette.BaseSurface.Shadowed"] = "#FFD0DBE5",
            ["Palette.Bevel.Highlight"] = "#80FFFFFF",
            ["Palette.Bevel.Shadow"] = "#66000000",
            ["Palette.Interactive.DisabledSurface"] = "#FFBAC5D0",
            ["Palette.Interactive.Surface"] = "#FFE5F0FA",
            ["Palette.Overlay.HoverTint"] = "#20000000",
            ["Palette.Text.Link"] = "#FF0078D4",
            ["Palette.Text.Primary"] = "#FF000000"
        };

    private static readonly int DefaultModDatabaseSearchResultLimit = DevConfig.DefaultModDatabaseSearchResultLimit;
    private static readonly int DefaultModDatabaseNewModsRecentMonths = DevConfig.DefaultModDatabaseNewModsRecentMonths;
    private static readonly int MaxModDatabaseNewModsRecentMonths = DevConfig.MaxModDatabaseNewModsRecentMonths;
    private static readonly int GameSessionVoteThreshold = DevConfig.GameSessionVoteThreshold;

    private static readonly string[] RedundantRootProfilePropertyNames =
    {
        "dataDirectory",
        "gameDirectory",
        "bulkUpdateModExclusions",
        "skippedModVersions",
        "modConfigPaths",
        "modUsageTracking",
        "customShortcutPath"
    };

    private readonly string _configurationPath;
    private readonly Dictionary<string, string> _customThemePaletteColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GameProfileState> _gameProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _installedColumnVisibility = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _modConfigPathsPath;

    private readonly Dictionary<string, ModConfigPathEntry> _storedModConfigPaths =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _themePaletteColors = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasPendingModConfigPathSave;
    private bool _hasPendingSave;
    private bool _isModUsageTrackingDisabled;
    private bool _isPersistenceEnabled;
    private double? _modInfoPanelLeft;
    private double? _modInfoPanelTop;
    private ListSortDirection _modsSortDirection = ListSortDirection.Ascending;
    private string? _modsSortMemberPath;
    private string? _selectedPresetName;
    private bool _suppressRefreshCachePrompt;
    private string? _suppressRefreshCachePromptVersion;
    private double? _windowHeight;
    private double? _windowWidth;

    public UserConfigurationService()
    {
        _configurationPath = DetermineConfigurationPath();
        _modConfigPathsPath = DetermineModConfigPathsPath(ModConfigPathsFileName);
        Load();

        _hasPendingSave |= !File.Exists(_configurationPath);
        _hasPendingModConfigPathSave = false;
    }

    public string? DataDirectory => ActiveProfile.DataDirectory;

    public string? GameDirectory => ActiveProfile.GameDirectory;

    public bool RequiresDataDirectorySelection => ActiveProfile.RequiresDataDirectorySelection;

    public bool RequiresGameDirectorySelection => ActiveProfile.RequiresGameDirectorySelection;

    public string ConfigurationVersion { get; private set; } = CurrentConfigurationVersion;

    public string ModManagerVersion { get; private set; } = CurrentModManagerVersion;

    public bool HasVersionMismatch { get; private set; }

    public string? PreviousConfigurationVersion { get; private set; }

    public string? PreviousModManagerVersion { get; private set; }

    public bool IsCompactView { get; private set; }

    public bool UseModDbDesignView { get; private set; } = true;

    public bool CacheAllVersionsLocally { get; private set; } = true;

    public ColorTheme ColorTheme { get; private set; } = ColorTheme.VintageStory;

    public bool ExcludeInstalledModDatabaseResults { get; private set; }

    public bool OnlyShowCompatibleModDatabaseResults { get; private set; }

    public bool DisableAutoRefresh { get; private set; }

    public bool DisableAutoRefreshWarningAcknowledged { get; private set; }

    public bool DisableInternetAccess { get; private set; }

    public bool EnableServerOptions { get; private set; }

    public bool AutomaticDataBackupsEnabled { get; private set; }

    public bool AutomaticDataBackupsWarningAcknowledged { get; private set; }

    public bool SuppressModlistSavePrompt { get; private set; }

    public bool SuppressRefreshCachePrompt
    {
        get
        {
            if (!_suppressRefreshCachePrompt) return false;

            if (_suppressRefreshCachePromptVersion is null) return true;

            return string.Equals(
                _suppressRefreshCachePromptVersion,
                ModManagerVersion,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool GameProfileCreationWarningAcknowledged { get; private set; }

    public ModlistAutoLoadBehavior ModlistAutoLoadBehavior { get; private set; } = ModlistAutoLoadBehavior.Prompt;

    public int ModDatabaseSearchResultLimit { get; private set; } = DefaultModDatabaseSearchResultLimit;

    public int ModDatabaseNewModsRecentMonths { get; private set; } = DefaultModDatabaseNewModsRecentMonths;

    public ModDatabaseAutoLoadMode ModDatabaseAutoLoadMode { get; private set; } =
        ModDatabaseAutoLoadMode.TotalDownloads;

    public double? WindowWidth => _windowWidth;

    public double? WindowHeight => _windowHeight;

    public double? ModInfoPanelLeft => _modInfoPanelLeft;

    public double? ModInfoPanelTop => _modInfoPanelTop;

    public string? CustomShortcutPath => ActiveProfile.CustomShortcutPath;

    public string? CloudUploaderName { get; private set; }

    public bool HasPendingModUsagePrompt => !_isModUsageTrackingDisabled
                                            && ActiveProfile.HasPendingModUsagePrompt
                                            && ActiveProfile.ModUsageSessionCounts.Count > 0;

    public bool IsModUsageTrackingEnabled => !_isModUsageTrackingDisabled;

    public string ActiveGameProfileName => ActiveProfile.Name;

    private GameProfileState ActiveProfile { get; set; } = new(DefaultGameProfileName);

    private Dictionary<string, string> ActiveModConfigPaths => ActiveProfile.ModConfigPaths;

    private Dictionary<string, bool> ActiveBulkUpdateModExclusions => ActiveProfile.BulkUpdateModExclusions;

    private Dictionary<string, string> ActiveSkippedModVersions => ActiveProfile.SkippedModVersions;

    private Dictionary<ModUsageTrackingKey, int> ActiveModUsageSessionCounts => ActiveProfile.ModUsageSessionCounts;

    private int ActiveLongRunningSessionCount
    {
        get => ActiveProfile.LongRunningSessionCount;
        set => ActiveProfile.LongRunningSessionCount = value;
    }

    private bool ActiveHasPendingModUsagePrompt
    {
        get => ActiveProfile.HasPendingModUsagePrompt;
        set => ActiveProfile.HasPendingModUsagePrompt = value;
    }

    public bool MigrationCheckCompleted { get; private set; }

    public bool RequireExactVsVersionMatch { get; private set; }

    public bool FirebaseAuthBackupCreated { get; private set; }

    public bool ClientSettingsCleanupCompleted { get; private set; }

    public bool RebuiltModlistMigrationCompleted { get; private set; }

    public IReadOnlyList<string> GetGameProfileNames()
    {
        return _gameProfiles.Keys
            .OrderBy(name => string.Equals(name, DefaultGameProfileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool IsDefaultGameProfile(string? name)
    {
        var normalized = NormalizeGameProfileName(name);
        return normalized is not null
               && string.Equals(normalized, DefaultGameProfileName, StringComparison.OrdinalIgnoreCase);
    }

    public bool TryCreateGameProfile(string? name, out string? normalizedName, out string? errorMessage)
    {
        normalizedName = NormalizeGameProfileName(name);

        if (normalizedName is null)
        {
            errorMessage = "Enter a profile name.";
            return false;
        }

        if (string.Equals(normalizedName, DefaultGameProfileName, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "The Default profile already exists.";
            return false;
        }

        if (_gameProfiles.ContainsKey(normalizedName))
        {
            errorMessage = "A profile with that name already exists.";
            return false;
        }

        GameProfileState profile = new(normalizedName);
        profile.RequiresDataDirectorySelection = true;
        profile.RequiresGameDirectorySelection = true;
        profile.DataDirectory = null;
        profile.GameDirectory = null;
        profile.CustomShortcutPath = null;
        _gameProfiles[normalizedName] = profile;

        Save();
        errorMessage = null;
        return true;
    }

    public bool TrySetActiveGameProfile(string? name)
    {
        var normalized = NormalizeGameProfileName(name);
        if (normalized is null) return false;

        if (!_gameProfiles.TryGetValue(normalized, out var profile)) return false;

        if (ReferenceEquals(ActiveProfile, profile)) return true;

        ActiveProfile = profile;
        RefreshActiveModConfigPathsFromHistory();
        Save();
        return true;
    }

    public bool TryDeleteGameProfiles(
        IReadOnlyCollection<string> profileNames,
        out string? errorMessage,
        out bool activeProfileChanged)
    {
        activeProfileChanged = false;

        if (profileNames is null || profileNames.Count == 0)
        {
            errorMessage = "Select at least one profile to delete.";
            return false;
        }

        var normalizedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profileName in profileNames)
        {
            if (profileName is null) continue;

            var normalized = NormalizeGameProfileName(profileName);
            if (normalized is null) continue;

            if (string.Equals(normalized, DefaultGameProfileName, StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "The Default profile cannot be deleted.";
                return false;
            }

            normalizedNames.Add(normalized);
        }

        if (normalizedNames.Count == 0)
        {
            errorMessage = "Select at least one profile to delete.";
            return false;
        }

        var removedAny = false;
        var removedProfiles = new List<string>();

        foreach (var name in normalizedNames)
            if (_gameProfiles.Remove(name))
            {
                removedAny = true;
                removedProfiles.Add(name);
            }

        if (!removedAny)
        {
            errorMessage = "No matching game profiles were found.";
            return false;
        }

        if (normalizedNames.Contains(ActiveProfile.Name))
        {
            var defaultProfile = EnsureProfile(DefaultGameProfileName);
            ActiveProfile = defaultProfile;
            activeProfileChanged = true;
        }

        Save();
        DeleteProfileBackupDirectories(removedProfiles);
        errorMessage = null;
        return true;
    }

    public string GetActiveGameProfileBackupDirectoryName()
    {
        return BuildBackupDirectoryName(ActiveProfile.Name);
    }

    private void DeleteProfileBackupDirectories(IEnumerable<string> profileNames)
    {
        if (profileNames is null) return;

        var baseDirectory = GetConfigurationDirectory();

        foreach (var profileName in profileNames)
        {
            if (string.IsNullOrWhiteSpace(profileName)) continue;

            var directoryName = BuildBackupDirectoryName(profileName);
            var directoryPath = Path.Combine(baseDirectory, directoryName);

            TryDeleteDirectory(directoryPath);
        }
    }

    private GameProfileState EnsureProfile(string name)
    {
        if (_gameProfiles.TryGetValue(name, out var profile)) return profile;

        profile = new GameProfileState(name);
        _gameProfiles[name] = profile;
        return profile;
    }

    private static string? NormalizeGameProfileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var normalized = name.Trim();
        if (normalized.Length > 128) normalized = normalized[..128];

        return normalized.Length == 0 ? null : normalized;
    }

    private static string BuildBackupDirectoryName(string profileName)
    {
        string sanitized = new(profileName
            .Where(ch => !Path.GetInvalidFileNameChars().Contains(ch))
            .ToArray());

        if (string.IsNullOrWhiteSpace(sanitized)) sanitized = DefaultGameProfileName;

        if (string.Equals(sanitized, DefaultGameProfileName, StringComparison.OrdinalIgnoreCase))
            return DevConfig.BackupDirectoryName;

        return $"{DevConfig.BackupDirectoryName}_{sanitized}";
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void LoadGameProfile(GameProfileState profile, JsonObject obj)
    {
        var dataDirectory = NormalizePath(GetOptionalString(obj["dataDirectory"]));
        if (dataDirectory is not null) profile.DataDirectory = dataDirectory;

        var gameDirectory = NormalizePath(GetOptionalString(obj["gameDirectory"]));
        if (gameDirectory is not null) profile.GameDirectory = gameDirectory;

        var shortcut = NormalizePath(GetOptionalString(obj["customShortcutPath"]));
        if (shortcut is not null) profile.CustomShortcutPath = shortcut;

        profile.RequiresDataDirectorySelection =
            obj["requiresDataDirectorySelection"]?.GetValue<bool?>() ?? false;
        profile.RequiresGameDirectorySelection =
            obj["requiresGameDirectorySelection"]?.GetValue<bool?>() ?? false;

        LoadBulkUpdateModExclusions(obj["bulkUpdateModExclusions"], profile.BulkUpdateModExclusions);
        LoadSkippedModVersions(obj["skippedModVersions"], profile.SkippedModVersions);
        LoadModUsageTracking(obj["modUsageTracking"], profile);
    }

    private void ApplyLegacyProfileData(JsonObject root, GameProfileState profile)
    {
        var shortcut = NormalizePath(GetOptionalString(root["customShortcutPath"]));
        if (profile.CustomShortcutPath is null) profile.CustomShortcutPath = shortcut;

        if (profile.BulkUpdateModExclusions.Count == 0)
            LoadBulkUpdateModExclusions(root["bulkUpdateModExclusions"], profile.BulkUpdateModExclusions);

        if (profile.SkippedModVersions.Count == 0)
            LoadSkippedModVersions(root["skippedModVersions"], profile.SkippedModVersions);

        if (profile.ModUsageSessionCounts.Count == 0) LoadModUsageTracking(root["modUsageTracking"], profile);
    }

    public void SetMigrationCheckCompleted()
    {
        if (MigrationCheckCompleted) return;

        MigrationCheckCompleted = true;
        Save();
    }

    public void SetFirebaseAuthBackupCreated()
    {
        if (FirebaseAuthBackupCreated) return;

        FirebaseAuthBackupCreated = true;
        Save();
    }

    public void SetClientSettingsCleanupCompleted()
    {
        if (ClientSettingsCleanupCompleted) return;

        ClientSettingsCleanupCompleted = true;
        Save();
    }

    public void SetRebuiltModlistMigrationCompleted()
    {
        if (RebuiltModlistMigrationCompleted) return;

        RebuiltModlistMigrationCompleted = true;
        Save();
    }

    public IReadOnlyDictionary<string, string> GetThemePaletteColors()
    {
        return new Dictionary<string, string>(_themePaletteColors, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<ModUsageTrackingKey, int> GetPendingModUsageCounts()
    {
        if (_isModUsageTrackingDisabled) return new Dictionary<ModUsageTrackingKey, int>();

        return new Dictionary<ModUsageTrackingKey, int>(ActiveModUsageSessionCounts);
    }

    public bool RecordLongRunningSession(IReadOnlyList<ModUsageTrackingEntry>? activeMods, out bool recordedUsage)
    {
        recordedUsage = false;

        if (_isModUsageTrackingDisabled) return false;

        var wasPending = HasPendingModUsagePrompt;

        if (activeMods is not null && activeMods.Count > 0)
            foreach (var entry in activeMods)
            {
                if (string.IsNullOrEmpty(entry.ModId)
                    || string.IsNullOrEmpty(entry.ModVersion)
                    || string.IsNullOrEmpty(entry.GameVersion))
                    continue;

                if (!entry.CanSubmitVote || entry.HasUserVote) continue;

                var key = new ModUsageTrackingKey(entry.ModId, entry.ModVersion, entry.GameVersion);
                if (ActiveModUsageSessionCounts.TryGetValue(key, out var existing))
                    ActiveModUsageSessionCounts[key] = existing >= int.MaxValue - 1 ? int.MaxValue : existing + 1;
                else
                    ActiveModUsageSessionCounts[key] = 1;

                recordedUsage = true;
            }

        if (ActiveLongRunningSessionCount < int.MaxValue) ActiveLongRunningSessionCount++;

        if (ActiveLongRunningSessionCount > GameSessionVoteThreshold)
            ActiveLongRunningSessionCount = GameSessionVoteThreshold;

        if (ActiveLongRunningSessionCount >= GameSessionVoteThreshold && ActiveModUsageSessionCounts.Count > 0)
            ActiveHasPendingModUsagePrompt = true;

        Save();

        var isPending = HasPendingModUsagePrompt;
        return !wasPending && isPending;
    }

    public void DisableModUsageTracking()
    {
        if (_isModUsageTrackingDisabled) return;

        _isModUsageTrackingDisabled = true;
        var hadTrackingState = ActiveLongRunningSessionCount != 0
                               || ActiveModUsageSessionCounts.Count > 0
                               || ActiveHasPendingModUsagePrompt;
        ResetModUsageTracking();
        if (!hadTrackingState) Save();
    }

    public void ResetModUsageCounts(IEnumerable<ModUsageTrackingKey>? keys)
    {
        if (keys is null) return;

        var changed = false;

        foreach (var key in keys)
        {
            if (!key.IsValid) continue;

            if (ActiveModUsageSessionCounts.Remove(key)) changed = true;
        }

        if (ActiveModUsageSessionCounts.Count == 0)
        {
            if (ActiveLongRunningSessionCount != 0 || ActiveHasPendingModUsagePrompt)
            {
                ActiveLongRunningSessionCount = 0;
                ActiveHasPendingModUsagePrompt = false;
                changed = true;
            }
        }
        else
        {
            var shouldPrompt = ActiveLongRunningSessionCount >= GameSessionVoteThreshold;
            if (ActiveHasPendingModUsagePrompt != shouldPrompt)
            {
                ActiveHasPendingModUsagePrompt = shouldPrompt;
                changed = true;
            }
        }

        if (changed) Save();
    }

    public void CompleteModUsageVotes(IEnumerable<ModUsageTrackingKey>? completedKeys)
    {
        var changed = false;

        if (completedKeys is not null)
            foreach (var key in completedKeys)
            {
                if (!key.IsValid) continue;

                if (ActiveModUsageSessionCounts.Remove(key)) changed = true;
            }

        if (ActiveModUsageSessionCounts.Count == 0)
        {
            if (ActiveLongRunningSessionCount != 0 || ActiveHasPendingModUsagePrompt)
            {
                ActiveLongRunningSessionCount = 0;
                ActiveHasPendingModUsagePrompt = false;
                changed = true;
            }
        }
        else
        {
            ActiveHasPendingModUsagePrompt = true;
            if (ActiveLongRunningSessionCount < GameSessionVoteThreshold)
            {
                ActiveLongRunningSessionCount = GameSessionVoteThreshold;
                changed = true;
            }
        }

        if (changed) Save();
    }

    public void ResetModUsageTracking()
    {
        if (ActiveLongRunningSessionCount == 0 && ActiveModUsageSessionCounts.Count == 0 &&
            !ActiveHasPendingModUsagePrompt) return;

        ActiveLongRunningSessionCount = 0;
        ActiveModUsageSessionCounts.Clear();
        ActiveHasPendingModUsagePrompt = false;
        Save();
    }

    public bool TrySetThemePaletteColor(string key, string color)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        var normalizedKey = key.Trim();
        if (!_themePaletteColors.ContainsKey(normalizedKey)) return false;

        if (!TryNormalizeHexColor(color, out var normalizedColor)) return false;

        if (_themePaletteColors.TryGetValue(normalizedKey, out var current)
            && string.Equals(current, normalizedColor, StringComparison.OrdinalIgnoreCase))
            return true;

        _themePaletteColors[normalizedKey] = normalizedColor;
        if (ColorTheme == ColorTheme.Custom)
        {
            EnsureCustomThemePaletteInitialized();
            _customThemePaletteColors[normalizedKey] = normalizedColor;
        }

        Save();
        return true;
    }

    public void ResetThemePalette()
    {
        if (ColorTheme == ColorTheme.Custom) ResetCustomThemePaletteToDefaults();

        ResetThemePaletteToDefaults();
        Save();
    }

    public static IReadOnlyDictionary<string, string> GetDefaultThemePalette(ColorTheme theme)
    {
        return new Dictionary<string, string>(GetDefaultPalette(theme), StringComparer.OrdinalIgnoreCase);
    }

    public (string? SortMemberPath, ListSortDirection Direction) GetModListSortPreference()
    {
        return (_modsSortMemberPath, _modsSortDirection);
    }

    public bool? GetInstalledColumnVisibility(string columnName)
    {
        var normalized = NormalizeInstalledColumnKey(columnName);

        if (normalized is null) return null;

        return _installedColumnVisibility.TryGetValue(normalized, out var value)
            ? value
            : null;
    }

    public void SetInstalledColumnVisibility(string columnName, bool isVisible)
    {
        var normalized = NormalizeInstalledColumnKey(columnName);

        if (normalized is null) throw new ArgumentException("Column name cannot be empty.", nameof(columnName));

        if (_installedColumnVisibility.TryGetValue(normalized, out var current)
            && current == isVisible)
            return;

        _installedColumnVisibility[normalized] = isVisible;
        Save();
    }

    public string GetConfigurationDirectory()
    {
        var directory = Path.GetDirectoryName(_configurationPath)
                        ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);
        return directory;
    }

    public void EnablePersistence()
    {
        if (_isPersistenceEnabled) return;

        _isPersistenceEnabled = true;

        if (_hasPendingSave) PersistConfiguration();

        if (_hasPendingModConfigPathSave) PersistModConfigPathHistory();
    }

    public string? GetLastSelectedPresetName()
    {
        return _selectedPresetName;
    }

    public void SetLastSelectedPresetName(string? name)
    {
        var normalized = NormalizePresetName(name);

        if (string.Equals(_selectedPresetName, normalized, StringComparison.OrdinalIgnoreCase)) return;

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

        var key = modId.Trim();
        if (ActiveModConfigPaths.TryGetValue(key, out path)) return true;

        if (_storedModConfigPaths.TryGetValue(key, out var entry) && entry is not null)
        {
            var combined = BuildFullConfigPath(entry);
            if (!string.IsNullOrWhiteSpace(combined))
            {
                var combinedPath = combined!;
                ActiveModConfigPaths[key] = combinedPath;
                path = combinedPath;
                return true;
            }
        }

        path = null;
        return false;
    }

    public void SetModConfigPath(string modId, string path, string? configName = null)
    {
        if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("Mod ID cannot be empty.", nameof(modId));

        var normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("The configuration path is invalid.", nameof(path));

        var modConfigDirectory = GetActiveModConfigDirectory();
        if (!string.IsNullOrWhiteSpace(modConfigDirectory) && !IsPathInDirectory(normalized, modConfigDirectory))
            throw new ArgumentException(
                "The configuration path must be inside the ModConfig directory.",
                nameof(path));

        var key = modId.Trim();
        ActiveModConfigPaths[key] = normalized;

        var normalizedConfigName = NormalizeConfigName(configName);
        if (normalizedConfigName is null) normalizedConfigName = ExtractFileName(normalized);

        UpdatePersistentModConfigPath(key, normalized, normalizedConfigName);
        Save();
    }

    public void RemoveModConfigPath(string? modId, bool preserveHistory = false)
    {
        if (string.IsNullOrWhiteSpace(modId)) return;

        var key = modId.Trim();
        var removed = ActiveModConfigPaths.Remove(key);
        var historyChanged = false;

        if (!preserveHistory) historyChanged = _storedModConfigPaths.Remove(key);

        if (removed) Save();

        if (historyChanged) SaveModConfigPathHistory();
    }

    public void SetCompactViewMode(bool isCompact)
    {
        if (IsCompactView == isCompact) return;

        IsCompactView = isCompact;
        Save();
    }

    public void SetModDbDesignViewMode(bool useModDbDesignView)
    {
        if (UseModDbDesignView == useModDbDesignView) return;

        UseModDbDesignView = useModDbDesignView;
        Save();
    }

    public void SetColorTheme(ColorTheme theme, IReadOnlyDictionary<string, string>? paletteOverride = null)
    {
        var paletteChanged = false;

        if (ColorTheme != theme)
        {
            ColorTheme = theme;
            ResetThemePaletteToDefaults();
            paletteChanged = true;
        }

        if (paletteOverride is not null) paletteChanged |= ApplyThemePaletteOverride(paletteOverride);

        if (paletteChanged) Save();
    }

    public void SetEnableServerOptions(bool enableServerOptions)
    {
        if (EnableServerOptions == enableServerOptions) return;

        EnableServerOptions = enableServerOptions;
        Save();
    }

    public void SetAutomaticDataBackupsEnabled(bool isEnabled)
    {
        if (AutomaticDataBackupsEnabled == isEnabled) return;

        AutomaticDataBackupsEnabled = isEnabled;
        Save();
    }

    public void SetAutomaticDataBackupsWarningAcknowledged(bool acknowledged)
    {
        if (AutomaticDataBackupsWarningAcknowledged == acknowledged) return;

        AutomaticDataBackupsWarningAcknowledged = acknowledged;
        Save();
    }

    public void SetModlistAutoLoadBehavior(ModlistAutoLoadBehavior behavior)
    {
        if (ModlistAutoLoadBehavior == behavior) return;

        ModlistAutoLoadBehavior = behavior;
        Save();
    }

    public void SetCacheAllVersionsLocally(bool cacheAllVersionsLocally)
    {
        if (CacheAllVersionsLocally == cacheAllVersionsLocally) return;

        CacheAllVersionsLocally = cacheAllVersionsLocally;
        Save();
    }

    public void SetModDatabaseAutoLoadMode(ModDatabaseAutoLoadMode mode)
    {
        if (ModDatabaseAutoLoadMode == mode) return;

        ModDatabaseAutoLoadMode = mode;
        Save();
    }

    public void SetModDatabaseSearchResultLimit(int limit)
    {
        var normalized = NormalizeModDatabaseSearchResultLimit(limit);

        if (ModDatabaseSearchResultLimit == normalized) return;

        ModDatabaseSearchResultLimit = normalized;
        Save();
    }

    public void SetModListSortPreference(string? sortMemberPath, ListSortDirection direction)
    {
        var normalized = string.IsNullOrWhiteSpace(sortMemberPath)
            ? null
            : sortMemberPath.Trim();

        if (string.Equals(_modsSortMemberPath, normalized, StringComparison.Ordinal)
            && _modsSortDirection == direction)
            return;

        _modsSortMemberPath = normalized;
        _modsSortDirection = direction;
        Save();
    }

    public void SetWindowDimensions(double width, double height)
    {
        var normalizedWidth = NormalizeWindowDimension(width);
        var normalizedHeight = NormalizeWindowDimension(height);

        if (!normalizedWidth.HasValue || !normalizedHeight.HasValue) return;

        var hasWidthChanged = !_windowWidth.HasValue || Math.Abs(_windowWidth.Value - normalizedWidth.Value) > 0.1;
        var hasHeightChanged = !_windowHeight.HasValue || Math.Abs(_windowHeight.Value - normalizedHeight.Value) > 0.1;

        if (!hasWidthChanged && !hasHeightChanged) return;

        _windowWidth = normalizedWidth;
        _windowHeight = normalizedHeight;
        Save();
    }

    public void SetModInfoPanelPosition(double left, double top)
    {
        var normalizedLeft = NormalizeModInfoCoordinate(left);
        var normalizedTop = NormalizeModInfoCoordinate(top);

        if (!normalizedLeft.HasValue || !normalizedTop.HasValue) return;

        var hasLeftChanged = !_modInfoPanelLeft.HasValue ||
                             Math.Abs(_modInfoPanelLeft.Value - normalizedLeft.Value) > 0.1;
        var hasTopChanged = !_modInfoPanelTop.HasValue || Math.Abs(_modInfoPanelTop.Value - normalizedTop.Value) > 0.1;

        if (!hasLeftChanged && !hasTopChanged) return;

        _modInfoPanelLeft = normalizedLeft;
        _modInfoPanelTop = normalizedTop;
        Save();
    }

    public void SetDataDirectory(string path)
    {
        ActiveProfile.DataDirectory = NormalizePath(path);
        ActiveProfile.RequiresDataDirectorySelection = false;
        EnsureModConfigDirectoryExists(ActiveProfile.DataDirectory);
        RefreshActiveModConfigPathsFromHistory();
        Save();
    }

    public void ClearDataDirectory()
    {
        if (ActiveProfile.DataDirectory is null)
        {
            ActiveProfile.RequiresDataDirectorySelection = true;
            return;
        }

        ActiveProfile.DataDirectory = null;
        ActiveProfile.RequiresDataDirectorySelection = true;
        Save();
    }

    public void SetGameDirectory(string path)
    {
        ActiveProfile.GameDirectory = NormalizePath(path);
        ActiveProfile.RequiresGameDirectorySelection = false;
        Save();
    }

    public void ClearGameDirectory()
    {
        if (ActiveProfile.GameDirectory is null)
        {
            ActiveProfile.RequiresGameDirectorySelection = true;
            return;
        }

        ActiveProfile.GameDirectory = null;
        ActiveProfile.RequiresGameDirectorySelection = true;
        Save();
    }

    public void SetCustomShortcutPath(string path)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("The shortcut path is invalid.", nameof(path));

        if (string.Equals(ActiveProfile.CustomShortcutPath, normalized, StringComparison.OrdinalIgnoreCase)) return;

        ActiveProfile.CustomShortcutPath = normalized;
        Save();
    }

    public void ClearCustomShortcutPath()
    {
        if (ActiveProfile.CustomShortcutPath is null) return;

        ActiveProfile.CustomShortcutPath = null;
        Save();
    }

    public void SetDisableAutoRefresh(bool disable)
    {
        if (DisableAutoRefresh == disable) return;

        DisableAutoRefresh = disable;
        Save();
    }

    public void SetDisableAutoRefreshWarningAcknowledged(bool acknowledged)
    {
        if (DisableAutoRefreshWarningAcknowledged == acknowledged) return;

        DisableAutoRefreshWarningAcknowledged = acknowledged;
        Save();
    }

    public void SetGameProfileCreationWarningAcknowledged(bool acknowledged)
    {
        if (GameProfileCreationWarningAcknowledged == acknowledged) return;

        GameProfileCreationWarningAcknowledged = acknowledged;
        Save();
    }

    public void SetDisableInternetAccess(bool disable)
    {
        if (DisableInternetAccess == disable) return;

        DisableInternetAccess = disable;
        Save();
    }

    public void SetSuppressModlistSavePrompt(bool suppress)
    {
        if (SuppressModlistSavePrompt == suppress) return;

        SuppressModlistSavePrompt = suppress;
        Save();
    }

    public void SetSuppressRefreshCachePrompt(bool suppress)
    {
        var normalizedVersion = suppress
            ? NormalizeVersion(ModManagerVersion) ?? ModManagerVersion
            : null;

        if (_suppressRefreshCachePrompt == suppress
            && string.Equals(
                _suppressRefreshCachePromptVersion,
                normalizedVersion,
                StringComparison.OrdinalIgnoreCase))
            return;

        _suppressRefreshCachePrompt = suppress;
        _suppressRefreshCachePromptVersion = normalizedVersion;
        Save();
    }

    public void SetCloudUploaderName(string? name)
    {
        var normalized = NormalizeUploaderName(name);

        if (string.Equals(CloudUploaderName, normalized, StringComparison.Ordinal)) return;

        CloudUploaderName = normalized;
        Save();
    }

    public void SetExcludeInstalledModDatabaseResults(bool exclude)
    {
        if (ExcludeInstalledModDatabaseResults == exclude) return;

        ExcludeInstalledModDatabaseResults = exclude;
        Save();
    }

    public void SetOnlyShowCompatibleModDatabaseResults(bool onlyCompatible)
    {
        if (OnlyShowCompatibleModDatabaseResults == onlyCompatible) return;

        OnlyShowCompatibleModDatabaseResults = onlyCompatible;
        Save();
    }

    public void SetRequireExactVsVersionMatch(bool requireExact)
    {
        if (RequireExactVsVersionMatch == requireExact) return;

        RequireExactVsVersionMatch = requireExact;
        Save();
    }

    public bool IsModExcludedFromBulkUpdates(string? modId)
    {
        var normalized = NormalizeModId(modId);
        if (normalized is null) return false;

        return ActiveBulkUpdateModExclusions.TryGetValue(normalized, out var isExcluded) && isExcluded;
    }

    public void SetModExcludedFromBulkUpdates(string? modId, bool isExcluded)
    {
        var normalized = NormalizeModId(modId);
        if (normalized is null) return;

        if (isExcluded)
        {
            if (ActiveBulkUpdateModExclusions.TryGetValue(normalized, out var current) && current) return;

            ActiveBulkUpdateModExclusions[normalized] = true;
            Save();
            return;
        }

        if (!ActiveBulkUpdateModExclusions.Remove(normalized)) return;

        Save();
    }

    public void SkipModVersion(string? modId, string? version)
    {
        var normalizedId = NormalizeModId(modId);
        if (normalizedId is null) return;

        var normalizedVersion = NormalizeVersion(version);
        if (normalizedVersion is null) return;

        if (ActiveSkippedModVersions.TryGetValue(normalizedId, out var current)
            && string.Equals(current, normalizedVersion, StringComparison.OrdinalIgnoreCase))
            return;

        ActiveSkippedModVersions[normalizedId] = normalizedVersion;
        Save();
    }

    public bool ShouldSkipModVersion(string? modId, string? version)
    {
        var normalizedId = NormalizeModId(modId);
        if (normalizedId is null) return false;

        if (!ActiveSkippedModVersions.TryGetValue(normalizedId, out var storedVersion)) return false;

        var normalizedVersion = NormalizeVersion(version);
        if (normalizedVersion is null
            || !string.Equals(storedVersion, normalizedVersion, StringComparison.OrdinalIgnoreCase))
            if (ActiveSkippedModVersions.Remove(normalizedId))
                Save();

        return string.Equals(storedVersion, normalizedVersion, StringComparison.OrdinalIgnoreCase);
    }


    private void Load()
    {
        _gameProfiles.Clear();
        GameProfileState defaultProfile = new(DefaultGameProfileName);
        _gameProfiles[defaultProfile.Name] = defaultProfile;
        ActiveProfile = defaultProfile;
        ResetCustomThemePaletteToDefaults();
        ColorTheme = ColorTheme.VintageStory;
        ResetThemePaletteToDefaults();
        _selectedPresetName = null;
        GameProfileCreationWarningAcknowledged = false;
        AutomaticDataBackupsWarningAcknowledged = false;

        try
        {
            if (!File.Exists(_configurationPath)) return;

            using var stream = File.OpenRead(_configurationPath);
            var node = JsonNode.Parse(stream);
            if (node is not JsonObject obj) return;

            var originalConfigurationVersion = NormalizeVersion(GetOptionalString(obj["configurationVersion"]));
            var originalModManagerVersion = NormalizeVersion(GetOptionalString(obj["modManagerVersion"]));
            InitializeVersionMetadata(originalConfigurationVersion, originalModManagerVersion);

            var rootDataDirectory = NormalizePath(GetOptionalString(obj["dataDirectory"]));
            var rootGameDirectory = NormalizePath(GetOptionalString(obj["gameDirectory"]));
            defaultProfile.DataDirectory = rootDataDirectory;
            defaultProfile.GameDirectory = rootGameDirectory;

            IsCompactView = obj["isCompactView"]?.GetValue<bool?>() ?? false;
            UseModDbDesignView = obj["useModDbDesignView"]?.GetValue<bool?>() ?? true;
            CacheAllVersionsLocally = obj["cacheAllVersionsLocally"]?.GetValue<bool?>() ?? true;
            DisableAutoRefresh = obj["disableAutoRefresh"]?.GetValue<bool?>() ?? false;
            DisableAutoRefreshWarningAcknowledged =
                obj["disableAutoRefreshWarningAcknowledged"]?.GetValue<bool?>() ?? false;
            DisableInternetAccess = obj["disableInternetAccess"]?.GetValue<bool?>() ?? false;
            _isModUsageTrackingDisabled = obj["modUsageTrackingDisabled"]?.GetValue<bool?>() ?? false;
            EnableServerOptions = obj["enableServerOptions"]?.GetValue<bool?>() ?? false;
            AutomaticDataBackupsEnabled = obj["automaticDataBackupsEnabled"]?.GetValue<bool?>() ?? false;
            AutomaticDataBackupsWarningAcknowledged =
                obj["automaticDataBackupsWarningAcknowledged"]?.GetValue<bool?>() ?? false;
            SuppressModlistSavePrompt = obj["suppressModlistSavePrompt"]?.GetValue<bool?>() ?? false;
            _suppressRefreshCachePrompt = obj["suppressRefreshCachePrompt"]?.GetValue<bool?>() ?? false;
            _suppressRefreshCachePromptVersion = NormalizeVersion(
                GetOptionalString(obj["suppressRefreshCachePromptVersion"]));
            GameProfileCreationWarningAcknowledged =
                obj["gameProfileCreationWarningAcknowledged"]?.GetValue<bool?>() ?? false;
            if (_suppressRefreshCachePrompt)
            {
                if (_suppressRefreshCachePromptVersion is null)
                {
                    _suppressRefreshCachePromptVersion = PreviousModManagerVersion ?? ModManagerVersion;
                    _hasPendingSave = true;
                }
            }
            else
            {
                _suppressRefreshCachePromptVersion = null;
            }

            var colorThemeValue = GetOptionalString(obj["colorTheme"]);
            var legacyDarkVsMode = obj["useDarkVsMode"]?.GetValue<bool?>();
            if (!string.IsNullOrWhiteSpace(colorThemeValue)
                && Enum.TryParse(colorThemeValue.Trim(), true, out ColorTheme parsedTheme))
            {
                ColorTheme = parsedTheme;
            }
            else
            {
                ColorTheme = legacyDarkVsMode.HasValue && !legacyDarkVsMode.Value
                    ? ColorTheme.Light
                    : ColorTheme.VintageStory;
                _hasPendingSave = true;
            }

            var hasCustomPalette = LoadCustomThemePalette(obj["customThemePalette"]);
            ResetThemePaletteToDefaults();
            ModlistAutoLoadBehavior = ParseModlistAutoLoadBehavior(GetOptionalString(obj["modlistAutoLoadBehavior"]));
            _modsSortMemberPath = NormalizeSortMemberPath(GetOptionalString(obj["modsSortMemberPath"]));
            _modsSortDirection = ParseSortDirection(GetOptionalString(obj["modsSortDirection"]));
            ModDatabaseSearchResultLimit =
                NormalizeModDatabaseSearchResultLimit(obj["modDatabaseSearchResultLimit"]?.GetValue<int?>());
            ModDatabaseNewModsRecentMonths = NormalizeModDatabaseNewModsRecentMonths(
                obj["modDatabaseNewModsRecentMonths"]?.GetValue<int?>());
            ModDatabaseAutoLoadMode = ParseModDatabaseAutoLoadMode(GetOptionalString(obj["modDatabaseAutoLoadMode"]));
            ExcludeInstalledModDatabaseResults = obj["excludeInstalledModDatabaseResults"]?.GetValue<bool?>() ?? false;
            OnlyShowCompatibleModDatabaseResults =
                obj["onlyShowCompatibleModDatabaseResults"]?.GetValue<bool?>() ?? false;
            RequireExactVsVersionMatch = obj["requireExactVsVersionMatch"]?.GetValue<bool?>() ?? false;
            _windowWidth = NormalizeWindowDimension(obj["windowWidth"]?.GetValue<double?>());
            _windowHeight = NormalizeWindowDimension(obj["windowHeight"]?.GetValue<double?>());
            _modInfoPanelLeft = NormalizeModInfoCoordinate(obj["modInfoPanelLeft"]?.GetValue<double?>());
            _modInfoPanelTop = NormalizeModInfoCoordinate(obj["modInfoPanelTop"]?.GetValue<double?>());

            LoadInstalledColumnVisibility(obj["installedColumnVisibility"]);
            LoadThemePalette(obj["themePalette"] ?? obj["darkVsPalette"]);
            if (ColorTheme == ColorTheme.Custom)
            {
                SyncCustomThemePaletteWithCurrentTheme();
                if (!hasCustomPalette) _hasPendingSave = true;
            }

            _selectedPresetName = NormalizePresetName(GetOptionalString(obj["selectedPreset"]));
            CloudUploaderName = NormalizeUploaderName(GetOptionalString(obj["cloudUploaderName"]));
            MigrationCheckCompleted = obj["migrationCheckCompleted"]?.GetValue<bool?>() ?? false;
            FirebaseAuthBackupCreated = obj["firebaseAuthBackupCreated"]?.GetValue<bool?>() ?? false;
            ClientSettingsCleanupCompleted = obj["clientSettingsCleanupCompleted"]?.GetValue<bool?>() ?? false;
            RebuiltModlistMigrationCompleted =
                obj["rebuiltModlistMigrationCompleted"]?.GetValue<bool?>() ?? false;

            var profilesFound = false;
            if (obj["gameProfiles"] is JsonObject profilesObj)
            {
                profilesFound = true;
                foreach (var (key, value) in profilesObj)
                {
                    var normalizedName = NormalizeGameProfileName(key);
                    if (normalizedName is null || value is not JsonObject profileObj) continue;

                    var profile = EnsureProfile(normalizedName);
                    LoadGameProfile(profile, profileObj);
                }
            }

            if (!profilesFound)
            {
                ApplyLegacyProfileData(obj, defaultProfile);
            }
            else
            {
                if (!_gameProfiles.TryGetValue(DefaultGameProfileName, out var existingDefault)
                    || existingDefault is null)
                {
                    defaultProfile = new GameProfileState(DefaultGameProfileName);
                    _gameProfiles[defaultProfile.Name] = defaultProfile;
                }
                else
                {
                    defaultProfile = existingDefault;
                }

                defaultProfile.DataDirectory ??= rootDataDirectory;
                defaultProfile.GameDirectory ??= rootGameDirectory;
                ApplyLegacyProfileData(obj, defaultProfile);
            }

            var rootShortcut = NormalizePath(GetOptionalString(obj["customShortcutPath"]));
            foreach (var profile in _gameProfiles.Values)
            {
                if (profile.DataDirectory is null)
                    if (!profile.RequiresDataDirectorySelection)
                        profile.DataDirectory = rootDataDirectory;

                if (profile.DataDirectory is not null) profile.RequiresDataDirectorySelection = false;

                if (profile.GameDirectory is null)
                    if (!profile.RequiresGameDirectorySelection)
                        profile.GameDirectory = rootGameDirectory;

                if (profile.GameDirectory is not null) profile.RequiresGameDirectorySelection = false;

                if (profile.CustomShortcutPath is null
                    && !profile.RequiresDataDirectorySelection
                    && !profile.RequiresGameDirectorySelection)
                    profile.CustomShortcutPath = rootShortcut;
            }

            var activeName = NormalizeGameProfileName(GetOptionalString(obj["activeGameProfile"]));
            if (activeName is not null && _gameProfiles.TryGetValue(activeName, out var activeProfile))
                ActiveProfile = activeProfile;
            else
                ActiveProfile = defaultProfile;

            if (_isModUsageTrackingDisabled)
                foreach (var profile in _gameProfiles.Values)
                {
                    profile.ModUsageSessionCounts.Clear();
                    profile.LongRunningSessionCount = 0;
                    profile.HasPendingModUsagePrompt = false;
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _gameProfiles.Clear();
            GameProfileState resetProfile = new(DefaultGameProfileName);
            _gameProfiles[resetProfile.Name] = resetProfile;
            ActiveProfile = resetProfile;
            ColorTheme = ColorTheme.VintageStory;
            ResetCustomThemePaletteToDefaults();
            ResetThemePaletteToDefaults();
            ConfigurationVersion = CurrentConfigurationVersion;
            ModManagerVersion = CurrentModManagerVersion;
            IsCompactView = false;
            UseModDbDesignView = true;
            CacheAllVersionsLocally = true;
            DisableAutoRefresh = false;
            DisableAutoRefreshWarningAcknowledged = false;
            DisableInternetAccess = false;
            EnableServerOptions = false;
            AutomaticDataBackupsWarningAcknowledged = false;
            SuppressModlistSavePrompt = false;
            _suppressRefreshCachePrompt = false;
            _suppressRefreshCachePromptVersion = null;
            ModlistAutoLoadBehavior = ModlistAutoLoadBehavior.Prompt;
            _modsSortMemberPath = null;
            _modsSortDirection = ListSortDirection.Ascending;
            _selectedPresetName = null;
            ModDatabaseSearchResultLimit = DefaultModDatabaseSearchResultLimit;
            ModDatabaseNewModsRecentMonths = DefaultModDatabaseNewModsRecentMonths;
            ModDatabaseAutoLoadMode = ModDatabaseAutoLoadMode.TotalDownloads;
            ExcludeInstalledModDatabaseResults = false;
            OnlyShowCompatibleModDatabaseResults = false;
            RequireExactVsVersionMatch = false;
            _windowWidth = null;
            _windowHeight = null;
            _modInfoPanelLeft = null;
            _modInfoPanelTop = null;
            CloudUploaderName = null;
            _installedColumnVisibility.Clear();
            _isModUsageTrackingDisabled = false;
            MigrationCheckCompleted = false;
            FirebaseAuthBackupCreated = false;
            GameProfileCreationWarningAcknowledged = false;
            ClientSettingsCleanupCompleted = false;
            RebuiltModlistMigrationCompleted = false;
        }

        LoadPersistentModConfigPaths();
        BackfillModConfigPathHistory();
    }

    private void Save()
    {
        _hasPendingSave = true;

        if (!_isPersistenceEnabled) return;

        PersistConfiguration();
    }

    private void PersistConfiguration()
    {
        try
        {
            var directory = Path.GetDirectoryName(_configurationPath)!;
            Directory.CreateDirectory(directory);

            var obj = new JsonObject
            {
                ["configurationVersion"] = ConfigurationVersion,
                ["modManagerVersion"] = ModManagerVersion,
                ["dataDirectory"] = DataDirectory,
                ["gameDirectory"] = GameDirectory,
                ["activeGameProfile"] = ActiveProfile.Name,
                ["isCompactView"] = IsCompactView,
                ["useModDbDesignView"] = UseModDbDesignView,
                ["cacheAllVersionsLocally"] = CacheAllVersionsLocally,
                ["disableAutoRefresh"] = DisableAutoRefresh,
                ["disableAutoRefreshWarningAcknowledged"] = DisableAutoRefreshWarningAcknowledged,
                ["disableInternetAccess"] = DisableInternetAccess,
                ["modUsageTrackingDisabled"] = _isModUsageTrackingDisabled,
                ["enableServerOptions"] = EnableServerOptions,
                ["automaticDataBackupsEnabled"] = AutomaticDataBackupsEnabled,
                ["automaticDataBackupsWarningAcknowledged"] = AutomaticDataBackupsWarningAcknowledged,
                ["suppressModlistSavePrompt"] = SuppressModlistSavePrompt,
                ["suppressRefreshCachePrompt"] = _suppressRefreshCachePrompt,
                ["suppressRefreshCachePromptVersion"] = _suppressRefreshCachePromptVersion,
                ["gameProfileCreationWarningAcknowledged"] = GameProfileCreationWarningAcknowledged,
                ["useDarkVsMode"] = ColorTheme != ColorTheme.Light,
                ["colorTheme"] = ColorTheme.ToString(),
                ["modlistAutoLoadBehavior"] = ModlistAutoLoadBehavior.ToString(),
                ["modsSortMemberPath"] = _modsSortMemberPath,
                ["modsSortDirection"] = _modsSortDirection.ToString(),
                ["modDatabaseSearchResultLimit"] = ModDatabaseSearchResultLimit,
                ["modDatabaseNewModsRecentMonths"] = ModDatabaseNewModsRecentMonths,
                ["modDatabaseAutoLoadMode"] = ModDatabaseAutoLoadMode.ToString(),
                ["excludeInstalledModDatabaseResults"] = ExcludeInstalledModDatabaseResults,
                ["onlyShowCompatibleModDatabaseResults"] = OnlyShowCompatibleModDatabaseResults,
                ["requireExactVsVersionMatch"] = RequireExactVsVersionMatch,
                ["windowWidth"] = _windowWidth,
                ["windowHeight"] = _windowHeight,
                ["modInfoPanelLeft"] = _modInfoPanelLeft,
                ["modInfoPanelTop"] = _modInfoPanelTop,
                ["bulkUpdateModExclusions"] = BuildBulkUpdateModExclusionsJson(ActiveProfile.BulkUpdateModExclusions),
                ["skippedModVersions"] = BuildSkippedModVersionsJson(ActiveProfile.SkippedModVersions),
                ["installedColumnVisibility"] = BuildInstalledColumnVisibilityJson(),
                ["modUsageTracking"] = BuildModUsageTrackingJson(ActiveProfile),
                ["themePalette"] = BuildThemePaletteJson(),
                ["darkVsPalette"] = BuildThemePaletteJson(),
                ["customThemePalette"] = BuildCustomThemePaletteJson(),
                ["selectedPreset"] = _selectedPresetName,
                ["customShortcutPath"] = ActiveProfile.CustomShortcutPath,
                ["cloudUploaderName"] = CloudUploaderName,
                ["migrationCheckCompleted"] = MigrationCheckCompleted,
                ["firebaseAuthBackupCreated"] = FirebaseAuthBackupCreated,
                ["clientSettingsCleanupCompleted"] = ClientSettingsCleanupCompleted,
                ["rebuiltModlistMigrationCompleted"] = RebuiltModlistMigrationCompleted,
                ["gameProfiles"] = BuildGameProfilesJson()
            };

            RemoveRedundantRootProfileValues(obj);

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

    private void RemoveRedundantRootProfileValues(JsonObject root)
    {
        if (root["gameProfiles"] is not JsonObject profiles
            || !profiles.ContainsKey(DefaultGameProfileName))
            return;

        foreach (var propertyName in RedundantRootProfilePropertyNames) root.Remove(propertyName);
    }

    private void InitializeVersionMetadata(string? configurationVersion, string? modManagerVersion)
    {
        var requiresSave = false;

        PreviousConfigurationVersion = configurationVersion;
        PreviousModManagerVersion = modManagerVersion;

        var configurationMismatch = !string.IsNullOrWhiteSpace(configurationVersion)
                                    && !string.Equals(configurationVersion, CurrentConfigurationVersion,
                                        StringComparison.OrdinalIgnoreCase);

        var modManagerMismatch = !string.IsNullOrWhiteSpace(modManagerVersion)
                                 && !string.Equals(modManagerVersion, CurrentModManagerVersion,
                                     StringComparison.OrdinalIgnoreCase);

        HasVersionMismatch = configurationMismatch || modManagerMismatch;

        var resolvedConfigurationVersion = string.IsNullOrWhiteSpace(configurationVersion)
            ? CurrentConfigurationVersion
            : configurationVersion!;

        var resolvedModManagerVersion = string.IsNullOrWhiteSpace(modManagerVersion)
            ? CurrentModManagerVersion
            : modManagerVersion!;

        if (!string.Equals(resolvedConfigurationVersion, CurrentConfigurationVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            resolvedConfigurationVersion = CurrentConfigurationVersion;
            requiresSave = true;
        }

        if (!string.Equals(resolvedModManagerVersion, CurrentModManagerVersion, StringComparison.OrdinalIgnoreCase))
        {
            resolvedModManagerVersion = CurrentModManagerVersion;
            requiresSave = true;
        }

        ConfigurationVersion = resolvedConfigurationVersion;
        ModManagerVersion = resolvedModManagerVersion;

        if (requiresSave) _hasPendingSave = true;
    }

    private static int NormalizeModDatabaseSearchResultLimit(int? value)
    {
        if (!value.HasValue) return DefaultModDatabaseSearchResultLimit;

        var normalized = value.Value;
        if (normalized <= 0) return DefaultModDatabaseSearchResultLimit;

        return Math.Max(normalized, 1);
    }

    private static int NormalizeModDatabaseNewModsRecentMonths(int? value)
    {
        if (!value.HasValue) return DefaultModDatabaseNewModsRecentMonths;

        var normalized = value.Value;
        if (normalized <= 0) return DefaultModDatabaseNewModsRecentMonths;

        return Math.Clamp(normalized, 1, MaxModDatabaseNewModsRecentMonths);
    }

    private static ModlistAutoLoadBehavior ParseModlistAutoLoadBehavior(string? value)
    {
        if (Enum.TryParse(value, true, out ModlistAutoLoadBehavior behavior)) return behavior;

        return ModlistAutoLoadBehavior.Prompt;
    }

    private static ModDatabaseAutoLoadMode ParseModDatabaseAutoLoadMode(string? value)
    {
        if (Enum.TryParse(value, true, out ModDatabaseAutoLoadMode mode)) return mode;

        return ModDatabaseAutoLoadMode.TotalDownloads;
    }

    private static double? NormalizeWindowDimension(double? dimension)
    {
        if (!dimension.HasValue) return null;

        var value = dimension.Value;
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return null;

        return value;
    }

    private static double? NormalizeModInfoCoordinate(double? coordinate)
    {
        if (!coordinate.HasValue) return null;

        var value = coordinate.Value;
        if (double.IsNaN(value) || double.IsInfinity(value)) return null;

        return value;
    }

    private static string DetermineConfigurationPath()
    {
        var preferredDirectory = GetPreferredConfigurationDirectory();
        return Path.Combine(preferredDirectory, ConfigurationFileName);
    }

    private static string DetermineModConfigPathsPath(string fileName)
    {
        var preferredDirectory = GetPreferredConfigurationDirectory();
        return Path.Combine(preferredDirectory, fileName);
    }

    private JsonObject BuildInstalledColumnVisibilityJson()
    {
        var result = new JsonObject();

        foreach (var pair in _installedColumnVisibility.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;

            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static JsonObject BuildBulkUpdateModExclusionsJson(IReadOnlyDictionary<string, bool> source)
    {
        var result = new JsonObject();

        foreach (var pair in source.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || !pair.Value) continue;

            result[pair.Key] = true;
        }

        return result;
    }

    private static JsonObject BuildSkippedModVersionsJson(IReadOnlyDictionary<string, string> source)
    {
        var result = new JsonObject();

        foreach (var pair in source.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;

            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static JsonObject BuildModUsageTrackingJson(GameProfileState profile)
    {
        var counts = new JsonArray();

        foreach (var pair in profile.ModUsageSessionCounts
                     .OrderBy(entry => entry.Key.ModId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(entry => entry.Key.ModVersion, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(entry => entry.Key.GameVersion, StringComparer.OrdinalIgnoreCase))
        {
            if (!pair.Key.IsValid || pair.Value <= 0) continue;

            counts.Add(new JsonObject
            {
                ["modId"] = pair.Key.ModId,
                ["modVersion"] = pair.Key.ModVersion,
                ["gameVersion"] = pair.Key.GameVersion,
                ["count"] = pair.Value
            });
        }

        return new JsonObject
        {
            ["longSessionCount"] = profile.LongRunningSessionCount,
            ["pendingPrompt"] = profile.HasPendingModUsagePrompt && counts.Count > 0,
            ["modCounts"] = counts
        };
    }

    private JsonObject BuildThemePaletteJson()
    {
        var defaults = ColorTheme == ColorTheme.Custom
            ? _customThemePaletteColors
            : GetDefaultPalette(ColorTheme);
        return BuildPaletteJson(_themePaletteColors, defaults);
    }

    private JsonObject BuildCustomThemePaletteJson()
    {
        return BuildPaletteJson(_customThemePaletteColors, GetDefaultPalette(ColorTheme.Custom));
    }

    private JsonObject BuildGameProfilesJson()
    {
        var result = new JsonObject();

        foreach (var profile in _gameProfiles.Values
                     .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var profileObject = new JsonObject
            {
                ["dataDirectory"] = profile.DataDirectory,
                ["gameDirectory"] = profile.GameDirectory,
                ["customShortcutPath"] = profile.CustomShortcutPath,
                ["bulkUpdateModExclusions"] = BuildBulkUpdateModExclusionsJson(profile.BulkUpdateModExclusions),
                ["skippedModVersions"] = BuildSkippedModVersionsJson(profile.SkippedModVersions),
                ["modUsageTracking"] = BuildModUsageTrackingJson(profile)
            };

            if (profile.RequiresDataDirectorySelection) profileObject["requiresDataDirectorySelection"] = true;

            if (profile.RequiresGameDirectorySelection) profileObject["requiresGameDirectorySelection"] = true;

            result[profile.Name] = profileObject;
        }

        return result;
    }

    private static JsonObject BuildPaletteJson(
        IReadOnlyDictionary<string, string> palette,
        IReadOnlyDictionary<string, string> defaults)
    {
        var result = new JsonObject();

        foreach (var pair in palette.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;

            result[pair.Key] = pair.Value;
        }

        foreach (var pair in defaults.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (result.ContainsKey(pair.Key)) continue;

            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private void LoadBulkUpdateModExclusions(JsonNode? node, Dictionary<string, bool> target)
    {
        target.Clear();

        if (node is not JsonObject obj) return;

        var requiresSave = false;

        foreach (var (key, value) in obj)
        {
            var normalized = NormalizeModId(key);
            if (normalized is null)
            {
                requiresSave = true;
                continue;
            }

            var isExcluded = value?.GetValue<bool?>() ?? false;
            if (!isExcluded)
            {
                requiresSave = true;
                continue;
            }

            if (target.ContainsKey(normalized)) continue;

            target[normalized] = true;
        }

        if (requiresSave) _hasPendingSave = true;
    }

    private void LoadSkippedModVersions(JsonNode? node, Dictionary<string, string> target)
    {
        target.Clear();

        if (node is not JsonObject obj) return;

        var requiresSave = false;

        foreach (var (key, value) in obj)
        {
            var normalizedId = NormalizeModId(key);
            if (normalizedId is null)
            {
                requiresSave = true;
                continue;
            }

            var version = NormalizeVersion(GetOptionalString(value));
            if (version is null)
            {
                requiresSave = true;
                continue;
            }

            if (target.ContainsKey(normalizedId)) continue;

            target[normalizedId] = version;
        }

        if (requiresSave) _hasPendingSave = true;
    }

    private void LoadInstalledColumnVisibility(JsonNode? node)
    {
        _installedColumnVisibility.Clear();

        if (node is not JsonObject obj) return;

        foreach (var pair in obj)
        {
            var columnKey = NormalizeInstalledColumnKey(pair.Key);
            var isVisible = pair.Value?.GetValue<bool?>();

            if (columnKey is null || !isVisible.HasValue) continue;

            _installedColumnVisibility[columnKey] = isVisible.Value;
        }
    }

    private void LoadThemePalette(JsonNode? node)
    {
        var defaults = GetCurrentThemeDefaults();

        if (node is not JsonObject obj)
        {
            EnsurePaletteDefaults(_themePaletteColors, defaults);
            _hasPendingSave = true;
            return;
        }

        var requiresSave = false;
        var processedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in obj)
        {
            var key = pair.Key;
            if (string.IsNullOrWhiteSpace(key)) continue;

            var normalizedKey = key.Trim();
            if (!_themePaletteColors.ContainsKey(normalizedKey))
            {
                if (defaults.ContainsKey(normalizedKey)) _themePaletteColors[normalizedKey] = defaults[normalizedKey];

                continue;
            }

            var value = GetOptionalString(pair.Value);
            if (!TryNormalizeHexColor(value, out var normalized))
            {
                requiresSave = true;
                continue;
            }

            _themePaletteColors[normalizedKey] = normalized;
            processedKeys.Add(normalizedKey);
        }

        foreach (var pair in defaults)
        {
            if (!processedKeys.Contains(pair.Key)) requiresSave = true;

            if (!_themePaletteColors.ContainsKey(pair.Key)) _themePaletteColors[pair.Key] = pair.Value;
        }

        if (requiresSave) _hasPendingSave = true;
    }

    private void LoadModUsageTracking(JsonNode? node, GameProfileState profile)
    {
        profile.LongRunningSessionCount = 0;
        profile.HasPendingModUsagePrompt = false;
        profile.ModUsageSessionCounts.Clear();

        if (node is not JsonObject obj) return;

        var storedCount = obj["longSessionCount"]?.GetValue<int?>();
        profile.LongRunningSessionCount = storedCount.HasValue && storedCount.Value > 0 ? storedCount.Value : 0;
        profile.HasPendingModUsagePrompt = obj["pendingPrompt"]?.GetValue<bool?>() ?? false;

        var countsNode = obj["modCounts"];

        if (countsNode is JsonArray array)
            foreach (var item in array)
            {
                if (item is not JsonObject entry) continue;

                var modId = GetOptionalString(entry["modId"]);
                var modVersion = GetOptionalString(entry["modVersion"]);
                var gameVersion = GetOptionalString(entry["gameVersion"]);
                var usageCount = entry["count"]?.GetValue<int?>();

                if (string.IsNullOrWhiteSpace(modId)
                    || string.IsNullOrWhiteSpace(modVersion)
                    || string.IsNullOrWhiteSpace(gameVersion)
                    || !usageCount.HasValue
                    || usageCount.Value <= 0)
                    continue;

                var key = new ModUsageTrackingKey(modId, modVersion, gameVersion);
                if (!key.IsValid) continue;

                profile.ModUsageSessionCounts[key] = usageCount.Value;
            }
        else if (countsNode is JsonObject legacyCounts)
            foreach (var (key, value) in legacyCounts)
            {
                if (string.IsNullOrWhiteSpace(key) || value is null) continue;

                var usageCount = value.GetValue<int?>();
                if (!usageCount.HasValue || usageCount.Value <= 0) continue;

                var normalizedId = NormalizeModId(key);
                if (normalizedId is null) continue;

                _hasPendingSave = true;
            }

        if (profile.ModUsageSessionCounts.Count == 0)
        {
            profile.LongRunningSessionCount = 0;
            profile.HasPendingModUsagePrompt = false;
        }
        else if (profile.LongRunningSessionCount >= GameSessionVoteThreshold)
        {
            profile.HasPendingModUsagePrompt = true;
        }
    }

    private bool LoadCustomThemePalette(JsonNode? node)
    {
        var defaults = GetDefaultPalette(ColorTheme.Custom);
        _customThemePaletteColors.Clear();

        var requiresSave = false;
        bool propertyFound;
        var processedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (node is JsonObject obj)
        {
            propertyFound = true;

            foreach (var pair in obj)
            {
                var key = pair.Key;
                if (string.IsNullOrWhiteSpace(key)) continue;

                var normalizedKey = key.Trim();
                if (!defaults.ContainsKey(normalizedKey)) continue;

                var value = GetOptionalString(pair.Value);
                if (!TryNormalizeHexColor(value, out var normalized))
                {
                    requiresSave = true;
                    continue;
                }

                _customThemePaletteColors[normalizedKey] = normalized;
                processedKeys.Add(normalizedKey);
            }
        }
        else
        {
            propertyFound = false;
            requiresSave = true;
        }

        foreach (var pair in defaults)
        {
            if (!processedKeys.Contains(pair.Key)) requiresSave = true;

            if (!_customThemePaletteColors.ContainsKey(pair.Key)) _customThemePaletteColors[pair.Key] = pair.Value;
        }

        if (requiresSave) _hasPendingSave = true;

        return propertyFound;
    }

    private void SyncCustomThemePaletteWithCurrentTheme()
    {
        EnsureCustomThemePaletteInitialized();

        foreach (var pair in _themePaletteColors) _customThemePaletteColors[pair.Key] = pair.Value;

        EnsurePaletteDefaults(_customThemePaletteColors, GetDefaultPalette(ColorTheme.Custom));
    }

    private void ResetThemePaletteToDefaults()
    {
        _themePaletteColors.Clear();

        if (ColorTheme == ColorTheme.Custom)
        {
            EnsureCustomThemePaletteInitialized();
            foreach (var pair in _customThemePaletteColors) _themePaletteColors[pair.Key] = pair.Value;

            EnsurePaletteDefaults(_themePaletteColors, GetDefaultPalette(ColorTheme.Custom));
            return;
        }

        foreach (var pair in GetDefaultPalette(ColorTheme)) _themePaletteColors[pair.Key] = pair.Value;
    }

    private bool ApplyThemePaletteOverride(IReadOnlyDictionary<string, string> paletteOverride)
    {
        var changed = false;

        foreach (var pair in paletteOverride)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;

            var normalizedKey = pair.Key.Trim();
            if (!_themePaletteColors.ContainsKey(normalizedKey)) continue;

            if (!TryNormalizeHexColor(pair.Value, out var normalizedValue)) continue;

            if (!_themePaletteColors.TryGetValue(normalizedKey, out var currentValue)
                || !string.Equals(currentValue, normalizedValue, StringComparison.OrdinalIgnoreCase))
            {
                _themePaletteColors[normalizedKey] = normalizedValue;
                if (ColorTheme == ColorTheme.Custom)
                {
                    EnsureCustomThemePaletteInitialized();
                    _customThemePaletteColors[normalizedKey] = normalizedValue;
                }

                changed = true;
            }
        }

        return changed;
    }

    private void ResetCustomThemePaletteToDefaults()
    {
        _customThemePaletteColors.Clear();

        foreach (var pair in GetDefaultPalette(ColorTheme.Custom)) _customThemePaletteColors[pair.Key] = pair.Value;
    }

    private void EnsureCustomThemePaletteInitialized()
    {
        if (_customThemePaletteColors.Count == 0)
        {
            ResetCustomThemePaletteToDefaults();
            return;
        }

        EnsurePaletteDefaults(_customThemePaletteColors, GetDefaultPalette(ColorTheme.Custom));
    }

    private IReadOnlyDictionary<string, string> GetCurrentThemeDefaults()
    {
        if (ColorTheme == ColorTheme.Custom)
        {
            EnsureCustomThemePaletteInitialized();
            return _customThemePaletteColors;
        }

        return GetDefaultPalette(ColorTheme);
    }

    private static void EnsurePaletteDefaults(
        IDictionary<string, string> palette,
        IReadOnlyDictionary<string, string> defaults)
    {
        foreach (var pair in defaults)
            if (!palette.ContainsKey(pair.Key))
                palette[pair.Key] = pair.Value;
    }

    private static IReadOnlyDictionary<string, string> GetDefaultPalette(ColorTheme theme)
    {
        return theme switch
        {
            ColorTheme.VintageStory => VintageStoryPaletteColors,
            ColorTheme.Dark => DarkPaletteColors,
            ColorTheme.Light => LightPaletteColors,
            ColorTheme.SurpriseMe => VintageStoryPaletteColors,
            ColorTheme.Custom => VintageStoryPaletteColors,
            _ => VintageStoryPaletteColors
        };
    }

    private void LoadPersistentModConfigPaths()
    {
        _storedModConfigPaths.Clear();

        var conversionNeeded = false;

        try
        {
            if (!File.Exists(_modConfigPathsPath)) return;

            using var stream = File.OpenRead(_modConfigPathsPath);
            var node = JsonNode.Parse(stream);
            if (node is not JsonObject obj) return;

            var currentVersion = NormalizeVersion(ModManagerVersion) ?? ModManagerVersion;
            var storedVersion = NormalizeVersion(GetOptionalString(obj[ModConfigPathHistoryVersionPropertyName]));
            conversionNeeded = string.IsNullOrWhiteSpace(storedVersion)
                               || !string.Equals(storedVersion, currentVersion, StringComparison.Ordinal);

            var entriesObj = obj[ModConfigPathHistoryEntriesPropertyName] as JsonObject;
            var usingLegacyRootEntries = entriesObj is null;
            entriesObj ??= obj;

            var modConfigDirectory = GetActiveModConfigDirectory();

            foreach (var (key, value) in entriesObj)
            {
                if (usingLegacyRootEntries
                    && string.Equals(key, ModConfigPathHistoryVersionPropertyName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(key) || value is not JsonObject entryObj) continue;

                var directoryValue = GetOptionalString(entryObj[ModConfigPathHistoryDirectoryPropertyName]);
                var fileNameValue = GetOptionalString(entryObj[ModConfigPathHistoryFileNamePropertyName]);
                var configNameValue = GetOptionalString(entryObj[ModConfigPathHistoryConfigNamePropertyName]);

                var relativeDirectory = NormalizeStoredModConfigDirectory(
                    directoryValue,
                    modConfigDirectory);
                var fileName = NormalizeStoredModConfigFileName(fileNameValue);
                var configName = NormalizeStoredModConfigName(configNameValue);

                if (relativeDirectory is null || fileName is null) continue;

                if (!conversionNeeded)
                {
                    var trimmedDirectory = string.IsNullOrWhiteSpace(directoryValue)
                        ? string.Empty
                        : directoryValue!.Trim();

                    if (Path.IsPathRooted(trimmedDirectory))
                    {
                        conversionNeeded = true;
                    }
                    else
                    {
                        var sanitizedDirectory = NormalizeRelativeModConfigDirectory(trimmedDirectory);
                        if (sanitizedDirectory is null
                            || !string.Equals(sanitizedDirectory, relativeDirectory, PathComparison))
                            conversionNeeded = true;
                    }

                    if (!conversionNeeded && !string.IsNullOrWhiteSpace(fileNameValue))
                    {
                        var trimmedFileName = fileNameValue!.Trim();
                        if (!string.Equals(trimmedFileName, fileName, PathComparison)) conversionNeeded = true;
                    }

                    if (!conversionNeeded && configNameValue is not null)
                    {
                        var trimmedConfigName = configNameValue!.Trim();
                        if (string.IsNullOrWhiteSpace(configName))
                        {
                            if (!string.IsNullOrWhiteSpace(trimmedConfigName)) conversionNeeded = true;
                        }
                        else if (!string.Equals(trimmedConfigName, configName, StringComparison.Ordinal))
                        {
                            conversionNeeded = true;
                        }
                    }
                }

                var trimmedId = key.Trim();
                if (trimmedId.Length == 0) continue;

                var entry = new ModConfigPathEntry(relativeDirectory, fileName, configName);
                _storedModConfigPaths[trimmedId] = entry;

                var combinedPath = BuildFullConfigPath(entry);
                if (!string.IsNullOrWhiteSpace(combinedPath)) ActiveModConfigPaths[trimmedId] = combinedPath!;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or ArgumentException or NotSupportedException)
        {
            _storedModConfigPaths.Clear();
            conversionNeeded = false;
        }

        if (conversionNeeded) SaveModConfigPathHistory();
    }

    private void BackfillModConfigPathHistory()
    {
        var historyChanged = false;

        foreach (var profile in _gameProfiles.Values)
        {
            if (profile.ModConfigPaths.Count == 0) continue;

            var dataDirectory = profile.DataDirectory;
            if (string.IsNullOrWhiteSpace(dataDirectory)) continue;

            foreach (var pair in profile.ModConfigPaths)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) continue;

                var normalizedPath = NormalizePath(pair.Value);
                if (string.IsNullOrWhiteSpace(normalizedPath)) continue;

                var configName = ExtractFileName(normalizedPath);

                if (TryStorePersistentModConfigPath(
                        pair.Key,
                        normalizedPath,
                        dataDirectory,
                        configName,
                        false))
                    historyChanged = true;
            }
        }

        if (historyChanged)
        {
            SaveModConfigPathHistory();
            RefreshActiveModConfigPathsFromHistory();
        }
    }

    private void UpdatePersistentModConfigPath(string modId, string normalizedPath, string? configName)
    {
        if (TryStorePersistentModConfigPath(
                modId,
                normalizedPath,
                ActiveProfile.DataDirectory,
                configName,
                true))
            SaveModConfigPathHistory();
    }

    private void RefreshActiveModConfigPathsFromHistory()
    {
        ActiveModConfigPaths.Clear();

        if (_storedModConfigPaths.Count == 0) return;

        foreach (var pair in _storedModConfigPaths)
        {
            var combined = BuildFullConfigPath(pair.Value);
            if (string.IsNullOrWhiteSpace(combined)) continue;

            ActiveModConfigPaths[pair.Key] = combined!;
        }
    }

    private void SaveModConfigPathHistory()
    {
        _hasPendingModConfigPathSave = true;

        if (!_isPersistenceEnabled) return;

        PersistModConfigPathHistory();
    }

    private void PersistModConfigPathHistory()
    {
        try
        {
            var directory = Path.GetDirectoryName(_modConfigPathsPath)!;
            Directory.CreateDirectory(directory);

            var version = NormalizeVersion(ModManagerVersion) ?? ModManagerVersion;

            var entries = new JsonObject();

            foreach (var pair in _storedModConfigPaths.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                var entry = pair.Value;
                var entryObject = new JsonObject
                {
                    [ModConfigPathHistoryDirectoryPropertyName] = entry.RelativeDirectoryPath,
                    [ModConfigPathHistoryFileNamePropertyName] = entry.FileName
                };

                if (!string.IsNullOrWhiteSpace(entry.ConfigName))
                    entryObject[ModConfigPathHistoryConfigNamePropertyName] = entry.ConfigName;

                entries[pair.Key] = entryObject;
            }

            var root = new JsonObject
            {
                [ModConfigPathHistoryVersionPropertyName] = version,
                [ModConfigPathHistoryEntriesPropertyName] = entries
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            File.WriteAllText(_modConfigPathsPath, root.ToJsonString(options));
            _hasPendingModConfigPathSave = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            // Persisting the configuration is a best-effort attempt. Ignore failures silently.
        }
    }

    private string? BuildFullConfigPath(ModConfigPathEntry entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.FileName)) return null;

        var modConfigDirectory = GetActiveModConfigDirectory();
        if (string.IsNullOrWhiteSpace(modConfigDirectory)) return null;

        var directory = string.IsNullOrWhiteSpace(entry.RelativeDirectoryPath)
            ? modConfigDirectory
            : Path.Combine(modConfigDirectory, entry.RelativeDirectoryPath);

        var combined = Path.Combine(directory, entry.FileName);
        return NormalizePath(combined) ?? combined;
    }

    private bool TryStorePersistentModConfigPath(
        string modId,
        string normalizedPath,
        string? dataDirectory,
        string? configName,
        bool removeOnFailure)
    {
        if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(normalizedPath)) return false;

        var trimmedId = modId.Trim();
        if (trimmedId.Length == 0) return false;

        if (!TryGetRelativeModConfigLocation(normalizedPath, dataDirectory, out var relativeDirectory,
                out var fileName))
        {
            if (removeOnFailure
                && !string.IsNullOrWhiteSpace(dataDirectory)
                && _storedModConfigPaths.Remove(trimmedId))
                return true;

            return false;
        }

        var normalizedConfigName = NormalizeStoredModConfigName(configName);

        if (_storedModConfigPaths.TryGetValue(trimmedId, out var existing)
            && existing is not null)
        {
            if (normalizedConfigName is null && !string.IsNullOrWhiteSpace(existing.ConfigName))
                normalizedConfigName = existing.ConfigName;

            if (string.Equals(existing.RelativeDirectoryPath, relativeDirectory, PathComparison)
                && string.Equals(existing.FileName, fileName, PathComparison)
                && string.Equals(existing.ConfigName, normalizedConfigName, StringComparison.Ordinal))
                return false;
        }

        var entry = new ModConfigPathEntry(relativeDirectory, fileName, normalizedConfigName);
        _storedModConfigPaths[trimmedId] = entry;
        return true;
    }

    private bool TryGetRelativeModConfigLocation(
        string normalizedPath,
        string? dataDirectory,
        out string relativeDirectory,
        out string fileName)
    {
        relativeDirectory = string.Empty;
        fileName = string.Empty;

        if (string.IsNullOrWhiteSpace(normalizedPath)) return false;

        var modConfigDirectory = GetModConfigDirectory(dataDirectory);
        if (string.IsNullOrWhiteSpace(modConfigDirectory)) return false;

        if (!IsPathInDirectory(normalizedPath, modConfigDirectory)) return false;

        var extractedFileName = ExtractFileName(normalizedPath);
        if (string.IsNullOrWhiteSpace(extractedFileName)) return false;

        fileName = extractedFileName;

        var directory = ExtractDirectoryPath(normalizedPath) ?? modConfigDirectory;
        var relativeDirectoryCandidate = Path.GetRelativePath(modConfigDirectory, directory);
        var normalizedRelativeDirectory = NormalizeRelativeModConfigDirectory(relativeDirectoryCandidate);
        if (normalizedRelativeDirectory is null) return false;

        relativeDirectory = normalizedRelativeDirectory;
        return true;
    }

    private string? GetActiveModConfigDirectory()
    {
        return GetModConfigDirectory(ActiveProfile.DataDirectory);
    }

    private static void EnsureModConfigDirectoryExists(string? dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) return;

        try
        {
            var combined = Path.Combine(dataDirectory, ModConfigDirectoryName);
            Directory.CreateDirectory(combined);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static string? GetModConfigDirectory(string? dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) return null;

        var combined = Path.Combine(dataDirectory, ModConfigDirectoryName);
        return NormalizePath(combined);
    }

    private static string? NormalizeStoredModConfigDirectory(string? storedDirectory, string? modConfigDirectory)
    {
        if (string.IsNullOrWhiteSpace(storedDirectory)) return string.Empty;

        var trimmed = storedDirectory.Trim();

        if (Path.IsPathRooted(trimmed))
        {
            var normalized = NormalizePath(trimmed);
            if (normalized is null) return null;

            if (!string.IsNullOrWhiteSpace(modConfigDirectory) && IsPathInDirectory(normalized, modConfigDirectory!))
            {
                var relative = Path.GetRelativePath(modConfigDirectory!, normalized);
                return NormalizeRelativeModConfigDirectory(relative);
            }

            var extracted = ExtractRelativePathFromModConfigFolder(normalized);
            if (extracted is null) return null;

            return NormalizeRelativeModConfigDirectory(extracted);
        }

        return NormalizeRelativeModConfigDirectory(trimmed);
    }

    private static string? NormalizeStoredModConfigFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var trimmed = fileName.Trim();
        var sanitized = Path.GetFileName(trimmed);

        if (string.IsNullOrWhiteSpace(sanitized)) return null;

        return sanitized;
    }

    private static string? NormalizeStoredModConfigName(string? configName)
    {
        if (string.IsNullOrWhiteSpace(configName)) return null;

        var trimmed = configName.Trim();
        if (trimmed.Length == 0) return null;

        var sanitized = Path.GetFileName(trimmed).Trim();

        if (sanitized.Length == 0) sanitized = trimmed;

        return sanitized.Length == 0 ? null : sanitized;
    }

    private static string? NormalizeConfigName(string? configName)
    {
        return NormalizeStoredModConfigName(configName);
    }

    private static string? NormalizeRelativeModConfigDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var trimmed = path.Trim();
        if (trimmed.Length == 0 || string.Equals(trimmed, ".", StringComparison.Ordinal)) return string.Empty;

        var sanitized = trimmed.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        sanitized = sanitized.Trim(DirectorySeparators);

        if (sanitized.Length == 0) return string.Empty;

        var segments = sanitized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return string.Empty;

        foreach (var segment in segments)
            if (string.Equals(segment, ".", StringComparison.Ordinal)
                || string.Equals(segment, "..", StringComparison.Ordinal))
                return null;

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static string? ExtractRelativePathFromModConfigFolder(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath)) return null;

        var sanitized = normalizedPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var search = Path.DirectorySeparatorChar + ModConfigDirectoryName;
        var index = sanitized.IndexOf(search, PathComparison);

        while (index >= 0)
        {
            var segmentEnd = index + search.Length;
            var isFolderMatch = segmentEnd == sanitized.Length
                                || sanitized[segmentEnd] == Path.DirectorySeparatorChar;

            if (isFolderMatch)
            {
                if (segmentEnd < sanitized.Length && sanitized[segmentEnd] == Path.DirectorySeparatorChar) segmentEnd++;

                if (segmentEnd >= sanitized.Length) return string.Empty;

                return sanitized[segmentEnd..];
            }

            index = sanitized.IndexOf(search, segmentEnd, PathComparison);
        }

        return null;
    }

    private static bool IsPathInDirectory(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory)) return false;

        var normalizedPath = NormalizePath(path);
        var normalizedDirectory = NormalizePath(directory);

        if (normalizedPath is null || normalizedDirectory is null) return false;

        if (!normalizedDirectory.EndsWith(Path.DirectorySeparatorChar))
            normalizedDirectory += Path.DirectorySeparatorChar;

        return normalizedPath.StartsWith(normalizedDirectory, PathComparison);
    }

    private static string? ExtractDirectoryPath(string normalizedPath)
    {
        var directory = Path.GetDirectoryName(normalizedPath);

        if (string.IsNullOrWhiteSpace(directory)) directory = Path.GetPathRoot(normalizedPath);

        if (string.IsNullOrWhiteSpace(directory)) return null;

        return NormalizePath(directory);
    }

    private static string? ExtractFileName(string normalizedPath)
    {
        var trimmed = normalizedPath.Trim();
        var fileName = Path.GetFileName(trimmed);

        if (string.IsNullOrWhiteSpace(fileName))
            fileName = Path.GetFileName(trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.IsNullOrWhiteSpace(fileName)) return null;

        return fileName;
    }

    private static string GetPreferredConfigurationDirectory()
    {
        var localAppData = GetFolder(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData)) return Path.Combine(localAppData, "Simple VS Manager");

        var appData = GetFolder(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData)) return Path.Combine(appData, "Simple VS Manager");

        var documents = GetFolder(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents)) return Path.Combine(documents, "Simple VS Manager");

        var personal = GetFolder(Environment.SpecialFolder.Personal);
        if (!string.IsNullOrWhiteSpace(personal)) return Path.Combine(personal, "Simple VS Manager");

        var userProfile = GetFolder(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile)) return Path.Combine(userProfile, ".simple-vs-manager");

        return Path.Combine(AppContext.BaseDirectory, "Simple VS Manager");
    }

    private static string? GetFolder(Environment.SpecialFolder folder)
    {
        try
        {
            var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }

        return null;
    }

    private static bool TryNormalizeHexColor(string? value, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.Trim();
        if (!trimmed.StartsWith('#') || trimmed.Length <= 1) return false;

        var hex = trimmed[1..];
        if (hex.Length is not 6 and not 8) return false;

        foreach (var c in hex)
            if (!IsHexDigit(c))
                return false;

        normalized = "#" + hex.ToUpperInvariant();
        return true;
    }

    private static bool IsHexDigit(char c)
    {
        return (c >= '0' && c <= '9')
               || (c >= 'A' && c <= 'F')
               || (c >= 'a' && c <= 'f');
    }

    private static string? GetOptionalString(JsonNode? node)
    {
        if (node is null) return null;

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
        var assembly = typeof(UserConfigurationService).Assembly;

        var version = TrimToSemanticVersion(
            VersionStringUtility.Normalize(
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion));
        if (!string.IsNullOrWhiteSpace(version)) return version!;

        version = TrimToSemanticVersion(
            VersionStringUtility.Normalize(
                assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version));
        if (!string.IsNullOrWhiteSpace(version)) return version!;

        version = TrimToSemanticVersion(VersionStringUtility.Normalize(assembly.GetName().Version?.ToString()));
        return string.IsNullOrWhiteSpace(version) ? "0.0.0" : version!;
    }

    private static string? TrimToSemanticVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        if (parts.Length <= 3) return string.Join('.', parts);

        return string.Join('.', parts.Take(3));
    }

    private static string? NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var normalized = VersionStringUtility.Normalize(version);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

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
        if (string.IsNullOrWhiteSpace(sortMemberPath)) return null;

        var trimmed = sortMemberPath.Trim();

        if (string.Equals(trimmed, nameof(ModListItemViewModel.DisplayName), StringComparison.OrdinalIgnoreCase))
            return nameof(ModListItemViewModel.NameSortKey);

        return trimmed;
    }

    private static string? NormalizeInstalledColumnKey(string? columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName)) return null;

        return columnName.Trim();
    }

    private static string? NormalizeModId(string? modId)
    {
        return string.IsNullOrWhiteSpace(modId) ? null : modId.Trim();
    }

    private static ListSortDirection ParseSortDirection(string? value)
    {
        if (Enum.TryParse(value, true, out ListSortDirection direction)) return direction;

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

    private sealed class GameProfileState
    {
        public GameProfileState(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public string? DataDirectory { get; set; }

        public string? GameDirectory { get; set; }

        public string? CustomShortcutPath { get; set; }

        public bool RequiresDataDirectorySelection { get; set; }

        public bool RequiresGameDirectorySelection { get; set; }

        public Dictionary<string, string> ModConfigPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, bool> BulkUpdateModExclusions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> SkippedModVersions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<ModUsageTrackingKey, int> ModUsageSessionCounts { get; } = new();

        public int LongRunningSessionCount { get; set; }

        public bool HasPendingModUsagePrompt { get; set; }
    }

    private sealed class ModConfigPathEntry
    {
        public ModConfigPathEntry(string relativeDirectoryPath, string fileName, string? configName)
        {
            RelativeDirectoryPath = relativeDirectoryPath;
            FileName = fileName;
            ConfigName = configName;
        }

        public string RelativeDirectoryPath { get; }

        public string FileName { get; }

        public string? ConfigName { get; }
    }
}