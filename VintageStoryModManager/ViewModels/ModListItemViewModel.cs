using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VintageStoryModManager.Models;
using VintageStoryModManager.Services;

namespace VintageStoryModManager.ViewModels;

/// <summary>
/// View model that wraps <see cref="ModEntry"/> for presentation in the UI.
/// </summary>
public sealed class ModListItemViewModel : ObservableObject
{
    private static readonly HttpClient HttpClient = new();

    private readonly Func<ModListItemViewModel, bool, Task<ActivationResult>> _activationHandler;
    private readonly IReadOnlyList<ModDependencyInfo> _dependencies;
    private readonly ModDependencyInfo? _gameDependency;
    private readonly IReadOnlyList<string> _authors;
    private readonly IReadOnlyList<string> _contributors;
    private readonly string? _description;
    private readonly string? _metadataError;
    private string? _loadError;
    private IReadOnlyList<ModDependencyInfo> _missingDependencies;
    private bool _dependencyHasErrors;
    private IReadOnlyList<string> _databaseTags;
    private IReadOnlyList<string> _databaseRequiredGameVersions;
    private ModReleaseInfo? _latestRelease;
    private ModReleaseInfo? _latestCompatibleRelease;
    private IReadOnlyList<ModReleaseInfo> _releases;
    private IReadOnlyList<ReleaseChangelog> _newerReleaseChangelogs = Array.Empty<ReleaseChangelog>();
    private int? _databaseDownloads;
    private int? _databaseComments;
    private int? _databaseRecentDownloads;
    private string? _modDatabaseAssetId;
    private string? _modDatabasePageUrl;
    private Uri? _modDatabasePageUri;
    private ICommand? _openModDatabasePageCommand;
    private string? _latestDatabaseVersion;
    private ImageSource? _modDatabaseLogo;
    private string? _modDatabaseLogoUrl;
    private readonly string? _installedGameVersion;
    private readonly string _searchIndex;
    private IReadOnlyList<ModVersionOptionViewModel> _versionOptions = Array.Empty<ModVersionOptionViewModel>();

    private double _modDatabaseRelevancyScore;
    private DateTime? _modDatabaseLastUpdatedUtc;

    private bool _isActive;
    private bool _suppressState;
    private string _tooltip = string.Empty;
    private string? _versionWarningMessage;
    private string? _activationError;
    private bool _hasActivationError;
    private string _statusText = string.Empty;
    private string _statusDetails = string.Empty;
    private bool _isSelected;
    private bool _hasUpdate;
    private string? _updateMessage;
    private ModVersionOptionViewModel? _selectedVersionOption;
    private string? _modDatabaseSide;

    public sealed record ReleaseChangelog(string Version, string Changelog);

    public ModListItemViewModel(
        ModEntry entry,
        bool isActive,
        string location,
        Func<ModListItemViewModel, bool, Task<ActivationResult>> activationHandler,
        string? installedGameVersion = null,
        bool isInstalled = false)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _activationHandler = activationHandler ?? throw new ArgumentNullException(nameof(activationHandler));
        _installedGameVersion = installedGameVersion;

        ModId = entry.ModId;
        DisplayName = string.IsNullOrWhiteSpace(entry.Name) ? entry.ModId : entry.Name;
        Version = entry.Version;
        NetworkVersion = entry.NetworkVersion;
        Website = entry.Website;
        SourcePath = entry.SourcePath;
        Location = location;
        SourceKind = entry.SourceKind;
        _authors = entry.Authors;
        _contributors = entry.Contributors;
        var databaseInfo = entry.DatabaseInfo;
        _databaseTags = databaseInfo?.Tags ?? Array.Empty<string>();
        _databaseRequiredGameVersions = databaseInfo?.RequiredGameVersions ?? Array.Empty<string>();
        _latestRelease = databaseInfo?.LatestRelease;
        _latestCompatibleRelease = databaseInfo?.LatestCompatibleRelease;
        _releases = databaseInfo?.Releases ?? Array.Empty<ModReleaseInfo>();
        _modDatabaseAssetId = databaseInfo?.AssetId;
        _modDatabasePageUrl = databaseInfo?.ModPageUrl;
        _modDatabasePageUri = TryCreateHttpUri(_modDatabasePageUrl);
        LogDebug($"Initial database page URL '{FormatValue(_modDatabasePageUrl)}' resolved to '{FormatUri(_modDatabasePageUri)}'.");
        if (_modDatabasePageUri != null)
        {
            Uri commandUri = _modDatabasePageUri;
            _openModDatabasePageCommand = new RelayCommand(() => LaunchUri(commandUri));
        }
        _databaseDownloads = databaseInfo?.Downloads;
        _databaseComments = databaseInfo?.Comments;
        _databaseRecentDownloads = databaseInfo?.DownloadsLastThirtyDays;
        _modDatabaseLogoUrl = databaseInfo?.LogoUrl;
        _modDatabaseLogo = CreateModDatabaseLogoImage();
        LogDebug($"Initial database logo creation result: {_modDatabaseLogo is not null}. Source URL: '{FormatValue(_modDatabaseLogoUrl)}'.");
        _modDatabaseRelevancyScore = entry.ModDatabaseSearchScore ?? 0;
        _modDatabaseLastUpdatedUtc = databaseInfo?.LastReleasedUtc ?? DetermineLastUpdatedFromReleases(_releases);
        if (_databaseRecentDownloads is null)
        {
            _databaseRecentDownloads = CalculateDownloadsLastThirtyDaysFromReleases(_releases);
        }
        _latestDatabaseVersion = _latestRelease?.Version
            ?? databaseInfo?.LatestVersion
            ?? _latestCompatibleRelease?.Version
            ?? databaseInfo?.LatestCompatibleVersion;
        _loadError = entry.LoadError;

