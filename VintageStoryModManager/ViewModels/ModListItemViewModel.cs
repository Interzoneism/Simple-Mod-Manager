using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VintageStoryModManager.Models;
using VintageStoryModManager.Services;
using Application = System.Windows.Application;

namespace VintageStoryModManager.ViewModels;

/// <summary>
///     View model that wraps <see cref="ModEntry" /> for presentation in the UI.
/// </summary>
public sealed class ModListItemViewModel : ObservableObject
{
    private static readonly HttpClient HttpClient = new();

    private readonly Func<ModListItemViewModel, bool, Task<ActivationResult>> _activationHandler;
    private readonly IReadOnlyList<string> _authors;
    private readonly IReadOnlyList<string> _contributors;
    private readonly string? _description;
    private readonly ModDependencyInfo? _gameDependency;
    private readonly string? _installedGameVersion;
    private readonly string? _metadataError;

    private readonly Func<bool>? _requireExactVersionMatch;
    private readonly ModLoadingTimingService? _timingService;
    private string? _searchIndex;
    private readonly Func<string?, string?, bool>? _shouldSkipVersion;
    private readonly ModEntry _modEntry;
    private readonly string _constructorLocation;
    private string? _activationError;
    private int? _databaseComments;
    private int? _databaseDownloads;
    private int? _databaseRecentDownloads;
    private IReadOnlyList<string> _databaseRequiredGameVersions;
    private int? _databaseTenDayDownloads;
    private bool _hasActivationError;

    private bool _isActive;
    private bool _isSelected;
    private bool _isUserReportLoading;
    private string? _latestReleaseUserReportDisplay;
    private string? _latestReleaseUserReportTooltip;
    private string? _loadError;
    private DateTime? _modDatabaseLastUpdatedUtc;
    private ImageSource? _modDatabaseLogo;
    private string? _modDatabaseLogoSource;
    private string? _modDatabaseLogoUrl;
    private string? _modDatabaseSide;
    private ICommand? _openModDatabasePageCommand;
    private IReadOnlyList<ModReleaseInfo> _releases;
    private ModVersionOptionViewModel? _selectedVersionOption;
    private string _statusDetails = string.Empty;
    private string _statusText = string.Empty;
    private bool _suppressState;
    private string _tooltip = string.Empty;
    private string? _updateMessage;
    private string _userReportDisplay = "—";
    private bool _userReportHasError;
    private string? _userReportTooltip;
    private string? _versionWarningMessage;
    private bool _hasInitializedUserReportState;
    private ImageSource? _icon;
    private bool _iconInitialized;

    // Cached tag display string to avoid repeated string.Join allocations
    private string? _cachedDatabaseTagsDisplay;

    // Category grouping support
    private string _categoryId = Models.ModCategory.UncategorizedId;
    private string _categoryName = Models.ModCategory.UncategorizedName;
    private int _categorySortOrder = int.MaxValue;

    // Property change batching support
    private int _propertyChangeSuspendCount;
    private readonly HashSet<string> _pendingPropertyChanges = new();

    public ModListItemViewModel(
        ModEntry entry,
        bool isActive,
        string location,
        Func<ModListItemViewModel, bool, Task<ActivationResult>> activationHandler,
        string? installedGameVersion = null,
        bool isInstalled = false,
        Func<string?, string?, bool>? shouldSkipVersion = null,
        Func<bool>? requireExactVersionMatch = null,
        bool initializeUserReportState = true,
        ModLoadingTimingService? timingService = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _activationHandler = activationHandler ?? throw new ArgumentNullException(nameof(activationHandler));
        _installedGameVersion = installedGameVersion;
        _shouldSkipVersion = shouldSkipVersion;
        _requireExactVersionMatch = requireExactVersionMatch;
        _timingService = timingService;

        // Store for lazy search index building
        _modEntry = entry;
        _constructorLocation = location;

        ModId = entry.ModId;
        DisplayName = string.IsNullOrWhiteSpace(entry.Name) ? entry.ModId : entry.Name;
        NameSortKey = string.IsNullOrWhiteSpace(entry.ManifestName) ? DisplayName : entry.ManifestName;
        Version = entry.Version;
        NetworkVersion = entry.NetworkVersion;
        Website = entry.Website;
        SourcePath = entry.SourcePath;
        Location = location;
        IsModDatabaseEntry = string.Equals(location, "Mod Database", StringComparison.Ordinal);
        SourceKind = entry.SourceKind;
        _authors = entry.Authors;
        _contributors = entry.Contributors;
        var databaseInfo = entry.DatabaseInfo;
        DatabaseTags = databaseInfo?.Tags ?? Array.Empty<string>();
        _databaseRequiredGameVersions = databaseInfo?.RequiredGameVersions ?? Array.Empty<string>();
        LatestRelease = databaseInfo?.LatestRelease;
        LatestCompatibleRelease = databaseInfo?.LatestCompatibleRelease;
        _releases = databaseInfo?.Releases ?? Array.Empty<ModReleaseInfo>();
        ModDatabaseAssetId = databaseInfo?.AssetId;
        ModDatabasePageUrl = databaseInfo?.ModPageUrl;
        ModDatabasePageUri = TryCreateHttpUri(ModDatabasePageUrl);
        LogDebug(
            $"Initial database page URL '{FormatValue(ModDatabasePageUrl)}' resolved to '{FormatUri(ModDatabasePageUri)}'.");
        if (ModDatabasePageUri != null)
        {
            var commandUri = ModDatabasePageUri;
            _openModDatabasePageCommand = new RelayCommand(() => LaunchUri(commandUri));
        }

        _databaseDownloads = databaseInfo?.Downloads;
        _databaseComments = databaseInfo?.Comments;
        _databaseRecentDownloads = databaseInfo?.DownloadsLastThirtyDays;
        _databaseTenDayDownloads = databaseInfo?.DownloadsLastTenDays;
        _modDatabaseLogoSource = databaseInfo?.LogoUrlSource;
        _modDatabaseLogoUrl = databaseInfo?.LogoUrl;
        // Defer logo image creation - will be created on first access via ModDatabasePreviewImage property
        _modDatabaseLogo = null;
        LogDebug(
            $"Deferred database logo creation. Source URL: '{FormatValue(_modDatabaseLogoUrl)}'.");
        ModDatabaseRelevancySortKey = entry.ModDatabaseSearchScore ?? 0;
        _modDatabaseLastUpdatedUtc = databaseInfo?.LastReleasedUtc ?? DetermineLastUpdatedFromReleases(_releases);
        if (_databaseRecentDownloads is null)
            _databaseRecentDownloads = CalculateDownloadsLastThirtyDaysFromReleases(_releases);
        if (_databaseTenDayDownloads is null)
            _databaseTenDayDownloads = CalculateDownloadsLastTenDaysFromReleases(_releases);
        LatestDatabaseVersion = LatestRelease?.Version
                                ?? databaseInfo?.LatestVersion
                                ?? LatestCompatibleRelease?.Version
                                ?? databaseInfo?.LatestCompatibleVersion;
        var initialUserReportVersion = !string.IsNullOrWhiteSpace(entry.Version)
            ? entry.Version
            : SelectPreferredUserReportVersion(LatestRelease, LatestCompatibleRelease, databaseInfo);
        SetUserReportVersion(initialUserReportVersion, false);
        _loadError = entry.LoadError;

        IsInstalled = isInstalled;

        WebsiteUri = TryCreateHttpUri(Website);
        LogDebug($"Website URL '{FormatValue(Website)}' resolved to '{FormatUri(WebsiteUri)}'.");
        OpenWebsiteCommand = WebsiteUri != null ? new RelayCommand(() => LaunchUri(WebsiteUri)) : null;

        var dependencies = entry.Dependencies ?? Array.Empty<ModDependencyInfo>();
        _gameDependency =
            dependencies.FirstOrDefault(d => string.Equals(d.ModId, "game", StringComparison.OrdinalIgnoreCase))
            ?? dependencies.FirstOrDefault(d => d.IsGameOrCoreDependency);

        if (dependencies.Count == 0)
        {
            Dependencies = Array.Empty<ModDependencyInfo>();
        }
        else
        {
            var filtered = dependencies.Where(d => !d.IsGameOrCoreDependency).ToArray();
            Dependencies = filtered.Length == 0 ? Array.Empty<ModDependencyInfo>() : filtered;
        }

        MissingDependencies = entry.MissingDependencies is { Count: > 0 }
            ? entry.MissingDependencies.ToArray()
            : Array.Empty<ModDependencyInfo>();
        DependencyHasErrors = entry.DependencyHasErrors;
        _description = entry.Description;
        _metadataError = entry.Error;
        Side = entry.Side;
        RequiredOnClient = entry.RequiredOnClient;
        RequiredOnServer = entry.RequiredOnServer;
        _modDatabaseSide = entry.DatabaseInfo?.Side;

        // Icon will be lazily created on first access via Icon property getter
        _icon = null;
        _iconInitialized = false;
        LogDebug($"Deferred icon image creation. Will be created on first access.");

        _isActive = isActive;
        HasErrors = entry.HasErrors;

        InitializeUpdateAvailability();
        InitializeVersionOptions();
        InitializeVersionWarning(_installedGameVersion);
        UpdateNewerReleaseChangelogs();
        UpdateStatusFromErrors();
        UpdateTooltip();
        if (initializeUserReportState)
        {
            InitializeUserReportState(_installedGameVersion, UserReportModVersion);
        }
        else
        {
            _userReportDisplay = string.Empty;
            _userReportTooltip = null;
            _hasInitializedUserReportState = false;
        }
        // Defer search index building - will be built on first search
        _searchIndex = null;
    }

    public string ModId { get; }

    public string DisplayName { get; }

    public string NameSortKey { get; }

    public string? Version { get; }

    public string VersionDisplay => string.IsNullOrWhiteSpace(Version) ? "—" : Version!;

    public string? NetworkVersion { get; }

