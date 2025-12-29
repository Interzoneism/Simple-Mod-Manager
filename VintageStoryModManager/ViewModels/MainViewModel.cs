using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleVsManager.Cloud;
using VintageStoryModManager.Models;
using VintageStoryModManager.Services;
using Application = System.Windows.Application;
using Timer = System.Threading.Timer;

namespace VintageStoryModManager.ViewModels;

/// <summary>
///     Main view model that coordinates mod discovery and activation.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const string InternetAccessDisabledStatusMessage = "Enable Internet Access in the File menu to use.";
    private const string TagsColumnName = "Tags";
    private const string UserReportsColumnName = "UserReports";
    private static readonly TimeSpan InstalledModsSearchDebounceMin = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan InstalledModsSearchDebounceMax = TimeSpan.FromMilliseconds(300);
    private const int LargeModListThreshold = 200;
    private const int VeryLargeModListThreshold = 500;
    private static readonly TimeSpan BusyStateReleaseDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan FastCheckInterval = TimeSpan.FromMinutes(2);
    private static readonly int MaxConcurrentDatabaseRefreshes = DevConfig.MaxConcurrentDatabaseRefreshes;
    private static readonly int MaxConcurrentUserReportRefreshes = DevConfig.MaxConcurrentUserReportRefreshes;
    private static readonly int MaxNewModsRecentMonths = DevConfig.MaxNewModsRecentMonths;
    private static readonly int InstalledModsIncrementalBatchSize = DevConfig.InstalledModsIncrementalBatchSize;

    // Database refresh delays for startup optimization
    private const int InitialRefreshDelayMs = 500;  // Delay before starting database refresh on initial load
    private const int IncrementalRefreshDelayMs = 300;  // Delay before refreshing after incremental updates

    private readonly object _busyStateLock = new();
    private readonly RelayCommand _clearSearchCommand;
    private readonly ClientSettingsWatcher _clientSettingsWatcher;
    private readonly ObservableCollection<CloudModlistListEntry> _cloudModlists = new();
    private readonly ObservableCollection<LocalModlistListEntry> _localModlists = new();
    private readonly UserConfigurationService _configuration;
    private readonly ModDatabaseService _databaseService;
    private readonly ModDiscoveryService _discoveryService;
    private readonly object _fastCheckTimerLock = new();
    private readonly object _searchDebounceLock = new();
    private readonly HashSet<ModListItemViewModel> _installedModSubscriptions = new();
    private readonly TagCacheService _tagCache = new();
    private readonly TagFilterService _tagFilterService;
    private readonly ObservableCollection<TagFilterOptionViewModel> _installedTagFilters = new();
    private string[] _lastInstalledAvailableTags = Array.Empty<string>();
    private readonly Dictionary<string, string> _latestReleaseUserReportEtags = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _modDetailsBusyScopeLock = new();
    private readonly object _databaseRefreshLock = new();
    private readonly Dictionary<string, ModEntry> _modEntriesBySourcePath = new(StringComparer.OrdinalIgnoreCase);

    private readonly BatchedObservableCollection<ModListItemViewModel> _mods = new();
    private readonly object _modsStateLock = new();
    private readonly ModDirectoryWatcher _modsWatcher;

    private readonly Dictionary<string, ModListItemViewModel> _modViewModelsBySourcePath =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ObservableCollection<ModListItemViewModel> _searchResults = new();
    private readonly HashSet<ModListItemViewModel> _searchResultSubscriptions = new();
    // Tag filtering is now handled by _tagFilterService
    private readonly ClientSettingsStore _settingsStore;
    private readonly RelayCommand _showModlistTabCommand;
    private readonly RelayCommand _showMainTabCommand;
    private readonly RelayCommand _showDatabaseTabCommand;
    private readonly ObservableCollection<SortOption> _sortOptions;
    private readonly HashSet<string> _suppressedTagEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _userReportEtags = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _userReportOperationLock = new();
    private readonly string _voteEtagCachePath;
    private readonly object _voteEtagPersistenceLock = new();

    private readonly SemaphoreSlim _userReportRefreshLimiter =
        new(MaxConcurrentUserReportRefreshes, MaxConcurrentUserReportRefreshes);

    private readonly ModVersionVoteService _voteService = new();
    private readonly ModLoadingTimingService _timingService = new();

    // Database info batching for improved UI performance
    private readonly object _databaseInfoBatchLock = new();
    private readonly List<(ModEntry entry, ModDatabaseInfo info, bool loadLogoImmediately)> _pendingDatabaseInfoUpdates = new();
    private Timer? _databaseInfoBatchTimer;
    private const int DatabaseInfoBatchDelayMs = 50; // Batch updates every 50ms
    private const int DatabaseInfoBatchSize = 20; // Apply up to 20 updates per batch

    private List<string>? _cachedBasePaths;
    private int _activeMods;
    private int _activeUserReportOperations;
    private bool _allowModDetailsRefresh = true;
    private bool _areUserReportsVisible = true;
    private int _busyOperationCount;
    private CancellationTokenSource? _busyReleaseCts;
    private bool _isInitialLoad = true;
    private bool _disposed;
    private Timer? _searchDebounceTimer;
    private CancellationTokenSource? _pendingSearchCts;
    private Timer? _fastCheckTimer;
    private bool _hasActiveBusyScope;
    private bool _hasEnabledUserReportFetching;
    private bool _hasFetchedUserReportsThisSession;
    private bool _hasMultipleSelectedMods;
    private volatile bool _hasPendingFastCheck;
    private bool _hasSelectedMods;
    private bool _hasSelectedTags;
    private bool _hasShownModDetailsLoadingStatus;
    private bool _isAutoRefreshDisabled;
    private bool _isBusy;
    private bool _isCompactView;
    private bool _isErrorStatus;
    private bool _isFastCheckInProgress;
    private int _isFastCheckRunning;
    private bool _isInstalledTagRefreshPending;
    private bool _isLoadingModDetails;
    private bool _isLoadingMods;
    private bool _isModDetailsProgressVisible;
    private double _modDetailsProgress;
    private int _modDetailsRefreshCompletedWork;
    private int _modDetailsRefreshTotalWork;
    private string _modDetailsProgressStage = string.Empty;
    private string _modDetailsStatusText = string.Empty;
    private double _loadingProgress;
    private string _loadingStatusText = string.Empty;
    private bool _isModDetailsRefreshForced;
    private bool _isModDetailsStatusActive;
    private Task? _databaseRefreshTask;
    private CancellationTokenSource? _databaseRefreshCts;
    private bool _isModInfoExpanded = true;
    private bool _isTagsColumnVisible = true;
    private bool _useModDbDesignView;
    private IDisposable? _modDetailsBusyScope;
    private string? _modsStateFingerprint;
    private int _pendingModDetailsRefreshCount;
    private string _searchText = string.Empty;
    private string[] _searchTokens = Array.Empty<string>();
    private ModListItemViewModel? _selectedMod;

    private SortOption? _selectedSortOption;
    private string _statusMessage = string.Empty;
    private bool _suppressInstalledTagFilterSelectionChanges;
    private int _totalMods;
    private int _updatableModsCount;
    private ViewSection _viewSection = ViewSection.MainTab;

    public event EventHandler<ModUserReportChangedEventArgs>? UserReportVoteSubmitted;

    public MainViewModel(
        string dataDirectory,
        UserConfigurationService configuration,
        string? gameDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(configuration);

        DataDirectory = Path.GetFullPath(dataDirectory);
        _voteEtagCachePath = Path.Combine(DataDirectory, "voteEtags.json");
        _configuration = configuration;

        _settingsStore = new ClientSettingsStore(DataDirectory);
        PerformClientSettingsCleanupIfNeeded();
        _discoveryService = new ModDiscoveryService(_settingsStore);
        _databaseService = new ModDatabaseService();
        _tagFilterService = new TagFilterService(_tagCache);
        InstalledGameVersion = VintageStoryVersionLocator.GetInstalledVersion(gameDirectory);
        _modsWatcher = new ModDirectoryWatcher(_discoveryService);
        _clientSettingsWatcher = new ClientSettingsWatcher(_settingsStore.SettingsPath);
        LoadVoteEtagsFromDisk();

        ModsView = CollectionViewSource.GetDefaultView(_mods);
        ModsView.Filter = FilterMod;
        SearchResultsView = CollectionViewSource.GetDefaultView(_searchResults);
        CloudModlistsView = CollectionViewSource.GetDefaultView(_cloudModlists);
        LocalModlistsView = CollectionViewSource.GetDefaultView(_localModlists);
        InstalledTagFilters = new ReadOnlyObservableCollection<TagFilterOptionViewModel>(_installedTagFilters);
        _mods.CollectionChanged += OnModsCollectionChanged;
        _searchResults.CollectionChanged += OnSearchResultsCollectionChanged;
        _sortOptions = new ObservableCollection<SortOption>(CreateSortOptions());
        SortOptions = new ReadOnlyObservableCollection<SortOption>(_sortOptions);
        SelectedSortOption = SortOptions.FirstOrDefault();
        SelectedSortOption?.Apply(ModsView);
        _hasEnabledUserReportFetching = FirebaseAnonymousAuthenticator.HasPersistedState();
        _isAutoRefreshDisabled = configuration.DisableAutoRefresh;
        _allowModDetailsRefresh = !_isAutoRefreshDisabled;

        _clearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => HasSearchText);
        ClearSearchCommand = _clearSearchCommand;

        _showMainTabCommand = new RelayCommand(() => SetViewSection(ViewSection.MainTab));
        _showDatabaseTabCommand = new RelayCommand(
            () => SetViewSection(ViewSection.DatabaseTab),
            () => !InternetAccessManager.IsInternetAccessDisabled);
        _showModlistTabCommand = new RelayCommand(
            () => SetViewSection(ViewSection.ModlistTab),
            () => !InternetAccessManager.IsInternetAccessDisabled);
        ShowMainTabCommand = _showMainTabCommand;
        ShowDatabaseTabCommand = _showDatabaseTabCommand;
        ShowModlistTabCommand = _showModlistTabCommand;

        RefreshCommand = new AsyncRelayCommand(LoadModsAsync);
        SetStatus("Ready.", false);

        InternetAccessManager.InternetAccessChanged += OnInternetAccessChanged;

        ResetFastCheckTimer();
    }

    public string DataDirectory { get; }

    public string? PlayerUid => _settingsStore.PlayerUid;

    public string? PlayerName => _settingsStore.PlayerName;

    public ICollectionView ModsView { get; }

    public ICollectionView SearchResultsView { get; }

    public ICollectionView CloudModlistsView { get; }

    public ICollectionView LocalModlistsView { get; }

    public ModDirectoryWatcher ModsWatcher => _modsWatcher;

    public ModLoadingTimingService TimingService => _timingService;

    public ReadOnlyObservableCollection<TagFilterOptionViewModel> InstalledTagFilters { get; }

    public ICollectionView CurrentModsView => _viewSection switch
    {
        ViewSection.DatabaseTab => SearchResultsView,
        ViewSection.ModlistTab => CloudModlistsView,
        _ => ModsView
    };

    public bool CanAccessCloudModlists => !InternetAccessManager.IsInternetAccessDisabled;

    public ReadOnlyObservableCollection<SortOption> SortOptions { get; }

    public SortOption? SelectedSortOption
    {
        get => _selectedSortOption;
        set
        {
            if (SetProperty(ref _selectedSortOption, value)) value?.Apply(ModsView);
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsLoadingMods
    {
        get => _isLoadingMods;
        private set
        {
            if (SetProperty(ref _isLoadingMods, value)) RecalculateIsBusy();
        }
    }

    public double LoadingProgress
    {
        get => _loadingProgress;
        private set => SetProperty(ref _loadingProgress, value);
    }

    public string LoadingStatusText
    {
        get => _loadingStatusText;
        private set => SetProperty(ref _loadingStatusText, value);
    }

    public bool IsLoadingModDetails
    {
        get => _isLoadingModDetails;
        private set
        {
            if (SetProperty(ref _isLoadingModDetails, value))
            {
                RecalculateIsBusy();
                UpdateModDetailsProgressVisibility();
            }
        }
    }

    public bool IsModDetailsProgressVisible
    {
        get => _isModDetailsProgressVisible;
        private set => SetProperty(ref _isModDetailsProgressVisible, value);
    }

    public bool IsFastCheckInProgress
    {
        get => _isFastCheckInProgress;
        private set
        {
            if (SetProperty(ref _isFastCheckInProgress, value)) UpdateModDetailsProgressVisibility();
        }
    }

    public double ModDetailsProgress
    {
        get => _modDetailsProgress;
        private set => SetProperty(ref _modDetailsProgress, value);
    }

    public string ModDetailsStatusText
    {
        get => _modDetailsStatusText;
        private set => SetProperty(ref _modDetailsStatusText, value);
    }

    public bool IsCompactView
    {
        get => _isCompactView;
        set => SetProperty(ref _isCompactView, value);
    }

    public bool HasSelectedTags
    {
        get => _hasSelectedTags;
        private set
        {
            if (SetProperty(ref _hasSelectedTags, value)) OnPropertyChanged(nameof(TagsColumnHeader));
        }
    }

    public string TagsColumnHeader => HasSelectedTags ? "Tags (*)" : "Tags";


    public bool IsModInfoExpanded
    {
        get => _isModInfoExpanded;
        set => SetProperty(ref _isModInfoExpanded, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value)) OnPropertyChanged(nameof(HasStatusMessage));
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public ModListItemViewModel? SelectedMod
    {
        get => _selectedMod;
        private set
        {
            if (SetProperty(ref _selectedMod, value)) OnPropertyChanged(nameof(HasSelectedMod));
        }
    }

    public bool HasSelectedMod => SelectedMod != null;

    public bool HasSelectedMods
    {
        get => _hasSelectedMods;
        private set => SetProperty(ref _hasSelectedMods, value);
    }

    public bool HasMultipleSelectedMods
    {
        get => _hasMultipleSelectedMods;
        private set => SetProperty(ref _hasMultipleSelectedMods, value);
    }

    public bool IsErrorStatus
    {
        get => _isErrorStatus;
        private set => SetProperty(ref _isErrorStatus, value);
    }

    public IRelayCommand ShowMainTabCommand { get; }

    public IRelayCommand ShowDatabaseTabCommand { get; }

    public IRelayCommand ShowModlistTabCommand { get; }

    public bool IsViewingModlistTab => _viewSection == ViewSection.ModlistTab;

    public bool IsViewingMainTab => _viewSection == ViewSection.MainTab;

    public bool SearchModDatabase => _viewSection == ViewSection.DatabaseTab;

    public bool UseModDbDesignView
    {
        get => _useModDbDesignView;
        set => SetProperty(ref _useModDbDesignView, value);
    }

    public bool HasCloudModlists => _cloudModlists.Count > 0;

    public bool HasLocalModlists => _localModlists.Count > 0;

    public string SearchText
    {
        get => _searchText;
        set
        {
            var newValue = value ?? string.Empty;
            if (!SetProperty(ref _searchText, newValue)) return;

            var hadSearchTokens = _searchTokens.Length > 0;
            _searchTokens = CreateSearchTokens(newValue);
            var hasSearchTokens = _searchTokens.Length > 0;

            OnPropertyChanged(nameof(HasSearchText));
            _clearSearchCommand.NotifyCanExecuteChanged();

            // Only refresh if the search filter state actually changed.
            // This avoids unnecessary refreshes when clearing an already-empty search
            // or during tab switches where the search text is cleared.
            if (hadSearchTokens || hasSearchTokens)
                TriggerDebouncedInstalledModsSearch();

        }
    }

    public bool HasSearchText => _searchTokens.Length > 0;

    public int TotalMods
    {
        get => _totalMods;
        private set
        {
            if (SetProperty(ref _totalMods, value)) OnPropertyChanged(nameof(SummaryText));
        }
    }

    public int ActiveMods
    {
        get => _activeMods;
        private set
        {
            if (SetProperty(ref _activeMods, value)) OnPropertyChanged(nameof(SummaryText));
        }
    }

    public int UpdatableModsCount
    {
        get => _updatableModsCount;
        private set
        {
            if (SetProperty(ref _updatableModsCount, value))
            {
                OnPropertyChanged(nameof(UpdateAllButtonLabel));
                OnPropertyChanged(nameof(UpdateAllModsMenuHeader));
            }
        }
    }

    public string SummaryText => TotalMods == 0
        ? "No mods found."
        : $"{ActiveMods} active of {TotalMods} mods";

    public string UpdateAllButtonLabel => UpdatableModsCount == 0
        ? "Manage Updates"
        : $"Manage Updates ({UpdatableModsCount})";

    public string UpdateAllModsMenuHeader => UpdatableModsCount == 0
        ? "_Update All Mods"
        : $"_Update All Mods ({UpdatableModsCount})";

    public string NoModsFoundMessage =>
        $"No mods found. If this is unexpected, verify that your VintageStoryData folder is correctly set: {DataDirectory}. You can change it in the File Menu.";

    public IRelayCommand ClearSearchCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public string? InstalledGameVersion { get; }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        InternetAccessManager.InternetAccessChanged -= OnInternetAccessChanged;
        _mods.CollectionChanged -= OnModsCollectionChanged;
        _searchResults.CollectionChanged -= OnSearchResultsCollectionChanged;

        DetachAllInstalledMods();
        DetachAllSearchResults();

        foreach (var filter in _installedTagFilters) filter.PropertyChanged -= OnInstalledTagFilterPropertyChanged;


        _installedTagFilters.Clear();

        lock (_fastCheckTimerLock)
        {
            _fastCheckTimer?.Dispose();
            _fastCheckTimer = null;
        }

        lock (_searchDebounceLock)
        {
            _searchDebounceTimer?.Dispose();
            _searchDebounceTimer = null;
            _pendingSearchCts?.Cancel();
            _pendingSearchCts?.Dispose();
            _pendingSearchCts = null;
        }

        lock (_databaseInfoBatchLock)
        {
            _databaseInfoBatchTimer?.Dispose();
            _databaseInfoBatchTimer = null;
            _pendingDatabaseInfoUpdates.Clear();
        }

        lock (_databaseRefreshLock)
        {
            _databaseRefreshCts?.Cancel();
            _databaseRefreshCts?.Dispose();
            _databaseRefreshCts = null;
        }

        if (_databaseRefreshTask != null)
        {
            try
            {
                _databaseRefreshTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // Ignore background refresh cancellations during shutdown.
            }
        }

        _clientSettingsWatcher.Dispose();
        _modDetailsBusyScope?.Dispose();
        _modDetailsBusyScope = null;
        _userReportRefreshLimiter.Dispose();
        _voteService.Dispose();
    }

    public IDisposable EnterBusyScope()
    {
        return BeginBusyScope();
    }

    public void SetInstalledColumnVisibility(string columnName, bool isVisible)
    {
        if (string.IsNullOrWhiteSpace(columnName)) return;

        if (string.Equals(columnName, TagsColumnName, StringComparison.OrdinalIgnoreCase))
            SetTagsColumnVisibility(isVisible);
        else if (string.Equals(columnName, UserReportsColumnName, StringComparison.OrdinalIgnoreCase))
            SetUserReportsColumnVisibility(isVisible);
    }

    private void SetViewSection(ViewSection section)
    {
        if (_viewSection == section) return;

        if (section == ViewSection.DatabaseTab && InternetAccessManager.IsInternetAccessDisabled)
        {
            SetStatus(InternetAccessDisabledStatusMessage, false);
            return;
        }

        if (section == ViewSection.ModlistTab && InternetAccessManager.IsInternetAccessDisabled)
        {
            SetStatus(InternetAccessDisabledStatusMessage, false);
            return;
        }


        _viewSection = section;

        if (!string.IsNullOrEmpty(_searchText)) SearchText = string.Empty;

        switch (section)
        {
            case ViewSection.DatabaseTab:
                SelectedMod = null;
                SetStatus("Showing mod database.", false);
                break;
            case ViewSection.MainTab:
                SelectedMod = null;
                SetStatus("Showing installed mods.", false);
                break;
            case ViewSection.ModlistTab:
                SelectedMod = null;
                SetStatus("Showing cloud modlists.", false);
                break;
        }

        // Notify critical property changes immediately
        OnPropertyChanged(nameof(IsViewingModlistTab));
        OnPropertyChanged(nameof(IsViewingMainTab));
        OnPropertyChanged(nameof(SearchModDatabase));
        OnPropertyChanged(nameof(CurrentModsView));

        // Defer non-critical property changes to avoid blocking UI thread during tab switch
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_viewSection == ViewSection.MainTab) FastCheck();
        }, DispatcherPriority.Background);
    }

    public void FastCheck()
    {
        ResetFastCheckTimer();

        if (_isAutoRefreshDisabled) return;

        if (InternetAccessManager.IsInternetAccessDisabled) return;

        _hasPendingFastCheck = true;

        if (Interlocked.CompareExchange(ref _isFastCheckRunning, 1, 0) == 0) _ = Task.Run(RunFastCheckAsync);
    }

    private async Task RunFastCheckAsync()
    {
        try
        {
            IsFastCheckInProgress = true;

            while (_hasPendingFastCheck)
            {
                _hasPendingFastCheck = false;

                if (InternetAccessManager.IsInternetAccessDisabled) break;

                var updateCandidates = await CheckForNewModReleasesAsync(CancellationToken.None, false)
                    .ConfigureAwait(false);

                if (updateCandidates.Count > 0) QueueDatabaseInfoRefresh(updateCandidates, true);
            }
        }
        catch
        {
            // Swallow failures to ensure subsequent checks can continue.
        }
        finally
        {
            IsFastCheckInProgress = false;

            Interlocked.Exchange(ref _isFastCheckRunning, 0);

            if (_hasPendingFastCheck && !InternetAccessManager.IsInternetAccessDisabled) FastCheck();
        }
    }

    private void ResetFastCheckTimer()
    {
        if (_disposed || _isAutoRefreshDisabled) return;

        lock (_fastCheckTimerLock)
        {
            _fastCheckTimer ??= new Timer(OnFastCheckTimerElapsed, null, Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _fastCheckTimer.Change(FastCheckInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void StopFastCheckTimer()
    {
        lock (_fastCheckTimerLock)
        {
            _fastCheckTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnFastCheckTimerElapsed(object? state)
    {
        if (_disposed || _isAutoRefreshDisabled) return;

        FastCheck();
    }

    private async Task<IReadOnlyList<ModEntry>> CheckForNewModReleasesAsync(
        CancellationToken cancellationToken,
        bool showProgress = true)
    {
        var entries = new List<ModEntry>();
        var completedChecks = 0;
        var modDetailsCheckEnqueued = false;

        try
        {
            entries = await InvokeOnDispatcherAsync(
                () =>
                {
                    var snapshot = new List<ModEntry>(_modEntriesBySourcePath.Count);
                    foreach (var entry in _modEntriesBySourcePath.Values)
                    {
                        if (entry is null || string.IsNullOrWhiteSpace(entry.ModId)) continue;

                        snapshot.Add(entry);
                    }

                    return snapshot;
                },
                cancellationToken).ConfigureAwait(false);

            if (entries.Count == 0) return Array.Empty<ModEntry>();

            var updateCandidates = new List<ModEntry>();
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (showProgress)
            {
                OnModDetailsRefreshEnqueued(entries.Count, "Checking for mod updates...");
                modDetailsCheckEnqueued = true;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                completedChecks++;

                var modId = entry.ModId;
                if (!processed.Add(modId)) continue;

                var latestVersion = await _databaseService
                    .TryFetchLatestReleaseVersionAsync(modId, cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(latestVersion)) continue;

                if (!IsDifferentVersion(entry.Version, latestVersion)) continue;

                var knownLatest = entry.DatabaseInfo?.LatestRelease?.Version
                                  ?? entry.DatabaseInfo?.LatestVersion;

                if (string.Equals(knownLatest, latestVersion, StringComparison.OrdinalIgnoreCase)) continue;

                updateCandidates.Add(entry);
            }

            return updateCandidates.Count == 0
                ? Array.Empty<ModEntry>()
                : updateCandidates;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<ModEntry>();
        }
        catch
        {
            return Array.Empty<ModEntry>();
        }
        finally
        {
            if (modDetailsCheckEnqueued)
            {
                var remaining = Math.Max(0, entries.Count - completedChecks);
                if (remaining > 0) completedChecks += remaining;

                OnModDetailsRefreshCompleted(completedChecks);
            }
        }
    }

    private async Task CheckForVoteChangesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(InstalledGameVersion)) return;

        var targets = await InvokeOnDispatcherAsync(
            () =>
            {
                var list = new List<VoteCheckTarget>(_mods.Count);
                foreach (var mod in _mods)
                {
                    var hasUserReportSummary = mod.UserReportSummary is not null;
                    var hasLatestReleaseSummary = mod.LatestReleaseUserReportSummary is not null;

                    if (!hasUserReportSummary && !hasLatestReleaseSummary) continue;

                    var latestReleaseVersion = hasLatestReleaseSummary ? mod.LatestRelease?.Version : null;
                    list.Add(new VoteCheckTarget(
                        mod,
                        hasUserReportSummary ? mod.UserReportModVersion : null,
                        hasUserReportSummary,
                        latestReleaseVersion,
                        hasLatestReleaseSummary));
                }

                return list;
            },
            cancellationToken).ConfigureAwait(false);

        if (targets.Count == 0) return;

        using var limiter = new SemaphoreSlim(MaxConcurrentUserReportRefreshes, MaxConcurrentUserReportRefreshes);
        var tasks = targets
            .Select(target => CheckVoteChangesForModAsync(target, limiter, cancellationToken))
            .ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task CheckVoteChangesForModAsync(
        VoteCheckTarget target,
        SemaphoreSlim limiter,
        CancellationToken cancellationToken)
    {
        await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (target.HasUserReportSummary
                && !string.IsNullOrWhiteSpace(target.UserReportVersion)
                && !string.IsNullOrWhiteSpace(InstalledGameVersion))
            {
                var key = BuildVoteEtagKey("current", target.Mod.ModId, target.UserReportVersion, InstalledGameVersion);
                _userReportEtags.TryGetValue(key, out var etag);

                var result = await _voteService
                    .GetVoteSummaryIfChangedAsync(target.Mod.ModId, target.UserReportVersion, InstalledGameVersion!,
                        etag, cancellationToken)
                    .ConfigureAwait(false);

                if (!result.IsNotModified)
                {
                    if (result.Summary is not null)
                        await InvokeOnDispatcherAsync(
                            () => target.Mod.ApplyUserReportSummary(result.Summary!),
                            cancellationToken,
                            DispatcherPriority.Background).ConfigureAwait(false);

                    StoreUserReportEtag(target.Mod.ModId, target.UserReportVersion, result.ETag);
                }
                else if (string.IsNullOrEmpty(etag) && !string.IsNullOrEmpty(result.ETag))
                {
                    StoreUserReportEtag(target.Mod.ModId, target.UserReportVersion, result.ETag);
                }
            }

            if (target.HasLatestReleaseSummary
                && !string.IsNullOrWhiteSpace(target.LatestReleaseVersion)
                && !string.IsNullOrWhiteSpace(InstalledGameVersion))
            {
                var key = BuildVoteEtagKey("latest", target.Mod.ModId, target.LatestReleaseVersion,
                    InstalledGameVersion);
                _latestReleaseUserReportEtags.TryGetValue(key, out var etag);

                var result = await _voteService
                    .GetVoteSummaryIfChangedAsync(target.Mod.ModId, target.LatestReleaseVersion, InstalledGameVersion!,
                        etag, cancellationToken)
                    .ConfigureAwait(false);

                if (!result.IsNotModified)
                {
                    if (result.Summary is not null)
                        await InvokeOnDispatcherAsync(
                            () => target.Mod.ApplyLatestReleaseUserReportSummary(result.Summary!),
                            cancellationToken,
                            DispatcherPriority.Background).ConfigureAwait(false);

                    StoreLatestReleaseUserReportEtag(target.Mod.ModId, target.LatestReleaseVersion, result.ETag);
                }
                else if (string.IsNullOrEmpty(etag) && !string.IsNullOrEmpty(result.ETag))
                {
                    StoreLatestReleaseUserReportEtag(target.Mod.ModId, target.LatestReleaseVersion, result.ETag);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Swallow unexpected failures for individual mods to allow other checks to continue.
        }
        finally
        {
            limiter.Release();
        }
    }

    private void StoreUserReportEtag(string modId, string? modVersion, string? etag)
    {
        UpdateVoteEtag(_userReportEtags, "current", modId, modVersion, etag);
    }

    private void StoreLatestReleaseUserReportEtag(string modId, string? modVersion, string? etag)
    {
        UpdateVoteEtag(_latestReleaseUserReportEtags, "latest", modId, modVersion, etag);
    }

    private string? GetVoteEtag(
        Dictionary<string, string> source,
        string prefix,
        string modId,
        string? modVersion)
    {
        if (string.IsNullOrWhiteSpace(modId)
            || string.IsNullOrWhiteSpace(modVersion)
            || string.IsNullOrWhiteSpace(InstalledGameVersion))
            return null;

        var key = BuildVoteEtagKey(prefix, modId, modVersion, InstalledGameVersion);
        return source.TryGetValue(key, out var etag) ? etag : null;
    }

    private void UpdateVoteEtag(
        Dictionary<string, string> target,
        string prefix,
        string modId,
        string? modVersion,
        string? etag)
    {
        if (string.IsNullOrWhiteSpace(modId)
            || string.IsNullOrWhiteSpace(modVersion)
            || string.IsNullOrWhiteSpace(InstalledGameVersion))
            return;

        var key = BuildVoteEtagKey(prefix, modId, modVersion, InstalledGameVersion);
        lock (_voteEtagPersistenceLock)
        {
            var changed = false;

            if (string.IsNullOrEmpty(etag))
            {
                changed = target.Remove(key);
            }
            else if (!target.TryGetValue(key, out var existing)
                     || !string.Equals(existing, etag, StringComparison.Ordinal))
            {
                target[key] = etag;
                changed = true;
            }

            if (changed) PersistVoteEtagsLocked();
        }
    }

    private static string BuildVoteEtagKey(string prefix, string modId, string modVersion, string? gameVersion)
    {
        return string.Concat(prefix, '|', modId, '|', modVersion, '|', gameVersion ?? string.Empty);
    }

    private void LoadVoteEtagsFromDisk()
    {
        lock (_voteEtagPersistenceLock)
        {
            try
            {
                if (!File.Exists(_voteEtagCachePath)) return;

                using var stream = File.OpenRead(_voteEtagCachePath);
                var state = JsonSerializer.Deserialize<VoteEtagCacheState>(stream);

                _userReportEtags.Clear();
                _latestReleaseUserReportEtags.Clear();

                if (state?.Current is { } current)
                    foreach (var entry in current)
                        _userReportEtags[entry.Key] = entry.Value;

                if (state?.Latest is { } latest)
                    foreach (var entry in latest)
                        _latestReleaseUserReportEtags[entry.Key] = entry.Value;
            }
            catch
            {
                // Ignore cache load failures; fall back to empty in-memory cache.
            }
        }
    }

    private void PersistVoteEtagsLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_voteEtagCachePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var state = new VoteEtagCacheState
            {
                Current = new Dictionary<string, string>(_userReportEtags, StringComparer.OrdinalIgnoreCase),
                Latest = new Dictionary<string, string>(_latestReleaseUserReportEtags, StringComparer.OrdinalIgnoreCase)
            };

            using var stream = File.Create(_voteEtagCachePath);
            JsonSerializer.Serialize(stream, state);
        }
        catch
        {
            // Persistence errors are non-fatal; ignore them.
        }
    }

    private sealed class VoteEtagCacheState
    {
        public Dictionary<string, string>? Current { get; set; }

        public Dictionary<string, string>? Latest { get; set; }
    }

    private static bool IsDifferentVersion(string? installedVersion, string? latestVersion)
    {
        if (string.IsNullOrWhiteSpace(latestVersion) || string.IsNullOrWhiteSpace(installedVersion)) return false;

        var normalizedInstalled = VersionStringUtility.Normalize(installedVersion);
        var normalizedLatest = VersionStringUtility.Normalize(latestVersion);

        if (!string.IsNullOrWhiteSpace(normalizedInstalled) && !string.IsNullOrWhiteSpace(normalizedLatest))
            return !string.Equals(normalizedInstalled, normalizedLatest, StringComparison.OrdinalIgnoreCase);

        return !string.Equals(installedVersion.Trim(), latestVersion.Trim(), StringComparison.OrdinalIgnoreCase);
    }
    public Task InitializeAsync()
    {
        return LoadModsAsync();
    }

    public void ReplaceCloudModlists(IEnumerable<CloudModlistListEntry>? entries)
    {
        _cloudModlists.Clear();

        if (entries is not null)
            foreach (var entry in entries)
                if (entry is not null)
                    _cloudModlists.Add(entry);

        CloudModlistsView.Refresh();
        OnPropertyChanged(nameof(HasCloudModlists));
    }

    public bool TryReplaceCloudModlist(CloudModlistListEntry existing, CloudModlistListEntry replacement)
    {
        var index = _cloudModlists.IndexOf(existing);
        if (index < 0) return false;

        _cloudModlists[index] = replacement;
        CloudModlistsView.Refresh();
        OnPropertyChanged(nameof(HasCloudModlists));
        return true;
    }

    public void ReplaceLocalModlists(IEnumerable<LocalModlistListEntry>? entries)
    {
        _localModlists.Clear();

        if (entries is not null)
            foreach (var entry in entries)
                if (entry is not null)
                    _localModlists.Add(entry);

        LocalModlistsView.Refresh();
        OnPropertyChanged(nameof(HasLocalModlists));
    }

    public IReadOnlyList<string> GetCurrentDisabledEntries()
    {
        return _settingsStore.GetDisabledEntriesSnapshot();
    }

    public IReadOnlyList<ModPresetModState> GetCurrentModStates()
    {
        return _mods
            .Select(mod => new ModPresetModState(mod.ModId, mod.Version, mod.IsActive, null, null))
            .ToList();
    }

    public IReadOnlyList<ModListItemViewModel> GetInstalledModsSnapshot()
    {
        return _mods.ToList();
    }

    public IReadOnlyList<ModUsageTrackingEntry> GetActiveModUsageSnapshot()
    {
        var result = new List<ModUsageTrackingEntry>();

        if (string.IsNullOrWhiteSpace(InstalledGameVersion)) return result;

        var gameVersion = InstalledGameVersion.Trim();
        var distinct = new HashSet<ModUsageTrackingKey>();

        foreach (var mod in _mods)
        {
            if (mod is null || !mod.IsActive) continue;

            if (string.IsNullOrWhiteSpace(mod.ModId) || string.IsNullOrWhiteSpace(mod.Version)) continue;

            var modId = mod.ModId.Trim();
            var modVersion = mod.Version.Trim();

            var key = new ModUsageTrackingKey(modId, modVersion, gameVersion);
            if (!distinct.Add(key)) continue;

            result.Add(new ModUsageTrackingEntry(
                modId,
                modVersion,
                gameVersion,
                mod.CanSubmitUserReport,
                mod.UserVoteOption.HasValue));
        }

        return result;
    }

    public IReadOnlyList<string> GetActiveModIdsSnapshot()
    {
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var mod in _mods)
        {
            if (mod is null || !mod.IsActive || string.IsNullOrWhiteSpace(mod.ModId)) continue;

            var trimmed = mod.ModId.Trim();
            if (trimmed.Length == 0) continue;

            if (distinct.Add(trimmed)) result.Add(trimmed);
        }

        return result;
    }

    public bool TryGetInstalledModDisplayName(string? modId, out string? displayName)
    {
        displayName = null;

        if (string.IsNullOrWhiteSpace(modId)) return false;

        foreach (var mod in _mods)
        {
            if (mod is null || string.IsNullOrWhiteSpace(mod.ModId)) continue;

            if (string.Equals(mod.ModId, modId, StringComparison.OrdinalIgnoreCase))
            {
                displayName = mod.DisplayName;
                return true;
            }
        }

        return false;
    }

    public async Task<bool> ApplyPresetAsync(ModPreset preset)
    {
        string? localError = null;

        bool success;
        if (preset.IncludesModStatus && preset.ModStates.Count > 0)
        {
            var installedMods = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in _mods)
            {
                if (mod is null || string.IsNullOrWhiteSpace(mod.ModId)) continue;

                var normalizedId = mod.ModId.Trim();
                if (!installedMods.ContainsKey(normalizedId))
                {
                    var version = string.IsNullOrWhiteSpace(mod.Version) ? null : mod.Version!.Trim();
                    installedMods.Add(normalizedId, version);
                }
            }

            success = await Task.Run(() =>
            {
                foreach (var state in preset.ModStates)
                {
                    if (state is null || string.IsNullOrWhiteSpace(state.ModId) ||
                        state.IsActive is not bool desiredState) continue;

                    var normalizedId = state.ModId.Trim();
                    var hasInstalledMod = installedMods.TryGetValue(normalizedId, out var installedVersion);

                    var recordedVersion = string.IsNullOrWhiteSpace(state.Version)
                        ? null
                        : state.Version!.Trim();

                    if (desiredState)
                    {
                        if (!hasInstalledMod) continue;

                        var versionsToActivate = new HashSet<string?>(StringComparer.OrdinalIgnoreCase)
                        {
                            null
                        };

                        if (!string.IsNullOrWhiteSpace(installedVersion)) versionsToActivate.Add(installedVersion);

                        if (!string.IsNullOrWhiteSpace(recordedVersion)) versionsToActivate.Add(recordedVersion);

                        foreach (var versionKey in versionsToActivate)
                            if (!_settingsStore.TrySetActive(normalizedId, versionKey, true, out var error))
                            {
                                localError = error;
                                return false;
                            }
                    }
                    else
                    {
                        string? versionToDisable;

                        if (hasInstalledMod && !string.IsNullOrWhiteSpace(installedVersion))
                        {
                            versionToDisable = installedVersion;
                        }
                        else if (!string.IsNullOrWhiteSpace(recordedVersion))
                        {
                            versionToDisable = recordedVersion;
                        }
                        else
                        {
                            versionToDisable = null;
                        }

                        if (!_settingsStore.TrySetActive(normalizedId, versionToDisable, false, out var error))
                        {
                            localError = error;
                            return false;
                        }
                    }
                }

                localError = null;
                return true;
            });
        }
        else
        {
            var entries = preset.DisabledEntries ?? Array.Empty<string>();

            success = await Task.Run(() =>
            {
                var result = _settingsStore.TryApplyDisabledEntries(entries, out var error);
                localError = error;
                return result;
            });
        }

        if (!success)
        {
            var message = string.IsNullOrWhiteSpace(localError)
                ? $"Failed to apply preset \"{preset.Name}\"."
                : localError!;
            SetStatus(message, true);
            return false;
        }

        foreach (var mod in _mods)
        {
            var isDisabled = _settingsStore.IsDisabled(mod.ModId, mod.Version);
            mod.SetIsActiveSilently(!isDisabled);
        }

        UpdateActiveCount();
        SelectedSortOption?.Apply(ModsView);
        ModsView.Refresh();
        SetStatus($"Applied preset \"{preset.Name}\".", false);
        return true;
    }

    public void ReportStatus(string message, bool isError = false)
    {
        SetStatus(message, isError);
    }

    public void OnInternetAccessStateChanged()
    {

        if (_allowModDetailsRefresh && _modEntriesBySourcePath.Count > 0)
            QueueDatabaseInfoRefresh(_modEntriesBySourcePath.Values.ToArray());
    }

    internal void SetAutoRefreshDisabled(bool disabled)
    {
        _isAutoRefreshDisabled = disabled;
        _allowModDetailsRefresh = !_isAutoRefreshDisabled;

        if (disabled)
            StopFastCheckTimer();
        else
            ResetFastCheckTimer();
    }

    internal void ForceNextRefreshToLoadDetails()
    {
        _isModDetailsRefreshForced = true;
    }

    internal void RefreshInstalledModDetails()
    {
        if (_modEntriesBySourcePath.Count > 0)
            QueueDatabaseInfoRefresh(_modEntriesBySourcePath.Values.ToArray(), forceRefresh: true);
    }

    internal void SetSelectedMod(ModListItemViewModel? mod, int selectionCount)
    {
        HasSelectedMods = selectionCount > 0;
        HasMultipleSelectedMods = selectionCount > 1;
        SelectedMod = mod;
    }

    internal void RemoveSearchResult(ModListItemViewModel mod)
    {
        if (mod is null) return;

        _searchResults.Remove(mod);

        if (ReferenceEquals(SelectedMod, mod)) SelectedMod = null;
    }

    public ModListItemViewModel? FindInstalledModById(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return null;

        var trimmed = modId.Trim();

        foreach (var mod in _mods)
        {
            if (mod is null || string.IsNullOrWhiteSpace(mod.ModId)) continue;

            if (string.Equals(mod.ModId.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)) return mod;
        }

        return null;
    }

    internal ModListItemViewModel? FindModBySourcePath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;

        return _modViewModelsBySourcePath.TryGetValue(sourcePath, out var viewModel)
            ? viewModel
            : null;
    }

    internal async Task<bool> PreserveActivationStateAsync(string modId, string? previousVersion, string? newVersion,
        bool wasActive)
    {
        string? localError = null;

        var success = await Task.Run(() =>
            _settingsStore.TryUpdateDisabledEntry(modId, previousVersion, newVersion, !wasActive, out localError));

        if (!success)
        {
            var message = string.IsNullOrWhiteSpace(localError)
                ? $"Failed to preserve the activation state for {modId}."
                : localError!;
            SetStatus(message, true);
        }

        return success;
    }

    internal async Task<ActivationResult> ApplyActivationChangeAsync(ModListItemViewModel mod, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(mod);

        string? localError = null;
        var success = await Task.Run(() =>
        {
            var result = _settingsStore.TrySetActive(mod.ModId, mod.Version, isActive, out var error);
            localError = error;
            return result;
        });

        if (!success)
        {
            var message = string.IsNullOrWhiteSpace(localError)
                ? $"Failed to update {mod.DisplayName}."
                : localError!;
            SetStatus(message, true);
            return new ActivationResult(false, message);
        }

        UpdateActiveCount();
        ReapplyActiveSortIfNeeded();
        SetStatus(isActive ? $"Activated {mod.DisplayName}." : $"Deactivated {mod.DisplayName}.", false);
        return new ActivationResult(true, null);
    }

    internal IReadOnlyCollection<string> GetSourcePathsForModsWithErrors()
    {
        if (_mods.Count == 0) return Array.Empty<string>();

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in _mods)
        {
            if (mod is null) continue;

            var sourcePath = mod.SourcePath;
            if (string.IsNullOrWhiteSpace(sourcePath)) continue;

            if (mod.HasLoadError || mod.DependencyHasErrors || mod.MissingDependencies.Count > 0)
                result.Add(sourcePath);
        }

        return result.Count == 0 ? Array.Empty<string>() : result.ToArray();
    }

    internal async Task RefreshModsWithErrorsAsync(IReadOnlyCollection<string>? includeSourcePaths = null)
    {
        if (_mods.Count == 0 && _modEntriesBySourcePath.Count == 0) return;

        _modsWatcher.EnsureWatchers();
        var changeSet = _modsWatcher.ConsumeChanges();
        if (changeSet.RequiresFullRescan)
        {
            await LoadModsAsync().ConfigureAwait(true);
            return;
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (includeSourcePaths is { Count: > 0 })
            foreach (var path in includeSourcePaths)
                if (!string.IsNullOrWhiteSpace(path))
                    candidates.Add(path);

        foreach (var path in changeSet.Paths)
            if (!string.IsNullOrWhiteSpace(path))
                candidates.Add(path);

        foreach (var mod in _mods)
        {
            if (mod is null || string.IsNullOrWhiteSpace(mod.SourcePath)) continue;

            if (mod.HasLoadError || mod.DependencyHasErrors || mod.MissingDependencies.Count > 0)
                candidates.Add(mod.SourcePath);
        }

        if (_modEntriesBySourcePath.Count > 0)
        {
            var allEntries = new List<ModEntry>(_modEntriesBySourcePath.Values);
            var recalculationSeed = new List<ModEntry>();

            foreach (var path in candidates)
                if (_modEntriesBySourcePath.TryGetValue(path, out var entry) && entry != null)
                    recalculationSeed.Add(entry);

            var impacted = recalculationSeed.Count == 0
                ? Array.Empty<ModEntry>()
                : await Task
                    .Run(() => _discoveryService.ApplyLoadStatusesIncremental(allEntries, recalculationSeed))
                    .ConfigureAwait(true);

            foreach (var entry in impacted)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.SourcePath)) continue;

                if (entry.HasLoadError
                    || entry.DependencyHasErrors
                    || (entry.MissingDependencies?.Count ?? 0) > 0)
                    candidates.Add(entry.SourcePath);
            }
        }

        if (candidates.Count == 0) return;

        var previousSelection = SelectedMod?.SourcePath;

        Dictionary<string, ModEntry> existingEntriesSnapshot =
            new(_modEntriesBySourcePath, StringComparer.OrdinalIgnoreCase);

        var reloadResults = await Task
            .Run(() => LoadChangedModEntries(candidates, existingEntriesSnapshot))
            .ConfigureAwait(true);

        var refreshedEntries = new List<ModEntry>(reloadResults.Count);
        var updatedEntriesForStatus = new List<ModEntry>(reloadResults.Count);
        HashSet<string>? removedModIds = null;

        foreach (var pair in reloadResults)
        {
            var path = pair.Key;
            var entry = pair.Value;

            if (entry == null)
            {
                _modEntriesBySourcePath.Remove(path);
                if (existingEntriesSnapshot.TryGetValue(path, out var previous))
                {
                    removedModIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(previous.ModId)) removedModIds.Add(previous.ModId);
                }

                continue;
            }

            _modEntriesBySourcePath[path] = entry;
            refreshedEntries.Add(entry);
            updatedEntriesForStatus.Add(entry);
        }

        IReadOnlyCollection<ModEntry> impactedEntries = Array.Empty<ModEntry>();

        if (_modEntriesBySourcePath.Count > 0)
        {
            var updatedEntriesSnapshot = new List<ModEntry>(_modEntriesBySourcePath.Values);
            impactedEntries = await Task
                .Run(() => _discoveryService.ApplyLoadStatusesIncremental(
                    updatedEntriesSnapshot,
                    updatedEntriesForStatus,
                    removedModIds))
                .ConfigureAwait(true);
        }

        ApplyPartialUpdates(reloadResults, previousSelection, impactedEntries);

        if (_allowModDetailsRefresh && refreshedEntries.Count > 0) QueueDatabaseInfoRefresh(refreshedEntries);

        TotalMods = _mods.Count;
        UpdateActiveCount();
        SelectedSortOption?.Apply(ModsView);
        await UpdateModsStateSnapshotAsync().ConfigureAwait(true);
    }

    private async Task LoadModsAsync()
    {
        if (IsLoadingMods) return;

        var forcedRefresh = _isModDetailsRefreshForced;
        _isModDetailsRefreshForced = false;
        var previousAllowDetails = _allowModDetailsRefresh;
        _allowModDetailsRefresh = !_isAutoRefreshDisabled || forcedRefresh;

        IsLoadingMods = true;
        LoadingProgress = 0;
        LoadingStatusText = string.Empty;
        using var busyScope = BeginBusyScope();
        SetStatus("Loading mods...", false);

        // Yield once so the UI thread has a chance to process the busy-state
        // notification before we start potentially expensive work below. This
        // keeps the refresh progress ring responsive instead of appearing to
        // freeze when the refresh begins.
        await Task.Yield();

        try
        {
            _modsWatcher.EnsureWatchers();
            var changeSet = _modsWatcher.ConsumeChanges();
            var requiresFullReload = _mods.Count == 0
                                     || changeSet.RequiresFullRescan
                                     || changeSet.Paths.Count == 0;

            var previousSelection = SelectedMod?.SourcePath;

            if (requiresFullReload)
            {
                await PerformFullReloadAsync(previousSelection).ConfigureAwait(true);
            }
            else
            {
                Dictionary<string, ModEntry> existingEntriesSnapshot =
                    new(_modEntriesBySourcePath, StringComparer.OrdinalIgnoreCase);
                var reloadResults =
                    await Task.Run(() => LoadChangedModEntries(changeSet.Paths, existingEntriesSnapshot));

                var updatedEntriesForStatus = new List<ModEntry>(reloadResults.Count);
                HashSet<string>? removedModIds = null;

                foreach (var pair in reloadResults)
                {
                    var path = pair.Key;
                    var entry = pair.Value;

                    if (entry == null)
                    {
                        _modEntriesBySourcePath.Remove(path);
                        if (existingEntriesSnapshot.TryGetValue(path, out var previous))
                        {
                            removedModIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            if (!string.IsNullOrWhiteSpace(previous.ModId)) removedModIds.Add(previous.ModId);
                        }
                    }
                    else
                    {
                        _modEntriesBySourcePath[path] = entry;
                        updatedEntriesForStatus.Add(entry);
                    }
                }

                var allEntries = new List<ModEntry>(_modEntriesBySourcePath.Values);
                var impacted = await Task
                    .Run(() => _discoveryService.ApplyLoadStatusesIncremental(allEntries, updatedEntriesForStatus,
                        removedModIds))
                    .ConfigureAwait(true);

                ApplyPartialUpdates(reloadResults, previousSelection, impacted);

                if (_allowModDetailsRefresh && updatedEntriesForStatus.Count > 0)
                {
                    // Defer database refresh to background during incremental updates
                    var entriesToRefresh = updatedEntriesForStatus.ToList();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(IncrementalRefreshDelayMs).ConfigureAwait(false);
                            QueueDatabaseInfoRefresh(entriesToRefresh);
                        }
                        catch (Exception ex)
                        {
                            // Log but don't crash - database refresh is not critical for app function
                            System.Diagnostics.Debug.WriteLine($"[MainViewModel] Deferred incremental refresh failed: {ex.Message}");
                        }
                    });
                }
            }

            TotalMods = _mods.Count;
            UpdateActiveCount();
            SelectedSortOption?.Apply(ModsView);
            await UpdateModsStateSnapshotAsync();

            // Defer clearing IsLoadingMods until after any queued CollectionChanged events are processed.
            // This ensures the guard in ModsView_OnCollectionChanged works correctly during the critical
            // window when CollectionChanged events may be queued but not yet processed.
            await Application.Current.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to load mods: {ex.Message}", true);
        }
        finally
        {
            IsLoadingMods = false;
            _allowModDetailsRefresh = previousAllowDetails;
        }
    }

    private async Task PerformFullReloadAsync(string? previousSelection)
    {
        // Copy previousEntries directly - no need for dispatcher since we're reading a dictionary
        // This optimization removes an unnecessary UI thread round-trip
        Dictionary<string, ModEntry> previousEntries = new(_modEntriesBySourcePath, StringComparer.OrdinalIgnoreCase);

        IProgress<LoadingProgressUpdate> progressReporter = new Progress<LoadingProgressUpdate>(update =>
        {
            LoadingProgress = update.Progress;
            LoadingStatusText = update.Status;
        });

        progressReporter.Report(new LoadingProgressUpdate(0, "Discovering mods..."));

        var batchSize = InstalledModsIncrementalBatchSize;

        var loadResult = await Task
            .Run<(List<ModEntry> entries, List<(string sourcePath, ModEntry entry, ModListItemViewModel viewModel)> viewModels)>(
                async () =>
            {
                var allEntries = new List<ModEntry>();
                var processedCount = 0;
                var batchesSinceYield = 0;
                const int yieldEveryNBatches = 5; // Yield every 5 batches instead of every batch to reduce context switching

                // Use incremental loading on a background thread to keep UI responsive
                await foreach (var batch in _discoveryService.LoadModsIncrementallyAsync(batchSize, CancellationToken.None))
                {
                    if (batch.Count == 0) continue;

                    foreach (var entry in batch)
                    {
                        ResetCalculatedModState(entry);
                        if (previousEntries.TryGetValue(entry.SourcePath, out var previous))
                            CopyTransientModState(previous, entry);
                        allEntries.Add(entry);
                    }

                    processedCount += batch.Count;
                    batchesSinceYield++;

                    // Update progress - we don't know total yet, so show a generic loading message
                    var discoveryProgress = Math.Min(45, 5 + processedCount * 0.1);
                    progressReporter.Report(
                        new LoadingProgressUpdate(discoveryProgress, $"Loading mods... ({processedCount} found)"));

                    // Yield less frequently to reduce context switch overhead
                    // Only yield every N batches to keep UI responsive without excessive overhead
                    if (batchesSinceYield >= yieldEveryNBatches)
                    {
                        batchesSinceYield = 0;
                        await Task.Yield();
                    }
                }

                if (allEntries.Count > 0)
                {
                    progressReporter.Report(new LoadingProgressUpdate(60, $"Processing {allEntries.Count} mods..."));
                    _discoveryService.ApplyLoadStatuses(allEntries);
                }

                progressReporter.Report(new LoadingProgressUpdate(80, $"Preparing {allEntries.Count} mods..."));

                // Pre-create view models off the UI thread to reduce dispatcher work
                // Note: CreateModViewModel is safe to call off UI thread because:
                // - _settingsStore.IsDisabled() is thread-safe (uses lock)
                // - GetDisplayPath() uses cached base paths (thread-safe read)
                // - ModListItemViewModel constructor doesn't require UI thread
                var pendingViewModels =
                    new List<(string sourcePath, ModEntry entry, ModListItemViewModel viewModel)>(allEntries.Count);
                foreach (var entry in allEntries)
                {
                    var viewModel = CreateModViewModel(entry);
                    pendingViewModels.Add((entry.SourcePath, entry, viewModel));
                }

                return (allEntries, pendingViewModels);
            })
            .ConfigureAwait(true);

        var entries = loadResult.entries;
        var viewModelEntries = loadResult.viewModels;

        progressReporter.Report(new LoadingProgressUpdate(90, $"Updating UI with {entries.Count} mods..."));

        await InvokeOnDispatcherAsync(() =>
        {
            _modEntriesBySourcePath.Clear();
            _modViewModelsBySourcePath.Clear();

            // Use batch operation to minimize UI notifications
            using (_mods.SuspendNotifications())
            {
                _mods.Clear();

                // Use pre-created view models to reduce work on UI thread
                foreach (var (sourcePath, entry, viewModel) in viewModelEntries)
                {
                    _modEntriesBySourcePath[sourcePath] = entry;
                    _modViewModelsBySourcePath[sourcePath] = viewModel;
                    _mods.Add(viewModel);
                }
            }

            TotalMods = _mods.Count;

            if (!string.IsNullOrWhiteSpace(previousSelection)
                && _modViewModelsBySourcePath.TryGetValue(previousSelection, out var selected))
                SelectedMod = selected;
            else
                SelectedMod = null;

            UpdateLoadedModsStatus();

            // Defer database refresh to background to show UI faster
            if (_allowModDetailsRefresh && entries.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Small delay to ensure UI is responsive first
                        await Task.Delay(InitialRefreshDelayMs).ConfigureAwait(false);
                        QueueDatabaseInfoRefresh(entries);

                        // Mark initial load as complete after first database refresh
                        _isInitialLoad = false;
                    }
                    catch (Exception ex)
                    {
                        // Log but don't crash - database refresh is not critical for app function
                        System.Diagnostics.Debug.WriteLine($"[MainViewModel] Deferred database refresh failed: {ex.Message}");
                        _isInitialLoad = false;
                    }
                });
            }
            else
            {
                // If no database refresh needed, mark as complete immediately
                _isInitialLoad = false;
            }
        }, CancellationToken.None).ConfigureAwait(true);

        // Complete
        progressReporter.Report(new LoadingProgressUpdate(100, $"Loaded {entries.Count} mods"));
    }

    private readonly record struct LoadingProgressUpdate(double Progress, string Status);

    public async Task<bool> CheckForModStateChangesAsync()
    {
        _clientSettingsWatcher.EnsureWatcher();
        var clientSettingsTriggeredRefresh = false;
        if (_clientSettingsWatcher.TryConsumePendingChanges())
        {
            var result = await ApplyClientSettingsChangesAsync().ConfigureAwait(true);
            if (!result.success)
                _clientSettingsWatcher.SignalPendingChange();
            else if (result.modStatesChanged) clientSettingsTriggeredRefresh = true;
        }

        _modsWatcher.EnsureWatchers();

        if (clientSettingsTriggeredRefresh) return true;

        if (_modsWatcher.HasPendingChanges) return true;

        if (_modsWatcher.IsWatching) return false;

        var fingerprint = await CaptureModsStateFingerprintAsync().ConfigureAwait(false);
        if (fingerprint is null) return false;

        lock (_modsStateLock)
        {
            if (_modsStateFingerprint is null)
            {
                _modsStateFingerprint = fingerprint;
                return false;
            }

            if (!string.Equals(_modsStateFingerprint, fingerprint, StringComparison.Ordinal))
            {
                _modsStateFingerprint = fingerprint;
                return true;
            }
        }

        return false;
    }

    private async Task<(bool success, bool modStatesChanged)> ApplyClientSettingsChangesAsync()
    {
        string? localError = null;
        var reloadSuccess = await Task.Run(() => _settingsStore.TryReload(out localError)).ConfigureAwait(true);

        if (!reloadSuccess)
        {
            if (!string.IsNullOrWhiteSpace(localError))
                await InvokeOnDispatcherAsync(
                    () => SetStatus($"Failed to reload client settings: {localError}", true),
                    CancellationToken.None).ConfigureAwait(true);

            return (false, false);
        }

        // Invalidate cached base paths after settings reload since search paths may have changed
        _cachedBasePaths = null;

        var modStatesChanged = false;
        await InvokeOnDispatcherAsync(() =>
        {
            foreach (var mod in _mods)
            {
                var shouldBeActive = !_settingsStore.IsDisabled(mod.ModId, mod.Version);
                if (mod.IsActive != shouldBeActive)
                {
                    mod.SetIsActiveSilently(shouldBeActive);
                    modStatesChanged = true;
                }
            }

            if (modStatesChanged)
            {
                UpdateActiveCount();
                ReapplyActiveSortIfNeeded();
            }
        }, CancellationToken.None).ConfigureAwait(true);

        return (true, modStatesChanged);
    }

    private ModListItemViewModel CreateModViewModel(ModEntry entry)
    {
        var isActive = !_settingsStore.IsDisabled(entry.ModId, entry.Version);
        var location = GetDisplayPath(entry.SourcePath);
        return new ModListItemViewModel(
            entry,
            isActive,
            location,
            ApplyActivationChangeAsync,
            InstalledGameVersion,
            true,
            _configuration.ShouldSkipModVersion,
            () => _configuration.RequireExactVsVersionMatch,
            _allowModDetailsRefresh,
            _timingService);
    }

    private async Task UpdateModsStateSnapshotAsync()
    {
        if (_modsWatcher.IsWatching)
        {
            lock (_modsStateLock)
            {
                _modsStateFingerprint = null;
            }

            return;
        }

        var fingerprint = await CaptureModsStateFingerprintAsync().ConfigureAwait(false);
        if (fingerprint is null) return;

        lock (_modsStateLock)
        {
            _modsStateFingerprint = fingerprint;
        }
    }

    private Task<string?> CaptureModsStateFingerprintAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                return _discoveryService.GetModsStateFingerprint();
            }
            catch (Exception)
            {
                return null;
            }
        });
    }

    private void UpdateActiveCount()
    {
        ActiveMods = _mods.Count(item => item.IsActive);
        UpdateUpdatableCount();
    }

    private void UpdateUpdatableCount()
    {
        UpdatableModsCount = _mods.Count(item => item.CanUpdate);
    }

    private void ClearSearchResults()
    {
        if (_searchResults.Count == 0)
        {
            SelectedMod = null;
            return;
        }

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() =>
            {
                _searchResults.Clear();
                SelectedMod = null;
            });
            return;
        }

        _searchResults.Clear();
        SelectedMod = null;
    }

    private void OnModsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (var mod in EnumerateModItems(e.NewItems)) AttachInstalledMod(mod);
                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (var mod in EnumerateModItems(e.OldItems)) DetachInstalledMod(mod);
                break;
            case NotifyCollectionChangedAction.Replace:
                foreach (var mod in EnumerateModItems(e.OldItems)) DetachInstalledMod(mod);

                foreach (var mod in EnumerateModItems(e.NewItems)) AttachInstalledMod(mod);
                break;
            case NotifyCollectionChangedAction.Reset:
                DetachAllInstalledMods();
                foreach (var mod in _mods) AttachInstalledMod(mod);
                break;
        }

        ScheduleInstalledTagFilterRefresh();
        UpdateActiveCount();
    }

    private void OnSearchResultsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (var mod in EnumerateModItems(e.NewItems)) AttachSearchResult(mod);
                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (var mod in EnumerateModItems(e.OldItems)) DetachSearchResult(mod);
                break;
            case NotifyCollectionChangedAction.Replace:
                foreach (var mod in EnumerateModItems(e.OldItems)) DetachSearchResult(mod);

                foreach (var mod in EnumerateModItems(e.NewItems)) AttachSearchResult(mod);
                break;
            case NotifyCollectionChangedAction.Reset:
                DetachAllSearchResults();
                foreach (var mod in _searchResults) AttachSearchResult(mod);
                break;
        }

    }

    private static IEnumerable<ModListItemViewModel> EnumerateModItems(IList? items)
    {
        if (items is null) yield break;

        foreach (var item in items)
            if (item is ModListItemViewModel mod)
                yield return mod;
    }

    // EnumerateModTags logic is now handled by TagFilterService.UpdateInstalledAvailableTagsFromMods

    private void AttachInstalledMod(ModListItemViewModel mod)
    {
        if (_installedModSubscriptions.Add(mod)) mod.PropertyChanged += OnInstalledModPropertyChanged;

        if (_allowModDetailsRefresh) QueueUserReportRefresh(mod);
    }

    private void DetachInstalledMod(ModListItemViewModel mod)
    {
        if (_installedModSubscriptions.Remove(mod)) mod.PropertyChanged -= OnInstalledModPropertyChanged;
    }

    private void DetachAllInstalledMods()
    {
        if (_installedModSubscriptions.Count == 0) return;

        foreach (var mod in _installedModSubscriptions) mod.PropertyChanged -= OnInstalledModPropertyChanged;

        _installedModSubscriptions.Clear();
    }

    private void AttachSearchResult(ModListItemViewModel mod)
    {
        if (_searchResultSubscriptions.Add(mod)) mod.PropertyChanged += OnSearchResultPropertyChanged;

        if (mod.CanSubmitUserReport) QueueUserReportRefresh(mod);

        QueueLatestReleaseUserReportRefresh(mod);
    }

    private void DetachSearchResult(ModListItemViewModel mod)
    {
        if (_searchResultSubscriptions.Remove(mod)) mod.PropertyChanged -= OnSearchResultPropertyChanged;
    }

    private void DetachAllSearchResults()
    {
        if (_searchResultSubscriptions.Count == 0) return;

        foreach (var mod in _searchResultSubscriptions) mod.PropertyChanged -= OnSearchResultPropertyChanged;

        _searchResultSubscriptions.Clear();
    }

    private void OnInstalledModPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(ModListItemViewModel.DatabaseTags), StringComparison.Ordinal))
        {
            ScheduleInstalledTagFilterRefresh();
            return;
        }

        if (string.Equals(e.PropertyName, nameof(ModListItemViewModel.IsActive), StringComparison.Ordinal))
        {
            UpdateActiveCount();
            return;
        }

        if (string.Equals(e.PropertyName, nameof(ModListItemViewModel.CanUpdate), StringComparison.Ordinal))
            UpdateUpdatableCount();
    }

    private void OnSearchResultPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ModListItemViewModel mod) return;

        if (string.Equals(e.PropertyName, nameof(ModListItemViewModel.DatabaseTags), StringComparison.Ordinal))
        {

            return;
        }

        if (string.Equals(e.PropertyName, nameof(ModListItemViewModel.UserReportModVersion), StringComparison.Ordinal))
            if (mod.CanSubmitUserReport)
                QueueUserReportRefresh(mod);
    }

    private async Task<ModVersionVoteSummary?> RunUserReportOperationAsync(
        Func<CancellationToken, Task<ModVersionVoteSummary?>> operation,
        CancellationToken cancellationToken)
    {
        using var userReportScope = BeginUserReportOperation();
        var entered = false;

        try
        {
            await _userReportRefreshLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;

            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (entered) _userReportRefreshLimiter.Release();
        }
    }

    private IDisposable BeginUserReportOperation()
    {
        bool shouldSetLoadingStatus;

        lock (_userReportOperationLock)
        {
            _activeUserReportOperations++;
            shouldSetLoadingStatus = _activeUserReportOperations == 1;
        }

        var busyScope = BeginBusyScope();
        return new UserReportOperationScope(this, busyScope);
    }

    private void EndUserReportOperation()
    {
        bool shouldSetLoadedStatus;

        lock (_userReportOperationLock)
        {
            if (_activeUserReportOperations > 0) _activeUserReportOperations--;

            shouldSetLoadedStatus = _activeUserReportOperations == 0;
        }
    }

    private void QueueLatestReleaseUserReportRefresh(ModListItemViewModel mod)
    {
        if (mod is null) return;

        if (!_areUserReportsVisible) return;

        _ = RunUserReportOperationAsync(
            ct => RefreshLatestReleaseUserReportCoreAsync(mod, true, ct),
            CancellationToken.None);
    }

    public Task<ModVersionVoteSummary?> RefreshLatestReleaseUserReportAsync(
        ModListItemViewModel mod,
        CancellationToken cancellationToken = default)
    {
        return RunUserReportOperationAsync(
            ct => RefreshLatestReleaseUserReportCoreAsync(mod, false, ct),
            cancellationToken);
    }

    private async Task<ModVersionVoteSummary?> RefreshLatestReleaseUserReportCoreAsync(
        ModListItemViewModel mod,
        bool suppressErrors,
        CancellationToken cancellationToken)
    {
        if (mod is null) return null;

        var latestReleaseVersion = mod.LatestRelease?.Version;
        if (string.IsNullOrWhiteSpace(latestReleaseVersion))
        {
            await InvokeOnDispatcherAsync(mod.ClearLatestReleaseUserReport, cancellationToken,
                    DispatcherPriority.Background)
                .ConfigureAwait(false);
            StoreLatestReleaseUserReportEtag(mod.ModId, null, null);
            return null;
        }

        if (string.Equals(mod.LatestReleaseUserReportVersion, latestReleaseVersion, StringComparison.OrdinalIgnoreCase)
            && mod.LatestReleaseUserReportSummary is not null)
            return mod.LatestReleaseUserReportSummary;

        if (string.IsNullOrWhiteSpace(InstalledGameVersion))
        {
            await InvokeOnDispatcherAsync(mod.ClearLatestReleaseUserReport, cancellationToken,
                    DispatcherPriority.Background)
                .ConfigureAwait(false);
            StoreLatestReleaseUserReportEtag(mod.ModId, latestReleaseVersion, null);
            return null;
        }

        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            await InvokeOnDispatcherAsync(mod.ClearLatestReleaseUserReport, cancellationToken,
                    DispatcherPriority.Background)
                .ConfigureAwait(false);
            StoreLatestReleaseUserReportEtag(mod.ModId, latestReleaseVersion, null);
            return null;
        }

        try
        {
            var etag = GetVoteEtag(_latestReleaseUserReportEtags, "latest", mod.ModId, latestReleaseVersion);

            var result = await _voteService
                .GetVoteSummaryIfChangedAsync(
                    mod.ModId,
                    latestReleaseVersion,
                    InstalledGameVersion!,
                    etag,
                    cancellationToken)
                .ConfigureAwait(false);

            var summary = result.Summary ?? mod.LatestReleaseUserReportSummary;

            if (!result.IsNotModified || mod.LatestReleaseUserReportSummary is null)
                if (summary is not null)
                    await InvokeOnDispatcherAsync(
                            () => mod.ApplyLatestReleaseUserReportSummary(summary),
                            cancellationToken,
                            DispatcherPriority.Background)
                        .ConfigureAwait(false);

            StoreLatestReleaseUserReportEtag(mod.ModId, latestReleaseVersion, result.ETag);

            return summary;
        }
        catch (InternetAccessDisabledException)
        {
            await InvokeOnDispatcherAsync(mod.ClearLatestReleaseUserReport, cancellationToken,
                    DispatcherPriority.Background)
                .ConfigureAwait(false);
            StoreLatestReleaseUserReportEtag(mod.ModId, latestReleaseVersion, null);
            return null;
        }
        catch (Exception ex)
        {
            if (!suppressErrors)
                StatusLogService.AppendStatus(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "Failed to refresh user reports for the latest release of {0}: {1}",
                        mod.DisplayName,
                        ex.Message),
                    true);

            await InvokeOnDispatcherAsync(mod.ClearLatestReleaseUserReport, cancellationToken,
                    DispatcherPriority.Background)
                .ConfigureAwait(false);
            StoreLatestReleaseUserReportEtag(mod.ModId, latestReleaseVersion, null);
            return null;
        }
    }

    private void QueueUserReportRefresh(ModListItemViewModel mod)
    {
        if (mod is null) return;

        if (!_areUserReportsVisible) return;

        _ = RunUserReportOperationAsync(
            ct => RefreshUserReportCoreAsync(mod, true, ct),
            CancellationToken.None);
    }

    public void EnableUserReportFetching(bool includeInstalledWhenAutoRefreshDisabled = false)
    {
        if (!_areUserReportsVisible) return;

        if (_hasFetchedUserReportsThisSession) return;

        _hasEnabledUserReportFetching = true;

        var allowInstalledRefresh = _allowModDetailsRefresh || includeInstalledWhenAutoRefreshDisabled;
        var hasQueuedRefresh = false;

        if (allowInstalledRefresh)
        {
            foreach (var mod in _installedModSubscriptions)
            {
                mod.EnsureUserReportStateInitialized();
                QueueUserReportRefresh(mod);
                hasQueuedRefresh = true;
            }
        }

        foreach (var mod in _searchResultSubscriptions)
        {
            mod.EnsureUserReportStateInitialized();
            if (mod.CanSubmitUserReport)
            {
                QueueUserReportRefresh(mod);
                hasQueuedRefresh = true;
            }

            QueueLatestReleaseUserReportRefresh(mod);
            hasQueuedRefresh = true;
        }

        if (hasQueuedRefresh) _hasFetchedUserReportsThisSession = true;
    }

    public Task<ModVersionVoteSummary?> RefreshUserReportAsync(
        ModListItemViewModel mod,
        CancellationToken cancellationToken = default)
    {
        return RunUserReportOperationAsync(
            ct => RefreshUserReportCoreAsync(mod, false, ct),
            cancellationToken);
    }

    public async Task<ModVersionVoteSummary?> SubmitUserReportVoteAsync(
        ModListItemViewModel mod,
        ModVersionVoteOption? option,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        if (mod is null) return null;

        if (string.IsNullOrWhiteSpace(InstalledGameVersion) || string.IsNullOrWhiteSpace(mod.UserReportModVersion))
        {
            await InvokeOnDispatcherAsync(
                    () => mod.SetUserReportUnavailable("User reports require a known Vintage Story and mod version."),
                    cancellationToken,
                    DispatcherPriority.Background)
                .ConfigureAwait(false);
            return null;
        }

        if (InternetAccessManager.IsInternetAccessDisabled)
            throw new InternetAccessDisabledException(
                "Internet access is disabled. Enable it in the File menu to submit your vote.");

        await InvokeOnDispatcherAsync(mod.SetUserReportLoading, cancellationToken, DispatcherPriority.Background)
            .ConfigureAwait(false);

        try
        {
            var (Summary, etag) = option.HasValue
                ? await _voteService
                    .SubmitVoteAsync(
                        mod.ModId,
                        mod.UserReportModVersion!,
                        InstalledGameVersion!,
                        option.Value,
                        comment,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await _voteService
                    .RemoveVoteAsync(mod.ModId, mod.UserReportModVersion!, InstalledGameVersion!, cancellationToken)
                    .ConfigureAwait(false);

            await InvokeOnDispatcherAsync(
                    () => mod.ApplyUserReportSummary(Summary),
                    cancellationToken,
                    DispatcherPriority.Background)
                .ConfigureAwait(false);

            StoreUserReportEtag(mod.ModId, mod.UserReportModVersion, etag);

            if (Summary is not null)
                RaiseUserReportVoteSubmitted(mod, Summary);

            return Summary;
        }
        catch (InternetAccessDisabledException)
        {
            await InvokeOnDispatcherAsync(mod.SetUserReportOffline, cancellationToken, DispatcherPriority.Background)
                .ConfigureAwait(false);
            StoreUserReportEtag(mod.ModId, mod.UserReportModVersion, null);
            throw;
        }
        catch (Exception ex)
        {
            StatusLogService.AppendStatus(
                string.Format(CultureInfo.CurrentCulture, "Failed to submit user report for {0}: {1}", mod.DisplayName,
                    ex.Message),
                true);
            throw;
        }
    }

    private void RaiseUserReportVoteSubmitted(ModListItemViewModel mod, ModVersionVoteSummary summary)
    {
        int? numericId = null;
        if (int.TryParse(mod.ModId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
            numericId = parsedId;

        UserReportVoteSubmitted?.Invoke(
            this,
            new ModUserReportChangedEventArgs(mod.ModId, mod.UserReportModVersion, numericId, summary));
    }

    private async Task<ModVersionVoteSummary?> RefreshUserReportCoreAsync(
        ModListItemViewModel mod,
        bool suppressErrors,
        CancellationToken cancellationToken)
    {
        if (mod is null) return null;

        if (string.IsNullOrWhiteSpace(InstalledGameVersion) || string.IsNullOrWhiteSpace(mod.UserReportModVersion))
        {
            await InvokeOnDispatcherAsync(
                    () => mod.SetUserReportUnavailable("User reports require a known Vintage Story and mod version."),
                    cancellationToken,
                    DispatcherPriority.Background)
                .ConfigureAwait(false);
            StoreUserReportEtag(mod.ModId, mod.UserReportModVersion, null);
            return null;
        }

        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            await InvokeOnDispatcherAsync(mod.SetUserReportOffline, cancellationToken, DispatcherPriority.Background)
                .ConfigureAwait(false);
            StoreUserReportEtag(mod.ModId, mod.UserReportModVersion, null);
            return null;
        }

        await InvokeOnDispatcherAsync(mod.SetUserReportLoading, cancellationToken, DispatcherPriority.Background)
            .ConfigureAwait(false);

        try
        {
            var etag = GetVoteEtag(_userReportEtags, "current", mod.ModId, mod.UserReportModVersion);

            var result = await _voteService
                .GetVoteSummaryIfChangedAsync(
                    mod.ModId,
                    mod.UserReportModVersion!,
                    InstalledGameVersion!,
                    etag,
                    cancellationToken)
                .ConfigureAwait(false);

            var summary = result.Summary ?? mod.UserReportSummary;

            if (!result.IsNotModified || mod.UserReportSummary is null)
                if (summary is not null)
                    await InvokeOnDispatcherAsync(
                            () => mod.ApplyUserReportSummary(summary),
                            cancellationToken,
                            DispatcherPriority.Background)
                        .ConfigureAwait(false);

            StoreUserReportEtag(mod.ModId, mod.UserReportModVersion, result.ETag);

            return summary;
        }
        catch (InternetAccessDisabledException)
        {
            await InvokeOnDispatcherAsync(mod.SetUserReportOffline, cancellationToken, DispatcherPriority.Background)
                .ConfigureAwait(false);
            StoreUserReportEtag(mod.ModId, mod.UserReportModVersion, null);
            return null;
        }
        catch (Exception ex)
        {
            if (!suppressErrors)
                StatusLogService.AppendStatus(
                    string.Format(CultureInfo.CurrentCulture, "Failed to refresh user reports for {0}: {1}",
                        mod.DisplayName, ex.Message),
                    true);

            await InvokeOnDispatcherAsync(
                    () => mod.SetUserReportError(ex.Message),
                    cancellationToken,
                    DispatcherPriority.Background)
                .ConfigureAwait(false);

            if (suppressErrors) return null;

            throw;
        }
    }

    private void SetTagsColumnVisibility(bool isVisible)
    {
        if (_isTagsColumnVisible == isVisible) return;

        _isTagsColumnVisible = isVisible;

        if (!isVisible)
        {
            foreach (var mod in _mods) mod.ClearDatabaseTags();

            foreach (var mod in _searchResults) mod.ClearDatabaseTags();

            return;
        }

        ScheduleInstalledTagFilterRefresh();
        if (_allowModDetailsRefresh && _modEntriesBySourcePath.Count > 0)
            QueueDatabaseInfoRefresh(_modEntriesBySourcePath.Values.ToArray());
    }

    private void SetUserReportsColumnVisibility(bool isVisible)
    {
        if (_areUserReportsVisible == isVisible) return;

        _areUserReportsVisible = isVisible;

        if (!isVisible)
        {
            _hasEnabledUserReportFetching = false;
            _hasFetchedUserReportsThisSession = false;
            return;
        }

        if (_allowModDetailsRefresh) EnableUserReportFetching();
    }

    private void ScheduleInstalledTagFilterRefresh()
    {
        if (!_isTagsColumnVisible) return;

        if (_isInstalledTagRefreshPending) return;

        _isInstalledTagRefreshPending = true;

        async void ExecuteAsync()
        {
            try
            {
                // Update tag filter service on background thread
                await Task.Run(() =>
                {
                    _tagFilterService.UpdateInstalledAvailableTagsFromMods(_mods);
                }).ConfigureAwait(false);

                await InvokeOnDispatcherAsync(
                        () => ApplyInstalledTagFilters(_tagFilterService.GetInstalledAvailableTags()),
                        CancellationToken.None,
                        DispatcherPriority.ContextIdle)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Swallow unexpected exceptions for resilience.
            }
            finally
            {
                _isInstalledTagRefreshPending = false;
            }
        }

        if (Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(ExecuteAsync));
        else
            ExecuteAsync();
    }

    private void ResetInstalledTagFilters(IEnumerable<string> tags)
    {
        if (!_isTagsColumnVisible) return;

        _tagFilterService.SetInstalledAvailableTags(tags.Concat(_tagFilterService.GetSelectedInstalledTags()));
        ApplyInstalledTagFilters(_tagFilterService.GetInstalledAvailableTags());
    }

    private void ApplyInstalledTagFilters(IReadOnlyList<string> normalized)
    {
        if (!_isTagsColumnVisible) return;

        var normalizedTags = normalized
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (_lastInstalledAvailableTags.SequenceEqual(normalizedTags, StringComparer.OrdinalIgnoreCase)) return;

        _lastInstalledAvailableTags = normalizedTags;

        _suppressInstalledTagFilterSelectionChanges = true;
        try
        {
            foreach (var filter in _installedTagFilters) filter.PropertyChanged -= OnInstalledTagFilterPropertyChanged;

            _installedTagFilters.Clear();

            foreach (var tag in normalizedTags)
            {
                var isSelected = _tagFilterService.IsInstalledTagSelected(tag);
                var option = new TagFilterOptionViewModel(tag, isSelected);
                option.PropertyChanged += OnInstalledTagFilterPropertyChanged;
                _installedTagFilters.Add(option);
            }
        }
        finally
        {
            _suppressInstalledTagFilterSelectionChanges = false;
        }

        SyncSelectedTagsToService(_installedTagFilters, isInstalled: true);
        UpdateHasSelectedTags();
        ModsView.Refresh();
    }

    private void OnInstalledTagFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressInstalledTagFilterSelectionChanges) return;

        if (!string.Equals(e.PropertyName, nameof(TagFilterOptionViewModel.IsSelected),
                StringComparison.Ordinal)) return;

        if (SyncSelectedTagsToService(_installedTagFilters, isInstalled: true))
        {
            UpdateHasSelectedTags();
            ModsView.Refresh();
        }
    }

    private void OnModDatabaseTagFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {

        if (!string.Equals(e.PropertyName, nameof(TagFilterOptionViewModel.IsSelected),
                StringComparison.Ordinal)) return;

    }

    private bool SyncSelectedTagsToService(IEnumerable<TagFilterOptionViewModel> filters, bool isInstalled)
    {
        var newSelection = filters
            .Where(filter => filter.IsSelected)
            .Select(filter => filter.Name)
            .ToList();

        return _tagFilterService.SetSelectedInstalledTags(newSelection);
    }

    private void UpdateHasSelectedTags()
    {
        HasSelectedTags = _tagFilterService.HasSelectedTags;
    }

    private IDisposable BeginBusyScope()
    {
        bool isBusy;
        CancellationTokenSource? pendingRelease = null;
        lock (_busyStateLock)
        {
            _busyOperationCount++;
            isBusy = _busyOperationCount > 0;

            if (_busyReleaseCts is not null)
            {
                pendingRelease = _busyReleaseCts;
                _busyReleaseCts = null;
            }
        }

        pendingRelease?.Cancel();

        UpdateIsBusy(isBusy);
        return new BusyScope(this);
    }

    private void EndBusyScope()
    {
        bool isBusy;
        CancellationTokenSource? pendingRelease = null;
        CancellationTokenSource? releaseToSchedule = null;
        lock (_busyStateLock)
        {
            if (_busyOperationCount > 0) _busyOperationCount--;

            isBusy = _busyOperationCount > 0;
            if (!isBusy)
            {
                pendingRelease = _busyReleaseCts;
                releaseToSchedule = new CancellationTokenSource();
                _busyReleaseCts = releaseToSchedule;
            }
        }

        pendingRelease?.Cancel();

        if (isBusy)
            UpdateIsBusy(true);
        else
            ScheduleBusyRelease(releaseToSchedule);
    }

    private void ScheduleBusyRelease(CancellationTokenSource? releaseCts)
    {
        if (releaseCts is null)
        {
            UpdateIsBusy(false);
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(BusyStateReleaseDelay, releaseCts.Token).ConfigureAwait(false);

                lock (_busyStateLock)
                {
                    if (!ReferenceEquals(_busyReleaseCts, releaseCts)) return;

                    _busyReleaseCts = null;
                }

                UpdateIsBusy(false);
            }
            catch (TaskCanceledException)
            {
            }
            finally
            {
                releaseCts.Dispose();
            }
        });
    }

    private void UpdateIsBusy(bool isBusy)
    {
        if (Application.Current?.Dispatcher is Dispatcher dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                _hasActiveBusyScope = isBusy;
                RecalculateIsBusy();
            }
            else
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    _hasActiveBusyScope = isBusy;
                    RecalculateIsBusy();
                }));
            }
        }
        else
        {
            _hasActiveBusyScope = isBusy;
            RecalculateIsBusy();
        }
    }

    private void RecalculateIsBusy()
    {
        var isBusy = _hasActiveBusyScope || _isLoadingMods || _isLoadingModDetails;

        if (Application.Current?.Dispatcher is Dispatcher dispatcher)
        {
            if (dispatcher.CheckAccess())
                IsBusy = isBusy;
            else
                dispatcher.BeginInvoke(new Action(() => IsBusy = isBusy));
        }
        else
        {
            IsBusy = isBusy;
        }
    }

    private void UpdateIsLoadingModDetails(bool isLoading)
    {
        if (Application.Current?.Dispatcher is Dispatcher dispatcher)
        {
            if (dispatcher.CheckAccess())
                IsLoadingModDetails = isLoading;
            else
                dispatcher.BeginInvoke(new Action(() => IsLoadingModDetails = isLoading));
        }
        else
        {
            IsLoadingModDetails = isLoading;
        }
    }

    private void UpdateModDetailsProgressVisibility()
    {
        IsModDetailsProgressVisible = _isLoadingModDetails && !_isFastCheckInProgress;
    }


    private void UpdateLoadedModsStatus()
    {
        if (IsModDetailsRefreshPending())
        {
            if (!_isModDetailsStatusActive) SetStatus(BuildModDetailsLoadingStatusMessage(), false, true);
        }
        else
        {
            SetStatus(BuildModDetailsReadyStatusMessage(), false);
        }
    }

    private bool IsModDetailsRefreshPending()
    {
        return Interlocked.CompareExchange(ref _pendingModDetailsRefreshCount, 0, 0) > 0;
    }

    private void OnModDetailsRefreshEnqueued(int count, string? statusText = null)
    {
        if (count <= 0) return;

        var newCount = Interlocked.Add(ref _pendingModDetailsRefreshCount, count);
        if (newCount <= 0)
        {
            Interlocked.Exchange(ref _pendingModDetailsRefreshCount, 0);
            return;
        }

        if (newCount == count) ResetModDetailsProgress();

        EnsureModDetailsBusyScope();
        UpdateIsLoadingModDetails(true);

        AddModDetailsWork(count, statusText);

        if (newCount == count || !_isModDetailsStatusActive)
            SetStatus(BuildModDetailsLoadingStatusMessage(), false, true);
    }

    private void OnModDetailsRefreshCompleted(int completedCount = 1)
    {
        if (completedCount <= 0) return;

        var newCount = Interlocked.Add(ref _pendingModDetailsRefreshCount, -completedCount);
        if (newCount < 0)
        {
            Interlocked.Exchange(ref _pendingModDetailsRefreshCount, 0);
            newCount = 0;
        }

        Interlocked.Add(ref _modDetailsRefreshCompletedWork, completedCount);
        UpdateModDetailsProgress();

        if (newCount <= 0)
        {
            Interlocked.Exchange(ref _pendingModDetailsRefreshCount, 0);
            ReleaseModDetailsBusyScope();
            UpdateIsLoadingModDetails(false);

            if (_isModDetailsStatusActive) SetStatus(BuildModDetailsReadyStatusMessage(), false);

            ResetModDetailsProgress();
        }
    }

    private void EnsureModDetailsBusyScope()
    {
        lock (_modDetailsBusyScopeLock)
        {
            _modDetailsBusyScope ??= BeginBusyScope();
        }
    }

    private void ReleaseModDetailsBusyScope()
    {
        lock (_modDetailsBusyScopeLock)
        {
            _modDetailsBusyScope?.Dispose();
            _modDetailsBusyScope = null;
        }
    }

    private string BuildModDetailsLoadingStatusMessage()
    {
        return $"Loaded {TotalMods} mods. Loading mod details...";
    }

    private string BuildModDetailsReadyStatusMessage()
    {
        if (_hasShownModDetailsLoadingStatus) return $"Loaded {TotalMods} mods. Mod details up to date.";

        return $"Loaded {TotalMods} mods.";
    }

    private void AddModDetailsWork(int count, string? statusText)
    {
        if (count <= 0) return;

        Interlocked.Add(ref _modDetailsRefreshTotalWork, count);
        UpdateModDetailsProgress(statusText);
    }

    private void UpdateModDetailsProgress(string? statusText = null)
    {
        if (!string.IsNullOrWhiteSpace(statusText)) _modDetailsProgressStage = statusText;

        var total = Interlocked.CompareExchange(ref _modDetailsRefreshTotalWork, 0, 0);
        if (total <= 0)
        {
            ModDetailsProgress = 0;
            ModDetailsStatusText = string.Empty;
            return;
        }

        var completed = Interlocked.CompareExchange(ref _modDetailsRefreshCompletedWork, 0, 0);
        completed = Math.Clamp(completed, 0, total);

        ModDetailsProgress = (double)completed / total * 100;

        var baseText = string.IsNullOrWhiteSpace(_modDetailsProgressStage)
            ? BuildModDetailsLoadingStatusMessage()
            : _modDetailsProgressStage;

        ModDetailsStatusText = $"{baseText} ({completed}/{total})";
    }

    private void ResetModDetailsProgress()
    {
        Interlocked.Exchange(ref _modDetailsRefreshCompletedWork, 0);
        Interlocked.Exchange(ref _modDetailsRefreshTotalWork, 0);
        _modDetailsProgressStage = string.Empty;
        ModDetailsProgress = 0;
        ModDetailsStatusText = string.Empty;
    }

    private Task UpdateSearchResultsAsync(IReadOnlyList<ModListItemViewModel> items,
        CancellationToken cancellationToken)
    {
        return InvokeOnDispatcherAsync(() =>
        {
            _searchResults.Clear();
            foreach (var item in items) _searchResults.Add(item);

            SelectedMod = null;
        }, cancellationToken);
    }

    private async Task LoadModDatabaseLogosAsync(IReadOnlyList<ModListItemViewModel> viewModels,
        CancellationToken cancellationToken)
    {
        if (viewModels.Count == 0) return;

        var tasks = new List<Task>(viewModels.Count);
        foreach (var viewModel in viewModels)
        {
            if (cancellationToken.IsCancellationRequested) break;

            tasks.Add(viewModel.LoadModDatabaseLogoAsync(cancellationToken));
        }

        if (tasks.Count == 0) return;

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Swallow cancellation so callers can continue gracefully.
        }
    }

    private Task<HashSet<string>> GetInstalledModIdsAsync(CancellationToken cancellationToken)
    {
        return InvokeOnDispatcherAsync(
            () =>
            {
                var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var mod in _mods)
                    if (!string.IsNullOrWhiteSpace(mod.ModId))
                        installed.Add(mod.ModId);

                return installed;
            },
            cancellationToken);
    }

    private static bool IsResultInstalled(ModDatabaseSearchResult result, HashSet<string> installedModIds)
    {
        if (installedModIds.Contains(result.ModId)) return true;

        foreach (var alternate in result.AlternateIds)
            if (!string.IsNullOrWhiteSpace(alternate) && installedModIds.Contains(alternate))
                return true;

        return false;
    }

    private static Task InvokeOnDispatcherAsync(Action action, CancellationToken cancellationToken,
        DispatcherPriority priority = DispatcherPriority.Normal)
    {
        if (cancellationToken.IsCancellationRequested) return Task.CompletedTask;

        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(action, priority, cancellationToken).Task;
        }

        action();
        return Task.CompletedTask;
    }

    private static Task<T> InvokeOnDispatcherAsync<T>(Func<T> function, CancellationToken cancellationToken,
        DispatcherPriority priority = DispatcherPriority.Normal)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);

        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess()) return Task.FromResult(function());

            return dispatcher.InvokeAsync(function, priority, cancellationToken).Task;
        }

        return Task.FromResult(function());
    }

    private static ModEntry CreateSearchResultEntry(ModDatabaseSearchResult result)
    {
        var authors = string.IsNullOrWhiteSpace(result.Author)
            ? Array.Empty<string>()
            : new[] { result.Author };

        var description = BuildSearchResultDescription(result);
        var pageUrl = BuildModDatabasePageUrl(result);

        var databaseInfo = result.DetailedInfo ?? new ModDatabaseInfo
        {
            Tags = result.Tags,
            AssetId = result.AssetId,
            ModPageUrl = pageUrl,
            Downloads = result.Downloads,
            Comments = result.Comments,
            Follows = result.Follows,
            TrendingPoints = result.TrendingPoints,
            LogoUrl = result.LogoUrl,
            LogoUrlSource = result.LogoUrlSource,
            LastReleasedUtc = result.LastReleasedUtc,
            Side = result.Side
        };

        return new ModEntry
        {
            ModId = result.ModId,
            Name = result.Name,
            ManifestName = result.Name,
            Description = description,
            Authors = authors,
            Website = pageUrl,
            SourceKind = ModSourceKind.SourceCode,
            SourcePath = string.Empty,
            Side = result.Side,
            ModDatabaseSearchScore = result.Score,
            DatabaseInfo = databaseInfo
        };
    }

    private ModListItemViewModel CreateSearchResultViewModel(ModEntry entry, bool isInstalled)
    {
        return new ModListItemViewModel(entry, false, "Mod Database", RejectActivationChangeAsync, InstalledGameVersion,
            isInstalled, null, () => _configuration.RequireExactVsVersionMatch, _allowModDetailsRefresh);
    }

    private static string? BuildSearchResultDescription(ModDatabaseSearchResult result)
    {
        return string.IsNullOrWhiteSpace(result.Summary) ? null : result.Summary.Trim();
    }

    private static string? BuildModDatabasePageUrl(ModDatabaseSearchResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.AssetId))
            return $"https://mods.vintagestory.at/show/mod/{result.AssetId}";

        if (!string.IsNullOrWhiteSpace(result.UrlAlias))
        {
            var alias = result.UrlAlias!.TrimStart('/');
            return string.IsNullOrWhiteSpace(alias) ? null : $"https://mods.vintagestory.at/{alias}";
        }

        return null;
    }

    private static Task<ActivationResult> RejectActivationChangeAsync(ModListItemViewModel mod, bool isActive)
    {
        return Task.FromResult(new ActivationResult(false, "Install this mod locally to manage its activation state."));
    }

    private bool FilterMod(object? item)
    {
        if (item is not ModListItemViewModel mod) return false;

        if (!_tagFilterService.PassesInstalledTagFilter(mod.DatabaseTags))
            return false;

        if (_searchTokens.Length == 0) return true;

        return mod.MatchesSearchTokens(_searchTokens);
    }

    private static string[] CreateSearchTokens(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();

        return value
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private string GetDisplayPath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return string.Empty;

        string best;
        try
        {
            best = Path.GetFullPath(fullPath);
        }
        catch (Exception)
        {
            return fullPath;
        }

        // Use cached base paths for performance - recalculating these for every mod is expensive
        var basePaths = _cachedBasePaths ??= GetBasePathsList();

        foreach (var candidate in basePaths)
            try
            {
                var relative = Path.GetRelativePath(candidate, best);
                if (!relative.StartsWith("..", StringComparison.Ordinal) && relative.Length < best.Length)
                    best = relative;
            }
            catch (Exception)
            {
                // Ignore invalid paths.
            }

        return best.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private List<string> GetBasePathsList()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;

            try
            {
                var full = Path.GetFullPath(candidate);
                set.Add(full);
            }
            catch (Exception)
            {
                // Ignore invalid paths.
            }
        }

        TryAdd(_settingsStore.DataDirectory);
        foreach (var path in _settingsStore.SearchBaseCandidates) TryAdd(path);

        TryAdd(Directory.GetCurrentDirectory());

        return set.ToList();
    }

    private IEnumerable<string> EnumerateBasePaths()
    {
        return _cachedBasePaths ??= GetBasePathsList();
    }

    private static IEnumerable<SortOption> CreateSortOptions()
    {
        yield return new SortOption(
            "Name (A → Z)",
            (nameof(ModListItemViewModel.NameSortKey), ListSortDirection.Ascending));
        yield return new SortOption(
            "Name (Z → A)",
            (nameof(ModListItemViewModel.NameSortKey), ListSortDirection.Descending));
        yield return new SortOption(
            "Active (Active → Inactive)",
            (nameof(ModListItemViewModel.ActiveSortOrder), ListSortDirection.Ascending),
            (nameof(ModListItemViewModel.NameSortKey), ListSortDirection.Ascending));
        yield return new SortOption(
            "Active (Inactive → Active)",
            (nameof(ModListItemViewModel.ActiveSortOrder), ListSortDirection.Descending),
            (nameof(ModListItemViewModel.NameSortKey), ListSortDirection.Ascending));
    }

    private void ReapplyActiveSortIfNeeded()
    {
        if (SelectedSortOption?.SortDescriptions is not { Count: > 0 } sorts) return;

        var primary = sorts[0];
        if (!IsActiveSortProperty(primary.Property)) return;

        SelectedSortOption.Apply(ModsView);
        ModsView.Refresh();
    }

    private static bool IsActiveSortProperty(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName)) return false;

        return string.Equals(propertyName, nameof(ModListItemViewModel.IsActive), StringComparison.OrdinalIgnoreCase)
               || string.Equals(propertyName, nameof(ModListItemViewModel.ActiveSortOrder),
                   StringComparison.OrdinalIgnoreCase);
    }

    private void SetStatus(string message, bool isError, bool isModDetailsStatus = false)
    {
        StatusLogService.AppendStatus(message, isError);
        StatusMessage = message;
        IsErrorStatus = isError;
        _isModDetailsStatusActive = isModDetailsStatus;
        _hasShownModDetailsLoadingStatus = isModDetailsStatus;
    }

    private Dictionary<string, ModEntry?> LoadChangedModEntries(
        IReadOnlyCollection<string> paths,
        IReadOnlyDictionary<string, ModEntry>? existingEntries)
    {
        var results = new Dictionary<string, ModEntry?>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            var entry = _discoveryService.LoadModFromPath(path);
            if (entry != null)
            {
                ResetCalculatedModState(entry);
                if (existingEntries != null && existingEntries.TryGetValue(path, out var previous))
                    CopyTransientModState(previous, entry);
            }

            results[path] = entry;
        }

        return results;
    }

    private static void ResetCalculatedModState(ModEntry entry)
    {
        entry.LoadError = null;
        entry.DependencyHasErrors = false;
        entry.MissingDependencies = Array.Empty<ModDependencyInfo>();
    }

    private static void CopyTransientModState(ModEntry source, ModEntry target)
    {
        if (source is null || target is null) return;

        var sameModId = string.Equals(source.ModId, target.ModId, StringComparison.OrdinalIgnoreCase);
        var sameVersion = string.Equals(source.Version, target.Version, StringComparison.OrdinalIgnoreCase)
                          || (string.IsNullOrWhiteSpace(source.Version) && string.IsNullOrWhiteSpace(target.Version));

        if (!sameModId || !sameVersion) return;

        if (target.DatabaseInfo is null && source.DatabaseInfo != null) target.DatabaseInfo = source.DatabaseInfo;

        if (source.ModDatabaseSearchScore.HasValue) target.ModDatabaseSearchScore = source.ModDatabaseSearchScore;
    }

    private void ApplyPartialUpdates(
        IReadOnlyDictionary<string, ModEntry?> changes,
        string? previousSelection,
        IReadOnlyCollection<ModEntry>? statusChanges = null)
    {
        var hasStatusChanges = statusChanges is { Count: > 0 };

        if (changes.Count == 0 && !hasStatusChanges)
        {
            SetStatus("Mods are up to date.", false);
            if (!string.IsNullOrWhiteSpace(previousSelection)
                && _modViewModelsBySourcePath.TryGetValue(previousSelection, out var selected))
                SelectedMod = selected;

            return;
        }

        var added = 0;
        var updated = 0;
        var removed = 0;

        // Use batch operation to minimize UI notifications during updates
        using (_mods.SuspendNotifications())
        {
            foreach (var change in changes)
            {
                var path = change.Key;
                var entry = change.Value;

                if (entry == null)
                {
                    if (_modViewModelsBySourcePath.TryGetValue(path, out var existingVm))
                    {
                        _mods.Remove(existingVm);
                        _modViewModelsBySourcePath.Remove(path);
                        removed++;

                        if (ReferenceEquals(SelectedMod, existingVm)) SelectedMod = null;
                    }

                    _modEntriesBySourcePath.Remove(path);
                    continue;
                }

                var viewModel = CreateModViewModel(entry);
                if (_modViewModelsBySourcePath.TryGetValue(path, out var existing))
                {
                    var index = _mods.IndexOf(existing);
                    if (index >= 0)
                        _mods[index] = viewModel;
                    else
                        _mods.Add(viewModel);

                    _modViewModelsBySourcePath[path] = viewModel;
                    _modEntriesBySourcePath[path] = entry;  // Update entry dictionary for updated mods
                    updated++;

                    if (ReferenceEquals(SelectedMod, existing)) SelectedMod = viewModel;
                }
                else
                {
                    _mods.Add(viewModel);
                    _modViewModelsBySourcePath[path] = viewModel;
                    _modEntriesBySourcePath[path] = entry;
                    added++;
                }
            }
        }

        if (statusChanges is { Count: > 0 })
        {
            foreach (var entry in statusChanges)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.SourcePath)) continue;
                if (changes.ContainsKey(entry.SourcePath)) continue;

                if (_modViewModelsBySourcePath.TryGetValue(entry.SourcePath, out var viewModel))
                {
                    viewModel.UpdateLoadError(entry.LoadError);
                    viewModel.UpdateDependencyIssues(
                        entry.DependencyHasErrors,
                        entry.MissingDependencies ?? Array.Empty<ModDependencyInfo>());
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(previousSelection)
            && _modViewModelsBySourcePath.TryGetValue(previousSelection, out var selectedAfter))
            SelectedMod = selectedAfter;
        else if (SelectedMod != null && !_mods.Contains(SelectedMod)) SelectedMod = null;

        var affected = added + updated + removed;
        if (affected == 0)
        {
            SetStatus(hasStatusChanges ? "Updated mod statuses." : "Mods are up to date.", false);
            return;
        }

        var parts = new List<string>();
        if (added > 0) parts.Add($"{added} added");

        if (updated > 0) parts.Add($"{updated} updated");

        if (removed > 0) parts.Add($"{removed} removed");

        var summary = parts.Count == 0 ? $"{affected} changed" : string.Join(", ", parts);
        SetStatus($"Applied changes to mods ({summary}).", false);
    }

    private void QueueDatabaseInfoRefresh(IEnumerable<ModEntry> entries, bool forceRefresh = false)
    {
        if (entries is null) return;

        if (!_allowModDetailsRefresh && !forceRefresh) return;

        var pending = entries
            .Where(entry => entry != null
                            && !string.IsNullOrWhiteSpace(entry.ModId)
                            && (forceRefresh || NeedsDatabaseRefresh(entry)))
            .ToArray();

        if (pending.Length == 0) return;

        OnModDetailsRefreshEnqueued(pending.Length);

        CancellationToken refreshToken;
        lock (_databaseRefreshLock)
        {
            _databaseRefreshCts?.Cancel();
            _databaseRefreshCts?.Dispose();
            _databaseRefreshCts = new CancellationTokenSource();
            refreshToken = _databaseRefreshCts.Token;
        }

        _databaseRefreshTask = Task.Run(() => RefreshDatabaseInfoBatchAsync(pending, refreshToken), refreshToken);
    }

    private async Task RefreshDatabaseInfoBatchAsync(ModEntry[] pending, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (InternetAccessManager.IsInternetAccessDisabled)
            {
                await PopulateOfflineDatabaseInfoAsync(pending, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Use progressive loading strategy for large mod counts
            if (pending.Length > 100)
            {
                await RefreshDatabaseInfoProgressivelyAsync(pending, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var limiter = new SemaphoreSlim(MaxConcurrentDatabaseRefreshes, MaxConcurrentDatabaseRefreshes);
                var refreshTasks = pending
                    .Select(entry => RefreshDatabaseInfoAsync(entry, limiter, cancellationToken))
                    .ToArray();

                await Task.WhenAll(refreshTasks).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected when a newer refresh supersedes the current one.
        }
        catch (Exception)
        {
            // Swallow unexpected exceptions from the refresh loop.
        }
        finally
        {
            // Flush any pending batched updates when refresh completes
            FlushDatabaseInfoBatch();
        }
    }

    private bool NeedsDatabaseRefresh(ModEntry entry)
    {
        if (entry is null) return false;

        if (_isTagsColumnVisible
            && TryGetTagSuppressionKey(entry, out var key)
            && key != null
            && _suppressedTagEntries.Contains(key))
            return true;

        return entry.DatabaseInfo == null || entry.DatabaseInfo.IsOfflineOnly;
    }

    private bool ShouldSkipOnlineDatabaseRefresh(ModDatabaseInfo? cachedInfo)
    {
        if (_isTagsColumnVisible) return false;

        if (_areUserReportsVisible) return false;

        if (cachedInfo is null || cachedInfo.IsOfflineOnly) return false;

        return true;
    }

    private static bool TryGetTagSuppressionKey(ModEntry entry, out string? key)
    {
        key = null;

        if (entry is null) return false;

        if (!string.IsNullOrWhiteSpace(entry.SourcePath))
        {
            key = entry.SourcePath;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(entry.ModId))
        {
            key = entry.ModId;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Progressively refreshes database info for large mod collections.
    /// First batch (visible items) gets high priority, rest are processed in background.
    /// </summary>
    private async Task RefreshDatabaseInfoProgressivelyAsync(ModEntry[] entries, CancellationToken cancellationToken)
    {
        const int priorityBatchSize = 50; // First 50 mods get immediate attention
        const int regularBatchSize = 20;  // Subsequent batches are smaller

        using var limiter = new SemaphoreSlim(MaxConcurrentDatabaseRefreshes, MaxConcurrentDatabaseRefreshes);

        // Process first batch with high priority (likely visible in UI)
        var priorityCount = Math.Min(priorityBatchSize, entries.Length);
        var priorityBatch = entries.Take(priorityCount).ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        var priorityTasks = priorityBatch
            .Select(entry => RefreshDatabaseInfoAsync(entry, limiter, cancellationToken))
            .ToArray();

        await Task.WhenAll(priorityTasks).ConfigureAwait(false);

        // Flush after priority batch to show initial results quickly
        FlushDatabaseInfoBatch();

        // Process remaining entries in smaller batches with delays to avoid overwhelming the system
        var remaining = entries.Skip(priorityCount).ToArray();
        for (int i = 0; i < remaining.Length; i += regularBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = remaining.Skip(i).Take(regularBatchSize).ToArray();
            var batchTasks = batch
                .Select(entry => RefreshDatabaseInfoAsync(entry, limiter, cancellationToken))
                .ToArray();

            await Task.WhenAll(batchTasks).ConfigureAwait(false);

            // Small delay between batches to keep UI responsive
            if (i + regularBatchSize < remaining.Length)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RefreshDatabaseInfoAsync(ModEntry entry, SemaphoreSlim limiter, CancellationToken cancellationToken)
    {
        await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        using var logScope = StatusLogService.BeginDebugScope(entry.Name, entry.ModId, "metadata");
        using var timingScope = _timingService.MeasureDatabaseInfoLoad();
        var cacheHit = false;
        var source = string.Empty;
        var tagCount = 0;
        var releaseCount = 0;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // On initial load, use cache-only approach for better startup performance
            // This skips expensive network checks and just uses whatever is cached
            if (_isInitialLoad)
            {
                ModDatabaseInfo? cachedInfo;
                using (_timingService.MeasureDbCacheLoad())
                {
                    cachedInfo = await _databaseService
                        .TryLoadCachedDatabaseInfoAsync(entry.ModId, entry.Version, InstalledGameVersion,
                            _configuration.RequireExactVsVersionMatch)
                        .ConfigureAwait(false);
                }

                if (cachedInfo != null)
                {
                    cacheHit = true;
                    using (_timingService.MeasureDbApplyInfo())
                    {
                        await ApplyDatabaseInfoAsync(entry, cachedInfo, false).ConfigureAwait(false);
                    }
                    tagCount = cachedInfo.Tags?.Count ?? 0;
                    releaseCount = cachedInfo.Releases?.Count ?? 0;
                    source = "cache";
                    return;
                }

                // No cache available, create offline info
                using (_timingService.MeasureDbOfflineInfo())
                {
                    await PopulateOfflineInfoForEntryAsync(entry).ConfigureAwait(false);
                }
                tagCount = entry.DatabaseInfo?.Tags?.Count ?? 0;
                releaseCount = entry.DatabaseInfo?.Releases?.Count ?? 0;
                source = "offline";
                return;
            }

            // For subsequent refreshes, use normal refresh logic with version checks
            ModDatabaseInfo? cachedInfo2;
            bool needsRefresh;
            using (_timingService.MeasureDbCacheLoad())
            {
                (cachedInfo2, needsRefresh) = await _databaseService
                    .TryLoadCachedDatabaseInfoWithRefreshCheckAsync(entry.ModId, entry.Version, InstalledGameVersion,
                        _configuration.RequireExactVsVersionMatch)
                    .ConfigureAwait(false);
            }

            cacheHit = cachedInfo2 != null;

            if (cachedInfo2 != null)
            {
                using (_timingService.MeasureDbApplyInfo())
                {
                    await ApplyDatabaseInfoAsync(entry, cachedInfo2, false).ConfigureAwait(false);
                }
                tagCount = cachedInfo2.Tags?.Count ?? 0;
                releaseCount = cachedInfo2.Releases?.Count ?? 0;

                if (InternetAccessManager.IsInternetAccessDisabled)
                {
                    source = "cache";
                    return;
                }

                // Skip network request if no refresh is needed (version unchanged)
                if (!needsRefresh)
                {
                    source = "cache";
                    return;
                }

                if (ShouldSkipOnlineDatabaseRefresh(cachedInfo2))
                {
                    source = "cache";
                    return;
                }
            }
            else if (InternetAccessManager.IsInternetAccessDisabled)
            {
                using (_timingService.MeasureDbOfflineInfo())
                {
                    await PopulateOfflineInfoForEntryAsync(entry).ConfigureAwait(false);
                }
                tagCount = entry.DatabaseInfo?.Tags?.Count ?? 0;
                releaseCount = entry.DatabaseInfo?.Releases?.Count ?? 0;
                source = "offline";
                return;
            }

            ModDatabaseInfo? info;
            try
            {
                // Pass the already-loaded cached info to avoid re-reading from disk
                using (_timingService.MeasureDbNetworkLoad())
                {
                    info = await _databaseService
                        .TryLoadDatabaseInfoAsync(entry.ModId, entry.Version, InstalledGameVersion,
                            _configuration.RequireExactVsVersionMatch, cachedInfo2, cancellationToken, _timingService)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                source = cacheHit ? "cache" : "error";
                return;
            }

            if (info is null)
            {
                if (cacheHit)
                {
                    source = "cache";
                    return;
                }

                using (_timingService.MeasureDbOfflineInfo())
                {
                    await PopulateOfflineInfoForEntryAsync(entry).ConfigureAwait(false);
                }
                tagCount = entry.DatabaseInfo?.Tags?.Count ?? 0;
                releaseCount = entry.DatabaseInfo?.Releases?.Count ?? 0;
                source = "offline";
                return;
            }

            using (_timingService.MeasureDbApplyInfo())
            {
                await ApplyDatabaseInfoAsync(entry, info).ConfigureAwait(false);
            }
            tagCount = info.Tags?.Count ?? tagCount;
            releaseCount = info.Releases?.Count ?? releaseCount;
            source = info.IsOfflineOnly ? "offline" : "net";
        }
        finally
        {
            limiter.Release();
            if (logScope != null)
            {
                logScope.SetCacheStatus(cacheHit);
                if (!string.IsNullOrWhiteSpace(source)) logScope.SetDetail("src", source);

                logScope.SetDetail("tags", tagCount);
                logScope.SetDetail("rel", releaseCount);
            }

            OnModDetailsRefreshCompleted();
        }
    }

    private async Task PopulateOfflineDatabaseInfoAsync(IEnumerable<ModEntry> entries, CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is not null)
                {
                    using (_timingService.MeasureDatabaseInfoLoad())
                    using (_timingService.MeasureDbOfflineInfo())
                    {
                        await PopulateOfflineInfoForEntryAsync(entry).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception)
            {
                // Swallow unexpected exceptions for resilience.
            }
            finally
            {
                OnModDetailsRefreshCompleted();
            }
    }

    private async Task PopulateOfflineInfoForEntryAsync(ModEntry entry)
    {
        if (entry is null) return;

        var cachedInfo = entry.DatabaseInfo;
        if (cachedInfo is null)
            cachedInfo = await _databaseService
                .TryLoadCachedDatabaseInfoAsync(entry.ModId, entry.Version, InstalledGameVersion,
                    _configuration.RequireExactVsVersionMatch)
                .ConfigureAwait(false);

        var offlineInfo = CreateOfflineDatabaseInfo(entry);

        var mergedInfo = MergeOfflineAndCachedInfo(offlineInfo, cachedInfo);
        if (mergedInfo is null) return;

        await ApplyDatabaseInfoAsync(entry, mergedInfo).ConfigureAwait(false);
    }

    private async Task ApplyDatabaseInfoAsync(ModEntry entry, ModDatabaseInfo info, bool loadLogoImmediately = true)
    {
        if (info is null) return;

        var preparedInfo = PrepareDatabaseInfoForVisibility(entry, info);

        // During initial load or when we have many pending updates, use batching for better performance
        // This dramatically reduces dispatcher overhead by grouping updates together
        if (_isInitialLoad || ShouldUseBatchedUpdates())
        {
            QueueDatabaseInfoUpdate(entry, preparedInfo, loadLogoImmediately);
            return;
        }

        // For individual updates (e.g., user-triggered refresh), apply immediately
        await ApplyDatabaseInfoImmediateAsync(entry, preparedInfo, loadLogoImmediately).ConfigureAwait(false);
    }

    private bool ShouldUseBatchedUpdates()
    {
        lock (_databaseInfoBatchLock)
        {
            // Use batching if we already have pending updates to continue the batch
            return _pendingDatabaseInfoUpdates.Count > 0;
        }
    }

    private void QueueDatabaseInfoUpdate(ModEntry entry, ModDatabaseInfo info, bool loadLogoImmediately)
    {
        lock (_databaseInfoBatchLock)
        {
            // Add to batch queue
            _pendingDatabaseInfoUpdates.Add((entry, info, loadLogoImmediately));

            // If batch is full, flush immediately
            if (_pendingDatabaseInfoUpdates.Count >= DatabaseInfoBatchSize)
            {
                // Dispose timer before flushing to prevent race condition
                _databaseInfoBatchTimer?.Dispose();
                _databaseInfoBatchTimer = null;
                FlushDatabaseInfoBatchLocked();
            }
            else
            {
                // Otherwise, schedule a timer to flush soon
                // Dispose old timer first to prevent race condition
                _databaseInfoBatchTimer?.Dispose();
                _databaseInfoBatchTimer = new Timer(
                    _ =>
                    {
                        lock (_databaseInfoBatchLock)
                        {
                            // Only flush if this timer hasn't been disposed/replaced
                            if (_databaseInfoBatchTimer != null)
                            {
                                FlushDatabaseInfoBatchLocked();
                            }
                        }
                    },
                    null,
                    DatabaseInfoBatchDelayMs,
                    Timeout.Infinite);
            }
        }
    }

    private void FlushDatabaseInfoBatch()
    {
        lock (_databaseInfoBatchLock)
        {
            FlushDatabaseInfoBatchLocked();
        }
    }

    /// <summary>
    /// Flushes pending database info updates. Must be called while holding _databaseInfoBatchLock.
    /// Note: This does NOT trigger recursive batching because ApplyDatabaseInfoBatchAsync
    /// applies updates directly without going through ApplyDatabaseInfoAsync.
    /// </summary>
    private void FlushDatabaseInfoBatchLocked()
    {
        if (_pendingDatabaseInfoUpdates.Count == 0) return;

        var batch = new List<(ModEntry, ModDatabaseInfo, bool)>(_pendingDatabaseInfoUpdates);
        _pendingDatabaseInfoUpdates.Clear();

        _databaseInfoBatchTimer?.Dispose();
        _databaseInfoBatchTimer = null;

        // Apply the batch on the dispatcher
        // Note: This is intentionally fire-and-forget, but we log any failures
        _ = ApplyDatabaseInfoBatchAsync(batch);
    }

    private async Task ApplyDatabaseInfoBatchAsync(List<(ModEntry entry, ModDatabaseInfo info, bool loadLogoImmediately)> batch)
    {
        if (batch.Count == 0) return;

        try
        {
            var dispatcherStopwatch = System.Diagnostics.Stopwatch.StartNew();
            await InvokeOnDispatcherAsync(
                    () =>
                    {
                        // Record dispatcher wait time once for the entire batch
                        dispatcherStopwatch.Stop();
                        _timingService.RecordDbApplyDispatcherTime(dispatcherStopwatch.Elapsed.TotalMilliseconds);

                        // Apply all updates in the batch
                        using (_timingService.MeasureDbApplyUiHandler(batch.Count))
                        {
                            foreach (var (entry, info, loadLogoImmediately) in batch)
                            {
                                if (!_modEntriesBySourcePath.TryGetValue(entry.SourcePath, out var currentEntry)
                                    || !ReferenceEquals(currentEntry, entry))
                                    continue;

                                // Measure entry update time
                                using (_timingService.MeasureDbApplyEntryUpdate())
                                {
                                    currentEntry.UpdateDatabaseInfo(info);
                                }

                                if (_modViewModelsBySourcePath.TryGetValue(entry.SourcePath, out var viewModel))
                                {
                                    // Measure view model update time
                                    using (_timingService.MeasureDbApplyViewModelUpdate())
                                    {
                                        viewModel.UpdateDatabaseInfo(info, loadLogoImmediately);
                                    }
                                }
                            }
                        }
                    },
                    CancellationToken.None,
                    DispatcherPriority.Background)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // Ignore cancellations when the dispatcher shuts down.
        }
        catch (Exception ex)
        {
            // Log batch application failures to diagnose missing UI updates
            System.Diagnostics.Debug.WriteLine($"[MainViewModel] Failed to apply database info batch ({batch.Count} items): {ex.Message}");
        }
    }

    private async Task ApplyDatabaseInfoImmediateAsync(ModEntry entry, ModDatabaseInfo info, bool loadLogoImmediately)
    {
        try
        {
            var dispatcherStopwatch = System.Diagnostics.Stopwatch.StartNew();
            await InvokeOnDispatcherAsync(
                    () =>
                    {
                        // Record dispatcher wait time
                        dispatcherStopwatch.Stop();
                        _timingService.RecordDbApplyDispatcherTime(dispatcherStopwatch.Elapsed.TotalMilliseconds);

                        if (!_modEntriesBySourcePath.TryGetValue(entry.SourcePath, out var currentEntry)
                            || !ReferenceEquals(currentEntry, entry))
                            return;

                        using (_timingService.MeasureDbApplyUiHandler(1))
                        {
                            // Measure entry update time
                            using (_timingService.MeasureDbApplyEntryUpdate())
                            {
                                currentEntry.UpdateDatabaseInfo(info);
                            }

                            if (_modViewModelsBySourcePath.TryGetValue(entry.SourcePath, out var viewModel))
                            {
                                // Measure view model update time
                                using (_timingService.MeasureDbApplyViewModelUpdate())
                                {
                                    viewModel.UpdateDatabaseInfo(info, loadLogoImmediately);
                                    // Defer user report refresh to avoid cascading updates during bulk loading
                                    // The user report will be loaded on-demand when visible or when explicitly requested
                                }
                            }
                        }
                    },
                    CancellationToken.None,
                    DispatcherPriority.Background)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // Ignore cancellations when the dispatcher shuts down.
        }
        catch (Exception)
        {
            // Ignore dispatcher failures to keep refresh resilient.
        }
    }

    private ModDatabaseInfo PrepareDatabaseInfoForVisibility(ModEntry entry, ModDatabaseInfo info)
    {
        if (!_isTagsColumnVisible)
        {
            if (TryGetTagSuppressionKey(entry, out var key) && key != null) _suppressedTagEntries.Add(key);

            return CreateInfoWithoutTags(info);
        }

        if (TryGetTagSuppressionKey(entry, out var visibleKey) && visibleKey != null)
            _suppressedTagEntries.Remove(visibleKey);

        return info;
    }

    private static ModDatabaseInfo CreateInfoWithoutTags(ModDatabaseInfo source)
    {
        return new ModDatabaseInfo
        {
            Tags = Array.Empty<string>(),
            CachedTagsVersion = null,
            AssetId = source.AssetId,
            ModPageUrl = source.ModPageUrl,
            LatestCompatibleVersion = source.LatestCompatibleVersion,
            LatestVersion = source.LatestVersion,
            RequiredGameVersions = source.RequiredGameVersions,
            Downloads = source.Downloads,
            Comments = source.Comments,
            Follows = source.Follows,
            TrendingPoints = source.TrendingPoints,
            LogoUrl = source.LogoUrl,
            LogoUrlSource = source.LogoUrlSource,
            DownloadsLastThirtyDays = source.DownloadsLastThirtyDays,
            DownloadsLastTenDays = source.DownloadsLastTenDays,
            LastReleasedUtc = source.LastReleasedUtc,
            CreatedUtc = source.CreatedUtc,
            LatestRelease = source.LatestRelease,
            LatestCompatibleRelease = source.LatestCompatibleRelease,
            Releases = source.Releases,
            IsOfflineOnly = source.IsOfflineOnly,
            Side = source.Side
        };
    }

    private ModDatabaseInfo? CreateOfflineDatabaseInfo(ModEntry entry)
    {
        if (entry is null) return null;

        var dependencies = entry.Dependencies ?? Array.Empty<ModDependencyInfo>();
        var installedRequiredGameVersions = ExtractRequiredGameVersions(dependencies);
        var releases = CreateOfflineReleases(entry, installedRequiredGameVersions, dependencies);
        var aggregatedRequiredGameVersions = AggregateRequiredGameVersions(installedRequiredGameVersions, releases);

        var latestRelease = releases.Count > 0 ? releases[0] : null;
        var latestCompatibleRelease = releases.FirstOrDefault(release => release.IsCompatibleWithInstalledGame);
        var lastUpdatedUtc = DetermineOfflineLastUpdatedUtc(entry, releases);

        var latestVersion = latestRelease?.Version ?? entry.Version;
        var latestCompatibleVersion = latestCompatibleRelease?.Version ?? entry.Version;

        return new ModDatabaseInfo
        {
            RequiredGameVersions = aggregatedRequiredGameVersions,
            LatestVersion = latestVersion,
            LatestCompatibleVersion = latestCompatibleVersion,
            LatestRelease = latestRelease,
            LatestCompatibleRelease = latestCompatibleRelease ?? latestRelease,
            Releases = releases,
            LastReleasedUtc = lastUpdatedUtc,
            IsOfflineOnly = true,
            Side = entry.Side
        };
    }

    private static ModDatabaseInfo? MergeOfflineAndCachedInfo(ModDatabaseInfo? offlineInfo, ModDatabaseInfo? cachedInfo)
    {
        if (offlineInfo is null) return cachedInfo;

        if (cachedInfo is null) return offlineInfo;

        var mergedReleases = MergeReleases(offlineInfo.Releases, cachedInfo.Releases);
        var latestRelease = offlineInfo.LatestRelease ?? cachedInfo.LatestRelease;
        if (latestRelease is null && mergedReleases is { Count: > 0 }) latestRelease = mergedReleases[0];

        var latestCompatibleRelease = offlineInfo.LatestCompatibleRelease ?? cachedInfo.LatestCompatibleRelease;
        if (latestCompatibleRelease is null && mergedReleases is { Count: > 0 })
            latestCompatibleRelease =
                mergedReleases.FirstOrDefault(release => release?.IsCompatibleWithInstalledGame == true);

        var requiredVersions = offlineInfo.RequiredGameVersions is { Count: > 0 }
            ? offlineInfo.RequiredGameVersions
            : cachedInfo.RequiredGameVersions;

        return new ModDatabaseInfo
        {
            Tags = cachedInfo.Tags ?? offlineInfo.Tags ?? Array.Empty<string>(),
            CachedTagsVersion = cachedInfo.CachedTagsVersion ?? offlineInfo.CachedTagsVersion,
            AssetId = cachedInfo.AssetId ?? offlineInfo.AssetId,
            ModPageUrl = cachedInfo.ModPageUrl ?? offlineInfo.ModPageUrl,
            LatestCompatibleVersion = offlineInfo.LatestCompatibleVersion ?? cachedInfo.LatestCompatibleVersion,
            LatestVersion = offlineInfo.LatestVersion ?? cachedInfo.LatestVersion,
            RequiredGameVersions = requiredVersions,
            Downloads = cachedInfo.Downloads ?? offlineInfo.Downloads,
            Comments = cachedInfo.Comments ?? offlineInfo.Comments,
            Follows = cachedInfo.Follows ?? offlineInfo.Follows,
            TrendingPoints = cachedInfo.TrendingPoints ?? offlineInfo.TrendingPoints,
            LogoUrl = cachedInfo.LogoUrl ?? offlineInfo.LogoUrl,
            LogoUrlSource = cachedInfo.LogoUrlSource ?? offlineInfo.LogoUrlSource,
            DownloadsLastThirtyDays = cachedInfo.DownloadsLastThirtyDays ?? offlineInfo.DownloadsLastThirtyDays,
            LastReleasedUtc = offlineInfo.LastReleasedUtc ?? cachedInfo.LastReleasedUtc,
            CreatedUtc = cachedInfo.CreatedUtc ?? offlineInfo.CreatedUtc,
            LatestRelease = latestRelease,
            LatestCompatibleRelease = latestCompatibleRelease ?? latestRelease,
            Releases = mergedReleases,
            IsOfflineOnly = offlineInfo.IsOfflineOnly && (cachedInfo?.IsOfflineOnly ?? true),
            Side = cachedInfo?.Side ?? offlineInfo.Side
        };
    }

    private static IReadOnlyList<ModReleaseInfo> MergeReleases(
        IReadOnlyList<ModReleaseInfo>? offlineReleases,
        IReadOnlyList<ModReleaseInfo>? cachedReleases)
    {
        if (offlineReleases is not { Count: > 0 })
            return cachedReleases is { Count: > 0 } ? cachedReleases : Array.Empty<ModReleaseInfo>();

        if (cachedReleases is not { Count: > 0 }) return offlineReleases;

        var byVersion = new Dictionary<string, ModReleaseInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var release in cachedReleases)
        {
            if (release is null || string.IsNullOrWhiteSpace(release.Version)) continue;

            if (!byVersion.ContainsKey(release.Version)) byVersion[release.Version] = release;
        }

        foreach (var release in offlineReleases)
        {
            if (release is null || string.IsNullOrWhiteSpace(release.Version)) continue;

            byVersion[release.Version] = release;
        }

        if (byVersion.Count == 0) return Array.Empty<ModReleaseInfo>();

        var merged = byVersion.Values.ToList();
        merged.Sort(CompareOfflineReleases);
        return merged;
    }

    private IReadOnlyList<ModReleaseInfo> CreateOfflineReleases(
        ModEntry entry,
        IReadOnlyList<string> installedRequiredGameVersions,
        IReadOnlyList<ModDependencyInfo> installedDependencies)
    {
        var releases = new List<ModReleaseInfo>();
        var seenVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var installedRelease = CreateOfflineRelease(entry, installedRequiredGameVersions, installedDependencies);
        if (installedRelease != null)
        {
            releases.Add(installedRelease);
            seenVersions.Add(installedRelease.Version);
        }

        foreach (var cachedRelease in EnumerateCachedModReleases(entry.ModId))
        {
            if (!seenVersions.Add(cachedRelease.Version)) continue;

            releases.Add(cachedRelease);
        }

        if (releases.Count == 0) return Array.Empty<ModReleaseInfo>();

        releases.Sort(CompareOfflineReleases);
        return releases.AsReadOnly();
    }

    private ModReleaseInfo? CreateOfflineRelease(
        ModEntry entry,
        IReadOnlyList<string> requiredGameVersions,
        IReadOnlyList<ModDependencyInfo> dependencies)
    {
        if (string.IsNullOrWhiteSpace(entry.Version)) return null;

        if (!TryCreateFileUri(entry.SourcePath, entry.SourceKind, out var downloadUri)) return null;

        var createdUtc = TryGetLastWriteTimeUtc(entry.SourcePath, entry.SourceKind);

        return new ModReleaseInfo
        {
            Version = entry.Version!,
            NormalizedVersion = VersionStringUtility.Normalize(entry.Version),
            DownloadUri = downloadUri!,
            FileName = TryGetReleaseFileName(entry.SourcePath),
            GameVersionTags = requiredGameVersions,
            IsCompatibleWithInstalledGame = DetermineInstalledGameCompatibility(dependencies),
            CreatedUtc = createdUtc
        };
    }

    private IEnumerable<ModReleaseInfo> EnumerateCachedModReleases(string modId)
    {
        var cacheDirectory = ModCacheLocator.GetModCacheDirectory(modId);
        if (string.IsNullOrWhiteSpace(cacheDirectory)) yield break;

        try
        {
            if (!Directory.Exists(cacheDirectory)) yield break;
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var file in ModCacheLocator.EnumerateCachedFiles(modId))
        {
            var release = TryCreateCachedRelease(file, modId);
            if (release != null) yield return release;
        }
    }

    private ModReleaseInfo? TryCreateCachedRelease(string archivePath, string expectedModId)
    {
        var lastWriteTimeUtc = DateTime.MinValue;
        var length = 0L;

        try
        {
            var fileInfo = new FileInfo(archivePath);
            if (fileInfo.Exists)
            {
                lastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
                length = fileInfo.Length;
            }
        }
        catch (Exception)
        {
            // Ignore filesystem probing failures; the cache simply will not be used.
        }

        if (ModManifestCacheService.TryGetManifest(archivePath, lastWriteTimeUtc, length, out var cachedManifest,
                out _))
            try
            {
                using var document = JsonDocument.Parse(cachedManifest);
                var root = document.RootElement;

                var modId = GetString(root, "modid") ?? GetString(root, "modID");
                if (!IsModIdMatch(expectedModId, modId))
                {
                    ModManifestCacheService.Invalidate(archivePath);
                }
                else
                {
                    var version = GetString(root, "version") ?? TryResolveVersionFromMap(root);
                    if (string.IsNullOrWhiteSpace(version)) return null;

                    var dependencies = ParseDependencies(root);
                    var requiredGameVersions = ExtractRequiredGameVersions(dependencies);

                    if (!TryCreateFileUri(archivePath, ModSourceKind.ZipArchive, out var downloadUri)) return null;

                    var createdUtc = TryGetLastWriteTimeUtc(archivePath, ModSourceKind.ZipArchive);
                    return new ModReleaseInfo
                    {
                        Version = version!,
                        NormalizedVersion = VersionStringUtility.Normalize(version),
                        DownloadUri = downloadUri!,
                        FileName = Path.GetFileName(archivePath),
                        GameVersionTags = requiredGameVersions,
                        IsCompatibleWithInstalledGame = DetermineInstalledGameCompatibility(dependencies),
                        CreatedUtc = createdUtc
                    };
                }
            }
            catch (Exception)
            {
                ModManifestCacheService.Invalidate(archivePath);
            }

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var infoEntry = FindArchiveEntry(archive, "modinfo.json");
            if (infoEntry == null) return null;

            string manifestContent;
            using (var infoStream = infoEntry.Open())
            using (var reader = new StreamReader(infoStream, Encoding.UTF8, true))
            {
                manifestContent = reader.ReadToEnd();
            }

            using var document = JsonDocument.Parse(manifestContent);
            var root = document.RootElement;

            var modId = GetString(root, "modid") ?? GetString(root, "modID");
            if (!IsModIdMatch(expectedModId, modId)) return null;

            var version = GetString(root, "version") ?? TryResolveVersionFromMap(root);
            if (string.IsNullOrWhiteSpace(version)) return null;

            var dependencies = ParseDependencies(root);
            var requiredGameVersions = ExtractRequiredGameVersions(dependencies);

            if (!TryCreateFileUri(archivePath, ModSourceKind.ZipArchive, out var downloadUri)) return null;

            var createdUtc = TryGetLastWriteTimeUtc(archivePath, ModSourceKind.ZipArchive);

            var cacheModId = !string.IsNullOrWhiteSpace(modId)
                ? modId
                : !string.IsNullOrWhiteSpace(expectedModId)
                    ? expectedModId
                    : Path.GetFileNameWithoutExtension(archivePath);

            ModManifestCacheService.StoreManifest(archivePath, lastWriteTimeUtc, length, cacheModId, version,
                manifestContent, null);

            return new ModReleaseInfo
            {
                Version = version!,
                NormalizedVersion = VersionStringUtility.Normalize(version),
                DownloadUri = downloadUri!,
                FileName = Path.GetFileName(archivePath),
                GameVersionTags = requiredGameVersions,
                IsCompatibleWithInstalledGame = DetermineInstalledGameCompatibility(dependencies),
                CreatedUtc = createdUtc
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsModIdMatch(string expectedModId, string? actualModId)
    {
        if (string.IsNullOrWhiteSpace(expectedModId) || string.IsNullOrWhiteSpace(actualModId)) return true;

        return string.Equals(actualModId, expectedModId, StringComparison.OrdinalIgnoreCase);
    }

    private static ZipArchiveEntry? FindArchiveEntry(ZipArchive archive, string entryName)
    {
        foreach (var entry in archive.Entries)
            if (string.Equals(entry.FullName, entryName, StringComparison.OrdinalIgnoreCase))
                return entry;

        return archive.Entries.FirstOrDefault(entry =>
            string.Equals(Path.GetFileName(entry.FullName), entryName, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }

        value = default;
        return false;
    }

    private static string? TryResolveVersionFromMap(JsonElement root)
    {
        if (!TryGetProperty(root, "versionmap", out var map) &&
            !TryGetProperty(root, "VersionMap", out map)) return null;

        if (map.ValueKind != JsonValueKind.Object) return null;

        string? preferred = null;
        string? fallback = null;
        foreach (var property in map.EnumerateObject())
        {
            var version = property.Value.GetString();
            if (version == null) continue;

            fallback = version;
            if (property.Name.Contains("1.21", StringComparison.OrdinalIgnoreCase)) preferred = version;
        }

        return preferred ?? fallback;
    }

    private static IReadOnlyList<ModDependencyInfo> ParseDependencies(JsonElement root)
    {
        if (!TryGetProperty(root, "dependencies", out var dependenciesElement)
            || dependenciesElement.ValueKind != JsonValueKind.Object)
            return Array.Empty<ModDependencyInfo>();

        var dependencies = new List<ModDependencyInfo>();
        foreach (var property in dependenciesElement.EnumerateObject())
        {
            var version = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : string.Empty;
            dependencies.Add(new ModDependencyInfo(property.Name, version));
        }

        return dependencies.Count == 0 ? Array.Empty<ModDependencyInfo>() : dependencies;
    }

    private static IReadOnlyList<string> AggregateRequiredGameVersions(
        IReadOnlyList<string> installedRequiredGameVersions,
        IReadOnlyList<ModReleaseInfo> releases)
    {
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var version in installedRequiredGameVersions)
            if (!string.IsNullOrWhiteSpace(version))
                versions.Add(version);

        foreach (var release in releases)
        {
            if (release?.GameVersionTags is null) continue;

            foreach (var tag in release.GameVersionTags)
                if (!string.IsNullOrWhiteSpace(tag))
                    versions.Add(tag);
        }

        return versions.Count == 0 ? Array.Empty<string>() : versions.ToArray();
    }

    private static DateTime? DetermineOfflineLastUpdatedUtc(ModEntry entry, IReadOnlyList<ModReleaseInfo> releases)
    {
        DateTime? lastUpdatedUtc = null;
        foreach (var release in releases)
        {
            if (release?.CreatedUtc is not DateTime created) continue;

            if (!lastUpdatedUtc.HasValue || created > lastUpdatedUtc) lastUpdatedUtc = created;
        }

        return lastUpdatedUtc ?? TryGetLastWriteTimeUtc(entry.SourcePath, entry.SourceKind);
    }

    private void OnInternetAccessChanged(object? sender, EventArgs e)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
                RefreshInternetAccessDependentState();
            else
                dispatcher.BeginInvoke(DispatcherPriority.Normal, RefreshInternetAccessDependentState);

            return;
        }

        RefreshInternetAccessDependentState();
    }

    private void RefreshInternetAccessDependentState()
    {
        var isOffline = InternetAccessManager.IsInternetAccessDisabled;

        foreach (var mod in _mods)
        {
            mod.RefreshInternetAccessDependentState();
            if (isOffline)
                mod.SetUserReportOffline();
            else
                QueueUserReportRefresh(mod);
        }

        foreach (var mod in _searchResults) mod.RefreshInternetAccessDependentState();

        _showDatabaseTabCommand.NotifyCanExecuteChanged();
        _showModlistTabCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAccessCloudModlists));

        if (InternetAccessManager.IsInternetAccessDisabled && _viewSection == ViewSection.DatabaseTab)
        {
            SetStatus(InternetAccessDisabledStatusMessage, false);
            SetViewSection(ViewSection.MainTab);
            return;
        }

        if (InternetAccessManager.IsInternetAccessDisabled && _viewSection == ViewSection.ModlistTab)
        {
            SetStatus(InternetAccessDisabledStatusMessage, false);
            SetViewSection(ViewSection.MainTab);
        }
    }

    private static int CompareOfflineReleases(ModReleaseInfo? left, ModReleaseInfo? right)
    {
        if (ReferenceEquals(left, right)) return 0;

        if (left is null) return 1;

        if (right is null) return -1;

        if (VersionStringUtility.IsCandidateVersionNewer(left.Version, right.Version)) return -1;

        if (VersionStringUtility.IsCandidateVersionNewer(right.Version, left.Version)) return 1;

        var leftTimestamp = left.CreatedUtc ?? DateTime.MinValue;
        var rightTimestamp = right.CreatedUtc ?? DateTime.MinValue;
        var dateComparison = rightTimestamp.CompareTo(leftTimestamp);
        if (dateComparison != 0) return dateComparison;

        return string.Compare(left.Version, right.Version, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ExtractRequiredGameVersions(IReadOnlyList<ModDependencyInfo> dependencies)
    {
        if (dependencies is null || dependencies.Count == 0) return Array.Empty<string>();

        var versions = dependencies
            .Where(dependency => dependency != null && dependency.IsGameOrCoreDependency)
            .Select(dependency => dependency.Version?.Trim())
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Select(version => version!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return versions.Length == 0 ? Array.Empty<string>() : versions;
    }

    private bool DetermineInstalledGameCompatibility(IReadOnlyList<ModDependencyInfo> dependencies)
    {
        if (dependencies is null || dependencies.Count == 0) return true;

        if (string.IsNullOrWhiteSpace(InstalledGameVersion)) return true;

        foreach (var dependency in dependencies)
        {
            if (dependency is null || !dependency.IsGameOrCoreDependency) continue;

            // Check if the installed game version satisfies the dependency's minimum version requirement
            // (e.g., if mod requires 1.21.0 and user has 1.21.4, that's OK as 1.21.4 >= 1.21.0)
            if (!VersionStringUtility.SatisfiesMinimumVersion(dependency.Version, InstalledGameVersion)) return false;

            // When exact version match is required, also verify first 3 version parts match
            // (e.g., with exact mode, 1.21.3 won't be compatible with 1.21.4 even though 1.21.4 >= 1.21.3)
            if (_configuration.RequireExactVsVersionMatch)
                if (!VersionStringUtility.MatchesFirstThreeDigits(dependency.Version, InstalledGameVersion))
                    return false;
        }

        return true;
    }

    private static bool TryCreateFileUri(string? path, ModSourceKind kind, out Uri? uri)
    {
        uri = null;

        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (kind == ModSourceKind.Folder)
                fullPath = Path.TrimEndingDirectorySeparator(fullPath) + Path.DirectorySeparatorChar;

            return Uri.TryCreate(fullPath, UriKind.Absolute, out uri);
        }
        catch (Exception)
        {
            uri = null;
            return false;
        }
    }

    private static string? TryGetReleaseFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var normalized = Path.GetFullPath(path);
            if (Directory.Exists(normalized)) normalized = Path.TrimEndingDirectorySeparator(normalized);

            var fileName = Path.GetFileName(normalized);
            return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public sealed class ModUserReportChangedEventArgs : EventArgs
    {
        public ModUserReportChangedEventArgs(
            string modId,
            string? modVersion,
            int? numericModId,
            ModVersionVoteSummary summary)
        {
            ModId = modId;
            ModVersion = modVersion;
            NumericModId = numericModId;
            Summary = summary ?? throw new ArgumentNullException(nameof(summary));
        }

        public string ModId { get; }

        public string? ModVersion { get; }

        public int? NumericModId { get; }

        public ModVersionVoteSummary Summary { get; }
    }

    private static DateTime? TryGetLastWriteTimeUtc(string? path, ModSourceKind kind)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return kind == ModSourceKind.Folder
                ? Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : null
                : File.Exists(path)
                    ? File.GetLastWriteTimeUtc(path)
                    : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private enum ViewSection
    {
        MainTab,
        DatabaseTab,
        ModlistTab
    }

    private sealed record VoteCheckTarget(
        ModListItemViewModel Mod,
        string? UserReportVersion,
        bool HasUserReportSummary,
        string? LatestReleaseVersion,
        bool HasLatestReleaseSummary);

    private sealed class UserReportOperationScope : IDisposable
    {
        private readonly IDisposable _busyScope;
        private readonly MainViewModel _owner;
        private bool _disposed;

        public UserReportOperationScope(MainViewModel owner, IDisposable busyScope)
        {
            _owner = owner;
            _busyScope = busyScope;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _busyScope.Dispose();
            _owner.EndUserReportOperation();
        }
    }

    private void PerformClientSettingsCleanupIfNeeded()
    {
        if (_configuration.ClientSettingsCleanupCompleted) return;

        try
        {
            _settingsStore.RemoveInvalidDisabledEntries();
            _configuration.SetClientSettingsCleanupCompleted();
        }
        catch (Exception)
        {
            // This cleanup is a best-effort operation.
        }
    }

    /// <summary>
    /// Calculates an adaptive debounce delay based on the collection size.
    /// Larger collections use longer delays to reduce CPU load during rapid typing.
    /// </summary>
    private TimeSpan CalculateAdaptiveSearchDebounce()
    {
        var modCount = _mods.Count;

        if (modCount <= LargeModListThreshold)
        {
            return InstalledModsSearchDebounceMin;
        }

        // Scale linearly between min and max based on collection size
        // At LargeModListThreshold (200) mods: min delay (100ms)
        // At VeryLargeModListThreshold (500)+ mods: max delay (300ms)
        var debounceRange = VeryLargeModListThreshold - LargeModListThreshold;
        var scale = Math.Min(1.0, (modCount - LargeModListThreshold) / (double)debounceRange);
        var delayMs = InstalledModsSearchDebounceMin.TotalMilliseconds +
                      (InstalledModsSearchDebounceMax.TotalMilliseconds - InstalledModsSearchDebounceMin.TotalMilliseconds) * scale;

        return TimeSpan.FromMilliseconds(delayMs);
    }

    private void TriggerDebouncedInstalledModsSearch()
    {
        lock (_searchDebounceLock)
        {
            if (_disposed) return;

            // Cancel any pending search
            _pendingSearchCts?.Cancel();
            _pendingSearchCts?.Dispose();
            _pendingSearchCts = new CancellationTokenSource();

            // Calculate adaptive debounce based on collection size
            var debounceDelay = CalculateAdaptiveSearchDebounce();

            // Initialize or restart the debounce timer
            // Note: Pass null to callback as we check _pendingSearchCts inside the callback
            // instead of capturing it in a closure, which prevents stale CTS references
            if (_searchDebounceTimer == null)
            {
                _searchDebounceTimer = new Timer(OnSearchDebounceTimerElapsed, null,
                    debounceDelay, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _searchDebounceTimer.Change(debounceDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnSearchDebounceTimerElapsed(object? state)
    {
        // Get the current CTS inside the callback to avoid capturing stale references
        CancellationTokenSource? cts;
        lock (_searchDebounceLock)
        {
            if (_disposed) return;
            cts = _pendingSearchCts;
        }

        if (cts == null) return;
        ExecuteInstalledModsSearch(cts);
    }

    private void ExecuteInstalledModsSearch(CancellationTokenSource cts)
    {
        if (cts.IsCancellationRequested) return;

        // Check disposal and verify this is still the current pending search
        bool shouldExecute;
        lock (_searchDebounceLock)
        {
            if (_disposed) return;
            shouldExecute = ReferenceEquals(cts, _pendingSearchCts);
        }

        if (!shouldExecute) return;

        // Execute the search on the UI thread using Input priority for better responsiveness
        // Input priority ensures search results appear quickly while still yielding to rendering
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(new Action(() => RefreshModsViewIfNotCancelled(cts)),
                DispatcherPriority.Input);
        }
        else
        {
            RefreshModsViewIfNotCancelled(cts);
        }
    }

    private void RefreshModsViewIfNotCancelled(CancellationTokenSource cts)
    {
        if (!cts.IsCancellationRequested)
        {
            ModsView.Refresh();
        }
    }

    private sealed class BusyScope : IDisposable
    {
        private readonly MainViewModel _owner;
        private bool _disposed;

        public BusyScope(MainViewModel owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _owner.EndBusyScope();
        }
    }
}