        IsInstalled = isInstalled;

        WebsiteUri = TryCreateHttpUri(Website);
        LogDebug($"Website URL '{FormatValue(Website)}' resolved to '{FormatUri(WebsiteUri)}'.");
        OpenWebsiteCommand = WebsiteUri != null ? new RelayCommand(() => LaunchUri(WebsiteUri)) : null;

        IReadOnlyList<ModDependencyInfo> dependencies = entry.Dependencies ?? Array.Empty<ModDependencyInfo>();
        _gameDependency = dependencies.FirstOrDefault(d => string.Equals(d.ModId, "game", StringComparison.OrdinalIgnoreCase))
            ?? dependencies.FirstOrDefault(d => d.IsGameOrCoreDependency);

        if (dependencies.Count == 0)
        {
            _dependencies = Array.Empty<ModDependencyInfo>();
        }
        else
        {
            ModDependencyInfo[] filtered = dependencies.Where(d => !d.IsGameOrCoreDependency).ToArray();
            _dependencies = filtered.Length == 0 ? Array.Empty<ModDependencyInfo>() : filtered;
        }
        _missingDependencies = entry.MissingDependencies is { Count: > 0 }
            ? entry.MissingDependencies.ToArray()
            : Array.Empty<ModDependencyInfo>();
        _dependencyHasErrors = entry.DependencyHasErrors;
        _description = entry.Description;
        _metadataError = entry.Error;
        Side = entry.Side;
        RequiredOnClient = entry.RequiredOnClient;
        RequiredOnServer = entry.RequiredOnServer;
        _modDatabaseSide = entry.DatabaseInfo?.Side;

        Icon = CreateImage(entry.IconBytes, "Icon bytes");
        LogDebug($"Icon image created: {Icon is not null}. Will fall back to database logo when null.");

        _isActive = isActive;
        HasErrors = entry.HasErrors;