    public string GameVersionDisplay
    {
        get
        {
            if (_gameDependency is { } dependency)
            {
                var version = dependency.Version?.Trim() ?? string.Empty;
                if (version.Length > 0) return version;
            }

            return string.IsNullOrWhiteSpace(NetworkVersion) ? "—" : NetworkVersion!;
        }
    }

    public string AuthorsDisplay => _authors.Count == 0 ? "—" : string.Join(", ", _authors);

    public string ContributorsDisplay => _contributors.Count == 0 ? "—" : string.Join(", ", _contributors);

    public string DependenciesDisplay => Dependencies.Count == 0
        ? "—"
        : string.Join(", ", Dependencies.Select(dependency => dependency.Display));

    public IReadOnlyList<ModDependencyInfo> Dependencies { get; }

    public IReadOnlyList<ModDependencyInfo> MissingDependencies { get; private set; }

    public bool DependencyHasErrors { get; private set; }

    public bool HasDependencyIssues => DependencyHasErrors || MissingDependencies.Count > 0;

    public bool CanFixDependencyIssues => HasDependencyIssues || HasLoadError;

    public IReadOnlyList<string> DatabaseTags { get; private set; }

    public string DatabaseTagsDisplay
    {
        get
        {
            var cached = _cachedDatabaseTagsDisplay;
            if (cached is not null)
                return cached;

            cached = DatabaseTags.Count == 0 ? "—" : string.Join(", ", DatabaseTags);
            _cachedDatabaseTagsDisplay = cached;
            return cached;
        }
    }

    public string? ModDatabaseAssetId { get; private set; }

    public string? ModDatabasePageUrl { get; private set; }

    public Uri? ModDatabasePageUri { get; private set; }

    public bool HasModDatabasePageLink => ModDatabasePageUri != null;

    public string ModDatabasePageUrlDisplay =>
        string.IsNullOrWhiteSpace(ModDatabasePageUrl) ? "—" : ModDatabasePageUrl!;

    public string DownloadsDisplay => _databaseDownloads.HasValue
        ? _databaseDownloads.Value.ToString("N0", CultureInfo.CurrentCulture)
        : "—";

    public int ModDatabaseDownloadsSortKey => _databaseDownloads ?? 0;

    public string RecentDownloadsDisplay => _databaseRecentDownloads.HasValue
        ? _databaseRecentDownloads.Value.ToString("N0", CultureInfo.CurrentCulture)
        : "—";

    public string TenDayDownloadsDisplay => _databaseTenDayDownloads.HasValue
        ? _databaseTenDayDownloads.Value.ToString("N0", CultureInfo.CurrentCulture)
        : "—";

    public int ModDatabaseRecentDownloadsSortKey => _databaseRecentDownloads ?? 0;

    public double ModDatabaseRelevancySortKey { get; }

    public long ModDatabaseLastUpdatedSortKey => _modDatabaseLastUpdatedUtc?.Ticks ?? 0L;

    public string CommentsDisplay => _databaseComments.HasValue
        ? _databaseComments.Value.ToString("N0", CultureInfo.CurrentCulture)
        : "—";

    public ImageSource? ModDatabasePreviewImage
    {
        get
        {
            // Prefer icon if available, otherwise use database logo
            var iconImage = Icon;
            if (iconImage != null)
                return iconImage;

            // Lazy load database logo on first access
            if (_modDatabaseLogo == null && !string.IsNullOrWhiteSpace(_modDatabaseLogoUrl))
            {
                _modDatabaseLogo = CreateModDatabaseLogoImage();
                LogDebug($"Lazy database logo loaded. URL='{FormatValue(_modDatabaseLogoUrl)}', Image created={_modDatabaseLogo is not null}.");
            }

            return _modDatabaseLogo;
        }
    }

    public bool HasModDatabasePreviewImage => ModDatabasePreviewImage != null;

    public string? LatestDatabaseVersion { get; private set; }

    public string LatestDatabaseVersionDisplay
    {
        get
        {
            if (InternetAccessManager.IsInternetAccessDisabled) return "?";

            return string.IsNullOrWhiteSpace(LatestDatabaseVersion) ? "—" : LatestDatabaseVersion!;
        }
    }

    public string LatestVersionSortKey
    {
        get
        {
            var version = string.IsNullOrWhiteSpace(LatestDatabaseVersion)
                ? LatestDatabaseVersionDisplay
                : LatestDatabaseVersion!;
            var order = CanUpdate ? 0 : 1;

            return string.Create(
                version.Length + 2,
                (Order: order, Version: version),
                static (span, state) =>
                {
                    span[0] = (char)('0' + state.Order);
                    span[1] = '|';
                    state.Version.AsSpan().CopyTo(span[2..]);
                });
        }
    }

    public bool IsInstalled { get; }

    public bool CanUpdate { get; private set; }

    public bool RequiresCompatibilitySelection => CanUpdate
                                                  && LatestRelease != null
                                                  && !LatestRelease.IsCompatibleWithInstalledGame
                                                  && LatestCompatibleRelease != null
                                                  && !string.Equals(LatestRelease.Version,
                                                      LatestCompatibleRelease.Version,
                                                      StringComparison.OrdinalIgnoreCase);

    public bool HasCompatibleUpdate => LatestCompatibleRelease != null;

    public bool LatestReleaseIsCompatible => LatestRelease?.IsCompatibleWithInstalledGame ?? false;

    public bool ShouldHighlightLatestVersion => CanUpdate;

    public ModReleaseInfo? LatestRelease { get; private set; }

    public ModReleaseInfo? LatestCompatibleRelease { get; private set; }

    public IReadOnlyList<ModVersionOptionViewModel> VersionOptions { get; private set; } =
        Array.Empty<ModVersionOptionViewModel>();

    public bool HasVersionOptions => VersionOptions.Count > 0;

    public bool HasDownloadableRelease => LatestRelease != null || LatestCompatibleRelease != null;

    public IReadOnlyList<ReleaseChangelog> NewerReleaseChangelogs { get; private set; } =
        Array.Empty<ReleaseChangelog>();

    public bool HasNewerReleaseChangelogs => NewerReleaseChangelogs.Count > 0;

    public ModVersionVoteSummary? LatestReleaseUserReportSummary { get; private set; }

    public string? LatestReleaseUserReportVersion { get; private set; }

    public string? LatestReleaseUserReportDisplay
    {
        get => _latestReleaseUserReportDisplay;
        private set
        {
            if (SetProperty(ref _latestReleaseUserReportDisplay, value))
                OnPropertyChanged(nameof(HasLatestReleaseUserReport));
        }
    }

    public string? LatestReleaseUserReportTooltip
    {
        get => _latestReleaseUserReportTooltip;
        private set => SetProperty(ref _latestReleaseUserReportTooltip, value);
    }

    public bool HasLatestReleaseUserReport => !string.IsNullOrWhiteSpace(_latestReleaseUserReportDisplay);

    public ModVersionVoteSummary? UserReportSummary { get; private set; }

    public string? UserReportModVersion { get; private set; }

    public ModVersionVoteCounts UserReportCounts => UserReportSummary?.Counts ?? ModVersionVoteCounts.Empty;

    public ModVersionVoteOption? UserVoteOption => UserReportSummary?.UserVote;

    public string UserReportDisplay
    {
        get => _userReportDisplay;
        private set => SetProperty(ref _userReportDisplay, value);
    }

    public string? UserReportTooltip
    {
        get => _userReportTooltip;
        private set => SetProperty(ref _userReportTooltip, value);
    }

    public bool IsUserReportLoading
    {
        get => _isUserReportLoading;
        private set => SetProperty(ref _isUserReportLoading, value);
    }

    public bool UserReportHasError
    {
        get => _userReportHasError;
        private set => SetProperty(ref _userReportHasError, value);
    }

    public bool CanSubmitUserReport => !string.IsNullOrWhiteSpace(_installedGameVersion)
                                       && !string.IsNullOrWhiteSpace(UserReportModVersion);

    public string InstallButtonToolTip
    {
        get
        {
            if (!HasDownloadableRelease) return "No downloadable releases are available.";

            if (LatestRelease?.IsCompatibleWithInstalledGame == true)
                return $"Install version {LatestRelease.Version}.";

            if (LatestCompatibleRelease != null)
                return $"Install compatible version {LatestCompatibleRelease.Version}.";

            if (LatestRelease != null) return $"Install version {LatestRelease.Version} (may be incompatible).";

            return "No downloadable releases are available.";
        }
    }

    public ModVersionOptionViewModel? SelectedVersionOption
    {
        get => _selectedVersionOption;
        set => SetProperty(ref _selectedVersionOption, value);
    }

    public string UpdateButtonToolTip
    {
        get
        {
            if (LatestRelease is null) return "No updates available.";

            if (LatestRelease.IsCompatibleWithInstalledGame) return $"Install version {LatestRelease.Version}.";

            if (LatestCompatibleRelease != null
                && !string.Equals(LatestRelease.Version, LatestCompatibleRelease.Version,
                    StringComparison.OrdinalIgnoreCase))
                return $"Latest: {LatestRelease.Version}. Compatible: {LatestCompatibleRelease.Version}.";

            return $"Install version {LatestRelease.Version} (may be incompatible).";
        }
    }

    public string DescriptionDisplay =>
        string.IsNullOrWhiteSpace(_description) ? "No description available." : _description!;

    public string? Website { get; }

    public Uri? WebsiteUri { get; }

    public bool HasWebsiteLink => WebsiteUri != null;

    public ICommand? OpenWebsiteCommand { get; }

    public ICommand? OpenModDatabasePageCommand => _openModDatabasePageCommand;

    public string SourcePath { get; }

    public string Location { get; }

    public bool IsModDatabaseEntry { get; }

    public ModSourceKind SourceKind { get; }

    public string SourceKindDisplay => SourceKind switch
    {
        ModSourceKind.ZipArchive => "Zip",
        ModSourceKind.Folder => "Folder",
        ModSourceKind.Assembly => "Assembly",
        ModSourceKind.SourceCode => "Source",
        _ => SourceKind.ToString()
    };

