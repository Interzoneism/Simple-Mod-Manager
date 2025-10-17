using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VintageStoryModManager.Models;
using VintageStoryModManager.Services;

namespace VintageStoryModManager.ViewModels;

/// <summary>
/// Main view model that coordinates mod discovery and activation.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ModDatabaseSearchDebounce = TimeSpan.FromMilliseconds(320);
    private const string InternetAccessDisabledStatusMessage = "Enable Internet Access in the File menu to use.";
    private const int MaxConcurrentDatabaseRefreshes = 4;
    private const int MaxNewModsRecentMonths = 24;
    private const int InstalledModsIncrementalBatchSize = 32;
    private const int MaxModDatabaseResultLimit = int.MaxValue;

    private enum ViewSection
    {
        InstalledMods,
        ModDatabase,
        CloudModlists
    }

    private readonly ObservableCollection<ModListItemViewModel> _mods = new();
    private readonly Dictionary<string, ModEntry> _modEntriesBySourcePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModListItemViewModel> _modViewModelsBySourcePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<ModListItemViewModel> _searchResults = new();
    private readonly ObservableCollection<CloudModlistListEntry> _cloudModlists = new();
    private readonly ClientSettingsStore _settingsStore;
    private readonly ModDiscoveryService _discoveryService;
    private readonly ModDatabaseService _databaseService;
    private readonly int _modDatabaseSearchResultLimit;
    private int _modDatabaseCurrentResultLimit;
    private readonly ObservableCollection<SortOption> _sortOptions;
    private readonly string? _installedGameVersion;
    private readonly object _modsStateLock = new();
    private readonly ModDirectoryWatcher _modsWatcher;
    private readonly int _newModsRecentMonths;

    private SortOption? _selectedSortOption;
    private bool _isBusy;
    private bool _isCompactView;
    private bool _useModDbDesignView;
    private bool _isModInfoExpanded = true;
    private string _statusMessage = string.Empty;
    private bool _isErrorStatus;
    private int _totalMods;
    private int _activeMods;
    private string? _modsStateFingerprint;
    private ModListItemViewModel? _selectedMod;
    private bool _hasSelectedMods;
    private bool _hasMultipleSelectedMods;
    private string _searchText = string.Empty;
    private string[] _searchTokens = Array.Empty<string>();
    private ViewSection _viewSection = ViewSection.InstalledMods;
    private CancellationTokenSource? _modDatabaseSearchCts;
    private readonly RelayCommand _clearSearchCommand;
    private readonly RelayCommand _showInstalledModsCommand;
    private readonly RelayCommand _showModDatabaseCommand;
    private readonly RelayCommand _showCloudModlistsCommand;
    private readonly RelayCommand _loadMoreModDatabaseResultsCommand;
    private ModDatabaseAutoLoadMode _modDatabaseAutoLoadMode = ModDatabaseAutoLoadMode.TotalDownloads;
    private readonly object _busyStateLock = new();
    private int _busyOperationCount;
    private bool _isLoadingMods;
    private bool _isLoadingModDetails;
    private int _pendingModDetailsRefreshCount;
    private readonly object _modDetailsBusyScopeLock = new();
    private IDisposable? _modDetailsBusyScope;
    private bool _isModDetailsStatusActive;
    private bool _hasShownModDetailsLoadingStatus;
    private readonly ObservableCollection<TagFilterOptionViewModel> _installedTagFilters = new();
    private readonly ObservableCollection<TagFilterOptionViewModel> _modDatabaseTagFilters = new();
    private readonly HashSet<ModListItemViewModel> _installedModSubscriptions = new();
    private readonly HashSet<ModListItemViewModel> _searchResultSubscriptions = new();
    private readonly List<string> _selectedInstalledTags = new();
    private readonly List<string> _selectedModDatabaseTags = new();
    private bool _suppressInstalledTagFilterSelectionChanges;
    private bool _suppressModDatabaseTagFilterSelectionChanges;
    private IReadOnlyList<string> _modDatabaseAvailableTags = Array.Empty<string>();
    private bool _isInstalledTagRefreshPending;
    private bool _isModDatabaseTagRefreshPending;
    private bool _excludeInstalledModDatabaseResults;
    private bool _canLoadMoreModDatabaseResults;
    private bool _isLoadMoreModDatabaseButtonVisible;
    private bool _isLoadMoreModDatabaseScrollThresholdReached;
    private bool _isModDatabaseLoading;
    private bool _hasRequestedAdditionalModDatabaseResults;
    private bool _hasSelectedTags;
    private bool _disposed;

    public MainViewModel(
        string dataDirectory,
        int modDatabaseSearchResultLimit,
        int newModsRecentMonths,
        ModDatabaseAutoLoadMode initialModDatabaseAutoLoadMode = ModDatabaseAutoLoadMode.TotalDownloads,
        string? gameDirectory = null,
        bool excludeInstalledModDatabaseResults = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        DataDirectory = Path.GetFullPath(dataDirectory);

        _settingsStore = new ClientSettingsStore(DataDirectory);
        _discoveryService = new ModDiscoveryService(_settingsStore);
        _databaseService = new ModDatabaseService();
        _modDatabaseSearchResultLimit = Math.Clamp(modDatabaseSearchResultLimit, 1, MaxModDatabaseResultLimit);
        _modDatabaseCurrentResultLimit = _modDatabaseSearchResultLimit;
        _newModsRecentMonths = Math.Clamp(newModsRecentMonths <= 0 ? 1 : newModsRecentMonths, 1, MaxNewModsRecentMonths);
        _modDatabaseAutoLoadMode = NormalizeModDatabaseAutoLoadMode(initialModDatabaseAutoLoadMode);
        _installedGameVersion = VintageStoryVersionLocator.GetInstalledVersion(gameDirectory);
        _modsWatcher = new ModDirectoryWatcher(_discoveryService);

        ModsView = CollectionViewSource.GetDefaultView(_mods);
        ModsView.Filter = FilterMod;
        SearchResultsView = CollectionViewSource.GetDefaultView(_searchResults);
        CloudModlistsView = CollectionViewSource.GetDefaultView(_cloudModlists);
        InstalledTagFilters = new ReadOnlyObservableCollection<TagFilterOptionViewModel>(_installedTagFilters);
        ModDatabaseTagFilters = new ReadOnlyObservableCollection<TagFilterOptionViewModel>(_modDatabaseTagFilters);
        _mods.CollectionChanged += OnModsCollectionChanged;
        _searchResults.CollectionChanged += OnSearchResultsCollectionChanged;
        _sortOptions = new ObservableCollection<SortOption>(CreateSortOptions());
        SortOptions = new ReadOnlyObservableCollection<SortOption>(_sortOptions);
        SelectedSortOption = SortOptions.FirstOrDefault();
        SelectedSortOption?.Apply(ModsView);

        _excludeInstalledModDatabaseResults = excludeInstalledModDatabaseResults;

        _clearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => HasSearchText);
        ClearSearchCommand = _clearSearchCommand;

        _showInstalledModsCommand = new RelayCommand(() => SetViewSection(ViewSection.InstalledMods));
        _showModDatabaseCommand = new RelayCommand(() => SetViewSection(ViewSection.ModDatabase));
        _showCloudModlistsCommand = new RelayCommand(
            () => SetViewSection(ViewSection.CloudModlists),
            () => !InternetAccessManager.IsInternetAccessDisabled);
        _loadMoreModDatabaseResultsCommand = new RelayCommand(
            LoadMoreModDatabaseResults,
            () => SearchModDatabase
                && !IsModDatabaseLoading
                && IsLoadMoreModDatabaseButtonVisible
                && IsLoadMoreModDatabaseScrollThresholdReached);
        ShowInstalledModsCommand = _showInstalledModsCommand;
        ShowModDatabaseCommand = _showModDatabaseCommand;
        ShowCloudModlistsCommand = _showCloudModlistsCommand;
        LoadMoreModDatabaseResultsCommand = _loadMoreModDatabaseResultsCommand;

        RefreshCommand = new AsyncRelayCommand(LoadModsAsync);
        SetStatus("Ready.", false);

        InternetAccessManager.InternetAccessChanged += OnInternetAccessChanged;
    }

    public string DataDirectory { get; }

    public string? PlayerUid => _settingsStore.PlayerUid;

    public string? PlayerName => _settingsStore.PlayerName;

    public ICollectionView ModsView { get; }

    public ICollectionView SearchResultsView { get; }

    public ICollectionView CloudModlistsView { get; }

    public ReadOnlyObservableCollection<TagFilterOptionViewModel> InstalledTagFilters { get; }

    public ReadOnlyObservableCollection<TagFilterOptionViewModel> ModDatabaseTagFilters { get; }

    public ICollectionView CurrentModsView => _viewSection switch
    {
        ViewSection.ModDatabase => SearchResultsView,
        ViewSection.CloudModlists => CloudModlistsView,
        _ => ModsView
    };

    public bool CanAccessCloudModlists => !InternetAccessManager.IsInternetAccessDisabled;

    public ReadOnlyObservableCollection<SortOption> SortOptions { get; }

    public SortOption? SelectedSortOption
    {
        get => _selectedSortOption;
        set
        {
            if (SetProperty(ref _selectedSortOption, value))
            {
                value?.Apply(ModsView);
            }
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
        private set => SetProperty(ref _isLoadingMods, value);
    }

    public bool IsLoadingModDetails
    {
        get => _isLoadingModDetails;
        private set => SetProperty(ref _isLoadingModDetails, value);
    }

    public bool IsCompactView
    {
        get => _isCompactView;
        set => SetProperty(ref _isCompactView, value);
    }

    public bool UseModDbDesignView
    {
        get => _useModDbDesignView;
        set => SetProperty(ref _useModDbDesignView, value);
    }

    public bool HasSelectedTags
    {
        get => _hasSelectedTags;
        private set
        {
            if (SetProperty(ref _hasSelectedTags, value))
            {
                OnPropertyChanged(nameof(TagsColumnHeader));
            }
        }
    }

    public string TagsColumnHeader => HasSelectedTags ? "Tags (*)" : "Tags";

    public bool ExcludeInstalledModDatabaseResults
    {
        get => _excludeInstalledModDatabaseResults;
        set
        {
            if (SetProperty(ref _excludeInstalledModDatabaseResults, value))
            {
                OnPropertyChanged(nameof(IncludeInstalledModDatabaseResults));

                if (_viewSection == ViewSection.ModDatabase)
                {
                    TriggerModDatabaseSearch();
                }
            }
        }
    }

    public bool IncludeInstalledModDatabaseResults
    {
        get => !ExcludeInstalledModDatabaseResults;
        set => ExcludeInstalledModDatabaseResults = !value;
    }

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
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public ModListItemViewModel? SelectedMod
    {
        get => _selectedMod;
        private set
        {
            if (SetProperty(ref _selectedMod, value))
            {
                OnPropertyChanged(nameof(HasSelectedMod));
            }
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

    public bool SearchModDatabase
    {
        get => _viewSection == ViewSection.ModDatabase;
        set
        {
            if (value)
            {
                SetViewSection(ViewSection.ModDatabase);
            }
            else if (_viewSection == ViewSection.ModDatabase)
            {
                SetViewSection(ViewSection.InstalledMods);
            }
        }
    }

    public IRelayCommand ShowInstalledModsCommand { get; }

    public IRelayCommand ShowModDatabaseCommand { get; }

    public IRelayCommand ShowCloudModlistsCommand { get; }

    public IRelayCommand LoadMoreModDatabaseResultsCommand { get; }

    public bool IsViewingCloudModlists => _viewSection == ViewSection.CloudModlists;

    public bool IsViewingInstalledMods => _viewSection == ViewSection.InstalledMods;

    public bool HasCloudModlists => _cloudModlists.Count > 0;

    public bool CanLoadMoreModDatabaseResults
    {
        get => _canLoadMoreModDatabaseResults;
        private set
        {
            if (SetProperty(ref _canLoadMoreModDatabaseResults, value))
            {
                NotifyLoadMoreCommandCanExecuteChanged();
            }
        }
    }

    public bool IsLoadMoreModDatabaseButtonVisible
    {
        get => _isLoadMoreModDatabaseButtonVisible;
        private set
        {
            if (SetProperty(ref _isLoadMoreModDatabaseButtonVisible, value))
            {
                if (!value && _isLoadMoreModDatabaseScrollThresholdReached)
                {
                    IsLoadMoreModDatabaseScrollThresholdReached = false;
                }

                NotifyLoadMoreCommandCanExecuteChanged();
            }
        }
    }

    public bool IsLoadMoreModDatabaseScrollThresholdReached
    {
        get => _isLoadMoreModDatabaseScrollThresholdReached;
        set
        {
            if (SetProperty(ref _isLoadMoreModDatabaseScrollThresholdReached, value))
            {
                NotifyLoadMoreCommandCanExecuteChanged();
            }
        }
    }

    public bool IsModDatabaseLoading
    {
        get => _isModDatabaseLoading;
        private set
        {
            if (SetProperty(ref _isModDatabaseLoading, value))
            {
                NotifyLoadMoreCommandCanExecuteChanged();
            }
        }
    }

    public bool IsTotalDownloadsMode
    {
        get => _modDatabaseAutoLoadMode == ModDatabaseAutoLoadMode.TotalDownloads;
        set
        {
            if (value)
            {
                SetAutoLoadMode(ModDatabaseAutoLoadMode.TotalDownloads);
            }
        }
    }

    public bool IsDownloadsLastThirtyDaysMode
    {
        get => _modDatabaseAutoLoadMode == ModDatabaseAutoLoadMode.DownloadsLastThirtyDays;
        set
        {
            if (value)
            {
                SetAutoLoadMode(ModDatabaseAutoLoadMode.DownloadsLastThirtyDays);
            }
        }
    }

    public bool IsDownloadsNewModsRecentMonthsMode
    {
        get => _modDatabaseAutoLoadMode == ModDatabaseAutoLoadMode.DownloadsNewModsRecentMonths;
        set
        {
            if (value)
            {
                SetAutoLoadMode(ModDatabaseAutoLoadMode.DownloadsNewModsRecentMonths);
            }
        }
    }

    public ModDatabaseAutoLoadMode ModDatabaseAutoLoadMode => _modDatabaseAutoLoadMode;

    public string DownloadsNewModsRecentMonthsLabel =>
        $"Created {BuildRecentMonthsPhrase()}";

    public bool IsShowingRecentDownloadMetric => SearchModDatabase && !HasSearchText
        && _modDatabaseAutoLoadMode == ModDatabaseAutoLoadMode.DownloadsLastThirtyDays;

    public string DownloadsColumnHeader => IsShowingRecentDownloadMetric
        ? "Downloads (30 days)"
        : "Downloads";

    private void SetViewSection(ViewSection section)
    {
        if (_viewSection == section)
        {
            return;
        }

        if (section == ViewSection.CloudModlists && InternetAccessManager.IsInternetAccessDisabled)
        {
            SetStatus(InternetAccessDisabledStatusMessage, false);
            return;
        }

        _modDatabaseSearchCts?.Cancel();

        bool hadSearchText = !string.IsNullOrEmpty(SearchText);
        bool modDatabaseSearchTriggeredByClearing = false;

        _viewSection = section;

        if (hadSearchText)
        {
            SearchText = string.Empty;
            modDatabaseSearchTriggeredByClearing = section == ViewSection.ModDatabase;
        }

        CanLoadMoreModDatabaseResults = false;

        if (section != ViewSection.ModDatabase)
        {
            IsLoadMoreModDatabaseButtonVisible = false;
            _hasRequestedAdditionalModDatabaseResults = false;
        }

        switch (section)
        {
            case ViewSection.ModDatabase:
                ClearSearchResults();
                SelectedMod = null;
                if (!modDatabaseSearchTriggeredByClearing)
                {
                    TriggerModDatabaseSearch();
                }
                break;
            case ViewSection.InstalledMods:
                ClearSearchResults();
                SelectedMod = null;
                SelectedSortOption?.Apply(ModsView);
                ModsView.Refresh();
                SetStatus("Showing installed mods.", false);
                break;
            case ViewSection.CloudModlists:
                ClearSearchResults();
                SelectedMod = null;
                SetStatus("Showing cloud modlists.", false);
                break;
        }

        OnPropertyChanged(nameof(SearchModDatabase));
        OnPropertyChanged(nameof(IsViewingCloudModlists));
        OnPropertyChanged(nameof(IsViewingInstalledMods));
        OnPropertyChanged(nameof(CurrentModsView));
        OnPropertyChanged(nameof(IsShowingRecentDownloadMetric));
        OnPropertyChanged(nameof(DownloadsColumnHeader));

        NotifyLoadMoreCommandCanExecuteChanged();
    }

    private void NotifyLoadMoreCommandCanExecuteChanged()
    {
        _loadMoreModDatabaseResultsCommand?.NotifyCanExecuteChanged();
    }

    private void SetAutoLoadMode(ModDatabaseAutoLoadMode mode)
    {
        if (_modDatabaseAutoLoadMode == mode)
        {
            return;
        }

        _modDatabaseAutoLoadMode = mode;
        OnPropertyChanged(nameof(ModDatabaseAutoLoadMode));
        OnPropertyChanged(nameof(IsTotalDownloadsMode));
        OnPropertyChanged(nameof(IsDownloadsLastThirtyDaysMode));
        OnPropertyChanged(nameof(IsDownloadsNewModsRecentMonthsMode));
        OnPropertyChanged(nameof(IsShowingRecentDownloadMetric));
        OnPropertyChanged(nameof(DownloadsColumnHeader));

        if (SearchModDatabase && !HasSearchText)
        {
            ClearSearchResults();
            TriggerModDatabaseSearch();
        }
    }

    private static ModDatabaseAutoLoadMode NormalizeModDatabaseAutoLoadMode(ModDatabaseAutoLoadMode mode)
    {
        return Enum.IsDefined(typeof(ModDatabaseAutoLoadMode), mode)
            ? mode
            : ModDatabaseAutoLoadMode.TotalDownloads;
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            string newValue = value ?? string.Empty;
            if (!SetProperty(ref _searchText, newValue))
            {
                return;
            }

            bool hadSearchTokens = _searchTokens.Length > 0;
            _searchTokens = CreateSearchTokens(newValue);
            bool hasSearchTokens = _searchTokens.Length > 0;

            OnPropertyChanged(nameof(HasSearchText));
            OnPropertyChanged(nameof(IsShowingRecentDownloadMetric));
            OnPropertyChanged(nameof(DownloadsColumnHeader));
            _clearSearchCommand.NotifyCanExecuteChanged();
            if (SearchModDatabase)
            {
                if (!hasSearchTokens && hadSearchTokens)
                {
                    ClearSearchResults();
                }

                TriggerModDatabaseSearch();
            }
            else
            {
                ModsView.Refresh();
            }
        }
    }

    public bool HasSearchText => _searchTokens.Length > 0;

    public int TotalMods
    {
        get => _totalMods;
        private set
        {
            if (SetProperty(ref _totalMods, value))
            {
                OnPropertyChanged(nameof(SummaryText));
            }
        }
    }

    public int ActiveMods
    {
        get => _activeMods;
        private set
        {
            if (SetProperty(ref _activeMods, value))
            {
                OnPropertyChanged(nameof(SummaryText));
            }
        }
    }

    public string SummaryText => TotalMods == 0
        ? "No mods found."
        : $"{ActiveMods} active of {TotalMods} mods";

    public string NoModsFoundMessage =>
        $"No mods found. If this is unexpected, verify that your VintageStoryData folder is correctly set: {DataDirectory}. You can change it in the File Menu.";

    public IRelayCommand ClearSearchCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public Task InitializeAsync() => LoadModsAsync();

    public void ReplaceCloudModlists(IEnumerable<CloudModlistListEntry>? entries)
    {
        _cloudModlists.Clear();

        if (entries is not null)
        {
            foreach (var entry in entries)
            {
                if (entry is not null)
                {
                    _cloudModlists.Add(entry);
                }
            }
        }

        CloudModlistsView.Refresh();
        OnPropertyChanged(nameof(HasCloudModlists));
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

    public bool TryGetInstalledModDisplayName(string? modId, out string? displayName)
    {
        displayName = null;

        if (string.IsNullOrWhiteSpace(modId))
        {
            return false;
        }

        foreach (ModListItemViewModel mod in _mods)
        {
            if (mod is null || string.IsNullOrWhiteSpace(mod.ModId))
            {
                continue;
            }

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
                if (mod is null || string.IsNullOrWhiteSpace(mod.ModId))
                {
                    continue;
                }

                string normalizedId = mod.ModId.Trim();
                if (!installedMods.ContainsKey(normalizedId))
                {
                    string? version = string.IsNullOrWhiteSpace(mod.Version) ? null : mod.Version!.Trim();
                    installedMods.Add(normalizedId, version);
                }
            }

            success = await Task.Run(() =>
            {
                foreach (var state in preset.ModStates)
                {
                    if (state is null || string.IsNullOrWhiteSpace(state.ModId) || state.IsActive is not bool desiredState)
                    {
                        continue;
                    }

                    string normalizedId = state.ModId.Trim();
                    if (!installedMods.TryGetValue(normalizedId, out string? installedVersion))
                    {
                        continue;
                    }

                    string? recordedVersion = string.IsNullOrWhiteSpace(state.Version)
                        ? null
                        : state.Version!.Trim();

                    if (desiredState)
                    {
                        var versionsToActivate = new HashSet<string?>(StringComparer.OrdinalIgnoreCase)
                        {
                            null
                        };

                        if (!string.IsNullOrWhiteSpace(installedVersion))
                        {
                            versionsToActivate.Add(installedVersion);
                        }

                        if (!string.IsNullOrWhiteSpace(recordedVersion))
                        {
                            versionsToActivate.Add(recordedVersion);
                        }

                        foreach (string? versionKey in versionsToActivate)
                        {
                            if (!_settingsStore.TrySetActive(normalizedId, versionKey, true, out string? error))
                            {
                                localError = error;
                                return false;
                            }
                        }
                    }
                    else
                    {
                        if (!_settingsStore.TrySetActive(normalizedId, null, false, out string? error))
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
            IReadOnlyList<string> entries = preset.DisabledEntries ?? Array.Empty<string>();

            success = await Task.Run(() =>
            {
                bool result = _settingsStore.TryApplyDisabledEntries(entries, out var error);
                localError = error;
                return result;
            });
        }

        if (!success)
        {
            string message = string.IsNullOrWhiteSpace(localError)
                ? $"Failed to apply preset \"{preset.Name}\"."
                : localError!;
            SetStatus(message, true);
            return false;
        }

        foreach (var mod in _mods)
        {
            bool isDisabled = _settingsStore.IsDisabled(mod.ModId, mod.Version);
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
        if (SearchModDatabase)
        {
            _modDatabaseSearchCts?.Cancel();

            if (InternetAccessManager.IsInternetAccessDisabled)
            {
                ClearSearchResults();
                SetStatus(InternetAccessDisabledStatusMessage, false);
            }
            else
            {
                TriggerModDatabaseSearch();
            }
        }

        if (_modEntriesBySourcePath.Count > 0)
        {
            QueueDatabaseInfoRefresh(_modEntriesBySourcePath.Values.ToArray());
        }
    }

    internal void SetSelectedMod(ModListItemViewModel? mod, int selectionCount)
    {
        HasSelectedMods = selectionCount > 0;
        HasMultipleSelectedMods = selectionCount > 1;
        SelectedMod = mod;
    }

    internal void RemoveSearchResult(ModListItemViewModel mod)
    {
        if (mod is null)
        {
            return;
        }

        _searchResults.Remove(mod);

        if (ReferenceEquals(SelectedMod, mod))
        {
            SelectedMod = null;
        }
    }

    internal ModListItemViewModel? FindInstalledModById(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            return null;
        }

        foreach (var mod in _mods)
        {
            if (string.Equals(mod.ModId, modId, StringComparison.OrdinalIgnoreCase))
            {
                return mod;
            }
        }

        return null;
    }

    internal ModListItemViewModel? FindModBySourcePath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        return _modViewModelsBySourcePath.TryGetValue(sourcePath, out var viewModel)
            ? viewModel
            : null;
    }

    internal string? InstalledGameVersion => _installedGameVersion;

    internal async Task<bool> PreserveActivationStateAsync(string modId, string? previousVersion, string? newVersion, bool wasActive)
    {
        string? localError = null;

        bool success = await Task.Run(() =>
            _settingsStore.TryUpdateDisabledEntry(modId, previousVersion, newVersion, shouldDisable: !wasActive, out localError));

        if (!success)
        {
            string message = string.IsNullOrWhiteSpace(localError)
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
        bool success = await Task.Run(() =>
        {
            bool result = _settingsStore.TrySetActive(mod.ModId, mod.Version, isActive, out var error);
            localError = error;
            return result;
        });

        if (!success)
        {
            string message = string.IsNullOrWhiteSpace(localError)
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
        if (_mods.Count == 0)
        {
            return Array.Empty<string>();
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in _mods)
        {
            if (mod is null)
            {
                continue;
            }

            string? sourcePath = mod.SourcePath;
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            if (mod.HasLoadError || mod.DependencyHasErrors || mod.MissingDependencies.Count > 0)
            {
                result.Add(sourcePath);
            }
        }

        return result.Count == 0 ? Array.Empty<string>() : result.ToArray();
    }

    internal async Task RefreshModsWithErrorsAsync(IReadOnlyCollection<string>? includeSourcePaths = null)
    {
        if (_mods.Count == 0 && _modEntriesBySourcePath.Count == 0)
        {
            return;
        }

        _modsWatcher.EnsureWatchers();
        ModDirectoryChangeSet changeSet = _modsWatcher.ConsumeChanges();
        if (changeSet.RequiresFullRescan)
        {
            await LoadModsAsync().ConfigureAwait(true);
            return;
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (includeSourcePaths is { Count: > 0 })
        {
            foreach (string path in includeSourcePaths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    candidates.Add(path);
                }
            }
        }

        foreach (string path in changeSet.Paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                candidates.Add(path);
            }
        }

        foreach (var mod in _mods)
        {
            if (mod is null || string.IsNullOrWhiteSpace(mod.SourcePath))
            {
                continue;
            }

            if (mod.HasLoadError || mod.DependencyHasErrors || mod.MissingDependencies.Count > 0)
            {
                candidates.Add(mod.SourcePath);
            }
        }

        if (_modEntriesBySourcePath.Count > 0)
        {
            var allEntries = new List<ModEntry>(_modEntriesBySourcePath.Values);
            await Task.Run(() => _discoveryService.ApplyLoadStatuses(allEntries)).ConfigureAwait(true);

            foreach (var entry in allEntries)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.SourcePath))
                {
                    continue;
                }

                if (entry.HasLoadError
                    || entry.DependencyHasErrors
                    || (entry.MissingDependencies?.Count ?? 0) > 0)
                {
                    candidates.Add(entry.SourcePath);
                }
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        string? previousSelection = SelectedMod?.SourcePath;

        Dictionary<string, ModEntry> existingEntriesSnapshot = new(_modEntriesBySourcePath, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, ModEntry?> reloadResults = await Task
            .Run(() => LoadChangedModEntries(candidates, existingEntriesSnapshot))
            .ConfigureAwait(true);

        var refreshedEntries = new List<ModEntry>(reloadResults.Count);

        foreach (var pair in reloadResults)
        {
            string path = pair.Key;
            ModEntry? entry = pair.Value;

            if (entry == null)
            {
                _modEntriesBySourcePath.Remove(path);
                continue;
            }

            _modEntriesBySourcePath[path] = entry;
            refreshedEntries.Add(entry);
        }

        if (_modEntriesBySourcePath.Count > 0)
        {
            var updatedEntries = new List<ModEntry>(_modEntriesBySourcePath.Values);
            await Task.Run(() => _discoveryService.ApplyLoadStatuses(updatedEntries)).ConfigureAwait(true);
        }

        ApplyPartialUpdates(reloadResults, previousSelection);

        if (refreshedEntries.Count > 0)
        {
            QueueDatabaseInfoRefresh(refreshedEntries);
        }

        TotalMods = _mods.Count;
        UpdateActiveCount();
        SelectedSortOption?.Apply(ModsView);
        ModsView.Refresh();
        await UpdateModsStateSnapshotAsync().ConfigureAwait(true);
    }

    private async Task LoadModsAsync()
    {
        if (IsLoadingMods)
        {
            return;
        }

        IsLoadingMods = true;
        using var busyScope = BeginBusyScope();
        SetStatus("Loading mods...", false);

        try
        {
            _modsWatcher.EnsureWatchers();
            ModDirectoryChangeSet changeSet = _modsWatcher.ConsumeChanges();
            bool requiresFullReload = _mods.Count == 0
                || changeSet.RequiresFullRescan
                || changeSet.Paths.Count == 0;

            string? previousSelection = SelectedMod?.SourcePath;

            if (requiresFullReload)
            {
                await PerformFullReloadAsync(previousSelection).ConfigureAwait(true);
            }
            else
            {
                Dictionary<string, ModEntry> existingEntriesSnapshot = new(_modEntriesBySourcePath, StringComparer.OrdinalIgnoreCase);
                var reloadResults = await Task.Run(() => LoadChangedModEntries(changeSet.Paths, existingEntriesSnapshot));

                foreach (var pair in reloadResults)
                {
                    if (pair.Value == null)
                    {
                        _modEntriesBySourcePath.Remove(pair.Key);
                    }
                    else
                    {
                        _modEntriesBySourcePath[pair.Key] = pair.Value;
                    }
                }

                var updatedEntries = new List<ModEntry>();
                foreach (var entry in reloadResults.Values)
                {
                    if (entry != null)
                    {
                        updatedEntries.Add(entry);
                    }
                }

                var allEntries = new List<ModEntry>(_modEntriesBySourcePath.Values);
                await Task.Run(() => _discoveryService.ApplyLoadStatuses(allEntries)).ConfigureAwait(true);

                ApplyPartialUpdates(reloadResults, previousSelection);

                if (updatedEntries.Count > 0)
                {
                    QueueDatabaseInfoRefresh(updatedEntries);
                }
            }

            TotalMods = _mods.Count;
            UpdateActiveCount();
            SelectedSortOption?.Apply(ModsView);
            ModsView.Refresh();
            await UpdateModsStateSnapshotAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to load mods: {ex.Message}", true);
        }
        finally
        {
            IsLoadingMods = false;
        }
    }

    private async Task PerformFullReloadAsync(string? previousSelection)
    {
        var entries = new List<ModEntry>();
        Dictionary<string, ModEntry> previousEntries = new(StringComparer.OrdinalIgnoreCase);

        await InvokeOnDispatcherAsync(() =>
        {
            foreach (var pair in _modEntriesBySourcePath)
            {
                previousEntries[pair.Key] = pair.Value;
            }

            _modEntriesBySourcePath.Clear();
            _modViewModelsBySourcePath.Clear();
            _mods.Clear();
            SelectedMod = null;
            TotalMods = 0;
        }, CancellationToken.None).ConfigureAwait(true);

        await foreach (var batch in _discoveryService.LoadModsIncrementallyAsync(InstalledModsIncrementalBatchSize))
        {
            if (batch.Count == 0)
            {
                continue;
            }

            entries.AddRange(batch);

            await InvokeOnDispatcherAsync(() =>
            {
                foreach (var entry in batch)
                {
                    ResetCalculatedModState(entry);
                    if (previousEntries.TryGetValue(entry.SourcePath, out var previous))
                    {
                        CopyTransientModState(previous, entry);
                    }

                    _modEntriesBySourcePath[entry.SourcePath] = entry;
                    var viewModel = CreateModViewModel(entry);
                    _modViewModelsBySourcePath[entry.SourcePath] = viewModel;
                    _mods.Add(viewModel);
                }

                TotalMods = _mods.Count;
            }, CancellationToken.None).ConfigureAwait(true);

            await Task.Yield();
        }

        if (entries.Count > 0)
        {
            await Task.Run(() => _discoveryService.ApplyLoadStatuses(entries)).ConfigureAwait(true);
        }

        await InvokeOnDispatcherAsync(() =>
        {
            foreach (var entry in entries)
            {
                if (_modViewModelsBySourcePath.TryGetValue(entry.SourcePath, out var viewModel))
                {
                    viewModel.UpdateLoadError(entry.LoadError);
                    viewModel.UpdateDependencyIssues(entry.DependencyHasErrors, entry.MissingDependencies);
                }
            }

            TotalMods = _mods.Count;

            if (!string.IsNullOrWhiteSpace(previousSelection)
                && _modViewModelsBySourcePath.TryGetValue(previousSelection, out var selected))
            {
                SelectedMod = selected;
            }
            else
            {
                SelectedMod = null;
            }

            if (entries.Count > 0)
            {
                QueueDatabaseInfoRefresh(entries);
            }

            UpdateLoadedModsStatus();
        }, CancellationToken.None).ConfigureAwait(true);
    }

    public async Task<bool> CheckForModStateChangesAsync()
    {
        _modsWatcher.EnsureWatchers();

        if (_modsWatcher.HasPendingChanges)
        {
            return true;
        }

        if (_modsWatcher.IsWatching)
        {
            return false;
        }

        string? fingerprint = await CaptureModsStateFingerprintAsync().ConfigureAwait(false);
        if (fingerprint is null)
        {
            return false;
        }

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

    private ModListItemViewModel CreateModViewModel(ModEntry entry)
    {
        bool isActive = !_settingsStore.IsDisabled(entry.ModId, entry.Version);
        string location = GetDisplayPath(entry.SourcePath);
        return new ModListItemViewModel(entry, isActive, location, ApplyActivationChangeAsync, _installedGameVersion, true);
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

        string? fingerprint = await CaptureModsStateFingerprintAsync().ConfigureAwait(false);
        if (fingerprint is null)
        {
            return;
        }

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
    }

    private void ClearSearchResults()
    {
        if (_searchResults.Count == 0)
        {
            SelectedMod = null;
            return;
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
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
        CanLoadMoreModDatabaseResults = false;
    }

    private void OnModsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (ModListItemViewModel mod in EnumerateModItems(e.NewItems))
                {
                    AttachInstalledMod(mod);
                }
                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (ModListItemViewModel mod in EnumerateModItems(e.OldItems))
                {
                    DetachInstalledMod(mod);
                }
                break;
            case NotifyCollectionChangedAction.Replace:
                foreach (ModListItemViewModel mod in EnumerateModItems(e.OldItems))
                {
                    DetachInstalledMod(mod);
                }

                foreach (ModListItemViewModel mod in EnumerateModItems(e.NewItems))
                {
                    AttachInstalledMod(mod);
                }
                break;
            case NotifyCollectionChangedAction.Reset:
                DetachAllInstalledMods();
                foreach (ModListItemViewModel mod in _mods)
                {
                    AttachInstalledMod(mod);
                }
                break;
        }

        ScheduleInstalledTagFilterRefresh();
    }

    private void OnSearchResultsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (ModListItemViewModel mod in EnumerateModItems(e.NewItems))
                {
                    AttachSearchResult(mod);
                }
                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (ModListItemViewModel mod in EnumerateModItems(e.OldItems))
                {
                    DetachSearchResult(mod);
                }
                break;
            case NotifyCollectionChangedAction.Replace:
                foreach (ModListItemViewModel mod in EnumerateModItems(e.OldItems))
                {
                    DetachSearchResult(mod);
                }

                foreach (ModListItemViewModel mod in EnumerateModItems(e.NewItems))
                {
                    AttachSearchResult(mod);
                }
                break;
            case NotifyCollectionChangedAction.Reset:
                DetachAllSearchResults();
                foreach (ModListItemViewModel mod in _searchResults)
                {
                    AttachSearchResult(mod);
                }
                break;
        }

        ScheduleModDatabaseTagRefresh();
    }

    private static IEnumerable<ModListItemViewModel> EnumerateModItems(IList? items)
    {
        if (items is null)
        {
            yield break;
        }

        foreach (object item in items)
        {
            if (item is ModListItemViewModel mod)
            {
                yield return mod;
            }
        }
    }

    private static IEnumerable<string> EnumerateModTags(IEnumerable<ModListItemViewModel> mods)
    {
        foreach (ModListItemViewModel mod in mods)
        {
            if (mod.DatabaseTags is not { Count: > 0 } tags)
            {
                continue;
            }

            foreach (string tag in tags)
            {
                yield return tag;
            }
        }
    }

    private void AttachInstalledMod(ModListItemViewModel mod)
    {
        if (_installedModSubscriptions.Add(mod))
        {
            mod.PropertyChanged += OnInstalledModPropertyChanged;
        }
    }

    private void DetachInstalledMod(ModListItemViewModel mod)
    {
        if (_installedModSubscriptions.Remove(mod))
        {
            mod.PropertyChanged -= OnInstalledModPropertyChanged;
        }
    }

    private void DetachAllInstalledMods()
    {
        if (_installedModSubscriptions.Count == 0)
        {
            return;
        }

        foreach (ModListItemViewModel mod in _installedModSubscriptions)
        {
            mod.PropertyChanged -= OnInstalledModPropertyChanged;
        }

        _installedModSubscriptions.Clear();
    }

    private void AttachSearchResult(ModListItemViewModel mod)
    {
        if (_searchResultSubscriptions.Add(mod))
        {
            mod.PropertyChanged += OnSearchResultPropertyChanged;
        }
    }

    private void DetachSearchResult(ModListItemViewModel mod)
    {
        if (_searchResultSubscriptions.Remove(mod))
        {
            mod.PropertyChanged -= OnSearchResultPropertyChanged;
        }
    }

    private void DetachAllSearchResults()
    {
        if (_searchResultSubscriptions.Count == 0)
        {
            return;
        }

        foreach (ModListItemViewModel mod in _searchResultSubscriptions)
        {
            mod.PropertyChanged -= OnSearchResultPropertyChanged;
        }

        _searchResultSubscriptions.Clear();
    }

    private void OnInstalledModPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(ModListItemViewModel.DatabaseTags), StringComparison.Ordinal))
        {
            return;
        }

        ScheduleInstalledTagFilterRefresh();
    }

    private void OnSearchResultPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(ModListItemViewModel.DatabaseTags), StringComparison.Ordinal))
        {
            return;
        }

        ScheduleModDatabaseTagRefresh();
    }

    private void ScheduleInstalledTagFilterRefresh()
    {
        if (_isInstalledTagRefreshPending)
        {
            return;
        }

        _isInstalledTagRefreshPending = true;

        async void ExecuteAsync()
        {
            try
            {
                List<string> tagSnapshot = EnumerateModTags(_mods).ToList();
                List<string> selectedSnapshot = _selectedInstalledTags.ToList();
                List<string> normalized = await Task.Run(
                        () => NormalizeAndSortTags(tagSnapshot.Concat(selectedSnapshot)))
                    .ConfigureAwait(false);

                await InvokeOnDispatcherAsync(
                        () => ApplyInstalledTagFilters(normalized),
                        CancellationToken.None,
                        DispatcherPriority.ContextIdle)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to refresh installed mod tags: {ex}");
            }
            finally
            {
                _isInstalledTagRefreshPending = false;
            }
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(ExecuteAsync));
        }
        else
        {
            ExecuteAsync();
        }
    }

    private void ScheduleModDatabaseTagRefresh()
    {
        if (_isModDatabaseTagRefreshPending)
        {
            return;
        }

        _isModDatabaseTagRefreshPending = true;

        async void ExecuteAsync()
        {
            try
            {
                IReadOnlyList<string> existing = _modDatabaseAvailableTags;
                List<string> tagSnapshot = EnumerateModTags(_searchResults).ToList();
                List<string> selectedSnapshot = _selectedModDatabaseTags.ToList();
                List<string> normalized = await Task.Run(
                        () => NormalizeAndSortTags(existing.Concat(tagSnapshot).Concat(selectedSnapshot)))
                    .ConfigureAwait(false);

                await InvokeOnDispatcherAsync(
                        () => ApplyNormalizedModDatabaseAvailableTags(normalized),
                        CancellationToken.None,
                        DispatcherPriority.ContextIdle)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to refresh mod database tags: {ex}");
            }
            finally
            {
                _isModDatabaseTagRefreshPending = false;
            }
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(ExecuteAsync));
        }
        else
        {
            ExecuteAsync();
        }
    }

    private void ResetInstalledTagFilters(IEnumerable<string> tags)
    {
        List<string> normalized = NormalizeAndSortTags(tags.Concat(_selectedInstalledTags));
        ApplyInstalledTagFilters(normalized);
    }

    private void ApplyInstalledTagFilters(IReadOnlyList<string> normalized)
    {
        _suppressInstalledTagFilterSelectionChanges = true;
        try
        {
            foreach (TagFilterOptionViewModel filter in _installedTagFilters)
            {
                filter.PropertyChanged -= OnInstalledTagFilterPropertyChanged;
            }

            _installedTagFilters.Clear();

            var selected = new HashSet<string>(_selectedInstalledTags, StringComparer.OrdinalIgnoreCase);
            foreach (string tag in normalized)
            {
                bool isSelected = selected.Contains(tag);
                var option = new TagFilterOptionViewModel(tag, isSelected);
                option.PropertyChanged += OnInstalledTagFilterPropertyChanged;
                _installedTagFilters.Add(option);
            }
        }
        finally
        {
            _suppressInstalledTagFilterSelectionChanges = false;
        }

        SyncSelectedTags(_installedTagFilters, _selectedInstalledTags);
        UpdateHasSelectedTags();
        ModsView.Refresh();
    }

    private void ResetModDatabaseTagFilters(IEnumerable<string> tags)
    {
        List<string> normalized = NormalizeAndSortTags(tags.Concat(_selectedModDatabaseTags));
        ApplyModDatabaseTagFilters(normalized);
    }

    private void ApplyModDatabaseTagFilters(IReadOnlyList<string> normalized)
    {
        _suppressModDatabaseTagFilterSelectionChanges = true;
        try
        {
            foreach (TagFilterOptionViewModel filter in _modDatabaseTagFilters)
            {
                filter.PropertyChanged -= OnModDatabaseTagFilterPropertyChanged;
            }

            _modDatabaseTagFilters.Clear();

            var selected = new HashSet<string>(_selectedModDatabaseTags, StringComparer.OrdinalIgnoreCase);
            foreach (string tag in normalized)
            {
                bool isSelected = selected.Contains(tag);
                var option = new TagFilterOptionViewModel(tag, isSelected);
                option.PropertyChanged += OnModDatabaseTagFilterPropertyChanged;
                _modDatabaseTagFilters.Add(option);
            }
        }
        finally
        {
            _suppressModDatabaseTagFilterSelectionChanges = false;
        }

        SyncSelectedTags(_modDatabaseTagFilters, _selectedModDatabaseTags);
        UpdateHasSelectedTags();
    }

    private void ApplyModDatabaseAvailableTags(IEnumerable<string> tags)
    {
        List<string> normalized = NormalizeAndSortTags(tags.Concat(_selectedModDatabaseTags));
        ApplyNormalizedModDatabaseAvailableTags(normalized);
    }

    private void ApplyNormalizedModDatabaseAvailableTags(IReadOnlyList<string> normalized)
    {
        if (TagListsEqual(_modDatabaseAvailableTags, normalized))
        {
            return;
        }

        _modDatabaseAvailableTags = normalized;
        ApplyModDatabaseTagFilters(_modDatabaseAvailableTags);
    }

    private void UpdateModDatabaseAvailableTagsFromViewModels()
    {
        ApplyModDatabaseAvailableTags(_modDatabaseAvailableTags.Concat(EnumerateModTags(_searchResults)));
    }

    private void OnInstalledTagFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressInstalledTagFilterSelectionChanges)
        {
            return;
        }

        if (!string.Equals(e.PropertyName, nameof(TagFilterOptionViewModel.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        if (SyncSelectedTags(_installedTagFilters, _selectedInstalledTags))
        {
            UpdateHasSelectedTags();
            ModsView.Refresh();
        }
    }

    private void OnModDatabaseTagFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressModDatabaseTagFilterSelectionChanges)
        {
            return;
        }

        if (!string.Equals(e.PropertyName, nameof(TagFilterOptionViewModel.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        if (SyncSelectedTags(_modDatabaseTagFilters, _selectedModDatabaseTags))
        {
            UpdateHasSelectedTags();
            TriggerModDatabaseSearch();
        }
    }

    private static bool SyncSelectedTags(IEnumerable<TagFilterOptionViewModel> filters, List<string> target)
    {
        List<string> newSelection = filters
            .Where(filter => filter.IsSelected)
            .Select(filter => filter.Name)
            .ToList();

        if (TagListsEqual(target, newSelection))
        {
            return false;
        }

        target.Clear();
        target.AddRange(newSelection);
        return true;
    }

    private void UpdateHasSelectedTags()
    {
        HasSelectedTags = _selectedInstalledTags.Count > 0 || _selectedModDatabaseTags.Count > 0;
    }

    private static List<string> NormalizeAndSortTags(IEnumerable<string> tags)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            string trimmed = tag.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!map.ContainsKey(trimmed))
            {
                map[trimmed] = trimmed;
            }
        }

        var list = map.Values.ToList();
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private static bool TagListsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private IReadOnlyList<string> GetSelectedModDatabaseTagsSnapshot()
    {
        if (_selectedModDatabaseTags.Count == 0)
        {
            return Array.Empty<string>();
        }

        return _selectedModDatabaseTags.ToArray();
    }

    private static IReadOnlyList<string> CollectModDatabaseTags(IEnumerable<ModDatabaseSearchResult> results)
    {
        if (results is null)
        {
            return Array.Empty<string>();
        }

        return NormalizeAndSortTags(results.SelectMany(GetTagsForResult));
    }

    private static IEnumerable<string> GetTagsForResult(ModDatabaseSearchResult result)
    {
        if (result.DetailedInfo?.Tags is { Count: > 0 } detailed)
        {
            return detailed;
        }

        return result.Tags ?? Array.Empty<string>();
    }

    private static bool ContainsAllTags(IEnumerable<string>? sourceTags, IReadOnlyList<string> requiredTags)
    {
        if (requiredTags.Count == 0)
        {
            return true;
        }

        if (sourceTags is null)
        {
            return false;
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string tag in sourceTags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            string trimmed = tag.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            set.Add(trimmed);
        }

        if (set.Count == 0)
        {
            return false;
        }

        foreach (string required in requiredTags)
        {
            if (!set.Contains(required))
            {
                return false;
            }
        }

        return true;
    }

    private void TriggerModDatabaseSearch(bool preserveResultLimit = false)
    {
        if (!SearchModDatabase)
        {
            return;
        }

        if (!preserveResultLimit)
        {
            ResetModDatabaseResultLimit();
            _hasRequestedAdditionalModDatabaseResults = false;
            IsLoadMoreModDatabaseButtonVisible = true;
        }

        CanLoadMoreModDatabaseResults = false;

        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            ClearSearchResults();
            SetStatus(InternetAccessDisabledStatusMessage, false);
            return;
        }

        _modDatabaseSearchCts?.Cancel();

        var cts = new CancellationTokenSource();
        _modDatabaseSearchCts = cts;

        bool hasSearchTokens = HasSearchText;
        string statusMessage;
        if (hasSearchTokens)
        {
            statusMessage = "Searching the mod database...";
        }
        else
        {
            statusMessage = _modDatabaseAutoLoadMode switch
            {
                ModDatabaseAutoLoadMode.DownloadsLastThirtyDays => "Loading top mods from the last 30 days...",
                ModDatabaseAutoLoadMode.DownloadsNewModsRecentMonths =>
                    $"Loading most downloaded newly created mods {BuildRecentMonthsPhrase()}...",
                _ => "Loading most downloaded mods..."
            };
        }
        SetStatus(statusMessage, false);

        IReadOnlyList<string> requiredTags = GetSelectedModDatabaseTagsSnapshot();
        UpdateIsModDatabaseLoading(true);
        _ = RunModDatabaseSearchAsync(SearchText, hasSearchTokens, requiredTags, cts);
    }

    private void ResetModDatabaseResultLimit()
    {
        _modDatabaseCurrentResultLimit = Math.Clamp(_modDatabaseSearchResultLimit, 1, MaxModDatabaseResultLimit);
    }

    private void LoadMoreModDatabaseResults()
    {
        if (!SearchModDatabase)
        {
            return;
        }

        _hasRequestedAdditionalModDatabaseResults = true;

        int increment = Math.Max(_modDatabaseSearchResultLimit, 1);
        int nextLimit = _modDatabaseCurrentResultLimit;
        long candidateLimit = (long)_modDatabaseCurrentResultLimit + increment;
        if (candidateLimit >= MaxModDatabaseResultLimit)
        {
            nextLimit = MaxModDatabaseResultLimit;
        }
        else
        {
            nextLimit = (int)candidateLimit;
        }
        if (nextLimit <= _modDatabaseCurrentResultLimit)
        {
            CanLoadMoreModDatabaseResults = false;
            IsLoadMoreModDatabaseButtonVisible = false;
            _hasRequestedAdditionalModDatabaseResults = false;
            return;
        }

        _modDatabaseCurrentResultLimit = nextLimit;
        TriggerModDatabaseSearch(preserveResultLimit: true);
    }

    private async Task RunModDatabaseSearchAsync(
        string query,
        bool hasSearchTokens,
        IReadOnlyList<string> requiredTags,
        CancellationTokenSource cts)
    {
        using var busyScope = BeginBusyScope();
        CancellationToken cancellationToken = cts.Token;

        try
        {
            await Task.Delay(ModDatabaseSearchDebounce, cancellationToken).ConfigureAwait(false);

            if (InternetAccessManager.IsInternetAccessDisabled)
            {
                await UpdateSearchResultsAsync(Array.Empty<ModListItemViewModel>(), cancellationToken).ConfigureAwait(false);
                await InvokeOnDispatcherAsync(
                        () => SetStatus(InternetAccessDisabledStatusMessage, false),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            bool excludeInstalled = ExcludeInstalledModDatabaseResults;
            HashSet<string> installedModIds = await GetInstalledModIdsAsync(cancellationToken).ConfigureAwait(false);
            int desiredResultCount = _modDatabaseCurrentResultLimit;
            int queryLimit = desiredResultCount;
            List<ModDatabaseSearchResult> finalResults = new();
            bool hasMoreResults = false;

            while (true)
            {
                IReadOnlyList<ModDatabaseSearchResult> results;
                if (hasSearchTokens)
                {
                    results = await _databaseService.SearchModsAsync(query, queryLimit, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    results = _modDatabaseAutoLoadMode switch
                    {
                        ModDatabaseAutoLoadMode.DownloadsLastThirtyDays =>
                            await _databaseService
                                .GetMostDownloadedModsLastThirtyDaysAsync(queryLimit, cancellationToken)
                                .ConfigureAwait(false),
                        ModDatabaseAutoLoadMode.DownloadsNewModsRecentMonths =>
                            await _databaseService
                                .GetMostDownloadedNewModsAsync(
                                    _newModsRecentMonths,
                                    queryLimit,
                                    cancellationToken)
                                .ConfigureAwait(false),
                        _ => await _databaseService
                            .GetMostDownloadedModsAsync(queryLimit, cancellationToken)
                            .ConfigureAwait(false)
                    };
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                IReadOnlyList<string> availableTags = CollectModDatabaseTags(results);
                List<string> combinedTags = await Task.Run(
                        () => NormalizeAndSortTags(availableTags.Concat(requiredTags)))
                    .ConfigureAwait(false);
                await InvokeOnDispatcherAsync(
                        () => ApplyNormalizedModDatabaseAvailableTags(combinedTags),
                        cancellationToken,
                        DispatcherPriority.ContextIdle)
                    .ConfigureAwait(false);

                IReadOnlyList<ModDatabaseSearchResult> filteredResults = requiredTags.Count > 0
                    ? results
                        .Where(result => ContainsAllTags(GetTagsForResult(result), requiredTags))
                        .ToList()
                    : results.ToList();

                if (filteredResults.Count == 0)
                {
                    await InvokeOnDispatcherAsync(
                            () =>
                            {
                                CanLoadMoreModDatabaseResults = false;

                                if (_hasRequestedAdditionalModDatabaseResults)
                                {
                                    IsLoadMoreModDatabaseButtonVisible = false;
                                    _hasRequestedAdditionalModDatabaseResults = false;
                                }
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    await UpdateSearchResultsAsync(Array.Empty<ModListItemViewModel>(), cancellationToken)
                        .ConfigureAwait(false);
                    await InvokeOnDispatcherAsync(
                            () => SetStatus(BuildNoModDatabaseResultsMessage(hasSearchTokens), false),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                IEnumerable<ModDatabaseSearchResult> candidateResults = filteredResults;
                if (excludeInstalled)
                {
                    candidateResults = candidateResults
                        .Where(result => !IsResultInstalled(result, installedModIds));
                }

                finalResults = candidateResults
                    .Take(desiredResultCount)
                    .ToList();

                bool hasAdditionalResults = filteredResults.Count > finalResults.Count;
                bool canRequestMore = results.Count >= queryLimit;
                if (!excludeInstalled
                    || finalResults.Count >= desiredResultCount
                    || !canRequestMore)
                {
                    hasMoreResults = hasAdditionalResults;
                    break;
                }

                int nextLimit;
                long candidateLimit = (long)queryLimit + desiredResultCount;
                if (candidateLimit >= MaxModDatabaseResultLimit)
                {
                    nextLimit = MaxModDatabaseResultLimit;
                }
                else
                {
                    nextLimit = (int)candidateLimit;
                }
                if (nextLimit <= queryLimit)
                {
                    break;
                }

                queryLimit = nextLimit;
            }

            if (finalResults.Count == 0)
            {
                await InvokeOnDispatcherAsync(
                        () =>
                        {
                            CanLoadMoreModDatabaseResults = false;

                            if (_hasRequestedAdditionalModDatabaseResults)
                            {
                                IsLoadMoreModDatabaseButtonVisible = false;
                                _hasRequestedAdditionalModDatabaseResults = false;
                            }
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                await UpdateSearchResultsAsync(Array.Empty<ModListItemViewModel>(), cancellationToken).ConfigureAwait(false);
                await InvokeOnDispatcherAsync(
                        () => SetStatus(BuildNoModDatabaseResultsMessage(hasSearchTokens), false),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var entries = finalResults
                .Select(result => new
                {
                    Entry = CreateSearchResultEntry(result),
                    IsInstalled = IsResultInstalled(result, installedModIds)
                })
                .ToList();

            if (entries.Count == 0)
            {
                await UpdateSearchResultsAsync(Array.Empty<ModListItemViewModel>(), cancellationToken).ConfigureAwait(false);
                await InvokeOnDispatcherAsync(
                        () => SetStatus(BuildNoModDatabaseResultsMessage(hasSearchTokens), false),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var viewModels = entries
                .Select(item => CreateSearchResultViewModel(item.Entry, item.IsInstalled))
                .ToList();

            await UpdateSearchResultsAsync(viewModels, cancellationToken).ConfigureAwait(false);

            await InvokeOnDispatcherAsync(
                    () =>
                    {
                        CanLoadMoreModDatabaseResults = hasMoreResults && _modDatabaseCurrentResultLimit < MaxModDatabaseResultLimit;

                        if (_hasRequestedAdditionalModDatabaseResults)
                        {
                            if (!CanLoadMoreModDatabaseResults)
                            {
                                IsLoadMoreModDatabaseButtonVisible = false;
                            }

                            _hasRequestedAdditionalModDatabaseResults = false;
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                ModEntry entry = entries[i].Entry;
                ModDatabaseInfo? cachedInfo = await _databaseService
                    .TryLoadCachedDatabaseInfoAsync(entry.ModId, entry.Version, _installedGameVersion, cancellationToken)
                    .ConfigureAwait(false);

                if (cachedInfo is null)
                {
                    continue;
                }

                entry.UpdateDatabaseInfo(cachedInfo);

                ModDatabaseInfo capturedInfo = cachedInfo;
                ModListItemViewModel viewModel = viewModels[i];

                await InvokeOnDispatcherAsync(
                        () => viewModel.UpdateDatabaseInfo(capturedInfo, loadLogoImmediately: false),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await InvokeOnDispatcherAsync(
                    () => SetStatus("Loading mod details...", false),
                    cancellationToken)
                .ConfigureAwait(false);

            await _databaseService.PopulateModDatabaseInfoAsync(entries.Select(item => item.Entry), _installedGameVersion, cancellationToken)
                .ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await InvokeOnDispatcherAsync(
                () =>
                {
                    for (int i = 0; i < entries.Count; i++)
                    {
                        ModDatabaseInfo? info = entries[i].Entry.DatabaseInfo;
                        if (info != null)
                        {
                            viewModels[i].UpdateDatabaseInfo(info, loadLogoImmediately: false);
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await InvokeOnDispatcherAsync(
                    () => SetStatus("Loading mod images...", false),
                    cancellationToken)
                .ConfigureAwait(false);

            await LoadModDatabaseLogosAsync(viewModels, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await InvokeOnDispatcherAsync(
                    () => SetStatus(BuildModDatabaseResultsMessage(hasSearchTokens, viewModels.Count), false),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Intentionally ignored.
        }
        catch (InternetAccessDisabledException)
        {
            await UpdateSearchResultsAsync(Array.Empty<ModListItemViewModel>(), cancellationToken).ConfigureAwait(false);
            await InvokeOnDispatcherAsync(
                    () =>
                    {
                        CanLoadMoreModDatabaseResults = false;

                        if (_hasRequestedAdditionalModDatabaseResults)
                        {
                            IsLoadMoreModDatabaseButtonVisible = false;
                            _hasRequestedAdditionalModDatabaseResults = false;
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await InvokeOnDispatcherAsync(
                    () => SetStatus(InternetAccessDisabledStatusMessage, false),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await InvokeOnDispatcherAsync(
                    () =>
                    {
                        CanLoadMoreModDatabaseResults = false;

                        if (_hasRequestedAdditionalModDatabaseResults)
                        {
                            IsLoadMoreModDatabaseButtonVisible = false;
                            _hasRequestedAdditionalModDatabaseResults = false;
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await InvokeOnDispatcherAsync(
                    () => SetStatus(BuildModDatabaseErrorMessage(hasSearchTokens, ex.Message), true),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_modDatabaseSearchCts, cts))
            {
                _modDatabaseSearchCts = null;
                UpdateIsModDatabaseLoading(false);
            }
        }
    }

    private void UpdateIsModDatabaseLoading(bool isLoading)
    {
        if (System.Windows.Application.Current?.Dispatcher is Dispatcher dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                IsModDatabaseLoading = isLoading;
            }
            else
            {
                dispatcher.BeginInvoke(new Action(() => IsModDatabaseLoading = isLoading));
            }
        }
        else
        {
            IsModDatabaseLoading = isLoading;
        }
    }

    private IDisposable BeginBusyScope()
    {
        bool isBusy;
        lock (_busyStateLock)
        {
            _busyOperationCount++;
            isBusy = _busyOperationCount > 0;
        }

        UpdateIsBusy(isBusy);
        return new BusyScope(this);
    }

    private void EndBusyScope()
    {
        bool isBusy;
        lock (_busyStateLock)
        {
            if (_busyOperationCount > 0)
            {
                _busyOperationCount--;
            }

            isBusy = _busyOperationCount > 0;
        }

        UpdateIsBusy(isBusy);
    }

    private void UpdateIsBusy(bool isBusy)
    {
        if (System.Windows.Application.Current?.Dispatcher is Dispatcher dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                IsBusy = isBusy;
            }
            else
            {
                dispatcher.BeginInvoke(new Action(() => IsBusy = isBusy));
            }
        }
        else
        {
            IsBusy = isBusy;
        }
    }

    private void UpdateIsLoadingModDetails(bool isLoading)
    {
        if (System.Windows.Application.Current?.Dispatcher is Dispatcher dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                IsLoadingModDetails = isLoading;
            }
            else
            {
                dispatcher.BeginInvoke(new Action(() => IsLoadingModDetails = isLoading));
            }
        }
        else
        {
            IsLoadingModDetails = isLoading;
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
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.EndBusyScope();
        }
    }

    private string BuildModDatabaseResultsMessage(bool hasSearchTokens, int resultCount)
    {
        if (hasSearchTokens)
        {
            return $"Found {resultCount} mods in the mod database.";
        }

        return _modDatabaseAutoLoadMode switch
        {
            ModDatabaseAutoLoadMode.DownloadsLastThirtyDays =>
                $"Showing {resultCount} of the most downloaded mods from the last 30 days.",
            ModDatabaseAutoLoadMode.DownloadsNewModsRecentMonths =>
                $"Showing {resultCount} of the most downloaded newly created mods {BuildRecentMonthsPhrase()}.",
            _ => $"Showing {resultCount} of the most downloaded mods."
        };
    }

    private void UpdateLoadedModsStatus()
    {
        if (IsModDetailsRefreshPending())
        {
            if (!_isModDetailsStatusActive)
            {
                SetStatus(BuildModDetailsLoadingStatusMessage(), false, isModDetailsStatus: true);
            }
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

    private void OnModDetailsRefreshEnqueued(int count)
    {
        if (count <= 0)
        {
            return;
        }

        int newCount = Interlocked.Add(ref _pendingModDetailsRefreshCount, count);
        if (newCount <= 0)
        {
            Interlocked.Exchange(ref _pendingModDetailsRefreshCount, 0);
            return;
        }

        EnsureModDetailsBusyScope();
        UpdateIsLoadingModDetails(true);

        if (newCount == count || !_isModDetailsStatusActive)
        {
            SetStatus(BuildModDetailsLoadingStatusMessage(), false, isModDetailsStatus: true);
        }
    }

    private void OnModDetailsRefreshCompleted()
    {
        int newCount = Interlocked.Decrement(ref _pendingModDetailsRefreshCount);
        if (newCount <= 0)
        {
            Interlocked.Exchange(ref _pendingModDetailsRefreshCount, 0);
            ReleaseModDetailsBusyScope();
            UpdateIsLoadingModDetails(false);

            if (_isModDetailsStatusActive)
            {
                SetStatus(BuildModDetailsReadyStatusMessage(), false);
            }
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
        if (_hasShownModDetailsLoadingStatus)
        {
            return $"Loaded {TotalMods} mods. Mod details up to date.";
        }

        return $"Loaded {TotalMods} mods.";
    }

    private string BuildNoModDatabaseResultsMessage(bool hasSearchTokens)
    {
        if (hasSearchTokens)
        {
            return "No mods found in the mod database.";
        }

        return _modDatabaseAutoLoadMode switch
        {
            ModDatabaseAutoLoadMode.DownloadsLastThirtyDays =>
                "No mods with downloads in the last 30 days were found.",
            ModDatabaseAutoLoadMode.DownloadsNewModsRecentMonths =>
                $"No mods created in the {BuildRecentMonthsPhrase()} were found.",
            _ => "No mods found in the mod database."
        };
    }

    private string BuildModDatabaseErrorMessage(bool hasSearchTokens, string errorMessage)
    {
        string operation;
        if (hasSearchTokens)
        {
            operation = "search the mod database";
        }
        else
        {
            operation = _modDatabaseAutoLoadMode switch
            {
                ModDatabaseAutoLoadMode.DownloadsLastThirtyDays =>
                    "load the most downloaded mods from the last 30 days",
                ModDatabaseAutoLoadMode.DownloadsNewModsRecentMonths =>
                    $"load the most downloaded new mods {BuildRecentMonthsPhrase()}",
                _ => "load the most downloaded mods from the mod database"
            };
        }

        return $"Failed to {operation}: {errorMessage}";
    }

    private string BuildRecentMonthsPhrase()
    {
        return _newModsRecentMonths == 1
            ? "last month"
            : $"in the last {_newModsRecentMonths} months";
    }

    private Task UpdateSearchResultsAsync(IReadOnlyList<ModListItemViewModel> items, CancellationToken cancellationToken)
    {
        return InvokeOnDispatcherAsync(() =>
        {
            _searchResults.Clear();
            foreach (var item in items)
            {
                _searchResults.Add(item);
            }

            SelectedMod = null;
        }, cancellationToken);
    }

    private async Task LoadModDatabaseLogosAsync(IReadOnlyList<ModListItemViewModel> viewModels, CancellationToken cancellationToken)
    {
        if (viewModels.Count == 0)
        {
            return;
        }

        var tasks = new List<Task>(viewModels.Count);
        foreach (ModListItemViewModel viewModel in viewModels)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            tasks.Add(viewModel.LoadModDatabaseLogoAsync(cancellationToken));
        }

        if (tasks.Count == 0)
        {
            return;
        }

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
                {
                    if (!string.IsNullOrWhiteSpace(mod.ModId))
                    {
                        installed.Add(mod.ModId);
                    }
                }

                return installed;
            },
            cancellationToken);
    }

    private static bool IsResultInstalled(ModDatabaseSearchResult result, HashSet<string> installedModIds)
    {
        if (installedModIds.Contains(result.ModId))
        {
            return true;
        }

        foreach (string alternate in result.AlternateIds)
        {
            if (!string.IsNullOrWhiteSpace(alternate) && installedModIds.Contains(alternate))
            {
                return true;
            }
        }

        return false;
    }

    private static Task InvokeOnDispatcherAsync(Action action, CancellationToken cancellationToken, DispatcherPriority priority = DispatcherPriority.Normal)
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

            return dispatcher.InvokeAsync(action, priority, cancellationToken).Task;
        }

        action();
        return Task.CompletedTask;
    }

    private static Task<T> InvokeOnDispatcherAsync<T>(Func<T> function, CancellationToken cancellationToken, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                return Task.FromResult(function());
            }

            return dispatcher.InvokeAsync(function, priority, cancellationToken).Task;
        }

        return Task.FromResult(function());
    }

    private static ModEntry CreateSearchResultEntry(ModDatabaseSearchResult result)
    {
        var authors = string.IsNullOrWhiteSpace(result.Author)
            ? Array.Empty<string>()
            : new[] { result.Author };

        string? description = BuildSearchResultDescription(result);
        string? pageUrl = BuildModDatabasePageUrl(result);

        ModDatabaseInfo databaseInfo = result.DetailedInfo ?? new ModDatabaseInfo
        {
            Tags = result.Tags,
            AssetId = result.AssetId,
            ModPageUrl = pageUrl,
            Downloads = result.Downloads,
            Comments = result.Comments,
            Follows = result.Follows,
            TrendingPoints = result.TrendingPoints,
            LogoUrl = result.LogoUrl,
            LastReleasedUtc = result.LastReleasedUtc
        };

        return new ModEntry
        {
            ModId = result.ModId,
            Name = result.Name,
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
        return new ModListItemViewModel(entry, false, "Mod Database", RejectActivationChangeAsync, _installedGameVersion, isInstalled);
    }

    private static string? BuildSearchResultDescription(ModDatabaseSearchResult result)
    {
        return string.IsNullOrWhiteSpace(result.Summary) ? null : result.Summary.Trim();
    }

    private static string? BuildModDatabasePageUrl(ModDatabaseSearchResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.AssetId))
        {
            return $"https://mods.vintagestory.at/show/mod/{result.AssetId}";
        }

        if (!string.IsNullOrWhiteSpace(result.UrlAlias))
        {
            string alias = result.UrlAlias!.TrimStart('/');
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
        if (item is not ModListItemViewModel mod)
        {
            return false;
        }

        if (_selectedInstalledTags.Count > 0 && !ContainsAllTags(mod.DatabaseTags, _selectedInstalledTags))
        {
            return false;
        }

        if (_searchTokens.Length == 0)
        {
            return true;
        }

        return mod.MatchesSearchTokens(_searchTokens);
    }

    private static string[] CreateSearchTokens(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private string GetDisplayPath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return string.Empty;
        }

        string best;
        try
        {
            best = Path.GetFullPath(fullPath);
        }
        catch (Exception)
        {
            return fullPath;
        }

        foreach (var candidate in EnumerateBasePaths())
        {
            try
            {
                string relative = Path.GetRelativePath(candidate, best);
                if (!relative.StartsWith("..", StringComparison.Ordinal) && relative.Length < best.Length)
                {
                    best = relative;
                }
            }
            catch (Exception)
            {
                // Ignore invalid paths.
            }
        }

        return best.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private IEnumerable<string> EnumerateBasePaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            try
            {
                string full = Path.GetFullPath(candidate);
                set.Add(full);
            }
            catch (Exception)
            {
                // Ignore invalid paths.
            }
        }

        TryAdd(_settingsStore.DataDirectory);
        foreach (var path in _settingsStore.SearchBaseCandidates)
        {
            TryAdd(path);
        }

        TryAdd(Directory.GetCurrentDirectory());

        return set;
    }

    private static IEnumerable<SortOption> CreateSortOptions()
    {
        yield return new SortOption("Name (A → Z)", (nameof(ModListItemViewModel.DisplayName), ListSortDirection.Ascending));
        yield return new SortOption("Name (Z → A)", (nameof(ModListItemViewModel.DisplayName), ListSortDirection.Descending));
        yield return new SortOption(
            "Active (Active → Inactive)",
            (nameof(ModListItemViewModel.ActiveSortOrder), ListSortDirection.Ascending),
            (nameof(ModListItemViewModel.DisplayName), ListSortDirection.Ascending));
        yield return new SortOption(
            "Active (Inactive → Active)",
            (nameof(ModListItemViewModel.ActiveSortOrder), ListSortDirection.Descending),
            (nameof(ModListItemViewModel.DisplayName), ListSortDirection.Ascending));
    }

    private void ReapplyActiveSortIfNeeded()
    {
        if (SelectedSortOption?.SortDescriptions is not { Count: > 0 } sorts)
        {
            return;
        }

        var primary = sorts[0];
        if (!IsActiveSortProperty(primary.Property))
        {
            return;
        }

        SelectedSortOption.Apply(ModsView);
        ModsView.Refresh();
    }

    private static bool IsActiveSortProperty(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        return string.Equals(propertyName, nameof(ModListItemViewModel.IsActive), StringComparison.OrdinalIgnoreCase)
            || string.Equals(propertyName, nameof(ModListItemViewModel.ActiveSortOrder), StringComparison.OrdinalIgnoreCase);
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

        foreach (string path in paths)
        {
            ModEntry? entry = _discoveryService.LoadModFromPath(path);
            if (entry != null)
            {
                ResetCalculatedModState(entry);
                if (existingEntries != null && existingEntries.TryGetValue(path, out var previous))
                {
                    CopyTransientModState(previous, entry);
                }
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
        if (source is null || target is null)
        {
            return;
        }

        bool sameModId = string.Equals(source.ModId, target.ModId, StringComparison.OrdinalIgnoreCase);
        bool sameVersion = string.Equals(source.Version, target.Version, StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrWhiteSpace(source.Version) && string.IsNullOrWhiteSpace(target.Version));

        if (!sameModId || !sameVersion)
        {
            return;
        }

        if (target.DatabaseInfo is null && source.DatabaseInfo != null)
        {
            target.DatabaseInfo = source.DatabaseInfo;
        }

        if (source.ModDatabaseSearchScore.HasValue)
        {
            target.ModDatabaseSearchScore = source.ModDatabaseSearchScore;
        }
    }

    private void ApplyPartialUpdates(IReadOnlyDictionary<string, ModEntry?> changes, string? previousSelection)
    {
        if (changes.Count == 0)
        {
            SetStatus("Mods are up to date.", false);
            if (!string.IsNullOrWhiteSpace(previousSelection)
                && _modViewModelsBySourcePath.TryGetValue(previousSelection, out var selected))
            {
                SelectedMod = selected;
            }

            return;
        }

        int added = 0;
        int updated = 0;
        int removed = 0;

        foreach (var change in changes)
        {
            string path = change.Key;
            ModEntry? entry = change.Value;

            if (entry == null)
            {
                if (_modViewModelsBySourcePath.TryGetValue(path, out var existingVm))
                {
                    _mods.Remove(existingVm);
                    _modViewModelsBySourcePath.Remove(path);
                    removed++;

                    if (ReferenceEquals(SelectedMod, existingVm))
                    {
                        SelectedMod = null;
                    }
                }

                _modEntriesBySourcePath.Remove(path);
                continue;
            }

            var viewModel = CreateModViewModel(entry);
            if (_modViewModelsBySourcePath.TryGetValue(path, out var existing))
            {
                int index = _mods.IndexOf(existing);
                if (index >= 0)
                {
                    _mods[index] = viewModel;
                }
                else
                {
                    _mods.Add(viewModel);
                }

                _modViewModelsBySourcePath[path] = viewModel;
                updated++;

                if (ReferenceEquals(SelectedMod, existing))
                {
                    SelectedMod = viewModel;
                }
            }
            else
            {
                _mods.Add(viewModel);
                _modViewModelsBySourcePath[path] = viewModel;
                _modEntriesBySourcePath[path] = entry;
                added++;
            }
        }

        foreach (var pair in _modViewModelsBySourcePath)
        {
            if (changes.ContainsKey(pair.Key))
            {
                continue;
            }

            if (_modEntriesBySourcePath.TryGetValue(pair.Key, out var entry))
            {
                pair.Value.UpdateLoadError(entry.LoadError);
                pair.Value.UpdateDependencyIssues(entry.DependencyHasErrors, entry.MissingDependencies);
            }
        }

        if (!string.IsNullOrWhiteSpace(previousSelection)
            && _modViewModelsBySourcePath.TryGetValue(previousSelection, out var selectedAfter))
        {
            SelectedMod = selectedAfter;
        }
        else if (SelectedMod != null && !_mods.Contains(SelectedMod))
        {
            SelectedMod = null;
        }

        int affected = added + updated + removed;
        if (affected == 0)
        {
            SetStatus("Mods are up to date.", false);
            return;
        }

        var parts = new List<string>();
        if (added > 0)
        {
            parts.Add($"{added} added");
        }

        if (updated > 0)
        {
            parts.Add($"{updated} updated");
        }

        if (removed > 0)
        {
            parts.Add($"{removed} removed");
        }

        string summary = parts.Count == 0 ? $"{affected} changed" : string.Join(", ", parts);
        SetStatus($"Applied changes to mods ({summary}).", false);
    }

    private void QueueDatabaseInfoRefresh(IEnumerable<ModEntry> entries)
    {
        if (entries is null)
        {
            return;
        }

        ModEntry[] pending = entries
            .Where(entry => entry != null
                && !string.IsNullOrWhiteSpace(entry.ModId)
                && NeedsDatabaseRefresh(entry))
            .ToArray();

        if (pending.Length == 0)
        {
            return;
        }

        OnModDetailsRefreshEnqueued(pending.Length);

        _ = Task.Run(async () =>
        {
            try
            {
                if (InternetAccessManager.IsInternetAccessDisabled)
                {
                    await PopulateOfflineDatabaseInfoAsync(pending).ConfigureAwait(false);
                    return;
                }

                using var limiter = new SemaphoreSlim(MaxConcurrentDatabaseRefreshes, MaxConcurrentDatabaseRefreshes);
                var refreshTasks = pending
                    .Select(entry => RefreshDatabaseInfoAsync(entry, limiter))
                    .ToArray();

                await Task.WhenAll(refreshTasks).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Swallow unexpected exceptions from the refresh loop.
            }
        });
    }

    private static bool NeedsDatabaseRefresh(ModEntry entry)
    {
        if (entry is null)
        {
            return false;
        }

        return entry.DatabaseInfo == null || entry.DatabaseInfo.IsOfflineOnly;
    }

    private async Task RefreshDatabaseInfoAsync(ModEntry entry, SemaphoreSlim limiter)
    {
        await limiter.WaitAsync().ConfigureAwait(false);

        try
        {
            ModDatabaseInfo? cachedInfo = await _databaseService
                .TryLoadCachedDatabaseInfoAsync(entry.ModId, entry.Version, _installedGameVersion)
                .ConfigureAwait(false);

            if (cachedInfo != null)
            {
                await ApplyDatabaseInfoAsync(entry, cachedInfo, loadLogoImmediately: false).ConfigureAwait(false);
                if (InternetAccessManager.IsInternetAccessDisabled)
                {
                    return;
                }
            }
            else if (InternetAccessManager.IsInternetAccessDisabled)
            {
                await PopulateOfflineInfoForEntryAsync(entry).ConfigureAwait(false);
                return;
            }

            ModDatabaseInfo? info;
            try
            {
                info = await _databaseService
                    .TryLoadDatabaseInfoAsync(entry.ModId, entry.Version, _installedGameVersion)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            if (info is null)
            {
                if (InternetAccessManager.IsInternetAccessDisabled)
                {
                    await PopulateOfflineInfoForEntryAsync(entry).ConfigureAwait(false);
                }

                return;
            }

            await ApplyDatabaseInfoAsync(entry, info).ConfigureAwait(false);
        }
        finally
        {
            limiter.Release();
            OnModDetailsRefreshCompleted();
        }
    }

    private async Task PopulateOfflineDatabaseInfoAsync(IEnumerable<ModEntry> entries)
    {
        foreach (ModEntry entry in entries)
        {
            try
            {
                if (entry is not null)
                {
                    await PopulateOfflineInfoForEntryAsync(entry).ConfigureAwait(false);
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
    }

    private async Task PopulateOfflineInfoForEntryAsync(ModEntry entry)
    {
        if (entry is null)
        {
            return;
        }

        ModDatabaseInfo? cachedInfo = entry.DatabaseInfo;
        if (cachedInfo is null)
        {
            cachedInfo = await _databaseService
                .TryLoadCachedDatabaseInfoAsync(entry.ModId, entry.Version, _installedGameVersion)
                .ConfigureAwait(false);
        }

        ModDatabaseInfo? offlineInfo = CreateOfflineDatabaseInfo(entry);

        ModDatabaseInfo? mergedInfo = MergeOfflineAndCachedInfo(offlineInfo, cachedInfo);
        if (mergedInfo is null)
        {
            return;
        }

        await ApplyDatabaseInfoAsync(entry, mergedInfo).ConfigureAwait(false);
    }

    private async Task ApplyDatabaseInfoAsync(ModEntry entry, ModDatabaseInfo info, bool loadLogoImmediately = true)
    {
        if (info is null)
        {
            return;
        }

        try
        {
            await InvokeOnDispatcherAsync(
                    () =>
                    {
                        if (!_modEntriesBySourcePath.TryGetValue(entry.SourcePath, out var currentEntry)
                            || !ReferenceEquals(currentEntry, entry))
                        {
                            return;
                        }

                        currentEntry.UpdateDatabaseInfo(info);

                        if (_modViewModelsBySourcePath.TryGetValue(entry.SourcePath, out var viewModel))
                        {
                            viewModel.UpdateDatabaseInfo(info, loadLogoImmediately);
                        }
                    },
                    CancellationToken.None,
                    DispatcherPriority.ContextIdle)
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

    private ModDatabaseInfo? CreateOfflineDatabaseInfo(ModEntry entry)
    {
        if (entry is null)
        {
            return null;
        }

        var dependencies = entry.Dependencies ?? Array.Empty<ModDependencyInfo>();
        IReadOnlyList<string> installedRequiredGameVersions = ExtractRequiredGameVersions(dependencies);
        IReadOnlyList<ModReleaseInfo> releases = CreateOfflineReleases(entry, installedRequiredGameVersions, dependencies);
        IReadOnlyList<string> aggregatedRequiredGameVersions = AggregateRequiredGameVersions(installedRequiredGameVersions, releases);

        ModReleaseInfo? latestRelease = releases.Count > 0 ? releases[0] : null;
        ModReleaseInfo? latestCompatibleRelease = releases.FirstOrDefault(release => release.IsCompatibleWithInstalledGame);
        DateTime? lastUpdatedUtc = DetermineOfflineLastUpdatedUtc(entry, releases);

        string? latestVersion = latestRelease?.Version ?? entry.Version;
        string? latestCompatibleVersion = latestCompatibleRelease?.Version ?? entry.Version;

        return new ModDatabaseInfo
        {
            RequiredGameVersions = aggregatedRequiredGameVersions,
            LatestVersion = latestVersion,
            LatestCompatibleVersion = latestCompatibleVersion,
            LatestRelease = latestRelease,
            LatestCompatibleRelease = latestCompatibleRelease ?? latestRelease,
            Releases = releases,
            LastReleasedUtc = lastUpdatedUtc,
            IsOfflineOnly = true
        };
    }

    private static ModDatabaseInfo? MergeOfflineAndCachedInfo(ModDatabaseInfo? offlineInfo, ModDatabaseInfo? cachedInfo)
    {
        if (offlineInfo is null)
        {
            return cachedInfo;
        }

        if (cachedInfo is null)
        {
            return offlineInfo;
        }

        IReadOnlyList<ModReleaseInfo> mergedReleases = MergeReleases(offlineInfo.Releases, cachedInfo.Releases);
        ModReleaseInfo? latestRelease = offlineInfo.LatestRelease ?? cachedInfo.LatestRelease;
        if (latestRelease is null && mergedReleases is { Count: > 0 })
        {
            latestRelease = mergedReleases[0];
        }

        ModReleaseInfo? latestCompatibleRelease = offlineInfo.LatestCompatibleRelease ?? cachedInfo.LatestCompatibleRelease;
        if (latestCompatibleRelease is null && mergedReleases is { Count: > 0 })
        {
            latestCompatibleRelease = mergedReleases.FirstOrDefault(release => release?.IsCompatibleWithInstalledGame == true);
        }

        IReadOnlyList<string> requiredVersions = offlineInfo.RequiredGameVersions is { Count: > 0 }
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
            DownloadsLastThirtyDays = cachedInfo.DownloadsLastThirtyDays ?? offlineInfo.DownloadsLastThirtyDays,
            LastReleasedUtc = offlineInfo.LastReleasedUtc ?? cachedInfo.LastReleasedUtc,
            CreatedUtc = cachedInfo.CreatedUtc ?? offlineInfo.CreatedUtc,
            LatestRelease = latestRelease,
            LatestCompatibleRelease = latestCompatibleRelease ?? latestRelease,
            Releases = mergedReleases,
            IsOfflineOnly = offlineInfo.IsOfflineOnly && (cachedInfo?.IsOfflineOnly ?? true)
        };
    }

    private static IReadOnlyList<ModReleaseInfo> MergeReleases(
        IReadOnlyList<ModReleaseInfo>? offlineReleases,
        IReadOnlyList<ModReleaseInfo>? cachedReleases)
    {
        if (offlineReleases is not { Count: > 0 })
        {
            return cachedReleases is { Count: > 0 } ? cachedReleases : Array.Empty<ModReleaseInfo>();
        }

        if (cachedReleases is not { Count: > 0 })
        {
            return offlineReleases;
        }

        var byVersion = new Dictionary<string, ModReleaseInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (ModReleaseInfo release in cachedReleases)
        {
            if (release is null || string.IsNullOrWhiteSpace(release.Version))
            {
                continue;
            }

            if (!byVersion.ContainsKey(release.Version))
            {
                byVersion[release.Version] = release;
            }
        }

        foreach (ModReleaseInfo release in offlineReleases)
        {
            if (release is null || string.IsNullOrWhiteSpace(release.Version))
            {
                continue;
            }

            byVersion[release.Version] = release;
        }

        if (byVersion.Count == 0)
        {
            return Array.Empty<ModReleaseInfo>();
        }

        List<ModReleaseInfo> merged = byVersion.Values.ToList();
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

        ModReleaseInfo? installedRelease = CreateOfflineRelease(entry, installedRequiredGameVersions, installedDependencies);
        if (installedRelease != null)
        {
            releases.Add(installedRelease);
            seenVersions.Add(installedRelease.Version);
        }

        foreach (ModReleaseInfo cachedRelease in EnumerateCachedModReleases(entry.ModId))
        {
            if (!seenVersions.Add(cachedRelease.Version))
            {
                continue;
            }

            releases.Add(cachedRelease);
        }

        if (releases.Count == 0)
        {
            return Array.Empty<ModReleaseInfo>();
        }

        releases.Sort(CompareOfflineReleases);
        return releases.AsReadOnly();
    }

    private ModReleaseInfo? CreateOfflineRelease(
        ModEntry entry,
        IReadOnlyList<string> requiredGameVersions,
        IReadOnlyList<ModDependencyInfo> dependencies)
    {
        if (string.IsNullOrWhiteSpace(entry.Version))
        {
            return null;
        }

        if (!TryCreateFileUri(entry.SourcePath, entry.SourceKind, out Uri? downloadUri))
        {
            return null;
        }

        DateTime? createdUtc = TryGetLastWriteTimeUtc(entry.SourcePath, entry.SourceKind);

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
        string? cacheDirectory = ModCacheLocator.GetModCacheDirectory(modId);
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            yield break;
        }

        try
        {
            if (!Directory.Exists(cacheDirectory))
            {
                yield break;
            }
        }
        catch (Exception)
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(cacheDirectory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (string file in files)
        {
            ModReleaseInfo? release = TryCreateCachedRelease(file, modId);
            if (release != null)
            {
                yield return release;
            }
        }
    }

    private ModReleaseInfo? TryCreateCachedRelease(string archivePath, string expectedModId)
    {
        DateTime lastWriteTimeUtc = DateTime.MinValue;
        long length = 0L;

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

        if (ModManifestCacheService.TryGetManifest(archivePath, lastWriteTimeUtc, length, out string cachedManifest, out _))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(cachedManifest);
                JsonElement root = document.RootElement;

                string? modId = GetString(root, "modid") ?? GetString(root, "modID");
                if (!IsModIdMatch(expectedModId, modId))
                {
                    ModManifestCacheService.Invalidate(archivePath);
                }
                else
                {
                    string? version = GetString(root, "version") ?? TryResolveVersionFromMap(root);
                    if (string.IsNullOrWhiteSpace(version))
                    {
                        return null;
                    }

                    IReadOnlyList<ModDependencyInfo> dependencies = ParseDependencies(root);
                    IReadOnlyList<string> requiredGameVersions = ExtractRequiredGameVersions(dependencies);

                    if (!TryCreateFileUri(archivePath, ModSourceKind.ZipArchive, out Uri? downloadUri))
                    {
                        return null;
                    }

                    DateTime? createdUtc = TryGetLastWriteTimeUtc(archivePath, ModSourceKind.ZipArchive);
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
        }

        try
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            ZipArchiveEntry? infoEntry = FindArchiveEntry(archive, "modinfo.json");
            if (infoEntry == null)
            {
                return null;
            }

            string manifestContent;
            using (Stream infoStream = infoEntry.Open())
            using (var reader = new StreamReader(infoStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                manifestContent = reader.ReadToEnd();
            }

            using JsonDocument document = JsonDocument.Parse(manifestContent);
            JsonElement root = document.RootElement;

            string? modId = GetString(root, "modid") ?? GetString(root, "modID");
            if (!IsModIdMatch(expectedModId, modId))
            {
                return null;
            }

            string? version = GetString(root, "version") ?? TryResolveVersionFromMap(root);
            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            IReadOnlyList<ModDependencyInfo> dependencies = ParseDependencies(root);
            IReadOnlyList<string> requiredGameVersions = ExtractRequiredGameVersions(dependencies);

            if (!TryCreateFileUri(archivePath, ModSourceKind.ZipArchive, out Uri? downloadUri))
            {
                return null;
            }

            DateTime? createdUtc = TryGetLastWriteTimeUtc(archivePath, ModSourceKind.ZipArchive);

            string cacheModId = !string.IsNullOrWhiteSpace(modId)
                ? modId
                : (!string.IsNullOrWhiteSpace(expectedModId) ? expectedModId : Path.GetFileNameWithoutExtension(archivePath));

            ModManifestCacheService.StoreManifest(archivePath, lastWriteTimeUtc, length, cacheModId, version, manifestContent, null);

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
        if (string.IsNullOrWhiteSpace(expectedModId) || string.IsNullOrWhiteSpace(actualModId))
        {
            return true;
        }

        return string.Equals(actualModId, expectedModId, StringComparison.OrdinalIgnoreCase);
    }

    private static ZipArchiveEntry? FindArchiveEntry(ZipArchive archive, string entryName)
    {
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.Equals(entry.FullName, entryName, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return archive.Entries.FirstOrDefault(entry =>
            string.Equals(Path.GetFileName(entry.FullName), entryName, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (TryGetProperty(element, propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? TryResolveVersionFromMap(JsonElement root)
    {
        if (!TryGetProperty(root, "versionmap", out JsonElement map) && !TryGetProperty(root, "VersionMap", out map))
        {
            return null;
        }

        if (map.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? preferred = null;
        string? fallback = null;
        foreach (JsonProperty property in map.EnumerateObject())
        {
            string? version = property.Value.GetString();
            if (version == null)
            {
                continue;
            }

            fallback = version;
            if (property.Name.Contains("1.21", StringComparison.OrdinalIgnoreCase))
            {
                preferred = version;
            }
        }

        return preferred ?? fallback;
    }

    private static IReadOnlyList<ModDependencyInfo> ParseDependencies(JsonElement root)
    {
        if (!TryGetProperty(root, "dependencies", out JsonElement dependenciesElement)
            || dependenciesElement.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<ModDependencyInfo>();
        }

        var dependencies = new List<ModDependencyInfo>();
        foreach (JsonProperty property in dependenciesElement.EnumerateObject())
        {
            string version = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : string.Empty;
            dependencies.Add(new ModDependencyInfo(property.Name, version));
        }

        return dependencies.Count == 0 ? Array.Empty<ModDependencyInfo>() : dependencies;
    }

    private static IReadOnlyList<string> AggregateRequiredGameVersions(
        IReadOnlyList<string> installedRequiredGameVersions,
        IReadOnlyList<ModReleaseInfo> releases)
    {
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string version in installedRequiredGameVersions)
        {
            if (!string.IsNullOrWhiteSpace(version))
            {
                versions.Add(version);
            }
        }

        foreach (ModReleaseInfo release in releases)
        {
            if (release?.GameVersionTags is null)
            {
                continue;
            }

            foreach (string tag in release.GameVersionTags)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    versions.Add(tag);
                }
            }
        }

        return versions.Count == 0 ? Array.Empty<string>() : versions.ToArray();
    }

    private static DateTime? DetermineOfflineLastUpdatedUtc(ModEntry entry, IReadOnlyList<ModReleaseInfo> releases)
    {
        DateTime? lastUpdatedUtc = null;
        foreach (ModReleaseInfo release in releases)
        {
            if (release?.CreatedUtc is not DateTime created)
            {
                continue;
            }

            if (!lastUpdatedUtc.HasValue || created > lastUpdatedUtc)
            {
                lastUpdatedUtc = created;
            }
        }

        return lastUpdatedUtc ?? TryGetLastWriteTimeUtc(entry.SourcePath, entry.SourceKind);
    }

    private void OnInternetAccessChanged(object? sender, EventArgs e)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                RefreshInternetAccessDependentState();
            }
            else
            {
                dispatcher.BeginInvoke(DispatcherPriority.Normal, RefreshInternetAccessDependentState);
            }

            return;
        }

        RefreshInternetAccessDependentState();
    }

    private void RefreshInternetAccessDependentState()
    {
        foreach (ModListItemViewModel mod in _mods)
        {
            mod.RefreshInternetAccessDependentState();
        }

        foreach (ModListItemViewModel mod in _searchResults)
        {
            mod.RefreshInternetAccessDependentState();
        }

        _showCloudModlistsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAccessCloudModlists));

        if (InternetAccessManager.IsInternetAccessDisabled && _viewSection == ViewSection.CloudModlists)
        {
            SetStatus(InternetAccessDisabledStatusMessage, false);
            SetViewSection(ViewSection.InstalledMods);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        InternetAccessManager.InternetAccessChanged -= OnInternetAccessChanged;
        _mods.CollectionChanged -= OnModsCollectionChanged;
        _searchResults.CollectionChanged -= OnSearchResultsCollectionChanged;

        DetachAllInstalledMods();
        DetachAllSearchResults();

        foreach (TagFilterOptionViewModel filter in _installedTagFilters)
        {
            filter.PropertyChanged -= OnInstalledTagFilterPropertyChanged;
        }

        foreach (TagFilterOptionViewModel filter in _modDatabaseTagFilters)
        {
            filter.PropertyChanged -= OnModDatabaseTagFilterPropertyChanged;
        }

        _installedTagFilters.Clear();
        _modDatabaseTagFilters.Clear();

        _modDetailsBusyScope?.Dispose();
        _modDetailsBusyScope = null;
    }

    private static int CompareOfflineReleases(ModReleaseInfo? left, ModReleaseInfo? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        if (VersionStringUtility.IsCandidateVersionNewer(left.Version, right.Version))
        {
            return -1;
        }

        if (VersionStringUtility.IsCandidateVersionNewer(right.Version, left.Version))
        {
            return 1;
        }

        DateTime leftTimestamp = left.CreatedUtc ?? DateTime.MinValue;
        DateTime rightTimestamp = right.CreatedUtc ?? DateTime.MinValue;
        int dateComparison = rightTimestamp.CompareTo(leftTimestamp);
        if (dateComparison != 0)
        {
            return dateComparison;
        }

        return string.Compare(left.Version, right.Version, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ExtractRequiredGameVersions(IReadOnlyList<ModDependencyInfo> dependencies)
    {
        if (dependencies is null || dependencies.Count == 0)
        {
            return Array.Empty<string>();
        }

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
        if (dependencies is null || dependencies.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(_installedGameVersion))
        {
            return true;
        }

        foreach (var dependency in dependencies)
        {
            if (dependency is null || !dependency.IsGameOrCoreDependency)
            {
                continue;
            }

            if (!VersionStringUtility.SatisfiesMinimumVersion(dependency.Version, _installedGameVersion))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCreateFileUri(string? path, ModSourceKind kind, out Uri? uri)
    {
        uri = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            if (kind == ModSourceKind.Folder)
            {
                fullPath = Path.TrimEndingDirectorySeparator(fullPath) + Path.DirectorySeparatorChar;
            }

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
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string normalized = Path.GetFullPath(path);
            if (Directory.Exists(normalized))
            {
                normalized = Path.TrimEndingDirectorySeparator(normalized);
            }

            string fileName = Path.GetFileName(normalized);
            return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTime? TryGetLastWriteTimeUtc(string? path, ModSourceKind kind)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return kind == ModSourceKind.Folder
                ? Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : null
                : File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