        InitializeUpdateAvailability();
        InitializeVersionOptions();
        InitializeVersionWarning(_installedGameVersion);
        UpdateNewerReleaseChangelogs();
        UpdateStatusFromErrors();
        UpdateTooltip();
        _searchIndex = BuildSearchIndex(entry, location);
    }

    public string ModId { get; }

    public string DisplayName { get; }

    public string? Version { get; }

    public string VersionDisplay => string.IsNullOrWhiteSpace(Version) ? "—" : Version!;

    public string? NetworkVersion { get; }

    public string GameVersionDisplay
    {
        get
        {
            if (_gameDependency is { } dependency)
            {
                string version = dependency.Version?.Trim() ?? string.Empty;
                if (version.Length > 0)
                {
                    return version;
                }
            }

            return string.IsNullOrWhiteSpace(NetworkVersion) ? "—" : NetworkVersion!;
        }
    }

    public string AuthorsDisplay => _authors.Count == 0 ? "—" : string.Join(", ", _authors);

    public string ContributorsDisplay => _contributors.Count == 0 ? "—" : string.Join(", ", _contributors);

    public string DependenciesDisplay => _dependencies.Count == 0
        ? "—"
        : string.Join(", ", _dependencies.Select(dependency => dependency.Display));

    public IReadOnlyList<ModDependencyInfo> Dependencies => _dependencies;

    public IReadOnlyList<ModDependencyInfo> MissingDependencies => _missingDependencies;

    public bool DependencyHasErrors => _dependencyHasErrors;

    public bool HasDependencyIssues => _dependencyHasErrors || _missingDependencies.Count > 0;

    public bool CanFixDependencyIssues => HasDependencyIssues || HasLoadError;

    public IReadOnlyList<string> DatabaseTags => _databaseTags;

    public string DatabaseTagsDisplay => _databaseTags.Count == 0 ? "—" : string.Join(", ", _databaseTags);

    public string? ModDatabaseAssetId => _modDatabaseAssetId;

    public string? ModDatabasePageUrl => _modDatabasePageUrl;

    public Uri? ModDatabasePageUri => _modDatabasePageUri;

    public bool HasModDatabasePageLink => ModDatabasePageUri != null;

    public string ModDatabasePageUrlDisplay => string.IsNullOrWhiteSpace(ModDatabasePageUrl) ? "—" : ModDatabasePageUrl!;

    public string DownloadsDisplay => _databaseDownloads.HasValue
        ? _databaseDownloads.Value.ToString("N0", CultureInfo.CurrentCulture)
        : "—";

    public int ModDatabaseDownloadsSortKey => _databaseDownloads ?? 0;

    public string RecentDownloadsDisplay => _databaseRecentDownloads.HasValue
        ? _databaseRecentDownloads.Value.ToString("N0", CultureInfo.CurrentCulture)
        : "—";

    public int ModDatabaseRecentDownloadsSortKey => _databaseRecentDownloads ?? 0;

    public double ModDatabaseRelevancySortKey => _modDatabaseRelevancyScore;

    public long ModDatabaseLastUpdatedSortKey => _modDatabaseLastUpdatedUtc?.Ticks ?? 0L;

    public string CommentsDisplay => _databaseComments.HasValue
        ? _databaseComments.Value.ToString("N0", CultureInfo.CurrentCulture)
        : "—";

    public ImageSource? ModDatabasePreviewImage => Icon ?? _modDatabaseLogo;

    public bool HasModDatabasePreviewImage => ModDatabasePreviewImage != null;

    public string? LatestDatabaseVersion => _latestDatabaseVersion;

    public string LatestDatabaseVersionDisplay
    {
        get
        {
            if (InternetAccessManager.IsInternetAccessDisabled)
            {
                return "?";
            }

            return string.IsNullOrWhiteSpace(LatestDatabaseVersion) ? "—" : LatestDatabaseVersion!;
        }
    }

    public string LatestVersionSortKey
    {
        get
        {
            string version = string.IsNullOrWhiteSpace(LatestDatabaseVersion)
                ? LatestDatabaseVersionDisplay
                : LatestDatabaseVersion!;
            int order = CanUpdate ? 0 : 1;

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

    public void RefreshInternetAccessDependentState()
    {
        OnPropertyChanged(nameof(LatestDatabaseVersionDisplay));
        OnPropertyChanged(nameof(LatestVersionSortKey));
    }

    public bool IsInstalled { get; }

    public bool CanUpdate => _hasUpdate;

    public bool RequiresCompatibilitySelection => _hasUpdate
        && _latestRelease != null
        && !_latestRelease.IsCompatibleWithInstalledGame
        && _latestCompatibleRelease != null
        && !string.Equals(_latestRelease.Version, _latestCompatibleRelease.Version, StringComparison.OrdinalIgnoreCase);

    public bool HasCompatibleUpdate => _latestCompatibleRelease != null;

    public bool LatestReleaseIsCompatible => _latestRelease?.IsCompatibleWithInstalledGame ?? false;

    public bool ShouldHighlightLatestVersion => _hasUpdate;

    public ModReleaseInfo? LatestRelease => _latestRelease;

    public ModReleaseInfo? LatestCompatibleRelease => _latestCompatibleRelease;

    public IReadOnlyList<ModVersionOptionViewModel> VersionOptions => _versionOptions;

    public bool HasVersionOptions => _versionOptions.Count > 0;

    public bool HasDownloadableRelease => _latestRelease != null || _latestCompatibleRelease != null;

    public IReadOnlyList<ReleaseChangelog> NewerReleaseChangelogs => _newerReleaseChangelogs;

    public bool HasNewerReleaseChangelogs => _newerReleaseChangelogs.Count > 0;

    public IReadOnlyList<ReleaseChangelog> GetChangelogEntriesForUpgrade(string? targetVersion)
    {
        if (_releases.Count == 0 || string.IsNullOrWhiteSpace(targetVersion))
        {
            return Array.Empty<ReleaseChangelog>();
        }

        string trimmedTarget = targetVersion.Trim();
        string? normalizedTarget = VersionStringUtility.Normalize(targetVersion);

        string? installedVersion = Version;
        string? normalizedInstalled = VersionStringUtility.Normalize(installedVersion);

        var entries = new List<ReleaseChangelog>();
        bool capturing = false;

        foreach (ModReleaseInfo release in _releases)
        {
            if (!capturing && DoesReleaseMatchVersion(release, trimmedTarget, normalizedTarget))
            {
                capturing = true;
            }

            if (!capturing)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(installedVersion)
                && DoesReleaseMatchVersion(release, installedVersion.Trim(), normalizedInstalled))
            {
                break;
            }

            string? changelog = release.Changelog?.Trim();
            if (!string.IsNullOrEmpty(changelog))
            {
                entries.Add(new ReleaseChangelog(release.Version, changelog));
            }
        }

        return entries.Count == 0 ? Array.Empty<ReleaseChangelog>() : entries;
    }

    public string InstallButtonToolTip
    {
        get
        {
            if (!HasDownloadableRelease)
            {
                return "No downloadable releases are available.";
            }

            if (_latestRelease?.IsCompatibleWithInstalledGame == true)
            {
                return $"Install version {_latestRelease.Version}.";
            }

            if (_latestCompatibleRelease != null)
            {
                return $"Install compatible version {_latestCompatibleRelease.Version}.";
            }

            if (_latestRelease != null)
            {
                return $"Install version {_latestRelease.Version} (may be incompatible).";
            }

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
            if (_latestRelease is null)
            {
                return "No updates available.";
            }

            if (_latestRelease.IsCompatibleWithInstalledGame)
            {
                return $"Install version {_latestRelease.Version}.";
            }

            if (_latestCompatibleRelease != null
                && !string.Equals(_latestRelease.Version, _latestCompatibleRelease.Version, StringComparison.OrdinalIgnoreCase))
            {
                return $"Latest: {_latestRelease.Version}. Compatible: {_latestCompatibleRelease.Version}.";
            }

            return $"Install version {_latestRelease.Version} (may be incompatible).";
        }
    }

    public string DescriptionDisplay => string.IsNullOrWhiteSpace(_description) ? "No description available." : _description!;

    public string? Website { get; }

    public Uri? WebsiteUri { get; }

    public bool HasWebsiteLink => WebsiteUri != null;

    public ICommand? OpenWebsiteCommand { get; }

    public ICommand? OpenModDatabasePageCommand => _openModDatabasePageCommand;

    public string SourcePath { get; }

    public string Location { get; }

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
            string? preferredSide = GetPreferredSide();
            return string.IsNullOrWhiteSpace(preferredSide) ? "—" : preferredSide!;
        }
    }

    public bool? RequiredOnClient { get; }

    public string RequiredOnClientDisplay => RequiredOnClient.HasValue ? (RequiredOnClient.Value ? "Yes" : "No") : "—";

    public bool? RequiredOnServer { get; }

    public string RequiredOnServerDisplay => RequiredOnServer.HasValue ? (RequiredOnServer.Value ? "Yes" : "No") : "—";

    public ImageSource? Icon { get; }

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
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(StatusSortOrder));
            }
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

            if (_isActive == value)
            {
                return;
            }

            bool previous = _isActive;
            if (!SetProperty(ref _isActive, value))
            {
                return;
            }

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

    public void UpdateLoadError(string? loadError)
    {
        string? normalized = string.IsNullOrWhiteSpace(loadError) ? null : loadError;
        if (string.Equals(_loadError, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _loadError = normalized;
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(CanFixDependencyIssues));
        UpdateStatusFromErrors();
        UpdateTooltip();
    }

    public void UpdateDatabaseInfo(ModDatabaseInfo info, bool loadLogoImmediately = true)
    {
        if (info is null)
        {
            return;
        }

        string? previousSide = NormalizeSide(_modDatabaseSide);
        string? updatedSide = NormalizeSide(info.Side);
        _modDatabaseSide = info.Side;
        if (!string.Equals(previousSide, updatedSide, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(SideDisplay));
        }

        IReadOnlyList<string> tags = info.Tags ?? Array.Empty<string>();
        if (HasDifferentContent(_databaseTags, tags))
        {
            _databaseTags = tags;
            OnPropertyChanged(nameof(DatabaseTags));
            OnPropertyChanged(nameof(DatabaseTagsDisplay));
        }

        IReadOnlyList<string> requiredVersions = info.RequiredGameVersions ?? Array.Empty<string>();
        if (HasDifferentContent(_databaseRequiredGameVersions, requiredVersions))
        {
            _databaseRequiredGameVersions = requiredVersions;
        }

        ModReleaseInfo? latestRelease = info.LatestRelease;
        ModReleaseInfo? latestCompatibleRelease = info.LatestCompatibleRelease;
        IReadOnlyList<ModReleaseInfo> releases = info.Releases ?? Array.Empty<ModReleaseInfo>();

        _latestRelease = latestRelease;
        _latestCompatibleRelease = latestCompatibleRelease;
        _releases = releases;

        UpdateModDatabaseMetrics(info, releases);

        int? downloads = info.Downloads;
        if (_databaseDownloads != downloads)
        {
            _databaseDownloads = downloads;
            OnPropertyChanged(nameof(DownloadsDisplay));
            OnPropertyChanged(nameof(ModDatabaseDownloadsSortKey));
        }

        int? comments = info.Comments;
        if (_databaseComments != comments)
        {
            _databaseComments = comments;
            OnPropertyChanged(nameof(CommentsDisplay));
        }

        LogDebug($"UpdateDatabaseInfo invoked. AssetId='{FormatValue(info.AssetId)}', PageUrl='{FormatValue(info.ModPageUrl)}', LogoUrl='{FormatValue(info.LogoUrl)}'.");

        string? logoUrl = info.LogoUrl;
        bool logoUrlChanged = !string.Equals(_modDatabaseLogoUrl, logoUrl, StringComparison.Ordinal);
        if (logoUrlChanged)
        {
            _modDatabaseLogoUrl = logoUrl;
            bool shouldCreateLogo = loadLogoImmediately && Icon is null;
            if (shouldCreateLogo)
            {
                _modDatabaseLogo = CreateModDatabaseLogoImage();
                LogDebug($"Updated database logo. New URL='{FormatValue(_modDatabaseLogoUrl)}', Image created={_modDatabaseLogo is not null}.");
            }
            else
            {
                if (_modDatabaseLogo is not null)
                {
                    LogDebug("Clearing previously loaded database logo to defer image refresh.");
                }

                _modDatabaseLogo = null;
                LogDebug($"Deferred database logo update. New URL='{FormatValue(_modDatabaseLogoUrl)}'. Logo creation skipped={!shouldCreateLogo}.");
            }

            OnPropertyChanged(nameof(ModDatabasePreviewImage));
            OnPropertyChanged(nameof(HasModDatabasePreviewImage));
        }
        else if (loadLogoImmediately && Icon is null && _modDatabaseLogo is null && !string.IsNullOrWhiteSpace(_modDatabaseLogoUrl))
        {
            _modDatabaseLogo = CreateModDatabaseLogoImage();
            OnPropertyChanged(nameof(ModDatabasePreviewImage));
            OnPropertyChanged(nameof(HasModDatabasePreviewImage));
            LogDebug($"Loaded deferred database logo. URL='{FormatValue(_modDatabaseLogoUrl)}', Image created={_modDatabaseLogo is not null}.");
        }

        if (!string.Equals(_modDatabaseAssetId, info.AssetId, StringComparison.Ordinal))
        {
            _modDatabaseAssetId = info.AssetId;
            OnPropertyChanged(nameof(ModDatabaseAssetId));
            LogDebug($"Database asset id updated to '{FormatValue(_modDatabaseAssetId)}'.");
        }

        string? pageUrl = info.ModPageUrl;
        if (!string.Equals(_modDatabasePageUrl, pageUrl, StringComparison.Ordinal))
        {
            _modDatabasePageUrl = pageUrl;
            OnPropertyChanged(nameof(ModDatabasePageUrl));
            OnPropertyChanged(nameof(ModDatabasePageUrlDisplay));
            LogDebug($"Database page URL updated to '{FormatValue(_modDatabasePageUrl)}'.");
        }

        Uri? pageUri = TryCreateHttpUri(pageUrl);
        if (_modDatabasePageUri != pageUri)
        {
            _modDatabasePageUri = pageUri;
            OnPropertyChanged(nameof(ModDatabasePageUri));
            OnPropertyChanged(nameof(HasModDatabasePageLink));
            LogDebug($"Database page URI resolved to '{FormatUri(_modDatabasePageUri)}'.");
        }

        ICommand? pageCommand = null;
        if (pageUri != null)
        {
            Uri commandUri = pageUri;
            pageCommand = new RelayCommand(() => LaunchUri(commandUri));
            LogDebug($"Database page command initialized for '{commandUri}'.");
        }

        SetProperty(ref _openModDatabasePageCommand, pageCommand, nameof(OpenModDatabasePageCommand));

        string? latestDatabaseVersion = latestRelease?.Version
            ?? info.LatestVersion
            ?? latestCompatibleRelease?.Version
            ?? info.LatestCompatibleVersion;

        if (!string.Equals(_latestDatabaseVersion, latestDatabaseVersion, StringComparison.Ordinal))
        {
            _latestDatabaseVersion = latestDatabaseVersion;
            OnPropertyChanged(nameof(LatestDatabaseVersion));
            OnPropertyChanged(nameof(LatestDatabaseVersionDisplay));
            OnPropertyChanged(nameof(LatestVersionSortKey));
            LogDebug($"Latest database version updated to '{FormatValue(_latestDatabaseVersion)}'.");
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

    public void EnsureModDatabaseLogoLoaded()
    {
        if (_modDatabaseLogo is not null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_modDatabaseLogoUrl))
        {
            return;
        }

        _modDatabaseLogo = CreateModDatabaseLogoImage();
        OnPropertyChanged(nameof(ModDatabasePreviewImage));
        OnPropertyChanged(nameof(HasModDatabasePreviewImage));
        LogDebug($"Deferred database logo load complete. URL='{FormatValue(_modDatabaseLogoUrl)}', Image created={_modDatabaseLogo is not null}.");
    }

    public async Task LoadModDatabaseLogoAsync(CancellationToken cancellationToken)
    {
        if (_modDatabaseLogo is not null)
        {
            return;
        }

        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            return;
        }

        string? logoUrl = _modDatabaseLogoUrl;
        if (string.IsNullOrWhiteSpace(logoUrl))
        {
            return;
        }

        Uri? uri = TryCreateHttpUri(logoUrl);
        if (uri is null)
        {
            LogDebug($"Async database logo load skipped. Unable to resolve URI from '{FormatValue(logoUrl)}'.");
            return;
        }

        try
        {
            InternetAccessManager.ThrowIfInternetAccessDisabled();

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogDebug($"Async database logo load failed for '{uri}'. HTTP status {(int)response.StatusCode} ({response.StatusCode}).");
                return;
            }

            byte[] payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (payload.Length == 0)
            {
                LogDebug($"Async database logo load returned no data for '{uri}'.");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            ImageSource? image = CreateBitmapFromBytes(payload, uri);
            if (image is null)
            {
                return;
            }

            await InvokeOnDispatcherAsync(
                    () =>
                    {
                        if (_modDatabaseLogo is not null)
                        {
                            return;
                        }

                        _modDatabaseLogo = image;
                        OnPropertyChanged(nameof(ModDatabasePreviewImage));
                        OnPropertyChanged(nameof(HasModDatabasePreviewImage));
                        LogDebug($"Async database logo load complete. URL='{FormatValue(_modDatabaseLogoUrl)}', Image created={_modDatabaseLogo is not null}.");
                    },
                    cancellationToken)
                .ConfigureAwait(false);
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
        if (_isActive == isActive)
        {
            return;
        }

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
        bool changed = false;

        if (_dependencyHasErrors != hasDependencyErrors)
        {
            _dependencyHasErrors = hasDependencyErrors;
            OnPropertyChanged(nameof(DependencyHasErrors));
            changed = true;
        }

        IReadOnlyList<ModDependencyInfo> normalizedMissing = missingDependencies is { Count: > 0 }
            ? missingDependencies.ToArray()
            : Array.Empty<ModDependencyInfo>();

        if (!ReferenceEquals(_missingDependencies, normalizedMissing))
        {
            if (_missingDependencies.Count != normalizedMissing.Count
                || !_missingDependencies.SequenceEqual(normalizedMissing))
            {
                _missingDependencies = normalizedMissing;
                OnPropertyChanged(nameof(MissingDependencies));
                changed = true;
            }
        }

        if (changed)
        {
            OnPropertyChanged(nameof(HasDependencyIssues));
            OnPropertyChanged(nameof(CanFixDependencyIssues));
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
        else if (_hasUpdate)
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
        string tooltipText = DisplayName;

        if (!string.IsNullOrWhiteSpace(_description))
        {
            tooltipText = string.Concat(
                tooltipText,
                Environment.NewLine,
                Environment.NewLine,
                _description.Trim());
        }

        if (!string.IsNullOrWhiteSpace(_versionWarningMessage))
        {
            tooltipText = string.Concat(
                tooltipText,
                Environment.NewLine,
                Environment.NewLine,
                _versionWarningMessage);
        }

        if (_hasUpdate && !string.IsNullOrWhiteSpace(_updateMessage))
        {
            tooltipText = string.Concat(
                tooltipText,
                Environment.NewLine,
                Environment.NewLine,
                _updateMessage);
        }

        Tooltip = tooltipText;
    }

    internal bool MatchesSearchTokens(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return true;
        }

        foreach (var token in tokens)
        {
            if (_searchIndex.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private void InitializeUpdateAvailability()
    {
        if (_latestRelease is null)
        {
            _hasUpdate = false;
            _updateMessage = null;
            return;
        }

        if (!VersionStringUtility.IsCandidateVersionNewer(_latestRelease.Version, Version))
        {
            _hasUpdate = false;
            _updateMessage = null;
            return;
        }

        _hasUpdate = true;
        _updateMessage = BuildUpdateMessage(_latestRelease);
    }

    private string BuildUpdateMessage(ModReleaseInfo release)
    {
        if (release.IsCompatibleWithInstalledGame)
        {
            return $"Update available: {release.Version}";
        }

        if (_latestCompatibleRelease != null
            && !string.Equals(_latestCompatibleRelease.Version, release.Version, StringComparison.OrdinalIgnoreCase))
        {
            return $"Update available: {release.Version} (latest compatible: {_latestCompatibleRelease.Version})";
        }

        return $"Update available: {release.Version} (may be incompatible with your Vintage Story version)";
    }

    private void InitializeVersionOptions()
    {
        if (_releases.Count == 0 && string.IsNullOrWhiteSpace(Version))
        {
            _versionOptions = Array.Empty<ModVersionOptionViewModel>();
            OnPropertyChanged(nameof(VersionOptions));
            OnPropertyChanged(nameof(HasVersionOptions));
            SetProperty(ref _selectedVersionOption, null);
            return;
        }

        string? normalizedInstalled = VersionStringUtility.Normalize(Version);
        var options = new List<ModVersionOptionViewModel>(_releases.Count + 1);
        bool hasInstalled = false;

        foreach (ModReleaseInfo release in _releases)
        {
            bool isInstalled = IsReleaseInstalled(release, Version, normalizedInstalled);
            if (isInstalled)
            {
                hasInstalled = true;
            }

            options.Add(ModVersionOptionViewModel.FromRelease(release, isInstalled));
        }

        if (!hasInstalled && !string.IsNullOrWhiteSpace(Version))
        {
            options.Insert(0, ModVersionOptionViewModel.FromInstalledVersion(Version!, normalizedInstalled));
        }

        IReadOnlyList<ModVersionOptionViewModel> finalized = options.Count == 0
            ? Array.Empty<ModVersionOptionViewModel>()
            : options.AsReadOnly();

        _versionOptions = finalized;
        OnPropertyChanged(nameof(VersionOptions));
        OnPropertyChanged(nameof(HasVersionOptions));

        ModVersionOptionViewModel? selected = finalized.FirstOrDefault(option => option.IsInstalled)
            ?? finalized.FirstOrDefault();

        SetProperty(ref _selectedVersionOption, selected);
    }

    private void UpdateNewerReleaseChangelogs()
    {
        IReadOnlyList<ReleaseChangelog> updated = BuildNewerReleaseChangelogList();
        if (ReferenceEquals(_newerReleaseChangelogs, updated))
        {
            return;
        }

        _newerReleaseChangelogs = updated;
        OnPropertyChanged(nameof(NewerReleaseChangelogs));
        OnPropertyChanged(nameof(HasNewerReleaseChangelogs));
    }

    private IReadOnlyList<ReleaseChangelog> BuildNewerReleaseChangelogList()
    {
        if (_releases.Count == 0)
        {
            return Array.Empty<ReleaseChangelog>();
        }

        int installedIndex = FindInstalledReleaseIndex();
        int endExclusive = installedIndex >= 0 ? installedIndex : _releases.Count;
        if (endExclusive <= 0)
        {
            return Array.Empty<ReleaseChangelog>();
        }

        var results = new List<ReleaseChangelog>();
        for (int i = 0; i < endExclusive; i++)
        {
            ModReleaseInfo release = _releases[i];
            if (string.IsNullOrWhiteSpace(release.Changelog))
            {
                continue;
            }

            string trimmed = release.Changelog.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            results.Add(new ReleaseChangelog(release.Version, trimmed));
        }

        return results.Count == 0 ? Array.Empty<ReleaseChangelog>() : results;
    }

    private int FindInstalledReleaseIndex()
    {
        if (_releases.Count == 0 || string.IsNullOrWhiteSpace(Version))
        {
            return -1;
        }

        string trimmedInstalled = Version!.Trim();
        string? normalizedInstalled = VersionStringUtility.Normalize(Version);

        for (int i = 0; i < _releases.Count; i++)
        {
            ModReleaseInfo release = _releases[i];
            if (!string.IsNullOrWhiteSpace(release.Version)
                && string.Equals(release.Version.Trim(), trimmedInstalled, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }

            if (normalizedInstalled != null
                && !string.IsNullOrWhiteSpace(release.NormalizedVersion)
                && string.Equals(release.NormalizedVersion, normalizedInstalled, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsReleaseInstalled(ModReleaseInfo release, string? installedVersion, string? normalizedInstalled)
    {
        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            return false;
        }

        string trimmedInstalled = installedVersion.Trim();
        if (!string.IsNullOrWhiteSpace(release.Version)
            && string.Equals(release.Version.Trim(), trimmedInstalled, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalizedInstalled != null && !string.IsNullOrWhiteSpace(release.NormalizedVersion))
        {
            return string.Equals(release.NormalizedVersion, normalizedInstalled, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool DoesReleaseMatchVersion(ModReleaseInfo release, string trimmedVersion, string? normalizedVersion)
    {
        if (!string.IsNullOrWhiteSpace(release.Version)
            && string.Equals(release.Version.Trim(), trimmedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(release.NormalizedVersion)
            && normalizedVersion != null
            && string.Equals(release.NormalizedVersion, normalizedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private void InitializeVersionWarning(string? installedGameVersion)
    {
        string? normalizedInstalled = VersionStringUtility.Normalize(installedGameVersion);
        IReadOnlyList<(string Normalized, string Original)> requiredVersions = BuildRequiredVersionCandidates();

        if (TryCreateVersionWarning(requiredVersions, normalizedInstalled, out string? message))
        {
            _versionWarningMessage = message;
        }
        else
        {
            _versionWarningMessage = null;
        }
    }

    private IReadOnlyList<(string Normalized, string Original)> BuildRequiredVersionCandidates()
    {
        if (_databaseRequiredGameVersions.Count > 0)
        {
            var normalized = new List<(string Normalized, string Original)>(_databaseRequiredGameVersions.Count);
            foreach (string tag in _databaseRequiredGameVersions)
            {
                string? normalizedTag = VersionStringUtility.Normalize(tag);
                if (!string.IsNullOrWhiteSpace(normalizedTag))
                {
                    normalized.Add((normalizedTag!, tag));
                }
            }

            if (normalized.Count > 0)
            {
                return normalized;
            }
        }

        if (_gameDependency is { Version: not null } dependency)
        {
            string? normalizedDependency = VersionStringUtility.Normalize(dependency.Version);
            if (!string.IsNullOrWhiteSpace(normalizedDependency))
            {
                return new (string, string)[]
                {
                    (normalizedDependency!, dependency.Version!)
                };
            }
        }

        return Array.Empty<(string, string)>();
    }

    private string AppendWarningText(string baseText)
    {
        if (string.IsNullOrWhiteSpace(_versionWarningMessage))
        {
            return baseText;
        }

        if (string.IsNullOrWhiteSpace(baseText))
        {
            return _versionWarningMessage!;
        }

        return string.Concat(baseText, Environment.NewLine, Environment.NewLine, _versionWarningMessage);
    }

    private static bool TryCreateVersionWarning(IReadOnlyCollection<(string Normalized, string Original)> requiredVersions, string? installedVersion, out string? message)
    {
        message = null;

        if (requiredVersions.Count == 0 || string.IsNullOrWhiteSpace(installedVersion))
        {
            return false;
        }

        if (!TryGetMajorMinor(installedVersion!, out int installedMajor, out int installedMinor))
        {
            return false;
        }

        bool hasComparable = false;
        foreach ((string normalized, _) in requiredVersions)
        {
            if (!TryGetMajorMinor(normalized, out int requiredMajor, out int requiredMinor))
            {
                continue;
            }

            hasComparable = true;
            if (requiredMajor == installedMajor && requiredMinor == installedMinor)
            {
                return false;
            }
        }

        if (!hasComparable)
        {
            return false;
        }

        string displayVersions = FormatRequiredVersions(requiredVersions.Select(pair => pair.Original));
        message = $"The mod asks for Vintage Story version {displayVersions} but you have {installedVersion}, it might be incompatible";
        return true;
    }

    private static string FormatRequiredVersions(IEnumerable<string> versions)
    {
        string[] filtered = versions
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Select(version => version.Trim())
            .Where(version => version.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (filtered.Length == 0)
        {
            return "—";
        }

        if (filtered.Length == 1)
        {
            return filtered[0];
        }

        if (filtered.Length == 2)
        {
            return string.Join(" or ", filtered);
        }

        return string.Join(", ", filtered.Take(filtered.Length - 1)) + " or " + filtered[^1];
    }

    private void UpdateModDatabaseMetrics(ModDatabaseInfo info, IReadOnlyList<ModReleaseInfo> releases)
    {
        int? recentDownloads = info?.DownloadsLastThirtyDays ?? CalculateDownloadsLastThirtyDaysFromReleases(releases);
        if (_databaseRecentDownloads != recentDownloads)
        {
            _databaseRecentDownloads = recentDownloads;
            OnPropertyChanged(nameof(RecentDownloadsDisplay));
            OnPropertyChanged(nameof(ModDatabaseRecentDownloadsSortKey));
        }

        DateTime? lastUpdated = DetermineLastUpdated(info, releases);
        if (_modDatabaseLastUpdatedUtc != lastUpdated)
        {
            _modDatabaseLastUpdatedUtc = lastUpdated;
            OnPropertyChanged(nameof(ModDatabaseLastUpdatedSortKey));
        }
    }

    private static int? CalculateDownloadsLastThirtyDaysFromReleases(IReadOnlyList<ModReleaseInfo> releases)
    {
        if (releases.Count == 0)
        {
            return null;
        }

        DateTime threshold = DateTime.UtcNow.AddDays(-30);
        int total = 0;
        bool hasData = false;

        foreach (var release in releases)
        {
            if (release?.CreatedUtc is not { } createdUtc || createdUtc < threshold)
            {
                continue;
            }

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
        if (info?.LastReleasedUtc is DateTime lastReleased)
        {
            return lastReleased;
        }

        return DetermineLastUpdatedFromReleases(releases);
    }

    private static DateTime? DetermineLastUpdatedFromReleases(IReadOnlyList<ModReleaseInfo> releases)
    {
        DateTime? latest = null;

        foreach (var release in releases)
        {
            if (release?.CreatedUtc is not { } createdUtc)
            {
                continue;
            }

            if (!latest.HasValue || createdUtc > latest.Value)
            {
                latest = createdUtc;
            }
        }

        return latest;
    }

    private static bool TryGetMajorMinor(string version, out int major, out int minor)
    {
        major = 0;
        minor = 0;

        string[] parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out major))
        {
            return false;
        }

        if (parts.Length > 1)
        {
            if (!int.TryParse(parts[1], out minor))
            {
                return false;
            }
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
        AppendText(builder, _latestRelease?.Version);
        AppendText(builder, _latestCompatibleRelease?.Version);
        AppendCollection(builder, _databaseTags);
        AppendCollection(builder, _databaseRequiredGameVersions);
        AppendCollection(builder, _authors);
        AppendCollection(builder, _contributors);
        AppendDependencies(builder, _dependencies);
        AppendReleases(builder, _releases);

        return builder.ToString();
    }

    private static void AppendText(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        AppendSegment(builder, value);

        string normalized = NormalizeSearchText(value);
        if (!string.IsNullOrEmpty(normalized) && !string.Equals(normalized, value, StringComparison.Ordinal))
        {
            AppendSegment(builder, normalized);
        }
    }

    private static void AppendCollection(StringBuilder builder, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            AppendText(builder, value);
        }
    }

    private static void AppendSegment(StringBuilder builder, string value)
    {
        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(value);
    }

    private static bool HasDifferentContent<T>(IReadOnlyList<T> current, IReadOnlyList<T> updated)
    {
        if (ReferenceEquals(current, updated))
        {
            return false;
        }

        if (current.Count != updated.Count)
        {
            return true;
        }

        EqualityComparer<T> comparer = EqualityComparer<T>.Default;
        for (int i = 0; i < current.Count; i++)
        {
            if (!comparer.Equals(current[i], updated[i]))
            {
                return true;
            }
        }

        return false;
    }

    private string? GetPreferredSide()
    {
        string? databaseSide = NormalizeSide(_modDatabaseSide);
        if (!string.IsNullOrWhiteSpace(databaseSide))
        {
            return databaseSide;
        }

        return NormalizeSide(Side);
    }

    private static string? NormalizeSide(string? side)
    {
        if (string.IsNullOrWhiteSpace(side))
        {
            return null;
        }

        string trimmed = side.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        string lower = trimmed.ToLowerInvariant();
        return lower switch
        {
            "both" => "both",
            "client" => "client",
            "server" => "server",
            "universal" or "all" or "any" => "both",
            _ => trimmed
        };
    }

    private static string NormalizeSearchText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (char character in normalized)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);

            if (category == UnicodeCategory.NonSpacingMark
                || category == UnicodeCategory.SpacingCombiningMark
                || category == UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
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
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? absolute) && IsSupportedScheme(absolute))
        {
            return absolute;
        }

        if (Uri.TryCreate($"https://{value}", UriKind.Absolute, out absolute) && IsSupportedScheme(absolute))
        {
            return absolute;
        }

        return null;
    }

    private static bool IsSupportedScheme(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

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
        return CreateImageFromUri(_modDatabaseLogoUrl, "Mod database logo", enableLogging: false);
    }

    private ImageSource? CreateBitmapFromBytes(byte[] payload, Uri sourceUri)
    {
        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
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
        string formattedUrl = FormatValue(url);
        if (enableLogging)
        {
            LogDebug($"{context}: Attempting to create image from URL {formattedUrl}.");
        }

        Uri? uri = TryCreateHttpUri(url);
        if (uri == null)
        {
            if (enableLogging)
            {
                LogDebug($"{context}: Unable to resolve absolute URI from {formattedUrl}.");
            }
            return null;
        }

        if (enableLogging)
        {
            LogDebug($"{context}: Resolved URI '{uri}'.");
        }

        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            if (enableLogging)
            {
                LogDebug($"{context}: Skipping image load because internet access is disabled.");
            }

            return null;
        }

        ImageSource? image = CreateImageSafely(
            () =>
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = uri;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.EndInit();
                TryFreezeImageSource(bitmap, $"{context} ({uri})", enableLogging ? LogDebug : null);
                return bitmap;
            },
            $"{context} ({uri})",
            enableLogging);

        if (enableLogging)
        {
            LogDebug(image is null
                ? $"{context}: Failed to load image from '{uri}'."
                : $"{context}: Successfully loaded image from '{uri}'.");
        }

        return image;
    }

    private ImageSource? CreateImage(byte[]? bytes, string context)
    {
        int length = bytes?.Length ?? 0;
        LogDebug($"{context}: Received {length} byte(s) for image creation.");
        if (bytes == null || length == 0)
        {
            LogDebug($"{context}: No bytes available; skipping image creation.");
            return null;
        }

        byte[] buffer = bytes;
        ImageSource? image = CreateImageSafely(
            () =>
            {
                using MemoryStream stream = new(buffer);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
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
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            try
            {
                if (enableLogging)
                {
                    LogDebug($"{context}: Invoking image creation on dispatcher thread.");
                }
                ImageSource? result = dispatcher.Invoke(factory);
                if (enableLogging)
                {
                    LogDebug($"{context}: Dispatcher invocation completed with result {(result is null ? "null" : "available")}.");
                }
                return result;
            }
            catch (Exception ex)
            {
                if (enableLogging)
                {
                    LogDebug($"{context}: Exception during dispatcher invocation: {ex.Message}.");
                }
                return null;
            }
        }

        try
        {
            ImageSource? result = factory();
            if (enableLogging)
            {
                LogDebug($"{context}: Image creation completed on current thread with result {(result is null ? "null" : "available")}.");
            }
            return result;
        }
        catch (Exception ex)
        {
            if (enableLogging)
            {
                LogDebug($"{context}: Exception during image creation: {ex.Message}.");
            }
            return null;
        }
    }

    private static Task InvokeOnDispatcherAsync(Action action, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
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
        if (image is not Freezable freezable)
        {
            return;
        }

        if (freezable.IsFrozen)
        {
            return;
        }

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
        if (value is null)
        {
            return "<null>";
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }

        return value;
    }

    private static string FormatUri(Uri? uri)
    {
        return uri?.AbsoluteUri ?? "<null>";
    }
}