    public string? Side { get; }

    public string SideDisplay
    {
        get
        {
            var preferredSide = GetPreferredSide();
            return string.IsNullOrWhiteSpace(preferredSide) ? "—" : GetCapitalizedSide(preferredSide)!;
        }
    }

    public string? SideSortValue => GetCapitalizedSide(GetPreferredSide());

    public bool? RequiredOnClient { get; }

    public string RequiredOnClientDisplay => RequiredOnClient.HasValue ? RequiredOnClient.Value ? "Yes" : "No" : "—";

    public bool? RequiredOnServer { get; }

    public string RequiredOnServerDisplay => RequiredOnServer.HasValue ? RequiredOnServer.Value ? "Yes" : "No" : "—";

    public ImageSource? Icon
    {
        get
        {
            if (!_iconInitialized)
            {
                _iconInitialized = true;

                using (_timingService?.MeasureIconLoad())
                {
                    _icon = CreateImage(_modEntry.IconBytes, "Icon bytes");
                    LogDebug($"Lazy icon image created: {_icon is not null}. Will fall back to database logo when null.");
                }
            }
            return _icon;
        }
    }

    public bool HasErrors { get; }

    public bool HasLoadError => !string.IsNullOrWhiteSpace(_loadError);

    public bool CanToggle => !HasErrors && !HasLoadError;

    public bool HasActivationError
    {
        get => _hasActivationError;
        private set => SetProperty(ref _hasActivationError, value);
    }

