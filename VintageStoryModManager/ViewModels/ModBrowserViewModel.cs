using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VintageStoryModManager.Models;
using VintageStoryModManager.Services;

namespace VintageStoryModManager.ViewModels;

/// <summary>
/// ViewModel for the main mod browser view.
/// </summary>
public partial class ModBrowserViewModel : ObservableObject
{
    private readonly IModApiService _modApiService;
    private readonly UserConfigurationService? _userConfigService;
    private readonly ModVersionVoteService _voteService;
    private readonly string? _installedGameVersion;
    private CancellationTokenSource? _searchCts;
    private const int DefaultLoadedMods = 45;
    private const int LoadMoreCount = 15;
    private const int PrefetchBufferCount = 10;
    private const double TitleWeight = 6.0;
    private const double AuthorWeight = 4.5;
    private const double SummaryWeight = 2.5;
    private bool _isInitializing;
    private bool _isTabVisible;
    private Func<DownloadableMod, Task>? _installModCallback;
    private readonly HashSet<int> _userReportsLoaded = new();
    private readonly HashSet<int> _modsWithLoadedLogos = new();
    private readonly HashSet<string> _normalizedInstalledModIds = new(StringComparer.OrdinalIgnoreCase);
    private long _lastLoadMoreTicks;
    private const int LoadMoreThrottleMs = 300;
    private int _activeSearchCount;

    #region Observable Properties

    [ObservableProperty]
    private ObservableCollection<DownloadableModOnList> _modsList = [];

    [ObservableProperty]
    private ObservableCollection<DownloadableModOnList> _visibleMods = [];

    [ObservableProperty]
    private int _visibleModsCount = DefaultLoadedMods;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _loadingMessage = "Loading mods...";

    [ObservableProperty]
    private string _textFilter = string.Empty;

    public bool HasSearchText => !string.IsNullOrWhiteSpace(TextFilter);

    [ObservableProperty]
    private ModAuthor? _selectedAuthor;

    [ObservableProperty]
    private ObservableCollection<ModAuthor> _availableAuthors = [];

    [ObservableProperty]
    private ObservableCollection<GameVersion> _selectedVersions = [];

    [ObservableProperty]
    private ObservableCollection<GameVersion> _availableVersions = [];

    [ObservableProperty]
    private ObservableCollection<ModTag> _selectedTags = [];

    [ObservableProperty]
    private ObservableCollection<ModTag> _availableTags = [];

    [ObservableProperty]
    private string _selectedSide = "any";

    [ObservableProperty]
    private string _selectedInstalledFilter = "all";

    [ObservableProperty]
    private bool _onlyFavorites;

    [ObservableProperty]
    private string _orderBy = "follows";

    [ObservableProperty]
    private string _orderByDirection = "desc";

    [ObservableProperty]
    private DownloadableModOnList? _selectedMod;

    [ObservableProperty]
    private DownloadableMod? _modToInstall;

    [ObservableProperty]
    private bool _isInstallDialogOpen;

    [ObservableProperty]
    private ObservableCollection<int> _favoriteMods = [];

    [ObservableProperty]
    private ObservableCollection<int> _installedMods = [];

    [ObservableProperty]
    private bool _useRelevantSearchResults = true;

    /// <summary>
    /// Gets whether to use correct (high-quality) thumbnails.
    /// When faster thumbnails are enabled (default), uses faster loading.
    /// When faster thumbnails are disabled, uses higher quality thumbnails which load slower.
    /// </summary>
    private bool ShouldUseCorrectThumbnails => !(_userConfigService?.UseFasterThumbnails ?? true);

    #endregion

    #region Filter Options

    public static List<KeyValuePair<string, string>> SideOptions =>
    [
        new("any", "Any"),
        new("both", "Both"),
        new("server", "Server"),
        new("client", "Client")
    ];

    public static List<KeyValuePair<string, string>> InstalledFilterOptions =>
    [
        new("all", "All"),
        new("installed", "Installed"),
        new("not-installed", "Not Installed")
    ];

    public static List<OrderByOption> OrderByOptions =>
    [
        new("trendingpoints", "Trending", "IconFire"),
        new("downloads", "Downloads", "IconDownload"),
        new("comments", "Comments", "IconMessage"),
        new("lastreleased", "Updated", "IconHistory"),
        new("asset.created", "Created", "IconCalendar"),
        new("follows", "Follows", "IconHeartSolid")
    ];

    #endregion