    public string? ActivationError
    {
        get => _activationError;
        private set
        {
            if (SetProperty(ref _activationError, value))
            {
                HasActivationError = !string.IsNullOrWhiteSpace(value);
                UpdateStatusFromErrors();
                UpdateTooltip();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value)) OnPropertyChanged(nameof(StatusSortOrder));
        }
    }

    public int StatusSortOrder => StatusText switch
    {
        "Error" => 0,
        "Warning" => 1,
        _ => 2
    };

    public string StatusDetails
    {
        get => _statusDetails;
        private set => SetProperty(ref _statusDetails, value);
    }

    public string Tooltip
    {
        get => _tooltip;
        private set => SetProperty(ref _tooltip, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_suppressState)
            {
                if (SetProperty(ref _isActive, value))
                {
                    OnPropertyChanged(nameof(ActiveSortOrder));
                    OnPropertyChanged(nameof(CanFixDependencyIssues));
                }

                return;
            }

            if (_isActive == value) return;

            var previous = _isActive;
            if (!SetProperty(ref _isActive, value)) return;

            OnPropertyChanged(nameof(ActiveSortOrder));
            OnPropertyChanged(nameof(CanFixDependencyIssues));
            _ = ApplyActivationChangeAsync(previous, value);
        }
    }

    public int ActiveSortOrder => _isActive ? 0 : 1;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    #region Category Properties

    /// <summary>
    ///     The ID of the category this mod is assigned to.
    /// </summary>
    public string CategoryId
    {
        get => _categoryId;
        private set
        {
            if (SetProperty(ref _categoryId, value))
                OnPropertyChanged(nameof(CategoryGroupKey));
        }
    }

    /// <summary>
    ///     The display name of the category this mod is assigned to.
    /// </summary>
    public string CategoryName
    {
        get => _categoryName;
        private set
        {
            if (SetProperty(ref _categoryName, value))
                OnPropertyChanged(nameof(CategoryGroupKey));
        }
    }

    /// <summary>
    ///     The sort order of the category (lower values appear first).
    /// </summary>
    public int CategorySortOrder
    {
        get => _categorySortOrder;
        private set
        {
            if (SetProperty(ref _categorySortOrder, value))
                OnPropertyChanged(nameof(CategoryGroupKey));
        }
    }

    /// <summary>
    ///     Composite key for grouping by category. Format: "{Order:D10}|{Name}"
    ///     This ensures categories are sorted by order, then by name for display.
    /// </summary>
    public string CategoryGroupKey => $"{_categorySortOrder:D10}|{_categoryName}";

    /// <summary>
    ///     Updates the category assignment for this mod.
    /// </summary>
    public void UpdateCategory(string categoryId, string categoryName, int categorySortOrder)
    {
        using (SuspendPropertyChangeNotifications())
        {
            CategoryId = categoryId ?? Models.ModCategory.UncategorizedId;
            CategoryName = categoryName ?? Models.ModCategory.UncategorizedName;
            CategorySortOrder = categorySortOrder;
        }
    }

    #endregion

    public void RefreshInternetAccessDependentState()
    {
        OnPropertyChanged(nameof(LatestDatabaseVersionDisplay));
        OnPropertyChanged(nameof(LatestVersionSortKey));
    }

    public IReadOnlyList<ReleaseChangelog> GetChangelogEntriesForUpgrade(string? targetVersion)
    {
        if (_releases.Count == 0 || string.IsNullOrWhiteSpace(targetVersion)) return Array.Empty<ReleaseChangelog>();

        var trimmedTarget = targetVersion.Trim();
        var normalizedTarget = VersionStringUtility.Normalize(targetVersion);

        var installedVersion = Version;
        var normalizedInstalled = VersionStringUtility.Normalize(installedVersion);

        var entries = new List<ReleaseChangelog>();
        var capturing = false;

        foreach (var release in _releases)
        {
            if (!capturing && DoesReleaseMatchVersion(release, trimmedTarget, normalizedTarget)) capturing = true;

            if (!capturing) continue;

            if (!string.IsNullOrWhiteSpace(installedVersion)
                && DoesReleaseMatchVersion(release, installedVersion.Trim(), normalizedInstalled))
                break;

            var changelog = release.Changelog?.Trim();
            if (!string.IsNullOrEmpty(changelog)) entries.Add(new ReleaseChangelog(release.Version, changelog));
        }

        return entries.Count == 0 ? Array.Empty<ReleaseChangelog>() : entries;
    }

    private bool SetUserReportVersion(string? version, bool reinitializeState)
    {
        var normalized = NormalizeVersion(version);
        if (string.Equals(UserReportModVersion, normalized, StringComparison.Ordinal)) return false;

        UserReportModVersion = normalized;
        OnPropertyChanged(nameof(UserReportModVersion));
        OnPropertyChanged(nameof(CanSubmitUserReport));

        if (reinitializeState) InitializeUserReportState(_installedGameVersion, UserReportModVersion);

        return true;
    }

    private static string? NormalizeVersion(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? SelectPreferredUserReportVersion(
        ModReleaseInfo? latestRelease,
        ModReleaseInfo? latestCompatibleRelease,
        ModDatabaseInfo? databaseInfo)
    {
        return latestRelease?.Version
               ?? databaseInfo?.LatestVersion
               ?? latestCompatibleRelease?.Version
               ?? databaseInfo?.LatestCompatibleVersion;
    }

    private void InitializeUserReportState(string? installedGameVersion, string? modVersion)
    {
        _hasInitializedUserReportState = true;

        if (string.IsNullOrWhiteSpace(installedGameVersion) || string.IsNullOrWhiteSpace(modVersion))
        {
            SetUserReportUnavailable("User reports require a known Vintage Story and mod version.");
            return;
        }

        SetUserReportLoading();
    }

    public void SetUserReportLoading()
    {
        _hasInitializedUserReportState = true;
        ClearUserReportSummary();
        UserReportHasError = false;
        IsUserReportLoading = true;
        UserReportDisplay = "Loading…";
        UserReportTooltip = "Fetching user reports…";
    }

    public void SetUserReportOffline()
    {
        _hasInitializedUserReportState = true;
        IsUserReportLoading = false;
        UserReportHasError = false;
        UserReportDisplay = "Offline";
        UserReportTooltip = "Enable Internet Access in the File menu to load user reports.";
    }

    public void SetUserReportUnavailable(string message)
    {
        _hasInitializedUserReportState = true;
        ClearUserReportSummary();
        UserReportHasError = false;
        IsUserReportLoading = false;
        UserReportDisplay = "Unavailable";
        UserReportTooltip = string.IsNullOrWhiteSpace(message) ? BuildUserReportTooltip(null) : message;
    }

    public void SetUserReportError(string message)
    {
        _hasInitializedUserReportState = true;
        IsUserReportLoading = false;
        UserReportHasError = true;
        UserReportTooltip = string.IsNullOrWhiteSpace(message)
            ? "Failed to load user reports. Try again later."
            : message;

        if (UserReportSummary is null) UserReportDisplay = "Error";
    }

    public void ApplyUserReportSummary(ModVersionVoteSummary summary)
    {
        using (_timingService?.MeasureUserReportsLoad())
        {
            UserReportSummary = summary ?? throw new ArgumentNullException(nameof(summary));
            _hasInitializedUserReportState = true;
            IsUserReportLoading = false;
            UserReportHasError = false;
            UserReportDisplay = BuildUserReportDisplay(summary);
            UserReportTooltip = BuildUserReportTooltip(summary);
            OnPropertyChanged(nameof(UserReportSummary));
            OnPropertyChanged(nameof(UserReportCounts));
            OnPropertyChanged(nameof(UserVoteOption));
        }
    }

    private void ClearUserReportSummary()
    {
        if (UserReportSummary is null)
        {
            OnPropertyChanged(nameof(UserReportSummary));
            OnPropertyChanged(nameof(UserReportCounts));
            OnPropertyChanged(nameof(UserVoteOption));
            return;
        }

        UserReportSummary = null;
        OnPropertyChanged(nameof(UserReportSummary));
        OnPropertyChanged(nameof(UserReportCounts));
        OnPropertyChanged(nameof(UserVoteOption));
    }

    public void EnsureUserReportStateInitialized()
    {
        if (_hasInitializedUserReportState) return;

        if (Application.Current?.Dispatcher.CheckAccess() == true)
        {
            InitializeUserReportState(_installedGameVersion, UserReportModVersion);
        }
        else
        {
            Application.Current?.Dispatcher.Invoke(() => EnsureUserReportStateInitialized());
        }
    }

    private static string BuildUserReportDisplay(ModVersionVoteSummary? summary)
    {
        if (summary is null || summary.TotalVotes == 0) return "No votes";

        var majority = summary.GetMajorityOption();
        if (majority is null)
            return string.Create(
                8 + summary.TotalVotes.ToString(CultureInfo.CurrentCulture).Length,
                summary.TotalVotes,
                static (span, total) =>
                {
                    const string prefix = "Mixed (";
                    prefix.AsSpan().CopyTo(span);
                    var offset = prefix.Length;
                    var totalText = total.ToString(CultureInfo.CurrentCulture);
                    totalText.AsSpan().CopyTo(span[offset..]);
                    span[offset + totalText.Length] = ')';
                });

        var count = summary.Counts.GetCount(majority.Value);
        var displayName = majority.Value.ToDisplayString();
        var countText = count.ToString(CultureInfo.CurrentCulture);
        return string.Concat(displayName, " (", countText, ")");
    }

    private string BuildUserReportTooltip(ModVersionVoteSummary? summary)
    {
        var versionText = string.IsNullOrWhiteSpace(_installedGameVersion)
            ? "this VS version"
            : string.Format(CultureInfo.CurrentCulture, "VS {0}", _installedGameVersion);

        if (summary is null)
            return string.Format(
                CultureInfo.CurrentCulture,
                "No votes recorded yet for {0}. Click to share your experience.",
                versionText);

        var counts = summary.Counts;
        var countsLine = BuildCountsSummary(versionText, counts);

        var commentsText = BuildNegativeCommentsText(summary, false);
        return string.IsNullOrEmpty(commentsText)
            ? countsLine
            : string.Concat(countsLine, Environment.NewLine, Environment.NewLine, commentsText);
    }

    private static string BuildCountsSummary(string versionText, ModVersionVoteCounts counts)
    {
        var builder = new StringBuilder();
        builder.AppendFormat(CultureInfo.CurrentCulture, "User reports for {0}:{1}", versionText, Environment.NewLine);
        builder.AppendFormat(CultureInfo.CurrentCulture, "Fully functional ({0}){1}", counts.FullyFunctional,
            Environment.NewLine);
        builder.AppendFormat(CultureInfo.CurrentCulture, "No issues noticed ({0}){1}", counts.NoIssuesSoFar,
            Environment.NewLine);
        builder.AppendFormat(CultureInfo.CurrentCulture, "Some issues but works ({0}){1}", counts.SomeIssuesButWorks,
            Environment.NewLine);
        builder.AppendFormat(CultureInfo.CurrentCulture, "Not functional ({0}){1}", counts.NotFunctional,
            Environment.NewLine);
        builder.AppendFormat(CultureInfo.CurrentCulture, "Crashes/Freezes game ({0})", counts.CrashesOrFreezesGame);
        return builder.ToString();
    }

    private static string BuildNegativeCommentsText(ModVersionVoteSummary summary, bool requireNegativeMajority)
    {
        if (summary is null) return string.Empty;

        if (requireNegativeMajority)
        {
            var majority = summary.GetMajorityOption();
            if (majority is not ModVersionVoteOption.NotFunctional
                && majority is not ModVersionVoteOption.CrashesOrFreezesGame)
                return string.Empty;
        }

        var builder = new StringBuilder();
        AppendCommentSection(builder, "Not functional reports", summary.Comments.NotFunctional);
        AppendCommentSection(builder, "Crashes/Freezes game reports", summary.Comments.CrashesOrFreezesGame);
        return builder.ToString();
    }

    private static void AppendCommentSection(StringBuilder builder, string heading, IReadOnlyList<string> comments)
    {
        if (comments is null || comments.Count == 0) return;

        var uniqueComments = new List<string>(comments.Count);
        var countsByComment = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var comment in comments)
        {
            if (string.IsNullOrWhiteSpace(comment)) continue;

            var trimmedComment = comment.Trim();
            if (countsByComment.TryGetValue(trimmedComment, out var count))
            {
                countsByComment[trimmedComment] = count + 1;
            }
            else
            {
                countsByComment.Add(trimmedComment, 1);
                uniqueComments.Add(trimmedComment);
            }
        }

        if (uniqueComments.Count == 0) return;

        if (builder.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
        }

        builder.Append(heading);
        builder.Append(':');

        foreach (var comment in uniqueComments)
        {
            builder.AppendLine();
            builder.Append(" • ");
            builder.Append(comment);

            if (countsByComment[comment] > 1)
            {
                builder.Append(' ');
                builder.Append('(');
                builder.Append(countsByComment[comment].ToString(CultureInfo.CurrentCulture));
                builder.Append(')');
            }
        }
    }

    public void ApplyLatestReleaseUserReportSummary(ModVersionVoteSummary summary)
    {
        if (summary is null) return;

        LatestReleaseUserReportSummary = summary;
        LatestReleaseUserReportVersion = summary.ModVersion;
        LatestReleaseUserReportDisplay = BuildLatestReleaseUserReportDisplay(summary);
        LatestReleaseUserReportTooltip = BuildLatestReleaseUserReportTooltip(summary);
    }

    public void ClearLatestReleaseUserReport()
    {
        if (LatestReleaseUserReportSummary is null
            && LatestReleaseUserReportVersion is null
            && _latestReleaseUserReportDisplay is null)
            return;

        LatestReleaseUserReportSummary = null;
        LatestReleaseUserReportVersion = null;
        LatestReleaseUserReportDisplay = null;
        LatestReleaseUserReportTooltip = null;
    }

    private static string? BuildLatestReleaseUserReportDisplay(ModVersionVoteSummary? summary)
    {
        if (summary is null || summary.TotalVotes == 0) return null;

        var display = BuildUserReportDisplay(summary);
        return string.Equals(display, "No votes", StringComparison.Ordinal) ? null : display;
    }

    private static string? BuildLatestReleaseUserReportTooltip(ModVersionVoteSummary? summary)
    {
        if (summary is null || summary.TotalVotes == 0) return null;

        var versionText = string.IsNullOrWhiteSpace(summary.VintageStoryVersion)
            ? "this Vintage Story version"
            : string.Format(CultureInfo.CurrentCulture, "Vintage Story {0}", summary.VintageStoryVersion);

        var countsLine = BuildCountsSummary(versionText, summary.Counts);
        var commentsText = BuildNegativeCommentsText(summary, true);

        return string.IsNullOrEmpty(commentsText)
            ? countsLine
            : string.Concat(countsLine, Environment.NewLine, Environment.NewLine, commentsText);
    }

    public void UpdateLoadError(string? loadError)
    {
        var normalized = string.IsNullOrWhiteSpace(loadError) ? null : loadError;
        if (string.Equals(_loadError, normalized, StringComparison.Ordinal)) return;

        _loadError = normalized;
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(CanFixDependencyIssues));
        UpdateStatusFromErrors();
        UpdateTooltip();
    }

    /// <summary>
    /// Updates database information for this mod with batched property change notifications.
    /// This method must be called from the UI thread (Dispatcher thread).
    /// </summary>
    public void UpdateDatabaseInfo(ModDatabaseInfo info, bool loadLogoImmediately = true)
    {
        if (info is null) return;

        // Batch all property changes to reduce UI update overhead.
        // Thread safety: This method is always invoked on the Dispatcher thread via
        // InvokeOnDispatcherAsync in MainViewModel.ApplyDatabaseInfoAsync, so no
        // synchronization is needed for the property change batching mechanism.
        using (SuspendPropertyChangeNotifications())
        {
            var previousSide = NormalizeSide(_modDatabaseSide);
            var updatedSide = NormalizeSide(info.Side);
            _modDatabaseSide = info.Side;
            if (!string.Equals(previousSide, updatedSide, StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(SideDisplay));
                OnPropertyChanged(nameof(SideSortValue));
            }

            var tags = info.Tags ?? Array.Empty<string>();
            if (HasDifferentContent(DatabaseTags, tags))
            {
                using (_timingService?.MeasureTagsLoad())
                {
                    DatabaseTags = tags;
                    _cachedDatabaseTagsDisplay = null; // Invalidate display cache
                    OnPropertyChanged(nameof(DatabaseTags));
                    OnPropertyChanged(nameof(DatabaseTagsDisplay));
                }
            }

            var requiredVersions = info.RequiredGameVersions ?? Array.Empty<string>();
            if (HasDifferentContent(_databaseRequiredGameVersions, requiredVersions))
                _databaseRequiredGameVersions = requiredVersions;

            var latestRelease = info.LatestRelease;
            var latestCompatibleRelease = info.LatestCompatibleRelease;
            var releases = info.Releases ?? Array.Empty<ModReleaseInfo>();

            if (string.IsNullOrWhiteSpace(Version))
            {
                var preferredUserReportVersion = SelectPreferredUserReportVersion(
                    latestRelease,
                    latestCompatibleRelease,
                    info);
                SetUserReportVersion(preferredUserReportVersion, true);
            }

            var latestReleaseVersion = latestRelease?.Version;
            if (!string.Equals(LatestReleaseUserReportVersion, latestReleaseVersion, StringComparison.OrdinalIgnoreCase))
                ClearLatestReleaseUserReport();

            LatestRelease = latestRelease;
            LatestCompatibleRelease = latestCompatibleRelease;
            _releases = releases;

            UpdateModDatabaseMetrics(info, releases);

            var downloads = info.Downloads;
            if (_databaseDownloads != downloads)
            {
                _databaseDownloads = downloads;
                OnPropertyChanged(nameof(DownloadsDisplay));
                OnPropertyChanged(nameof(ModDatabaseDownloadsSortKey));
            }

            var comments = info.Comments;
            if (_databaseComments != comments)
            {
                _databaseComments = comments;
                OnPropertyChanged(nameof(CommentsDisplay));
            }

            LogDebug(
                $"UpdateDatabaseInfo invoked. AssetId='{FormatValue(info.AssetId)}', PageUrl='{FormatValue(info.ModPageUrl)}', LogoUrl='{FormatValue(info.LogoUrl)}'.");

            var logoUrl = info.LogoUrl;
            var logoSource = info.LogoUrlSource;
            var logoUrlChanged = !string.Equals(_modDatabaseLogoUrl, logoUrl, StringComparison.Ordinal);
            _modDatabaseLogoSource = logoSource ?? _modDatabaseLogoSource;
            if (logoUrlChanged)
            {
                _modDatabaseLogoUrl = logoUrl;
                var shouldCreateLogo = loadLogoImmediately && Icon is null;
                if (shouldCreateLogo)
                {
                    _modDatabaseLogo = CreateModDatabaseLogoImage();
                    LogDebug(
                        $"Updated database logo. New URL='{FormatValue(_modDatabaseLogoUrl)}', Image created={_modDatabaseLogo is not null}.");
                }
                else
                {
                    if (_modDatabaseLogo is not null)
                        LogDebug("Clearing previously loaded database logo to defer image refresh.");

                    _modDatabaseLogo = null;
                    LogDebug(
                        $"Deferred database logo update. New URL='{FormatValue(_modDatabaseLogoUrl)}'. Logo creation skipped={!shouldCreateLogo}.");
                }

                OnPropertyChanged(nameof(ModDatabasePreviewImage));
                OnPropertyChanged(nameof(HasModDatabasePreviewImage));
            }
            else if (loadLogoImmediately && Icon is null && _modDatabaseLogo is null &&
                     !string.IsNullOrWhiteSpace(_modDatabaseLogoUrl))
            {
                _modDatabaseLogoSource = logoSource ?? _modDatabaseLogoSource;
                _modDatabaseLogo = CreateModDatabaseLogoImage();
                OnPropertyChanged(nameof(ModDatabasePreviewImage));
                OnPropertyChanged(nameof(HasModDatabasePreviewImage));
                LogDebug(
                    $"Loaded deferred database logo. URL='{FormatValue(_modDatabaseLogoUrl)}', Image created={_modDatabaseLogo is not null}.");
            }

            if (!string.Equals(ModDatabaseAssetId, info.AssetId, StringComparison.Ordinal))
            {
                ModDatabaseAssetId = info.AssetId;
                OnPropertyChanged(nameof(ModDatabaseAssetId));
                LogDebug($"Database asset id updated to '{FormatValue(ModDatabaseAssetId)}'.");
            }

            var pageUrl = info.ModPageUrl;
            if (!string.Equals(ModDatabasePageUrl, pageUrl, StringComparison.Ordinal))
            {
                ModDatabasePageUrl = pageUrl;
                OnPropertyChanged(nameof(ModDatabasePageUrl));
                OnPropertyChanged(nameof(ModDatabasePageUrlDisplay));
                LogDebug($"Database page URL updated to '{FormatValue(ModDatabasePageUrl)}'.");
            }

            var pageUri = TryCreateHttpUri(pageUrl);
            if (ModDatabasePageUri != pageUri)
            {
                ModDatabasePageUri = pageUri;
                OnPropertyChanged(nameof(ModDatabasePageUri));
                OnPropertyChanged(nameof(HasModDatabasePageLink));
                LogDebug($"Database page URI resolved to '{FormatUri(ModDatabasePageUri)}'.");
            }

            ICommand? pageCommand = null;
            if (pageUri != null)
            {
                var commandUri = pageUri;
                pageCommand = new RelayCommand(() => LaunchUri(commandUri));
                LogDebug($"Database page command initialized for '{commandUri}'.");
            }

            SetProperty(ref _openModDatabasePageCommand, pageCommand, nameof(OpenModDatabasePageCommand));

            var latestDatabaseVersion = latestRelease?.Version
                                        ?? info.LatestVersion
                                        ?? latestCompatibleRelease?.Version
                                        ?? info.LatestCompatibleVersion;

            if (!string.Equals(LatestDatabaseVersion, latestDatabaseVersion, StringComparison.Ordinal))
            {
                LatestDatabaseVersion = latestDatabaseVersion;
                OnPropertyChanged(nameof(LatestDatabaseVersion));
                OnPropertyChanged(nameof(LatestDatabaseVersionDisplay));
                OnPropertyChanged(nameof(LatestVersionSortKey));
                LogDebug($"Latest database version updated to '{FormatValue(LatestDatabaseVersion)}'.");
            }

            InitializeUpdateAvailability();
            InitializeVersionOptions();
            InitializeVersionWarning(_installedGameVersion);
            UpdateNewerReleaseChangelogs();

            OnPropertyChanged(nameof(LatestRelease));
            OnPropertyChanged(nameof(LatestCompatibleRelease));
            OnPropertyChanged(nameof(LatestReleaseIsCompatible));
            OnPropertyChanged(nameof(ShouldHighlightLatestVersion));
            OnPropertyChanged(nameof(CanUpdate));
            OnPropertyChanged(nameof(LatestVersionSortKey));
            OnPropertyChanged(nameof(RequiresCompatibilitySelection));
            OnPropertyChanged(nameof(HasCompatibleUpdate));
            OnPropertyChanged(nameof(HasDownloadableRelease));
            OnPropertyChanged(nameof(InstallButtonToolTip));
            OnPropertyChanged(nameof(UpdateButtonToolTip));

            UpdateStatusFromErrors();
            UpdateTooltip();
        }
    }

    public void ClearDatabaseTags()
    {
        if (DatabaseTags.Count == 0) return;

        DatabaseTags = Array.Empty<string>();
        _cachedDatabaseTagsDisplay = null; // Invalidate display cache
        OnPropertyChanged(nameof(DatabaseTags));
        OnPropertyChanged(nameof(DatabaseTagsDisplay));
    }

    public void RefreshSkippedUpdateState()
    {
        var previousHasUpdate = CanUpdate;
        var previousMessage = _updateMessage;

        InitializeUpdateAvailability();

        if (previousHasUpdate != CanUpdate)
        {
            OnPropertyChanged(nameof(CanUpdate));
            OnPropertyChanged(nameof(ShouldHighlightLatestVersion));
            OnPropertyChanged(nameof(LatestVersionSortKey));
            OnPropertyChanged(nameof(RequiresCompatibilitySelection));
            OnPropertyChanged(nameof(HasCompatibleUpdate));
            OnPropertyChanged(nameof(HasDownloadableRelease));
            OnPropertyChanged(nameof(UpdateButtonToolTip));
            UpdateStatusFromErrors();
            UpdateTooltip();
            return;
        }

        if (!string.Equals(previousMessage, _updateMessage, StringComparison.Ordinal))
        {
            UpdateStatusFromErrors();
            UpdateTooltip();
        }
    }

    public void EnsureModDatabaseLogoLoaded()
    {
        if (_modDatabaseLogo is not null) return;

        if (string.IsNullOrWhiteSpace(_modDatabaseLogoUrl)) return;

        _modDatabaseLogo = CreateModDatabaseLogoImage();
        OnPropertyChanged(nameof(ModDatabasePreviewImage));
        OnPropertyChanged(nameof(HasModDatabasePreviewImage));
        LogDebug(
            $"Deferred database logo load complete. URL='{FormatValue(_modDatabaseLogoUrl)}', Image created={_modDatabaseLogo is not null}.");
    }

    public async Task LoadModDatabaseLogoAsync(CancellationToken cancellationToken)
    {
        var logoUrl = _modDatabaseLogoUrl;
        if (string.IsNullOrWhiteSpace(logoUrl)) return;

        var uri = TryCreateHttpUri(logoUrl);
        if (uri is null)
        {
            LogDebug($"Async database logo load skipped. Unable to resolve URI from '{FormatValue(logoUrl)}'.");
            return;
        }

        // Track whether we need to update the UI (only if _modDatabaseLogo was null)
        var needsUiUpdate = _modDatabaseLogo is null;

        try
        {
            // Try to load from cache first
            var cachedBytes = await ModImageCacheService
                .TryGetCachedImageAsync(logoUrl, cancellationToken, GetModLogoCacheDescriptor())
                .ConfigureAwait(false);

            if (cachedBytes is { Length: > 0 })
            {
                // Image is already cached - only update UI if needed
                if (needsUiUpdate)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var cachedImage = CreateBitmapFromBytes(cachedBytes, uri);
                    if (cachedImage is not null)
                    {
                        await InvokeOnDispatcherAsync(
                                () =>
                                {
                                    if (_modDatabaseLogo is not null) return;

                                    _modDatabaseLogo = cachedImage;
                                    OnPropertyChanged(nameof(ModDatabasePreviewImage));
                                    OnPropertyChanged(nameof(HasModDatabasePreviewImage));
                                    LogDebug(
                                        $"Loaded database logo from cache. URL='{FormatValue(_modDatabaseLogoUrl)}'.");
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                // Image is cached, we're done
                return;
            }

            // If not cached, download from network and cache it (even if logo is already loaded)
            if (InternetAccessManager.IsInternetAccessDisabled) return;

            InternetAccessManager.ThrowIfInternetAccessDisabled();

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await HttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogDebug(
                    $"Async database logo load failed for '{uri}'. HTTP status {(int)response.StatusCode} ({response.StatusCode}).");
                return;
            }

            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (payload.Length == 0)
            {
                LogDebug($"Async database logo load returned no data for '{uri}'.");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Cache the downloaded image for future use
            await ModImageCacheService
                .StoreImageAsync(logoUrl, payload, cancellationToken, GetModLogoCacheDescriptor())
                .ConfigureAwait(false);

            // Only update UI if the logo wasn't already set
            if (needsUiUpdate)
            {
                var image = CreateBitmapFromBytes(payload, uri);
                if (image is null) return;

                await InvokeOnDispatcherAsync(
                        () =>
                        {
                            if (_modDatabaseLogo is not null) return;

                            _modDatabaseLogo = image;
                            OnPropertyChanged(nameof(ModDatabasePreviewImage));
                            OnPropertyChanged(nameof(HasModDatabasePreviewImage));
                            LogDebug(
                                $"Async database logo load complete. URL='{FormatValue(_modDatabaseLogoUrl)}', Image created={_modDatabaseLogo is not null}.");
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                LogDebug(
                    $"Cached database logo for future use. URL='{FormatValue(_modDatabaseLogoUrl)}'.");
            }
        }
        catch (OperationCanceledException)
        {
            // Respect cancellation requests without surfacing an error.
        }
        catch (Exception ex)
        {
            LogDebug($"Async database logo load failed with error: {ex.Message}.");
        }
    }

    public void SetIsActiveSilently(bool isActive)
    {
        if (_isActive == isActive) return;

        _suppressState = true;
        try
        {
            if (SetProperty(ref _isActive, isActive))
            {
                ActivationError = null;
                OnPropertyChanged(nameof(ActiveSortOrder));
                OnPropertyChanged(nameof(CanFixDependencyIssues));
            }
        }
        finally
        {
            _suppressState = false;
        }
    }

    public void UpdateDependencyIssues(bool hasDependencyErrors, IReadOnlyList<ModDependencyInfo> missingDependencies)
    {
        using (_timingService?.MeasureDependencyCheck())
        {
            var changed = false;

            if (DependencyHasErrors != hasDependencyErrors)
            {
                DependencyHasErrors = hasDependencyErrors;
                OnPropertyChanged(nameof(DependencyHasErrors));
                changed = true;
            }

            IReadOnlyList<ModDependencyInfo> normalizedMissing = missingDependencies is { Count: > 0 }
                ? missingDependencies.ToArray()
                : Array.Empty<ModDependencyInfo>();

            if (!ReferenceEquals(MissingDependencies, normalizedMissing))
                if (MissingDependencies.Count != normalizedMissing.Count
                    || !MissingDependencies.SequenceEqual(normalizedMissing))
                {
                    MissingDependencies = normalizedMissing;
                    OnPropertyChanged(nameof(MissingDependencies));
                    changed = true;
                }

            if (changed)
            {
                OnPropertyChanged(nameof(HasDependencyIssues));
                OnPropertyChanged(nameof(CanFixDependencyIssues));
            }
        }
    }

    private async Task ApplyActivationChangeAsync(bool previous, bool current)
    {
        ActivationResult result;
        try
        {
            result = await _activationHandler(this, current);
        }
        catch (Exception ex)
        {
            result = new ActivationResult(false, ex.Message);
        }

        if (!result.Success)
        {
            _suppressState = true;
            try
            {
                if (SetProperty(ref _isActive, previous, nameof(IsActive)))
                {
                    OnPropertyChanged(nameof(ActiveSortOrder));
                    OnPropertyChanged(nameof(CanFixDependencyIssues));
                }
            }
            finally
            {
                _suppressState = false;
            }

            ActivationError = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "Failed to update activation state."
                : result.ErrorMessage;
        }
        else
        {
            ActivationError = null;
        }
    }

    private void UpdateStatusFromErrors()
    {
        if (HasErrors)
        {
            StatusText = "Error";
            StatusDetails = AppendWarningText(_metadataError ?? "Metadata error.");
        }
        else if (HasLoadError)
        {
            StatusText = "Error";
            StatusDetails = AppendWarningText(_loadError ?? string.Empty);
        }
        else if (HasActivationError)
        {
            StatusText = "Error";
            StatusDetails = AppendWarningText(ActivationError ?? string.Empty);
        }
        else if (!string.IsNullOrWhiteSpace(_versionWarningMessage))
        {
            StatusText = "Warning";
            StatusDetails = _versionWarningMessage!;
        }
        else if (CanUpdate)
        {
            StatusText = "Update";
            StatusDetails = string.IsNullOrWhiteSpace(_updateMessage)
                ? "Update available."
                : _updateMessage!;
        }
        else
        {
            StatusText = string.Empty;
            StatusDetails = string.Empty;
        }
    }

    private void UpdateTooltip()
    {
        var tooltipText = DisplayName;

        if (!string.IsNullOrWhiteSpace(_description))
            tooltipText = string.Concat(
                tooltipText,
                Environment.NewLine,
                Environment.NewLine,
                _description.Trim());

        if (!string.IsNullOrWhiteSpace(_versionWarningMessage))
            tooltipText = string.Concat(
                tooltipText,
                Environment.NewLine,
                Environment.NewLine,
                _versionWarningMessage);

        if (CanUpdate && !string.IsNullOrWhiteSpace(_updateMessage))
            tooltipText = string.Concat(
                tooltipText,
                Environment.NewLine,
                Environment.NewLine,
                _updateMessage);

        Tooltip = tooltipText;
    }

    internal bool MatchesSearchTokens(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) return true;

        // Lazy initialization of search index on first search
        if (_searchIndex == null)
        {
            _searchIndex = BuildSearchIndex(_modEntry, _constructorLocation);
            LogDebug($"Lazy search index built for mod '{DisplayName}'.");
        }

        // Use ReadOnlySpan for more efficient substring searches
        var searchIndexSpan = _searchIndex.AsSpan();

        foreach (var token in tokens)
        {
            if (searchIndexSpan.IndexOf(token.AsSpan(), StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }

        return true;
    }

    private void InitializeUpdateAvailability()
    {
        using (_timingService?.MeasureUpdateCheck())
        {
            if (LatestRelease is null)
            {
                CanUpdate = false;
                _updateMessage = null;
                return;
            }

            if (!VersionStringUtility.IsCandidateVersionNewer(LatestRelease.Version, Version))
            {
                CanUpdate = false;
                _updateMessage = null;
                return;
            }

            if (_shouldSkipVersion?.Invoke(ModId, LatestRelease.Version) == true)
            {
                CanUpdate = false;
                _updateMessage = null;
                return;
            }

            CanUpdate = true;
            _updateMessage = BuildUpdateMessage(LatestRelease);
        }
    }

    private string BuildUpdateMessage(ModReleaseInfo release)
    {
        if (release.IsCompatibleWithInstalledGame) return $"Update available: {release.Version}";

        if (LatestCompatibleRelease != null
            && !string.Equals(LatestCompatibleRelease.Version, release.Version, StringComparison.OrdinalIgnoreCase))
            return $"Update available: {release.Version} (latest compatible: {LatestCompatibleRelease.Version})";

        return $"Update available: {release.Version} (may be incompatible with your Vintage Story version)";
    }

    private void InitializeVersionOptions()
    {
        if (_releases.Count == 0 && string.IsNullOrWhiteSpace(Version))
        {
            VersionOptions = Array.Empty<ModVersionOptionViewModel>();
            OnPropertyChanged(nameof(VersionOptions));
            OnPropertyChanged(nameof(HasVersionOptions));
            SetProperty(ref _selectedVersionOption, null);
            return;
        }

        var normalizedInstalled = VersionStringUtility.Normalize(Version);
        var options = new List<ModVersionOptionViewModel>(_releases.Count + 1);
        var hasInstalled = false;

        foreach (var release in _releases)
        {
            var isInstalled = IsReleaseInstalled(release, Version, normalizedInstalled);
            if (isInstalled) hasInstalled = true;

            options.Add(ModVersionOptionViewModel.FromRelease(release, isInstalled));
        }

        if (!hasInstalled && !string.IsNullOrWhiteSpace(Version))
            options.Insert(0, ModVersionOptionViewModel.FromInstalledVersion(Version!, normalizedInstalled));

        IReadOnlyList<ModVersionOptionViewModel> finalized = options.Count == 0
            ? Array.Empty<ModVersionOptionViewModel>()
            : options.AsReadOnly();

        VersionOptions = finalized;
        OnPropertyChanged(nameof(VersionOptions));
        OnPropertyChanged(nameof(HasVersionOptions));

        var selected = finalized.FirstOrDefault(option => option.IsInstalled)
                       ?? finalized.FirstOrDefault();

        SetProperty(ref _selectedVersionOption, selected);
    }

    private void UpdateNewerReleaseChangelogs()
    {
        using (_timingService?.MeasureChangelogLoad())
        {
            var updated = BuildNewerReleaseChangelogList();
            if (ReferenceEquals(NewerReleaseChangelogs, updated)) return;

            NewerReleaseChangelogs = updated;
            OnPropertyChanged(nameof(NewerReleaseChangelogs));
            OnPropertyChanged(nameof(HasNewerReleaseChangelogs));
        }
    }

    private IReadOnlyList<ReleaseChangelog> BuildNewerReleaseChangelogList()
    {
        if (_releases.Count == 0) return Array.Empty<ReleaseChangelog>();

        var installedIndex = FindInstalledReleaseIndex();
        var endExclusive = installedIndex >= 0 ? installedIndex : _releases.Count;
        if (endExclusive <= 0) return Array.Empty<ReleaseChangelog>();

        var results = new List<ReleaseChangelog>();
        for (var i = 0; i < endExclusive; i++)
        {
            var release = _releases[i];
            if (string.IsNullOrWhiteSpace(release.Changelog)) continue;

            var trimmed = release.Changelog.Trim();
            if (trimmed.Length == 0) continue;

            results.Add(new ReleaseChangelog(release.Version, trimmed));
        }

        return results.Count == 0 ? Array.Empty<ReleaseChangelog>() : results;
    }

    private int FindInstalledReleaseIndex()
    {
        if (_releases.Count == 0 || string.IsNullOrWhiteSpace(Version)) return -1;

        var trimmedInstalled = Version!.Trim();
        var normalizedInstalled = VersionStringUtility.Normalize(Version);

        for (var i = 0; i < _releases.Count; i++)
        {
            var release = _releases[i];
            if (!string.IsNullOrWhiteSpace(release.Version)
                && string.Equals(release.Version.Trim(), trimmedInstalled, StringComparison.OrdinalIgnoreCase))
                return i;

            if (normalizedInstalled != null
                && !string.IsNullOrWhiteSpace(release.NormalizedVersion)
                && string.Equals(release.NormalizedVersion, normalizedInstalled, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static bool IsReleaseInstalled(ModReleaseInfo release, string? installedVersion,
        string? normalizedInstalled)
    {
        if (string.IsNullOrWhiteSpace(installedVersion)) return false;

        var trimmedInstalled = installedVersion.Trim();
        if (!string.IsNullOrWhiteSpace(release.Version)
            && string.Equals(release.Version.Trim(), trimmedInstalled, StringComparison.OrdinalIgnoreCase))
            return true;

        if (normalizedInstalled != null && !string.IsNullOrWhiteSpace(release.NormalizedVersion))
            return string.Equals(release.NormalizedVersion, normalizedInstalled, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private static bool DoesReleaseMatchVersion(ModReleaseInfo release, string trimmedVersion,
        string? normalizedVersion)
    {
        if (!string.IsNullOrWhiteSpace(release.Version)
            && string.Equals(release.Version.Trim(), trimmedVersion, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(release.NormalizedVersion)
            && normalizedVersion != null
            && string.Equals(release.NormalizedVersion, normalizedVersion, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private void InitializeVersionWarning(string? installedGameVersion)
    {
        var normalizedInstalled = VersionStringUtility.Normalize(installedGameVersion);
        var requiredVersions = BuildRequiredVersionCandidates();

        var requireExact = _requireExactVersionMatch?.Invoke() ?? false;
        if (TryCreateVersionWarning(requiredVersions, normalizedInstalled, requireExact, out var message))
            _versionWarningMessage = message;
        else
            _versionWarningMessage = null;
    }

    private IReadOnlyList<(string Normalized, string Original)> BuildRequiredVersionCandidates()
    {
        if (_databaseRequiredGameVersions.Count > 0)
        {
            var normalized = new List<(string Normalized, string Original)>(_databaseRequiredGameVersions.Count);
            foreach (var tag in _databaseRequiredGameVersions)
            {
                var normalizedTag = VersionStringUtility.Normalize(tag);
                if (!string.IsNullOrWhiteSpace(normalizedTag)) normalized.Add((normalizedTag!, tag));
            }

            if (normalized.Count > 0) return normalized;
        }

        if (_gameDependency is { Version: not null } dependency)
        {
            var normalizedDependency = VersionStringUtility.Normalize(dependency.Version);
            if (!string.IsNullOrWhiteSpace(normalizedDependency))
                return new[]
                {
                    (normalizedDependency!, dependency.Version!)
                };
        }

        return Array.Empty<(string, string)>();
    }

    private string AppendWarningText(string baseText)
    {
        if (string.IsNullOrWhiteSpace(_versionWarningMessage)) return baseText;

        if (string.IsNullOrWhiteSpace(baseText)) return _versionWarningMessage!;

        return string.Concat(baseText, Environment.NewLine, Environment.NewLine, _versionWarningMessage);
    }

    private static bool TryCreateVersionWarning(
        IReadOnlyCollection<(string Normalized, string Original)> requiredVersions, string? installedVersion,
        bool requireExact, out string? message)
    {
        message = null;

        if (requiredVersions.Count == 0 || string.IsNullOrWhiteSpace(installedVersion)) return false;

        if (!TryGetMajorMinor(installedVersion!, out var installedMajor, out var installedMinor)) return false;

        var hasComparable = false;
        foreach (var (normalized, _) in requiredVersions)
        {
            if (!TryGetMajorMinor(normalized, out var requiredMajor, out var requiredMinor)) continue;

            hasComparable = true;

            if (requireExact)
            {
                // Exact mode: Strict - compare first three version parts (major.minor.patch)
                // Return false (no warning) if first 3 parts match exactly
                if (VersionStringUtility.MatchesFirstThreeDigits(normalized, installedVersion)) return false;
            }
            else
            {
                // Relaxed mode (default): Lenient - only compare major.minor (first two parts)
                // Return false (no warning) if major.minor match, ignoring patch version differences
                if (requiredMajor == installedMajor && requiredMinor == installedMinor) return false;
            }
        }

        if (!hasComparable) return false;

        message = "This mod isn't marked as compatible with your Vintage Story version, but might work anyway. " +
                  "Check user reports column or read the comments on the Mod DB page for more info.";
        return true;
    }

    private void UpdateModDatabaseMetrics(ModDatabaseInfo info, IReadOnlyList<ModReleaseInfo> releases)
    {
        var recentDownloads = info?.DownloadsLastThirtyDays ?? CalculateDownloadsLastThirtyDaysFromReleases(releases);
        if (_databaseRecentDownloads != recentDownloads)
        {
            _databaseRecentDownloads = recentDownloads;
            OnPropertyChanged(nameof(RecentDownloadsDisplay));
            OnPropertyChanged(nameof(ModDatabaseRecentDownloadsSortKey));
        }

        var tenDayDownloads = info?.DownloadsLastTenDays ?? CalculateDownloadsLastTenDaysFromReleases(releases);
        if (_databaseTenDayDownloads != tenDayDownloads)
        {
            _databaseTenDayDownloads = tenDayDownloads;
            OnPropertyChanged(nameof(TenDayDownloadsDisplay));
        }

        var lastUpdated = DetermineLastUpdated(info, releases);
        if (_modDatabaseLastUpdatedUtc != lastUpdated)
        {
            _modDatabaseLastUpdatedUtc = lastUpdated;
            OnPropertyChanged(nameof(ModDatabaseLastUpdatedSortKey));
        }
    }

    private static int? CalculateDownloadsLastThirtyDaysFromReleases(IReadOnlyList<ModReleaseInfo> releases)
    {
        return CalculateRecentDownloadsFromReleases(releases, 30);
    }

    private static int? CalculateDownloadsLastTenDaysFromReleases(IReadOnlyList<ModReleaseInfo> releases)
    {
        return CalculateRecentDownloadsFromReleases(releases, 10);
    }

    private static int? CalculateRecentDownloadsFromReleases(IReadOnlyList<ModReleaseInfo> releases, int days)
    {
        if (releases.Count == 0) return null;

        var threshold = DateTime.UtcNow.AddDays(-days);
        var total = 0;
        var hasData = false;

        foreach (var release in releases)
        {
            if (release?.CreatedUtc is not { } createdUtc || createdUtc < threshold) continue;

            if (release.Downloads.HasValue)
            {
                hasData = true;
                total += Math.Max(0, release.Downloads.Value);
            }
        }

        return hasData ? total : null;
    }

    private static DateTime? DetermineLastUpdated(ModDatabaseInfo? info, IReadOnlyList<ModReleaseInfo> releases)
    {
        if (info?.LastReleasedUtc is DateTime lastReleased) return lastReleased;

        return DetermineLastUpdatedFromReleases(releases);
    }

    private static DateTime? DetermineLastUpdatedFromReleases(IReadOnlyList<ModReleaseInfo> releases)
    {
        DateTime? latest = null;

        foreach (var release in releases)
        {
            if (release?.CreatedUtc is not { } createdUtc) continue;

            if (!latest.HasValue || createdUtc > latest.Value) latest = createdUtc;
        }

        return latest;
    }

    private static bool TryGetMajorMinor(string version, out int major, out int minor)
    {
        major = 0;
        minor = 0;

        var span = version.AsSpan();
        var dotIndex = span.IndexOf('.');

        // Parse major version
        var majorSpan = dotIndex >= 0 ? span.Slice(0, dotIndex) : span;
        if (!int.TryParse(majorSpan.Trim(), out major)) return false;

        // Parse minor version if available
        if (dotIndex >= 0 && dotIndex + 1 < span.Length)
        {
            var remainingSpan = span.Slice(dotIndex + 1);
            var nextDotIndex = remainingSpan.IndexOf('.');
            var minorSpan = nextDotIndex >= 0 ? remainingSpan.Slice(0, nextDotIndex) : remainingSpan;

            if (!int.TryParse(minorSpan.Trim(), out minor)) return false;
        }
        else
        {
            minor = 0;
        }

        return true;
    }

    private string BuildSearchIndex(ModEntry entry, string location)
    {
        var builder = new StringBuilder();

        AppendText(builder, DisplayName);
        AppendText(builder, entry.Name);
        AppendText(builder, entry.ModId);
        AppendText(builder, ModId);
        AppendText(builder, entry.Version);
        AppendText(builder, Version);
        AppendText(builder, entry.NetworkVersion);
        AppendText(builder, NetworkVersion);
        AppendText(builder, entry.Description);
        AppendText(builder, _description);
        AppendText(builder, entry.Error);
        AppendText(builder, _metadataError);
        AppendText(builder, entry.LoadError);
        AppendText(builder, _loadError);
        AppendText(builder, entry.Side);
        AppendText(builder, Side);
        AppendText(builder, location);
        AppendText(builder, entry.SourcePath);
        AppendText(builder, entry.SourceKind.ToString());
        AppendText(builder, Website);
        AppendText(builder, ModDatabasePageUrl);
        AppendText(builder, ModDatabaseAssetId);
        AppendText(builder, LatestDatabaseVersion);
        AppendText(builder, LatestDatabaseVersionDisplay);
        AppendText(builder, LatestRelease?.Version);
        AppendText(builder, LatestCompatibleRelease?.Version);
        AppendCollection(builder, DatabaseTags);
        AppendCollection(builder, _databaseRequiredGameVersions);
        AppendCollection(builder, _authors);
        AppendCollection(builder, _contributors);
        AppendDependencies(builder, Dependencies);
        AppendReleases(builder, _releases);

        return builder.ToString();
    }

    private static void AppendText(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        AppendSegment(builder, value);

        var normalized = NormalizeSearchText(value);
        if (!string.IsNullOrEmpty(normalized) && !string.Equals(normalized, value, StringComparison.Ordinal))
            AppendSegment(builder, normalized);
    }

    private static void AppendCollection(StringBuilder builder, IEnumerable<string> values)
    {
        foreach (var value in values) AppendText(builder, value);
    }

    private static void AppendSegment(StringBuilder builder, string value)
    {
        if (builder.Length > 0) builder.Append(' ');

        builder.Append(value);
    }

    private static bool HasDifferentContent<T>(IReadOnlyList<T> current, IReadOnlyList<T> updated)
    {
        if (ReferenceEquals(current, updated)) return false;

        if (current.Count != updated.Count) return true;

        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < current.Count; i++)
            if (!comparer.Equals(current[i], updated[i]))
                return true;

        return false;
    }

    private string? GetPreferredSide()
    {
        var databaseSide = NormalizeSide(_modDatabaseSide);
        if (!string.IsNullOrWhiteSpace(databaseSide)) return databaseSide;

        return NormalizeSide(Side);
    }

    private static string? NormalizeSide(string? side)
    {
        if (string.IsNullOrWhiteSpace(side)) return null;

        var trimmed = side.Trim();
        if (trimmed.Length == 0) return null;

        var lower = trimmed.ToLowerInvariant();
        return lower switch
        {
            "both" => "both",
            "client" => "client",
            "server" => "server",
            "universal" or "all" or "any" => "both",
            _ => trimmed
        };
    }

    private static string? GetCapitalizedSide(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty) return null;

        if (trimmed.Length == 1) return char.ToUpperInvariant(trimmed[0]).ToString(CultureInfo.InvariantCulture);

        var firstCharacter = char.ToUpperInvariant(trimmed[0]).ToString(CultureInfo.InvariantCulture);
        return string.Concat(firstCharacter, trimmed.Slice(1).ToString());
    }

    private static string NormalizeSearchText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);

            if (category == UnicodeCategory.NonSpacingMark
                || category == UnicodeCategory.SpacingCombiningMark
                || category == UnicodeCategory.EnclosingMark)
                continue;

            if (char.IsLetterOrDigit(character)) builder.Append(character);
        }

        return builder.ToString();
    }

    private static void AppendDependencies(StringBuilder builder, IReadOnlyList<ModDependencyInfo> dependencies)
    {
        foreach (var dependency in dependencies)
        {
            AppendText(builder, dependency.ModId);
            AppendText(builder, dependency.Display);
            AppendText(builder, dependency.Version);
        }
    }

    private static void AppendReleases(StringBuilder builder, IReadOnlyList<ModReleaseInfo> releases)
    {
        foreach (var release in releases)
        {
            AppendText(builder, release.Version);
            AppendCollection(builder, release.GameVersionTags);
        }
    }

    private static Uri? TryCreateHttpUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && IsSupportedScheme(absolute)) return absolute;

        if (Uri.TryCreate($"https://{value}", UriKind.Absolute, out absolute) && IsSupportedScheme(absolute))
            return absolute;

        return null;
    }

    private static bool IsSupportedScheme(Uri uri)
    {
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static void LaunchUri(Uri uri)
    {
        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            ModManagerMessageBox.Show(
                "Internet access is disabled. Enable Internet Access in the File menu to open web links.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // Opening a browser is best-effort; ignore failures.
        }
    }

    private ImageSource? CreateModDatabaseLogoImage()
    {
        var logoUrl = _modDatabaseLogoUrl;
        if (string.IsNullOrWhiteSpace(logoUrl)) return null;

        // Try to load from cache first (synchronous file read)
        try
        {
            var cachedBytes = ModImageCacheService.TryGetCachedImage(logoUrl, GetModLogoCacheDescriptor());

            if (cachedBytes is { Length: > 0 })
            {
                var uri = TryCreateHttpUri(logoUrl);
                if (uri is not null)
                {
                    var image = CreateBitmapFromBytes(cachedBytes, uri);
                    if (image is not null)
                    {
                        LogDebug($"Loaded database logo from cache for '{FormatValue(logoUrl)}'.");
                        return image;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Ignore cache read failures, fall back to network
        }

        // Fall back to network loading
        return CreateImageFromUri(logoUrl, "Mod database logo", false);
    }

    private ModImageCacheDescriptor GetModLogoCacheDescriptor()
    {
        return new ModImageCacheDescriptor(ModId, DisplayName, _modDatabaseLogoSource);
    }

    private ImageSource? CreateBitmapFromBytes(byte[] payload, Uri sourceUri)
    {
        try
        {
            using var stream = new MemoryStream(payload, false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            // Remove IgnoreImageCache to prevent flashing - use WPF's internal cache
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            TryFreezeImageSource(bitmap, $"Async mod database logo ({sourceUri})", LogDebug);
            return bitmap;
        }
        catch (Exception ex)
        {
            LogDebug($"Async database logo load failed to create bitmap: {ex.Message}.");
            return null;
        }
    }

    private ImageSource? CreateImageFromUri(string? url, string context, bool enableLogging = true)
    {
        var formattedUrl = FormatValue(url);
        if (enableLogging) LogDebug($"{context}: Attempting to create image from URL {formattedUrl}.");

        var uri = TryCreateHttpUri(url);
        if (uri == null)
        {
            if (enableLogging) LogDebug($"{context}: Unable to resolve absolute URI from {formattedUrl}.");
            return null;
        }

        if (enableLogging) LogDebug($"{context}: Resolved URI '{uri}'.");

        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            if (enableLogging) LogDebug($"{context}: Skipping image load because internet access is disabled.");

            return null;
        }

        var image = CreateImageSafely(
            () =>
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = uri;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                // Remove IgnoreImageCache to prevent flashing - use WPF's internal cache
                bitmap.EndInit();
                TryFreezeImageSource(bitmap, $"{context} ({uri})", enableLogging ? LogDebug : null);
                return bitmap;
            },
            $"{context} ({uri})",
            enableLogging);

        if (enableLogging)
            LogDebug(image is null
                ? $"{context}: Failed to load image from '{uri}'."
                : $"{context}: Successfully loaded image from '{uri}'.");

        return image;
    }

    private ImageSource? CreateImage(byte[]? bytes, string context)
    {
        var length = bytes?.Length ?? 0;
        LogDebug($"{context}: Received {length} byte(s) for image creation.");
        if (bytes == null || length == 0)
        {
            LogDebug($"{context}: No bytes available; skipping image creation.");
            return null;
        }

        var buffer = bytes;
        var image = CreateImageSafely(
            () =>
            {
                using MemoryStream stream = new(buffer);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat;
                bitmap.EndInit();
                TryFreezeImageSource(bitmap, $"{context} (byte stream)", LogDebug);
                return bitmap;
            },
            $"{context} (byte stream)");

        LogDebug(image is null
            ? $"{context}: Failed to create bitmap from bytes."
            : $"{context}: Successfully created bitmap from bytes.");

        return image;
    }

    private ImageSource? CreateImageSafely(Func<ImageSource?> factory, string context, bool enableLogging = true)
    {
        // Executing image creation on the current thread.
        // Note: The factory MUST freeze the image (e.g. bitmap.Freeze()) if created on a background thread
        // to ensure it can be accessed by the UI thread.
        try
        {
            var result = factory();
            if (enableLogging)
                LogDebug(
                    $"{context}: Image creation completed on current thread with result {(result is null ? "null" : "available")}.");
            return result;
        }
        catch (Exception ex)
        {
            if (enableLogging) LogDebug($"{context}: Exception during image creation: {ex.Message}.");
            return null;
        }
    }

    private static Task InvokeOnDispatcherAsync(Action action, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.CompletedTask;

        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;
        }

        action();
        return Task.CompletedTask;
    }

    private static void LogDebug(string message)
    {
        _ = message;
    }

    private static void TryFreezeImageSource(ImageSource image, string context, Action<string>? log)
    {
        if (image is not Freezable freezable) return;

        if (freezable.IsFrozen) return;

        if (!freezable.CanFreeze)
        {
            log?.Invoke($"{context}: Image cannot be frozen; continuing without freezing.");
            return;
        }

        try
        {
            freezable.Freeze();
        }
        catch (Exception ex)
        {
            log?.Invoke($"{context}: Failed to freeze image: {ex.Message}. Continuing without freezing.");
        }
    }

    private static string FormatValue(string? value)
    {
        if (value is null) return "<null>";

        if (string.IsNullOrWhiteSpace(value)) return "<empty>";

        return value;
    }

    private static string FormatUri(Uri? uri)
    {
        return uri?.AbsoluteUri ?? "<null>";
    }

    /// <summary>
    /// Suspends property change notifications during batch updates to reduce UI overhead.
    /// </summary>
    private IDisposable SuspendPropertyChangeNotifications()
    {
        return new PropertyChangeSuspensionScope(this);
    }

    /// <summary>
    /// Override OnPropertyChanged to support batching.
    /// </summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_propertyChangeSuspendCount > 0)
        {
            // Only batch specific property changes; null PropertyName (to refresh all bindings) is intentionally ignored
            if (e.PropertyName != null)
            {
                _pendingPropertyChanges.Add(e.PropertyName);
            }
            return;
        }

        base.OnPropertyChanged(e);
    }

    private void BeginPropertyChangeSuspension()
    {
        _propertyChangeSuspendCount++;
    }

    private void EndPropertyChangeSuspension()
    {
        if (_propertyChangeSuspendCount == 0)
        {
            // Already at zero, nothing to do (defensive programming)
            return;
        }

        _propertyChangeSuspendCount--;

        if (_propertyChangeSuspendCount == 0 && _pendingPropertyChanges.Count > 0)
        {
            // Fire all pending property changes
            foreach (var propertyName in _pendingPropertyChanges)
            {
                base.OnPropertyChanged(new PropertyChangedEventArgs(propertyName));
            }
            _pendingPropertyChanges.Clear();
        }
    }

    private sealed class PropertyChangeSuspensionScope : IDisposable
    {
        private readonly ModListItemViewModel _viewModel;
        private bool _disposed;

        public PropertyChangeSuspensionScope(ModListItemViewModel viewModel)
        {
            _viewModel = viewModel;
            _viewModel.BeginPropertyChangeSuspension();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _viewModel.EndPropertyChangeSuspension();
        }
    }

    public sealed record ReleaseChangelog(string Version, string Changelog);
}