    public ModBrowserViewModel(IModApiService modApiService, UserConfigurationService? userConfigService = null)
    {
        _modApiService = modApiService;
        _userConfigService = userConfigService;
        _voteService = new ModVersionVoteService();
        _installedGameVersion = VintageStoryVersionLocator.GetInstalledVersion(_userConfigService?.GameDirectory);
        _isInitializing = true;

        // Subscribe to collection changes for multi-select filters
        SelectedVersions.CollectionChanged += OnFilterCollectionChanged;
        SelectedTags.CollectionChanged += OnFilterCollectionChanged;

        // Load saved settings if available
        // Use public properties to ensure PropertyChanged events are raised
        if (_userConfigService != null)
        {
            OrderBy = _userConfigService.ModBrowserOrderBy;
            OrderByDirection = _userConfigService.ModBrowserOrderByDirection;
            SelectedSide = _userConfigService.ModBrowserSelectedSide;
            SelectedInstalledFilter = _userConfigService.ModBrowserSelectedInstalledFilter;
            OnlyFavorites = _userConfigService.ModBrowserOnlyFavorites;
            FavoriteMods = new ObservableCollection<int>(_userConfigService.ModBrowserFavoriteModIds);
            UseRelevantSearchResults = _userConfigService.ModBrowserRelevantSearch;
        }

        FavoriteMods.CollectionChanged += OnFavoriteModsCollectionChanged;

        _isInitializing = false;
    }

    /// <summary>
    /// Sets the callback to be invoked when a mod needs to be installed.
    /// </summary>
    public void SetInstallModCallback(Func<DownloadableMod, Task> callback)
    {
        _installModCallback = callback;
    }

    /// <summary>
    /// Updates whether the Mod Browser tab is currently visible to the user.
    /// </summary>
    public void SetTabVisibility(bool isVisible)
    {
        _isTabVisible = isVisible;

        if (!isVisible)
        {
            _searchCts?.Cancel();
        }
    }

    private async void OnFilterCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        try
        {
            // Save selections to config
            if (_userConfigService != null)
            {
                if (sender == SelectedVersions)
                {
                    var versionIds = SelectedVersions.Select(v => v.TagId.ToString()).ToList();
                    _userConfigService.SetModBrowserSelectedVersionIds(versionIds);
                }
                else if (sender == SelectedTags)
                {
                    var tagIds = SelectedTags.Select(t => t.TagId).ToList();
                    _userConfigService.SetModBrowserSelectedTagIds(tagIds);
                }
            }

            IsSearching = true;
            await SearchModsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error during filter search: {ex.Message}");
        }
    }

    private void OnFavoriteModsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isInitializing)
            return;

        _userConfigService?.SetModBrowserFavoriteModIds(FavoriteMods);

        if (OnlyFavorites)
        {
            IsSearching = true;
            _ = SearchModsAsync();
        }
    }

    /// <summary>
    /// Gets the visible mods based on pagination.
    /// </summary>
    // public IEnumerable<DownloadableModOnList> VisibleMods =>
    //    ModsList.Take(VisibleModsCount);

    private List<DownloadableModOnList> GetPrefetchMods()
    {
        var maxCount = Math.Min(VisibleModsCount + PrefetchBufferCount, ModsList.Count);
        return ModsList.Take(maxCount).ToList();
    }


    /// <summary>
    /// Checks if a mod is a favorite.
    /// </summary>
    public bool IsModFavorite(int modId) => FavoriteMods.Contains(modId);

    /// <summary>
    /// Checks if a mod is installed.
    /// </summary>
    public bool IsModInstalled(int modId)
    {
        return IsModInstalledById(modId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Updates the installed mod cache using the provided identifiers.
    /// </summary>
    /// <param name="modIds">A collection of mod identifiers to normalize and track.</param>
    /// <param name="numericModIds">Optional numeric identifiers for compatibility with existing bindings.</param>
    public void UpdateInstalledMods(IEnumerable<string> modIds, IEnumerable<int>? numericModIds = null)
    {
        _normalizedInstalledModIds.Clear();
        foreach (var modId in modIds)
        {
            AddInstalledModId(modId);
        }

        InstalledMods.Clear();
        if (numericModIds != null)
        {
            foreach (var modId in numericModIds.Distinct())
                InstalledMods.Add(modId);
        }

        RefreshInstalledFlags();
    }

    /// <summary>
    /// Adds a single installed mod identifier to the cache and updates the UI state.
    /// </summary>
    /// <param name="modId">The mod identifier to add.</param>
    /// <param name="numericModId">Optional numeric identifier to keep the installed list in sync.</param>
    public void AddInstalledMod(string modId, int? numericModId = null)
    {
        AddInstalledModId(modId);

        if (numericModId.HasValue && !InstalledMods.Contains(numericModId.Value))
            InstalledMods.Add(numericModId.Value);

        RefreshInstalledFlags();
    }

    /// <summary>
    /// Marks the specified mod's user report data as stale and refreshes it when available.
    /// </summary>
    /// <param name="modId">The numeric mod identifier to refresh.</param>
    /// <param name="summary">An optional vote summary to apply immediately without re-fetching.</param>
    public void InvalidateUserReport(int modId, ModVersionVoteSummary? summary = null)
    {
        lock (_userReportsLoaded)
        {
            _userReportsLoaded.Remove(modId);
        }

        var mod = ModsList.FirstOrDefault(m => m.ModId == modId);
        if (mod is null)
            return;

        if (summary is not null)
        {
            ApplyUserReportSummary(mod, summary);
            lock (_userReportsLoaded)
            {
                _userReportsLoaded.Add(modId);
            }

            return;
        }

        _ = PopulateUserReportsAsync(new[] { mod }, CancellationToken.None);
    }

    /// <summary>
    /// Invalidates and refreshes user reports for all visible/loaded mods in the browser.
    /// This should be called when the votes cache file changes.
    /// </summary>
    public void InvalidateAllVisibleUserReports()
    {
        List<DownloadableModOnList> visibleMods;
        lock (_userReportsLoaded)
        {
            // Get all visible mods that have loaded user reports
            visibleMods = VisibleMods
                .Where(m => _userReportsLoaded.Contains(m.ModId))
                .ToList();

            // Clear the loaded tracking for these mods
            foreach (var mod in visibleMods)
            {
                _userReportsLoaded.Remove(mod.ModId);
            }
        }

        if (visibleMods.Count == 0)
            return;

        // Refresh user reports for all visible mods
        _ = PopulateUserReportsAsync(visibleMods, CancellationToken.None);
    }

    #region Commands

    [RelayCommand]
    private void GoBack()
    {
        // In a full implementation, this would navigate back or close the view
        // For the standalone browser, we'll just close the window
        System.Windows.Application.Current?.MainWindow?.Close();
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        await Task.WhenAll(
            LoadAuthorsAsync(),
            LoadGameVersionsAsync(),
            LoadTagsAsync()
        );

        // Restore previously selected versions and tags after loading available options
        RestoreSavedSelections();

        await SearchModsAsync();
    }

    public Task RefreshSearchAsync()
    {
        return SearchModsAsync();
    }

    [RelayCommand]
    private async Task SearchModsAsync()
    {
        if (!_isTabVisible)
            return;

        // Cancel any pending search
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        // Increment active search counter
        Interlocked.Increment(ref _activeSearchCount);

        try
        {
            IsSearching = true;
            LoadingMessage = "Searching mods...";

            // Clear the list immediately so the progress indicator is visible
            ModsList.Clear();
            VisibleMods.Clear();
            VisibleModsCount = 0;
            // OnPropertyChanged(nameof(VisibleMods)); // Handled by ObservableCollection

            // Add a small delay to debounce rapid typing
            var debounceDelay = string.IsNullOrWhiteSpace(TextFilter) ? 50 : 400;
            await Task.Delay(debounceDelay, token);

            if (token.IsCancellationRequested)
                return;

            var mods = await _modApiService.QueryModsAsync(
                textFilter: TextFilter,
                authorFilter: SelectedAuthor,
                versionsFilter: SelectedVersions,
                tagsFilter: SelectedTags,
                orderBy: OrderBy,
                orderByOrder: OrderByDirection,
                cancellationToken: token);

            if (token.IsCancellationRequested)
                return;

            // Apply client-side filters
            var filteredMods = ApplyClientSideFilters(mods);

            if (ShouldApplyRelevantSorting())
            {
                filteredMods = SortByRelevance(filteredMods, TextFilter);
            }

            // Clear tracking before new search
            _userReportsLoaded.Clear();
            _modsWithLoadedLogos.Clear();

            // Pre-populate thumbnails for initially visible mods to prevent flickering on first load
            // Only if "Faster thumbnails" setting is disabled (i.e., using correct thumbnails)
            if (ShouldUseCorrectThumbnails)
            {
                var initialVisibleCount = Math.Min(DefaultLoadedMods, filteredMods.Count);
                var initialVisibleMods = filteredMods.Take(initialVisibleCount).ToList();
                await PopulateModThumbnailsAsync(initialVisibleMods, token);
            }

            if (token.IsCancellationRequested)
                return;

            foreach (var mod in filteredMods)
            {
                ModsList.Add(mod);
            }

            var initialBatch = ModsList.Take(DefaultLoadedMods);
            foreach (var mod in initialBatch)
            {
                VisibleMods.Add(mod);
            }

            VisibleModsCount = VisibleMods.Count;
            // OnPropertyChanged(nameof(VisibleMods));

            var modsToPrefetch = GetPrefetchMods();
            // Only populate user reports for visible mods + prefetch buffer
            _ = PopulateUserReportsAsync(modsToPrefetch, token);
        }
        catch (OperationCanceledException)
        {
            // Expected when search is cancelled
        }
        finally
        {
            // Decrement active search counter and only set IsSearching to false when no searches are active
            if (Interlocked.Decrement(ref _activeSearchCount) == 0)
            {
                IsSearching = false;
            }
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        TextFilter = string.Empty;
    }

    [RelayCommand]
    private async Task LoadMore()
    {
        // Throttle load requests to prevent rapid consecutive calls (lock-free, thread-safe)
        var nowTicks = Environment.TickCount64;

        // Atomically check and update timestamp to prevent race conditions
        while (true)
        {
            var lastTicks = Interlocked.Read(ref _lastLoadMoreTicks);
            var timeSinceLastLoad = nowTicks - lastTicks;

            if (timeSinceLastLoad < LoadMoreThrottleMs)
            {
                return; // Too soon, throttle this request
            }

            // Try to atomically update the timestamp
            if (Interlocked.CompareExchange(ref _lastLoadMoreTicks, nowTicks, lastTicks) == lastTicks)
            {
                break; // Successfully updated, proceed with load
            }
            // Another thread updated the timestamp, retry the check
        }

        var previousCount = VisibleModsCount;
        var nextBatch = ModsList.Skip(previousCount).Take(LoadMoreCount).ToList();

        if (nextBatch.Count == 0) return;

        foreach (var mod in nextBatch)
        {
            VisibleMods.Add(mod);
        }

        VisibleModsCount = VisibleMods.Count;

        // Notify UI of changes - using Normal priority to maintain responsiveness
        // OnPropertyChanged(nameof(VisibleMods)); // Not needed

        await Task.CompletedTask;

        // Load metadata for newly visible mods + prefetch buffer asynchronously
        var prefetchEndIndex = Math.Min(VisibleModsCount + PrefetchBufferCount, ModsList.Count);
        var modsToPrefetch = ModsList.Skip(previousCount).Take(prefetchEndIndex - previousCount).ToList();

        if (modsToPrefetch.Any())
        {
            var token = _searchCts?.Token ?? CancellationToken.None;

            // Run these operations in parallel on background threads to avoid blocking UI
            _ = Task.Run(async () =>
            {
                try
                {
                    // Run thumbnail and user report loading in parallel for better performance
                    var tasks = new List<Task>();

                    // Only populate thumbnails if "Faster thumbnails" setting is disabled (i.e., using correct thumbnails)
                    if (ShouldUseCorrectThumbnails)
                    {
                        tasks.Add(PopulateModThumbnailsAsync(modsToPrefetch, token));
                    }

                    tasks.Add(PopulateUserReportsAsync(modsToPrefetch, token));

                    await Task.WhenAll(tasks);
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancelled
                }
            }, token);
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        TextFilter = string.Empty;
        SelectedAuthor = null;
        SelectedVersions.Clear();
        SelectedTags.Clear();
        SelectedSide = "any";
        SelectedInstalledFilter = "all";
        OnlyFavorites = false;
    }

    [RelayCommand]
    private void ToggleFavoriteFilter()
    {
        OnlyFavorites = !OnlyFavorites;
    }

    [RelayCommand]
    private void ToggleRelevantSearchResults()
    {
        UseRelevantSearchResults = !UseRelevantSearchResults;
    }

    [RelayCommand]
    private async Task OpenModDetailsAsync(DownloadableModOnList mod)
    {
        SelectedMod = mod;
        ModToInstall = await _modApiService.GetModAsync(mod.ModId);
        IsInstallDialogOpen = true;
    }

    [RelayCommand]
    private void CloseInstallDialog()
    {
        IsInstallDialogOpen = false;
        ModToInstall = null;
        SelectedMod = null;
    }

    [RelayCommand]
    private void ToggleFavorite(int modId)
    {
        if (FavoriteMods.Contains(modId))
        {
            FavoriteMods.Remove(modId);
        }
        else
        {
            FavoriteMods.Add(modId);
        }
        // Notify the UI that FavoriteMods has changed so MultiBindings re-evaluate
        OnPropertyChanged(nameof(FavoriteMods));
    }

    [RelayCommand]
    private async Task InstallModAsync(int modId)
    {
        // Check if internet access is disabled
        if (InternetAccessManager.IsInternetAccessDisabled)
            return;

        // Fetch full mod details
        var mod = await _modApiService.GetModAsync(modId);
        if (mod == null)
            return;

        // If a callback is registered, use it (MainWindow will handle the actual installation)
        if (_installModCallback != null)
        {
            await _installModCallback(mod);
        }
        else
        {
            // Fallback: just mark as installed in the UI
            AddInstalledMod(mod.ModIdStr ?? mod.ModId.ToString(CultureInfo.InvariantCulture), modId);
        }
    }

    [RelayCommand]
    private void OpenModInBrowser(int assetId)
    {
        var url = $"https://mods.vintagestory.at/show/mod/{assetId}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ChangeOrderBy(string newOrderBy)
    {
        if (OrderBy == newOrderBy)
        {
            // Toggle direction
            OrderByDirection = OrderByDirection == "desc" ? "asc" : "desc";
        }
        else
        {
            OrderBy = newOrderBy;
            OrderByDirection = "desc";
        }
    }

    [RelayCommand]
    private void ToggleOrderDirection()
    {
        OrderByDirection = OrderByDirection == "desc" ? "asc" : "desc";
    }

    [RelayCommand]
    private void ToggleVersion(GameVersion version)
    {
        if (SelectedVersions.Contains(version))
        {
            SelectedVersions.Remove(version);
        }
        else
        {
            SelectedVersions.Add(version);
        }
    }

    [RelayCommand]
    private void ToggleTag(ModTag tag)
    {
        if (SelectedTags.Contains(tag))
        {
            SelectedTags.Remove(tag);
        }
        else
        {
            SelectedTags.Add(tag);
        }
    }

    #endregion

    #region Private Methods

    private void RestoreSavedSelections()
    {
        if (_userConfigService == null) return;

        // Temporarily unsubscribe to prevent triggering searches while restoring
        SelectedVersions.CollectionChanged -= OnFilterCollectionChanged;
        SelectedTags.CollectionChanged -= OnFilterCollectionChanged;

        try
        {
            // Restore selected versions
            var savedVersionIds = _userConfigService.ModBrowserSelectedVersionIds;
            foreach (var versionId in savedVersionIds)
            {
                if (long.TryParse(versionId, out var tagId))
                {
                    var version = AvailableVersions.FirstOrDefault(v => v.TagId == tagId);
                    if (version != null && !SelectedVersions.Contains(version))
                    {
                        SelectedVersions.Add(version);
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[ModBrowserViewModel] Failed to parse version ID '{versionId}' as long");
                }
            }

            // Restore selected tags
            var savedTagIds = _userConfigService.ModBrowserSelectedTagIds;
            foreach (var tagId in savedTagIds)
            {
                var tag = AvailableTags.FirstOrDefault(t => t.TagId == tagId);
                if (tag != null && !SelectedTags.Contains(tag))
                {
                    SelectedTags.Add(tag);
                }
            }
        }
        finally
        {
            // Re-subscribe
            SelectedVersions.CollectionChanged += OnFilterCollectionChanged;
            SelectedTags.CollectionChanged += OnFilterCollectionChanged;
        }
    }

    private async Task LoadAuthorsAsync()
    {
        var authors = await _modApiService.GetAuthorsAsync();
        AvailableAuthors.Clear();
        foreach (var author in authors)
        {
            AvailableAuthors.Add(author);
        }
    }

    private async Task LoadGameVersionsAsync()
    {
        var versions = await _modApiService.GetGameVersionsAsync();
        AvailableVersions.Clear();
        foreach (var version in versions)
        {
            AvailableVersions.Add(version);
        }
    }

    private async Task LoadTagsAsync()
    {
        var tags = await _modApiService.GetTagsAsync();
        AvailableTags.Clear();
        foreach (var tag in tags)
        {
            AvailableTags.Add(tag);
        }
    }

    private bool IsModInstalled(DownloadableModOnList mod)
    {
        foreach (var candidate in GetCandidateModIds(mod))
        {
            if (IsModInstalledById(candidate))
                return true;
        }

        return false;
    }

    private bool IsModInstalledById(string? modId)
    {
        var normalized = NormalizeModId(modId);
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        return _normalizedInstalledModIds.Contains(normalized);
    }

    private void AddInstalledModId(string? modId)
    {
        var normalized = NormalizeModId(modId);
        if (string.IsNullOrWhiteSpace(normalized)) return;

        _normalizedInstalledModIds.Add(normalized);
    }

    private void RefreshInstalledFlags()
    {
        foreach (var mod in ModsList)
        {
            mod.IsInstalled = IsModInstalled(mod);
        }
    }

    private static IEnumerable<string> GetCandidateModIds(DownloadableModOnList mod)
    {
        if (mod.ModId > 0)
            yield return mod.ModId.ToString(CultureInfo.InvariantCulture);

        if (mod.ModIdStrings is { Count: > 0 })
        {
            foreach (var id in mod.ModIdStrings)
            {
                if (!string.IsNullOrWhiteSpace(id)) yield return id;
            }
        }

        if (!string.IsNullOrWhiteSpace(mod.Name))
            yield return mod.Name;
    }

    private static string? GetPreferredUserReportModId(DownloadableModOnList mod)
    {
        if (mod.ModIdStrings is { Count: > 0 })
        {
            foreach (var id in mod.ModIdStrings)
            {
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }
        }

        if (mod.ModId > 0)
            return mod.ModId.ToString(CultureInfo.InvariantCulture);

        return null;
    }

    private static string? NormalizeModId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetter(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (char.IsDigit(ch))
            {
                if (builder.Length == 0) builder.Append('m');
                builder.Append(ch);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private List<DownloadableModOnList> ApplyClientSideFilters(List<DownloadableModOnList> mods)
    {
        foreach (var mod in mods)
        {
            mod.IsInstalled = IsModInstalled(mod);
        }

        var filtered = mods.AsEnumerable();

        // Side filter
        if (SelectedSide != "any")
        {
            filtered = filtered.Where(m => m.Side.Equals(SelectedSide, StringComparison.OrdinalIgnoreCase));
        }

        // Installed filter
        if (SelectedInstalledFilter == "installed")
        {
            filtered = filtered.Where(m => m.IsInstalled);
        }
        else if (SelectedInstalledFilter == "not-installed")
        {
            filtered = filtered.Where(m => !m.IsInstalled);
        }

        // Favorites filter
        if (OnlyFavorites)
        {
            filtered = filtered.Where(m => FavoriteMods.Contains(m.ModId));
        }

        return filtered.ToList();
    }

    private bool ShouldApplyRelevantSorting()
    {
        return UseRelevantSearchResults && !string.IsNullOrWhiteSpace(TextFilter);
    }

    private List<DownloadableModOnList> SortByRelevance(IEnumerable<DownloadableModOnList> mods, string query)
    {
        var searchTerms = ExtractSearchTerms(query);
        if (searchTerms.Count == 0)
        {
            return mods.ToList();
        }

        var normalizedPhrase = string.Join(' ', searchTerms);

        return mods
            .Select((mod, index) => new
            {
                Mod = mod,
                Score = CalculateRelevanceScore(mod, searchTerms, normalizedPhrase),
                Index = index
            })
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Mod)
            .ToList();
    }

    private static List<string> ExtractSearchTerms(string query)
    {
        return query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length > 0)
            .Take(10)
            .ToList();
    }

    private double CalculateRelevanceScore(DownloadableModOnList mod, IReadOnlyList<string> terms, string normalizedPhrase)
    {
        double score = 0;

        foreach (var term in terms)
        {
            score += ScoreField(mod.Name, term, TitleWeight);
            score += ScoreField(mod.Author, term, AuthorWeight);
            score += ScoreField(mod.Summary, term, SummaryWeight);
        }

        score += ScoreField(mod.Name, normalizedPhrase, TitleWeight * 1.25);
        score += ScoreField(mod.Author, normalizedPhrase, AuthorWeight * 1.15);
        score += ScoreField(mod.Summary, normalizedPhrase, SummaryWeight * 0.9);

        return score;
    }

    private static double ScoreField(string? field, string term, double weight)
    {
        if (string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(term))
        {
            return 0;
        }

        double score = 0;
        var searchIndex = 0;
        while (searchIndex < field.Length)
        {
            var matchIndex = field.IndexOf(term, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                break;
            }

            score += weight;

            if (matchIndex == 0 || char.IsWhiteSpace(field[matchIndex - 1]))
            {
                score += weight * 0.5;
            }

            var endIndex = matchIndex + term.Length;
            if (endIndex == field.Length || char.IsWhiteSpace(field[endIndex]))
            {
                score += weight * 0.25;
            }

            searchIndex = endIndex;
        }

        return score;
    }

    private async Task PopulateModThumbnailsAsync(IEnumerable<DownloadableModOnList> mods, CancellationToken cancellationToken)
    {
        List<DownloadableModOnList> modsToLoad;
        lock (_modsWithLoadedLogos)
        {
            modsToLoad = mods
                .Where(mod => string.IsNullOrWhiteSpace(mod.LogoFileDatabase) && !_modsWithLoadedLogos.Contains(mod.ModId))
                .ToList();

            foreach (var mod in modsToLoad)
            {
                _modsWithLoadedLogos.Add(mod.ModId);
            }
        }

        if (modsToLoad.Count == 0)
            return;

        const int maxConcurrentLoads = 4;
        using var semaphore = new SemaphoreSlim(maxConcurrentLoads);

        var tasks = modsToLoad.Select(async mod =>
        {
            var semaphoreAcquired = false;
            var logoUpdated = false;
            try
            {
                await semaphore.WaitAsync(cancellationToken);
                semaphoreAcquired = true;
                if (cancellationToken.IsCancellationRequested)
                    return;

                var modIdentifier = mod.ModId;

                var modDetails = await _modApiService.GetModAsync(modIdentifier, cancellationToken);

                var logoUrl = modDetails?.LogoFileDatabase;
                if (!string.IsNullOrWhiteSpace(logoUrl))
                {
                    mod.LogoFileDatabase = logoUrl;
                    logoUpdated = true;
                }
            }
            catch (OperationCanceledException)
            {
                // Respect cancellation without releasing an un-acquired semaphore slot
                throw;
            }
            catch (Exception ex)
            {
                // Log thumbnail loading failures for debugging
                System.Diagnostics.Debug.WriteLine($"[ModBrowser] Failed to load thumbnail for mod {mod.ModId} ({mod.Name}): {ex.Message}");
            }
            finally
            {
                if (semaphoreAcquired)
                {
                    semaphore.Release();
                }

                if (!logoUpdated)
                {
                    lock (_modsWithLoadedLogos)
                    {
                        _modsWithLoadedLogos.Remove(mod.ModId);
                    }
                }
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task PopulateUserReportsAsync(IEnumerable<DownloadableModOnList> mods, CancellationToken cancellationToken)
    {
        // Filter to only mods that haven't had their user reports loaded yet (thread-safe)
        List<DownloadableModOnList> modsToLoad;
        lock (_userReportsLoaded)
        {
            modsToLoad = mods.Where(m => !_userReportsLoaded.Contains(m.ModId)).ToList();
        }

        if (modsToLoad.Count == 0)
            return;

        foreach (var mod in modsToLoad)
        {
            mod.ShowUserReportBadge = false;
            mod.UserReportDisplay = string.Empty;
            mod.UserReportTooltip = "Fetching user reports for this mod version.";
        }

        // Load user reports in parallel with a concurrency limit
        const int maxConcurrentLoads = 5;
        using var semaphore = new SemaphoreSlim(maxConcurrentLoads);
        var tasks = modsToLoad.Select(async mod =>
        {
            // Check cancellation before acquiring semaphore
            if (cancellationToken.IsCancellationRequested) return;

            try
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    await LoadUserReportAsync(mod, cancellationToken);
                    lock (_userReportsLoaded)
                    {
                        _userReportsLoaded.Add(mod.ModId);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelled
            }
            catch (Exception ex)
            {
                mod.ShowUserReportBadge = false;
                mod.UserReportDisplay = "Unavailable";
                mod.UserReportTooltip = string.Format(
                    CultureInfo.CurrentCulture,
                    "Failed to load user reports: {0}",
                    ex.Message);
                lock (_userReportsLoaded)
                {
                    _userReportsLoaded.Add(mod.ModId); // Mark as loaded even if failed to avoid retry
                }
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task LoadUserReportAsync(DownloadableModOnList mod, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_installedGameVersion))
        {
            mod.ShowUserReportBadge = false;
            mod.UserReportDisplay = string.Empty;
            mod.UserReportTooltip = "User reports require a known Vintage Story version.";
            return;
        }

        var modDetails = await _modApiService.GetModAsync(mod.ModId, cancellationToken);
        if (modDetails?.Releases is null || modDetails.Releases.Count == 0)
        {
            mod.ShowUserReportBadge = false;
            mod.UserReportDisplay = string.Empty;
            mod.UserReportTooltip = "No releases available to load user reports.";
            return;
        }

        var latestRelease = modDetails.Releases
            .OrderByDescending(r => DateTime.TryParse(r.Created, out var date) ? date : DateTime.MinValue)
            .FirstOrDefault();

        if (latestRelease is null || string.IsNullOrWhiteSpace(latestRelease.ModVersion))
        {
            mod.ShowUserReportBadge = false;
            mod.UserReportDisplay = string.Empty;
            mod.UserReportTooltip = "Latest release version is unavailable for this mod.";
            return;
        }

        var modId = GetPreferredUserReportModId(mod);
        if (string.IsNullOrWhiteSpace(modId))
        {
            mod.ShowUserReportBadge = false;
            mod.UserReportDisplay = string.Empty;
            mod.UserReportTooltip = "User reports are unavailable for this mod.";
            return;
        }

        var summary = await _voteService.GetVoteSummaryAsync(
            modId,
            latestRelease.ModVersion,
            _installedGameVersion,
            cancellationToken);

        var display = BuildUserReportDisplay(summary);
        mod.ShowUserReportBadge = !string.IsNullOrWhiteSpace(display);
        mod.UserReportDisplay = display ?? string.Empty;
        mod.UserReportTooltip = BuildUserReportTooltip(summary, _installedGameVersion);
    }

    private void ApplyUserReportSummary(DownloadableModOnList mod, ModVersionVoteSummary summary)
    {
        var display = BuildUserReportDisplay(summary);
        mod.ShowUserReportBadge = !string.IsNullOrWhiteSpace(display);
        mod.UserReportDisplay = display ?? string.Empty;
        mod.UserReportTooltip = BuildUserReportTooltip(summary, _installedGameVersion ?? string.Empty);
    }

    private static string? BuildUserReportDisplay(ModVersionVoteSummary? summary)
    {
        if (summary is null || summary.TotalVotes == 0) return null;

        var majority = summary.GetMajorityOption();
        if (majority is null)
        {
            return string.Concat(
                "Mixed (",
                summary.TotalVotes.ToString(CultureInfo.CurrentCulture),
                ")");
        }

        var count = summary.Counts.GetCount(majority.Value);
        var displayName = majority.Value.ToDisplayString();
        return string.Concat(
            displayName,
            " (",
            count.ToString(CultureInfo.CurrentCulture),
            ")");
    }

    private static string BuildUserReportTooltip(ModVersionVoteSummary? summary, string vintageStoryVersion)
    {
        if (summary is null || summary.TotalVotes == 0)
        {
            return string.IsNullOrWhiteSpace(vintageStoryVersion)
                ? "No user reports yet. Click to share your experience."
                : string.Format(
                    CultureInfo.CurrentCulture,
                    "No user reports yet for Vintage Story {0}. Click to share your experience.",
                    vintageStoryVersion);
        }

        var counts = summary.Counts;
        var header = string.IsNullOrWhiteSpace(vintageStoryVersion)
            ? "User reports:"
            : string.Format(CultureInfo.CurrentCulture, "User reports for Vintage Story {0}:", vintageStoryVersion);

        return string.Join(Environment.NewLine, new[]
        {
            header,
            string.Format(CultureInfo.CurrentCulture, "Fully functional ({0})", counts.FullyFunctional),
            string.Format(CultureInfo.CurrentCulture, "No issues noticed ({0})", counts.NoIssuesSoFar),
            string.Format(CultureInfo.CurrentCulture, "Some issues but works ({0})", counts.SomeIssuesButWorks),
            string.Format(CultureInfo.CurrentCulture, "Not functional ({0})", counts.NotFunctional),
            string.Format(CultureInfo.CurrentCulture, "Crashes/Freezes game ({0})", counts.CrashesOrFreezesGame)
        });
    }

    #endregion

    #region Property Changed Handlers

    partial void OnTextFilterChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        if (!_isInitializing)
        {
            IsSearching = true;
            _ = SearchModsAsync();
        }
    }

    partial void OnSelectedAuthorChanged(ModAuthor? value)
    {
        if (!_isInitializing)
        {
            IsSearching = true;
            _ = SearchModsAsync();
        }
    }

    partial void OnSelectedSideChanged(string value)
    {
        if (!_isInitializing)
        {
            IsSearching = true;
            _userConfigService?.SetModBrowserSelectedSide(value);
            _ = SearchModsAsync();
        }
    }

    partial void OnSelectedInstalledFilterChanged(string value)
    {
        if (!_isInitializing)
        {
            IsSearching = true;
            _userConfigService?.SetModBrowserSelectedInstalledFilter(value);
            _ = SearchModsAsync();
        }
    }

    partial void OnOnlyFavoritesChanged(bool value)
    {
        if (!_isInitializing)
        {
            IsSearching = true;
            _userConfigService?.SetModBrowserOnlyFavorites(value);
            _ = SearchModsAsync();
        }
    }

    partial void OnOrderByChanged(string value)
    {
        if (!_isInitializing)
        {
            IsSearching = true;
            _userConfigService?.SetModBrowserOrderBy(value);
            _ = SearchModsAsync();
        }
    }

    partial void OnOrderByDirectionChanged(string value)
    {
        if (!_isInitializing)
        {
            IsSearching = true;
            _userConfigService?.SetModBrowserOrderByDirection(value);
            _ = SearchModsAsync();
        }
    }

    partial void OnUseRelevantSearchResultsChanged(bool value)
    {
        if (!_isInitializing)
        {
            IsSearching = true;
            _userConfigService?.SetModBrowserRelevantSearch(value);
            _ = SearchModsAsync();
        }
    }

    #endregion
}

/// <summary>
/// Represents an order by option with icon.
/// </summary>
public record OrderByOption(string Key, string Display, string Icon);
