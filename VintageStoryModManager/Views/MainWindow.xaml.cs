using CommunityToolkit.Mvvm.Input;
using Microsoft.VisualBasic;
using ModernWpf.Controls;
using SimpleVsManager.Cloud;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.TextFormatting;
using System.Windows.Navigation;
using System.Windows.Threading;
using System.Xml;
using VintageStoryModManager.Models;
using VintageStoryModManager.Services;
using VintageStoryModManager.ViewModels;
using VintageStoryModManager.Views.Dialogs;
using CloudModConfigOption = VintageStoryModManager.Views.Dialogs.CloudModlistDetailsDialog.CloudModConfigOption;
using FileRecycleOption = Microsoft.VisualBasic.FileIO.RecycleOption;
using FileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using FileUIOption = Microsoft.VisualBasic.FileIO.UIOption;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WinForms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
using WpfMessageBox = VintageStoryModManager.Services.ModManagerMessageBox;
using WpfToolTip = System.Windows.Controls.ToolTip;

namespace VintageStoryModManager.Views;

public partial class MainWindow : Window
{
    private static readonly double ModListScrollMultiplier = DevConfig.ModListScrollMultiplier;
    private static readonly double ModDbDesignScrollMultiplier = DevConfig.ModDbDesignScrollMultiplier;
    private static readonly double LoadMoreScrollThreshold = DevConfig.LoadMoreScrollThreshold;
    private static readonly double HoverOverlayOpacity = DevConfig.HoverOverlayOpacity;
    private static readonly double SelectionOverlayOpacity = DevConfig.SelectionOverlayOpacity;
    private static readonly double ModInfoPanelHorizontalOverhang = DevConfig.ModInfoPanelHorizontalOverhang;
    private static readonly double DefaultModInfoPanelLeft = DevConfig.DefaultModInfoPanelLeft;
    private static readonly double DefaultModInfoPanelTop = DevConfig.DefaultModInfoPanelTop;
    private static readonly double DefaultModInfoPanelRightMargin = DevConfig.DefaultModInfoPanelRightMargin;
    private static readonly string ManagerModDatabaseUrl = DevConfig.ManagerModDatabaseUrl;
    private static readonly string ManagerModDatabaseModId = DevConfig.ManagerModDatabaseModId;
    private static readonly string ModDatabaseUnavailableMessage = DevConfig.ModDatabaseUnavailableMessage;
    private static readonly string PresetDirectoryName = DevConfig.PresetDirectoryName;
    private static readonly string ModListDirectoryName = DevConfig.ModListDirectoryName;
    private static readonly string CloudModListCacheDirectoryName = DevConfig.CloudModListCacheDirectoryName;
    private static readonly string BackupDirectoryName = DevConfig.BackupDirectoryName;
    private static readonly int AutomaticConfigMaxWordDistance = DevConfig.AutomaticConfigMaxWordDistance;
    private static readonly HttpClient ConnectivityTestHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly string[] ExperimentalModDebugLogPrefixes =
    {
        "client-debug",
        "client-main",
        "server-debug",
        "server-main"
    };

    private static readonly string[] ExperimentalModDebugLogExtensions =
    {
        ".txt",
        ".log"
    };

    private static readonly string[] ExperimentalModDebugIgnoredLinePhrases =
    {
        "Check for mod systems in mod ",
        "Loaded assembly ",
        "Instantiate mod systems for ",
        "Starting system:",
        "Mods, sorted by dependency:",
        "External Origins in load order:"
    };

    // Patterns for summarizing repetitive log lines
    private static readonly string[] SummarizableLinePrefixes =
    {
        "Patch file",
        "Lang key not found:",
        "[Config lib] Values patched:",
        "Loading sound file, game may stutter",
        "[Config lib] Patched",
        "Block must have a unique code",
        "Failed resolving a blocks blockdrop or smeltedstack",
        "Missing mapping for texture code"

    };

    // Summary key prefixes to avoid collisions between different summary types
    private const string SummaryKeyPatchModPrefix = "__PATCH_MOD__";
    private const string SummaryKeyLinePrefix = "__PREFIX__";

    private static readonly Regex PatchAssetMissingRegex = new(
        @"\bPatch \d+ in (?<mod>[^:\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly record struct PresetLoadOptions(bool ApplyModStatus, bool ApplyModVersions, bool ForceExclusive);

    private enum ModlistLoadMode
    {
        Replace,
        Add
    }

    private static readonly PresetLoadOptions StandardPresetLoadOptions = new(true, false, false);
    private static readonly PresetLoadOptions ModListLoadOptions = new(true, true, true);
    private readonly record struct ManagerDeletionResult(List<string> DeletedPaths, List<string> FailedPaths);
    private readonly record struct InstalledModLogIdentifier(string SearchValue, string DisplayLabel);

    private sealed class ModUsagePromptData
    {
        public ModUsagePromptData(
            IReadOnlyList<ModUsageVoteCandidateViewModel> candidates,
            IReadOnlyList<ModUsageTrackingKey> candidateKeys,
            int skippedCount)
        {
            Candidates = candidates ?? Array.Empty<ModUsageVoteCandidateViewModel>();
            CandidateKeys = candidateKeys ?? Array.Empty<ModUsageTrackingKey>();
            SkippedCount = skippedCount < 0 ? 0 : skippedCount;
        }

        public IReadOnlyList<ModUsageVoteCandidateViewModel> Candidates { get; }

        public IReadOnlyList<ModUsageTrackingKey> CandidateKeys { get; }

        public int SkippedCount { get; }
    }

    private enum InstalledModsColumn
    {
        Active,
        Icon,
        Name,
        Installed,
        Version,
        LatestVersion,
        Downloads,
        Authors,
        Tags,
        UserReports,
        Status,
        Side
    }

    private static readonly DependencyProperty BoundModProperty =
        DependencyProperty.RegisterAttached(
            "BoundMod",
            typeof(ModListItemViewModel),
            typeof(MainWindow));

    private static readonly DependencyProperty BoundModHandlerProperty =
        DependencyProperty.RegisterAttached(
            "BoundModHandler",
            typeof(PropertyChangedEventHandler),
            typeof(MainWindow));

    private static readonly DependencyProperty RowIsHoveredProperty =
        DependencyProperty.RegisterAttached(
            "RowIsHovered",
            typeof(bool),
            typeof(MainWindow));

    private readonly UserConfigurationService _userConfiguration;
    private readonly List<MenuItem> _developerProfileMenuItems = new();
    private MainViewModel? _viewModel;
    private string? _dataDirectory;
    private string? _gameDirectory;
    private string? _customShortcutPath;
    private bool _isInitializing;
    private bool _isApplyingPreset;
    private bool _isWindowActive;

    private DispatcherTimer? _modsWatcherTimer;
    private bool _isAutomaticRefreshRunning;
    private bool _isFullRefreshInProgress;

    private readonly List<ModListItemViewModel> _selectedMods = new();
    private readonly Dictionary<ModListItemViewModel, PropertyChangedEventHandler> _selectedModPropertyHandlers = new();
    private ModListItemViewModel? _selectionAnchor;
    private INotifyCollectionChanged? _modsCollection;
    private bool _isApplyingMultiToggle;
    private readonly ModDatabaseService _modDatabaseService = new();
    private readonly ModUpdateService _modUpdateService = new();
    private readonly ModCompatibilityCommentsService _modCompatibilityCommentsService = new();
    private bool _isModUpdateInProgress;
    private bool _isDependencyResolutionRefreshPending;
    private bool _refreshAfterModlistLoadPending;
    private bool _isRefreshingAfterModlistLoad;
    private ScrollViewer? _modsScrollViewer;
    private ScrollViewer? _modDatabaseCardsScrollViewer;
    private bool _suppressSortPreferenceSave;
    private string? _cachedSortMemberPath;
    private ListSortDirection? _cachedSortDirection;

    public IAsyncRelayCommand RefreshModsUiCommand { get; }
    private SortOption? _cachedSortOption;
    private readonly SemaphoreSlim _backupSemaphore = new(1, 1);
    private readonly SemaphoreSlim _cloudStoreLock = new(1, 1);
    private FirebaseModlistStore? _cloudModlistStore;
    private bool _cloudModlistsLoaded;
    private bool _isCloudModlistRefreshInProgress;
    private CloudModlistListEntry? _selectedCloudModlist;
    private string? _recentLocalModBackupDirectory;
    private List<string>? _recentLocalModBackupModNames;
    private readonly Dictionary<InstalledModsColumn, bool> _installedColumnVisibilityPreferences = new();
    private bool _isSearchingModDatabase;
    private bool _isDraggingModInfoPanel;
    private System.Windows.Point _modInfoDragOffset;
    private GameSessionMonitor? _gameSessionMonitor;
    private bool _hasAppliedInitialModInfoPanelPosition;
    private ModUsagePromptData? _modUsagePromptData;
    private bool _isModUsageDialogOpen;


    public MainWindow()
    {
        RefreshModsUiCommand = new AsyncRelayCommand(
            RefreshModsWithErrorHandlingAsync,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);

        _userConfiguration = new UserConfigurationService();

        InitializeComponent();

        DeveloperProfileManager.CurrentProfileChanged += DeveloperProfileManager_OnCurrentProfileChanged;

        RootGrid.SizeChanged += RootGrid_OnSizeChanged;

        UpdateModlistLoadingUiState();

        InitializeColumnVisibilityMenu();

        ApplyStoredWindowDimensions();
        CacheAllVersionsMenuItem.IsChecked = _userConfiguration.CacheAllVersionsLocally;
        RequireExactVsVersionMenuItem.IsChecked = _userConfiguration.RequireExactVsVersionMatch;
        DisableInternetAccessMenuItem.IsChecked = _userConfiguration.DisableInternetAccess;
        InternetAccessManager.SetInternetAccessDisabled(_userConfiguration.DisableInternetAccess);
        StatusLogService.IsLoggingEnabled = _userConfiguration.EnableDebugLogging;

        UpdateThemeMenuSelection(_userConfiguration.ColorTheme);

        if (ManagerVersionMenuItem is not null)
        {
            string? managerVersion = GetManagerInformationalVersion();
            if (string.IsNullOrWhiteSpace(managerVersion))
            {
                ManagerVersionMenuItem.Visibility = Visibility.Collapsed;
            }
            else
            {
                ManagerVersionMenuItem.Header = $"Version: {managerVersion}";
                ManagerVersionMenuItem.Visibility = Visibility.Visible;
            }
        }

        UpdateModlistAutoLoadMenu(_userConfiguration.ModlistAutoLoadBehavior);

        TryInitializePaths();
        RefreshDeveloperProfilesMenuEntries();

        UpdateGameVersionMenuItem(VintageStoryVersionLocator.GetInstalledVersion(_gameDirectory));

        if (!string.IsNullOrWhiteSpace(_dataDirectory))
        {
            try
            {
                InitializeViewModel();
            }
            catch (Exception ex)
            {
                HandleViewModelInitializationFailure(ex);
            }
        }

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_OnClosing;
        InternetAccessManager.InternetAccessChanged += InternetAccessManager_OnInternetAccessChanged;

        UpdateCloudModlistControlsEnabledState();
    }

    private void InitializeColumnVisibilityMenu()
    {
        RegisterColumnMenuItem(ActiveColumnMenuItem, InstalledModsColumn.Active);
        RegisterColumnMenuItem(IconColumnMenuItem, InstalledModsColumn.Icon);
        RegisterColumnMenuItem(NameColumnMenuItem, InstalledModsColumn.Name);
        RegisterColumnMenuItem(VersionColumnMenuItem, InstalledModsColumn.Version);
        RegisterColumnMenuItem(LatestVersionColumnMenuItem, InstalledModsColumn.LatestVersion);
        RegisterColumnMenuItem(AuthorsColumnMenuItem, InstalledModsColumn.Authors);
        RegisterColumnMenuItem(TagsColumnMenuItem, InstalledModsColumn.Tags);
        RegisterColumnMenuItem(UserReportsColumnMenuItem, InstalledModsColumn.UserReports);
        RegisterColumnMenuItem(StatusColumnMenuItem, InstalledModsColumn.Status);
        RegisterColumnMenuItem(SideColumnMenuItem, InstalledModsColumn.Side);

        UpdateColumnVisibility();
    }

    private void RegisterColumnMenuItem(MenuItem? menuItem, InstalledModsColumn column)
    {
        if (menuItem == null)
        {
            return;
        }

        menuItem.Tag = column;
        if (_userConfiguration.GetInstalledColumnVisibility(column.ToString()) is bool storedVisibility)
        {
            menuItem.IsChecked = storedVisibility;
        }
        _installedColumnVisibilityPreferences[column] = menuItem.IsChecked;
        menuItem.Checked += InstalledModsColumnMenuItem_OnChecked;
        menuItem.Unchecked += InstalledModsColumnMenuItem_OnChecked;
    }

    private void InstalledModsColumnMenuItem_OnChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not InstalledModsColumn column)
        {
            return;
        }

        _installedColumnVisibilityPreferences[column] = menuItem.IsChecked;
        _userConfiguration.SetInstalledColumnVisibility(column.ToString(), menuItem.IsChecked);
        UpdateColumnVisibility();
    }

    private void UpdateColumnVisibility()
    {
        bool isSearching = _isSearchingModDatabase;
        bool isCompactView = _viewModel?.IsCompactView == true;

        SetColumnVisibility(ActiveColumn, InstalledModsColumn.Active, !isSearching);
        SetColumnVisibility(IconColumn, InstalledModsColumn.Icon, !isSearching && !isCompactView);
        SetColumnVisibility(NameColumn, InstalledModsColumn.Name, true);
        SetColumnVisibility(InstalledColumn, InstalledModsColumn.Installed, isSearching);
        SetColumnVisibility(VersionColumn, InstalledModsColumn.Version, !isSearching);
        SetColumnVisibility(LatestVersionColumn, InstalledModsColumn.LatestVersion, true);
        SetColumnVisibility(DownloadsColumn, InstalledModsColumn.Downloads, isSearching);
        SetColumnVisibility(AuthorsColumn, InstalledModsColumn.Authors, true);
        SetColumnVisibility(TagsColumn, InstalledModsColumn.Tags, true);
        SetColumnVisibility(UserReportsColumn, InstalledModsColumn.UserReports, true);
        SetColumnVisibility(StatusColumn, InstalledModsColumn.Status, !isSearching);
        SetColumnVisibility(SideColumn, InstalledModsColumn.Side, true);
    }

    private void SetColumnVisibility(DataGridColumn? column, InstalledModsColumn columnKey, bool isContextuallyVisible)
    {
        if (column == null)
        {
            return;
        }

        bool isPreferredVisible = _installedColumnVisibilityPreferences.TryGetValue(columnKey, out bool preference)
            ? preference
            : true;

        column.Visibility = isPreferredVisible && isContextuallyVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        ApplyStoredModInfoPanelPosition();

        _userConfiguration.EnablePersistence();

        // Ensure firebase-auth.json is backed up if it exists and hasn't been backed up yet
        FirebaseAnonymousAuthenticator.EnsureStartupBackup(_userConfiguration);

        await CheckAndPromptMigrationAsync().ConfigureAwait(true);

        await PromptCacheRefreshIfNeededAsync().ConfigureAwait(true);

        if (_viewModel != null)
        {
            await InitializeViewModelAsync(_viewModel).ConfigureAwait(true);
            await EnsureInstalledModsCachedAsync(_viewModel).ConfigureAwait(true);
            await CreateAppStartedBackupAsync().ConfigureAwait(true);
        }

        await RefreshDeleteCachedModsMenuHeaderAsync();
        await RefreshManagerUpdateLinkAsync();
    }

    private async Task PromptCacheRefreshIfNeededAsync()
    {
        if (!_userConfiguration.HasVersionMismatch || _userConfiguration.SuppressRefreshCachePrompt)
        {
            return;
        }

        string currentVersion = _userConfiguration.ModManagerVersion;
        string? previousVersion = _userConfiguration.PreviousModManagerVersion
            ?? _userConfiguration.PreviousConfigurationVersion;

        string message = previousVersion is null
            ? $"Simple VS Manager {currentVersion} is now installed. Clearing cached mod data is recommended after updates to avoid stale information.\n\nWould you like to clear the caches now?"
            : $"Simple VS Manager was updated from version {previousVersion} to {currentVersion}. Clearing cached mod data is recommended after updates to avoid stale information.\n\nWould you like to clear the caches now?";

        var buttonOverrides = new MessageDialogButtonContentOverrides
        {
            Yes = "Clear caches now",
            No = "Remind me later"
        };

        var extraButton = new MessageDialogExtraButton("Don't remind me again", MessageBoxResult.Cancel);

        MessageBoxResult result = WpfMessageBox.Show(
            this,
            message,
            "Simple VS Manager",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            extraButton,
            buttonOverrides);

        if (result == MessageBoxResult.Yes)
        {
            await ClearManagerCachesForVersionUpdateAsync().ConfigureAwait(true);
        }
        else if (result == MessageBoxResult.Cancel)
        {
            _userConfiguration.SetSuppressRefreshCachePrompt(true);
        }
    }

    private async Task ClearManagerCachesForVersionUpdateAsync()
    {
        try
        {
            await Task.Run(() => ClearManagerCaches(preserveModCache: false)).ConfigureAwait(true);
            await RefreshDeleteCachedModsMenuHeaderAsync().ConfigureAwait(true);

            WpfMessageBox.Show(
                this,
                "Cached mod data cleared successfully. Fresh data will be downloaded as needed.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(
                this,
                $"Failed to clear cached mod data:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task CheckAndPromptMigrationAsync()
    {
        // Check if migration check has already been completed
        if (_userConfiguration.MigrationCheckCompleted)
        {
            return;
        }

        // Check if migration is needed
        if (!ConfigurationMigrationService.ShouldOfferMigration(out string? oldConfigVersion))
        {
            // Mark as completed even if no migration needed
            _userConfiguration.SetMigrationCheckCompleted();
            return;
        }

        // Prompt the user
        string message = $"Simple VS Manager is moving its configuration and cache files from Documents to AppData/Local for better system integration.\n\n" +
            $"Old location: Documents\\Simple VS Manager\n" +
            $"New location: AppData\\Local\\Simple VS Manager\n\n" +
            $"Would you like to copy your existing settings and cache to the new location?\n\n" +
            $"Selecting 'No' will start fresh with default settings.";

        MessageBoxResult result = WpfMessageBox.Show(
            this,
            message,
            "Simple VS Manager - Configuration Migration",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            await PerformMigrationAsync().ConfigureAwait(true);
        }

        // Mark migration check as completed regardless of user choice
        _userConfiguration.SetMigrationCheckCompleted();
    }

    private Task PerformMigrationAsync()
    {
        // Create and show a blocking dialog
        var stackPanel = new StackPanel();
        stackPanel.VerticalAlignment = VerticalAlignment.Center;
        stackPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;

        var textBlock = new TextBlock();
        textBlock.Text = "Migrating configuration and cache files...";
        textBlock.FontSize = 14;
        textBlock.Margin = new Thickness(20);
        textBlock.TextAlignment = TextAlignment.Center;

        var progressBar = new System.Windows.Controls.ProgressBar();
        progressBar.IsIndeterminate = true;
        progressBar.Width = 300;
        progressBar.Height = 20;
        progressBar.Margin = new Thickness(20);

        stackPanel.Children.Add(textBlock);
        stackPanel.Children.Add(progressBar);

        var progressWindow = new Window();
        progressWindow.Title = "Migrating Configuration";
        progressWindow.Width = 400;
        progressWindow.Height = 150;
        progressWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        progressWindow.Owner = this;
        progressWindow.ResizeMode = ResizeMode.NoResize;
        progressWindow.WindowStyle = WindowStyle.ToolWindow;
        progressWindow.Content = stackPanel;

        bool migrationSuccess = false;

        // Show the dialog and perform migration in background
        progressWindow.Loaded += async (s, e) =>
        {
            await Task.Run(() =>
            {
                migrationSuccess = ConfigurationMigrationService.PerformMigration();
            }).ConfigureAwait(true);

            // Close on UI thread
            progressWindow.Dispatcher.Invoke(() => progressWindow.Close());
        };

        progressWindow.ShowDialog();

        // Show result
        if (migrationSuccess)
        {
            WpfMessageBox.Show(
                this,
                "Configuration and cache files have been successfully migrated to the new location.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            WpfMessageBox.Show(
                this,
                "Migration could not be completed. Starting with default settings.\n\nYou can manually copy files from Documents\\Simple VS Manager to AppData\\Local\\Simple VS Manager if needed.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return Task.CompletedTask;
    }

    private Task TryShowModUsagePromptAsync()
    {
        if (_isModUsageDialogOpen)
        {
            return Task.CompletedTask;
        }

        if (_viewModel is null || !_userConfiguration.IsModUsageTrackingEnabled)
        {
            _modUsagePromptData = null;
            UpdateModUsagePromptIndicator(false);
            return Task.CompletedTask;
        }

        if (!_userConfiguration.HasPendingModUsagePrompt)
        {
            _modUsagePromptData = null;
            UpdateModUsagePromptIndicator(false);
            return Task.CompletedTask;
        }

        ModUsagePromptData? data = PrepareModUsagePromptData();
        if (data is null || data.Candidates.Count == 0)
        {
            _modUsagePromptData = null;
            UpdateModUsagePromptIndicator(false);
            _gameSessionMonitor?.RefreshPromptState();
            return Task.CompletedTask;
        }

        ModUsagePromptData? previousData = _modUsagePromptData;
        _modUsagePromptData = data;
        UpdateModUsagePromptIndicator(true);

        bool shouldLogSkipped = data.SkippedCount > 0
            && (previousData is null
                || previousData.SkippedCount != data.SkippedCount
                || previousData.Candidates.Count != data.Candidates.Count);

        if (shouldLogSkipped)
        {
            StatusLogService.AppendStatus(
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Skipped {0} mod(s) that cannot receive automatic votes.",
                    data.SkippedCount),
                false);
        }

        return Task.CompletedTask;
    }

    private ModUsagePromptData? PrepareModUsagePromptData()
    {
        if (_viewModel is null || !_userConfiguration.IsModUsageTrackingEnabled)
        {
            return null;
        }

        IReadOnlyDictionary<ModUsageTrackingKey, int> usageCounts = _userConfiguration.GetPendingModUsageCounts();
        if (usageCounts.Count == 0)
        {
            _userConfiguration.ResetModUsageTracking();
            return null;
        }

        string? installedGameVersion = _viewModel.InstalledGameVersion;
        if (string.IsNullOrWhiteSpace(installedGameVersion))
        {
            _userConfiguration.ResetModUsageTracking();
            return null;
        }

        installedGameVersion = installedGameVersion.Trim();

        var candidates = new List<ModUsageVoteCandidateViewModel>();
        var candidateKeys = new List<ModUsageTrackingKey>();
        var keysToClear = new List<ModUsageTrackingKey>();
        int skippedCount = 0;

        foreach (KeyValuePair<ModUsageTrackingKey, int> entry in usageCounts.OrderByDescending(pair => pair.Value))
        {
            ModUsageTrackingKey key = entry.Key;
            if (!key.IsValid)
            {
                keysToClear.Add(key);
                continue;
            }

            ModListItemViewModel? mod = _viewModel.FindInstalledModById(key.ModId);
            if (mod is null)
            {
                keysToClear.Add(key);
                skippedCount++;
                continue;
            }

            if (!mod.CanSubmitUserReport)
            {
                keysToClear.Add(key);
                skippedCount++;
                continue;
            }

            string? modVersion = mod.Version;
            if (string.IsNullOrWhiteSpace(modVersion)
                || !string.Equals(modVersion.Trim(), key.ModVersion, StringComparison.OrdinalIgnoreCase))
            {
                keysToClear.Add(key);
                skippedCount++;
                continue;
            }

            if (!string.Equals(installedGameVersion, key.GameVersion, StringComparison.OrdinalIgnoreCase))
            {
                keysToClear.Add(key);
                skippedCount++;
                continue;
            }

            if (mod.UserVoteOption.HasValue)
            {
                keysToClear.Add(key);
                continue;
            }

            candidates.Add(new ModUsageVoteCandidateViewModel(mod, entry.Value, key));
            candidateKeys.Add(key);
        }

        if (keysToClear.Count > 0)
        {
            _userConfiguration.ResetModUsageCounts(keysToClear);
        }

        if (candidates.Count == 0)
        {
            if (skippedCount > 0)
            {
                StatusLogService.AppendStatus("No mods were eligible for automatic \"No issues\" votes.", false);
            }

            if (_userConfiguration.GetPendingModUsageCounts().Count == 0)
            {
                _userConfiguration.ResetModUsageTracking();
            }

            return null;
        }

        return new ModUsagePromptData(candidates, candidateKeys, skippedCount);
    }

    private void UpdateModUsagePromptIndicator(bool isVisible)
    {
        if (ModUsagePromptTextBlock is null)
        {
            return;
        }

        ModUsagePromptTextBlock.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ShowModUsagePromptDialogAsync()
    {
        if (_viewModel is null || !_userConfiguration.IsModUsageTrackingEnabled)
        {
            return;
        }

        if (_isModUsageDialogOpen)
        {
            return;
        }

        ModUsagePromptData? data = _modUsagePromptData ?? PrepareModUsagePromptData();
        if (data is null || data.Candidates.Count == 0)
        {
            _modUsagePromptData = null;
            UpdateModUsagePromptIndicator(false);
            _gameSessionMonitor?.RefreshPromptState();
            return;
        }

        _modUsagePromptData = data;
        _isModUsageDialogOpen = true;

        try
        {
            var dialog = new ModUsageNoIssuesDialog(data.Candidates)
            {
                Owner = this
            };

            _ = dialog.ShowDialog();

            IReadOnlyList<ModUsageTrackingKey> candidateKeys = data.CandidateKeys;

            if (dialog.Result == ModUsageNoIssuesDialogResult.DisableTracking)
            {
                _userConfiguration.DisableModUsageTracking();
                _userConfiguration.ResetModUsageCounts(candidateKeys);
                _gameSessionMonitor?.RefreshPromptState();
                StopGameSessionMonitor();
                return;
            }

            if (dialog.Result != ModUsageNoIssuesDialogResult.SubmitVotes)
            {
                _userConfiguration.ResetModUsageCounts(candidateKeys);
                _gameSessionMonitor?.RefreshPromptState();
                return;
            }

            IReadOnlyList<ModUsageVoteCandidateViewModel> selected = dialog.SelectedCandidates;
            if (selected.Count == 0)
            {
                _userConfiguration.ResetModUsageCounts(candidateKeys);
                _gameSessionMonitor?.RefreshPromptState();
                return;
            }

            _viewModel.EnableUserReportFetching();

            var successfulKeys = new List<ModUsageTrackingKey>();
            var errors = new List<string>();

            using IDisposable? busyScope = _viewModel.EnterBusyScope();
            foreach (ModUsageVoteCandidateViewModel candidate in selected)
            {
                try
                {
                    await _viewModel
                        .SubmitUserReportVoteAsync(candidate.Mod, ModVersionVoteOption.NoIssuesSoFar, null)
                        .ConfigureAwait(true);

                    successfulKeys.Add(candidate.TrackingKey);
                }
                catch (InternetAccessDisabledException ex)
                {
                    errors.Add(ex.Message);
                    break;
                }
                catch (Exception ex)
                {
                    errors.Add(string.Format(
                        CultureInfo.CurrentCulture,
                        "{0}: {1}",
                        candidate.DisplayLabel,
                        ex.Message));
                }
            }

            if (successfulKeys.Count > 0)
            {
                _userConfiguration.CompleteModUsageVotes(successfulKeys);
                StatusLogService.AppendStatus(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "Submitted \"No issues\" votes for {0} mod(s).",
                        successfulKeys.Count),
                    false);
            }

            _userConfiguration.ResetModUsageCounts(candidateKeys);
            _gameSessionMonitor?.RefreshPromptState();

            if (errors.Count > 0)
            {
                string message = string.Join(Environment.NewLine, errors.Distinct(StringComparer.OrdinalIgnoreCase));
                WpfMessageBox.Show(
                    this,
                    "Some votes could not be submitted:" + Environment.NewLine + message,
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else if (successfulKeys.Count > 0)
            {
                WpfMessageBox.Show(
                    this,
                    "Thanks! Your \"No issues\" votes were submitted.",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        finally
        {
            _isModUsageDialogOpen = false;
            _modUsagePromptData = null;
            await TryShowModUsagePromptAsync().ConfigureAwait(true);
        }
    }
    private Task EnsureInstalledModsCachedAsync(MainViewModel viewModel, bool ignoreUserSetting = false)
    {
        if (viewModel is null || (!_userConfiguration.CacheAllVersionsLocally && !ignoreUserSetting))
        {
            return Task.CompletedTask;
        }

        IReadOnlyList<ModListItemViewModel> installedMods = viewModel.GetInstalledModsSnapshot();
        if (installedMods.Count == 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            foreach (ModListItemViewModel mod in installedMods)
            {
                if (mod is null || !mod.IsInstalled)
                {
                    continue;
                }

                ModCacheService.EnsureModCached(mod.ModId, mod.Version, mod.SourcePath, mod.SourceKind);
            }
        });
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isApplyingPreset || _viewModel?.IsLoadingMods == true)
        {
            const string message =
                "A modlist is still being applied. Exiting now may leave some mods missing or disabled. Do you want to exit anyway?";

            MessageBoxResult result = WpfMessageBox.Show(
                message,
                "Simple VS Manager",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        SaveWindowDimensions();
        SaveUploaderName();
        DisposeCurrentViewModel();
        InternetAccessManager.InternetAccessChanged -= InternetAccessManager_OnInternetAccessChanged;
        DeveloperProfileManager.CurrentProfileChanged -= DeveloperProfileManager_OnCurrentProfileChanged;
    }

    private void RootGrid_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isDraggingModInfoPanel)
        {
            return;
        }

        EnsureModInfoPanelWithinBounds(true);
    }

    private void ModInfoBorder_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

        if (IsModInfoDragInitiationBlocked(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _isDraggingModInfoPanel = true;
        _modInfoDragOffset = e.GetPosition(border);
        border.CaptureMouse();
        e.Handled = true;
    }

    private void ModInfoBorder_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingModInfoPanel || sender is not Border border)
        {
            return;
        }

        System.Windows.Point pointerPosition = e.GetPosition(RootGrid);
        double left = pointerPosition.X - _modInfoDragOffset.X;
        double top = pointerPosition.Y - _modInfoDragOffset.Y;

        SetModInfoPanelPosition(left, top, persist: false);
        e.Handled = true;
    }

    private void ModInfoBorder_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingModInfoPanel || sender is not Border border)
        {
            return;
        }

        _isDraggingModInfoPanel = false;
        border.ReleaseMouseCapture();
        EnsureModInfoPanelWithinBounds(true);
        e.Handled = true;
    }

    private void ModInfoBorder_OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingModInfoPanel)
        {
            return;
        }

        _isDraggingModInfoPanel = false;
        EnsureModInfoPanelWithinBounds(true);
    }

    private void ApplyStoredModInfoPanelPosition()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            double left = _userConfiguration.ModInfoPanelLeft ?? ComputeDefaultModInfoPanelLeft();
            double top = _userConfiguration.ModInfoPanelTop ?? DefaultModInfoPanelTop;

            SetModInfoPanelPosition(left, top, persist: false);
            _hasAppliedInitialModInfoPanelPosition = true;
        }), DispatcherPriority.Loaded);
    }

    private void EnsureModInfoPanelWithinBounds(bool persist)
    {
        if (MODINFO_border is null)
        {
            return;
        }

        double left = Canvas.GetLeft(MODINFO_border);
        if (double.IsNaN(left))
        {
            left = _userConfiguration.ModInfoPanelLeft ?? ComputeDefaultModInfoPanelLeft();
        }

        double top = Canvas.GetTop(MODINFO_border);
        if (double.IsNaN(top))
        {
            top = _userConfiguration.ModInfoPanelTop ?? DefaultModInfoPanelTop;
        }

        bool shouldPersist = persist && _hasAppliedInitialModInfoPanelPosition;
        SetModInfoPanelPosition(left, top, shouldPersist);
    }

    private void SetModInfoPanelPosition(double left, double top, bool persist)
    {
        if (RootGrid is null || MODINFO_border is null)
        {
            return;
        }

        double containerWidth = RootGrid.ActualWidth;
        double containerHeight = RootGrid.ActualHeight;

        if (containerWidth <= 0 || containerHeight <= 0)
        {
            Dispatcher.BeginInvoke(new Action(() => SetModInfoPanelPosition(left, top, persist)), DispatcherPriority.Loaded);
            return;
        }

        double panelWidth = GetModInfoPanelWidth();
        double panelHeight = GetModInfoPanelHeight();

        double minLeft = -ModInfoPanelHorizontalOverhang;
        double maxLeft = Math.Max(minLeft, containerWidth - panelWidth + ModInfoPanelHorizontalOverhang);
        double minTop = 0;
        double maxTop = Math.Max(minTop, containerHeight - panelHeight);

        double clampedLeft = Math.Min(Math.Max(left, minLeft), maxLeft);
        double clampedTop = Math.Min(Math.Max(top, minTop), maxTop);

        Canvas.SetLeft(MODINFO_border, clampedLeft);
        Canvas.SetTop(MODINFO_border, clampedTop);

        if (persist)
        {
            _userConfiguration.SetModInfoPanelPosition(clampedLeft, clampedTop);
        }
    }

    private double ComputeDefaultModInfoPanelLeft()
    {
        double containerWidth = RootGrid?.ActualWidth ?? ActualWidth;

        if (containerWidth <= 0)
        {
            containerWidth = Width;
        }

        double panelWidth = GetModInfoPanelWidth();

        double preferredLeft = DefaultModInfoPanelLeft;

        if (containerWidth > 0 && panelWidth > 0)
        {
            double rightAlignedLeft = containerWidth - panelWidth - DefaultModInfoPanelRightMargin;
            if (!double.IsNaN(rightAlignedLeft))
            {
                preferredLeft = Math.Min(preferredLeft, rightAlignedLeft);
            }

            double minLeft = -ModInfoPanelHorizontalOverhang;
            double maxLeft = Math.Max(minLeft, containerWidth - panelWidth + ModInfoPanelHorizontalOverhang);

            return Math.Min(Math.Max(preferredLeft, minLeft), maxLeft);
        }

        return preferredLeft;
    }

    private double GetModInfoPanelWidth()
    {
        if (MODINFO_border is null)
        {
            return 0;
        }

        double width = MODINFO_border.ActualWidth;
        if (width <= 0)
        {
            width = MODINFO_border.Width;
        }

        return double.IsNaN(width) ? 0 : width;
    }

    private double GetModInfoPanelHeight()
    {
        if (MODINFO_border is null)
        {
            return 0;
        }

        double height = MODINFO_border.ActualHeight;
        if (height <= 0)
        {
            height = MODINFO_border.Height;
        }

        return double.IsNaN(height) ? 0 : height;
    }

    private static bool IsModInfoDragInitiationBlocked(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Border border && border.Name == nameof(MODINFO_border))
            {
                break;
            }

            if (source is System.Windows.Controls.Primitives.ButtonBase || source is Selector || source is System.Windows.Controls.Primitives.TextBoxBase || source is Hyperlink || source is Slider || source is System.Windows.Controls.Primitives.ScrollBar)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void DisableInternetAccessMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
        {
            return;
        }

        bool isDisabled = menuItem.IsChecked;
        InternetAccessManager.SetInternetAccessDisabled(isDisabled);
        _userConfiguration.SetDisableInternetAccess(isDisabled);

        _viewModel?.OnInternetAccessStateChanged();
    }

    private void ThemeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not ColorTheme theme)
        {
            return;
        }

        if (theme == ColorTheme.Custom)
        {
            ShowCustomThemeEditor();
            return;
        }

        ColorTheme currentTheme = _userConfiguration.ColorTheme;
        IReadOnlyDictionary<string, string>? paletteOverride = null;

        if (theme == ColorTheme.SurpriseMe)
        {
            paletteOverride = GenerateSurprisePalette();
        }

        if (theme == currentTheme && paletteOverride is null)
        {
            UpdateThemeMenuSelection(currentTheme);
            return;
        }

        UpdateThemeMenuSelection(theme);
        _userConfiguration.SetColorTheme(theme, paletteOverride);
        IReadOnlyDictionary<string, string> palette = _userConfiguration.GetThemePaletteColors();
        App.ApplyTheme(theme, palette.Count > 0 ? palette : null);
    }

    private void UpdateThemeMenuSelection(ColorTheme theme)
    {
        if (VintageStoryThemeMenuItem is not null)
        {
            VintageStoryThemeMenuItem.IsChecked = theme == ColorTheme.VintageStory;
        }

        if (DarkThemeMenuItem is not null)
        {
            DarkThemeMenuItem.IsChecked = theme == ColorTheme.Dark;
        }

        if (LightThemeMenuItem is not null)
        {
            LightThemeMenuItem.IsChecked = theme == ColorTheme.Light;
        }

        if (SurpriseMeThemeMenuItem is not null)
        {
            SurpriseMeThemeMenuItem.IsChecked = theme == ColorTheme.SurpriseMe;
        }

        if (CustomThemeMenuItem is not null)
        {
            CustomThemeMenuItem.IsChecked = theme == ColorTheme.Custom;
        }
    }

    private static IReadOnlyDictionary<string, string> GenerateSurprisePalette()
    {
        IReadOnlyDictionary<string, string> defaults = UserConfigurationService.GetDefaultThemePalette(ColorTheme.VintageStory);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in defaults)
        {
            result[pair.Key] = GenerateRandomColor(pair.Value);
        }

        return result;
    }

    private static string GenerateRandomColor(string baseColor)
    {
        byte alpha = 0xFF;

        if (!string.IsNullOrWhiteSpace(baseColor) && baseColor.Length == 9)
        {
            string alphaComponent = baseColor.Substring(1, 2);
            if (byte.TryParse(alphaComponent, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte parsedAlpha))
            {
                alpha = parsedAlpha;
            }
        }

        Span<byte> rgb = stackalloc byte[3];
        RandomNumberGenerator.Fill(rgb);

        return $"#{alpha:X2}{rgb[0]:X2}{rgb[1]:X2}{rgb[2]:X2}";
    }

    private void ShowCustomThemeEditor()
    {
        if (_userConfiguration.ColorTheme != ColorTheme.Custom)
        {
            _userConfiguration.SetColorTheme(ColorTheme.Custom);
        }

        UpdateThemeMenuSelection(ColorTheme.Custom);

        IReadOnlyDictionary<string, string> palette = _userConfiguration.GetThemePaletteColors();
        App.ApplyTheme(ColorTheme.Custom, palette.Count > 0 ? palette : null);

        var dialog = new ThemePaletteEditorDialog(_userConfiguration)
        {
            Owner = this
        };

        _ = dialog.ShowDialog();
        UpdateThemeMenuSelection(_userConfiguration.ColorTheme);
    }

    private void AlwaysClearModlistsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        HandleModlistAutoLoadMenuClick(
            sender,
            ModlistAutoLoadBehavior.Replace,
            ModlistAutoLoadBehavior.Prompt);
    }

    private void AlwaysAddModlistsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        HandleModlistAutoLoadMenuClick(
            sender,
            ModlistAutoLoadBehavior.Add,
            ModlistAutoLoadBehavior.Prompt);
    }

    private void HandleModlistAutoLoadMenuClick(object sender, ModlistAutoLoadBehavior enabledBehavior, ModlistAutoLoadBehavior disabledBehavior)
    {
        if (sender is not MenuItem menuItem)
        {
            return;
        }

        ModlistAutoLoadBehavior newBehavior = menuItem.IsChecked ? enabledBehavior : disabledBehavior;
        SetModlistAutoLoadBehavior(newBehavior);
    }

    private void SetModlistAutoLoadBehavior(ModlistAutoLoadBehavior behavior)
    {
        UpdateModlistAutoLoadMenu(behavior);
        _userConfiguration.SetModlistAutoLoadBehavior(behavior);
    }

    private void UpdateModlistAutoLoadMenu(ModlistAutoLoadBehavior behavior)
    {
        if (AlwaysClearModlistsMenuItem is not null)
        {
            AlwaysClearModlistsMenuItem.IsChecked = behavior == ModlistAutoLoadBehavior.Replace;
        }

        if (AlwaysAddModlistsMenuItem is not null)
        {
            AlwaysAddModlistsMenuItem.IsChecked = behavior == ModlistAutoLoadBehavior.Add;
        }
    }

    private void UpdateGameVersionMenuItem(string? gameVersion)
    {
        if (GameVersionMenuItem is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            GameVersionMenuItem.Visibility = Visibility.Collapsed;
            return;
        }

        GameVersionMenuItem.Header = $"Vintage Story: {gameVersion}";
        GameVersionMenuItem.Visibility = Visibility.Visible;
    }

    private void ApplyStoredWindowDimensions()
    {
        double? storedWidth = _userConfiguration.WindowWidth;
        double? storedHeight = _userConfiguration.WindowHeight;

        if (!storedWidth.HasValue && !storedHeight.HasValue)
        {
            return;
        }

        SizeToContent = SizeToContent.Manual;

        if (storedWidth.HasValue)
        {
            Width = storedWidth.Value;
        }

        if (storedHeight.HasValue)
        {
            Height = storedHeight.Value;
        }
    }

    private void SaveWindowDimensions()
    {
        if (_userConfiguration is null)
        {
            return;
        }

        Rect bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;

        _userConfiguration.SetWindowDimensions(bounds.Width, bounds.Height);
    }

    private void SetUsernameDisplay(string? name)
    {
        string? sanitized = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        if (UsernameTextbox is not null)
        {
            UsernameTextbox.Text = sanitized ?? string.Empty;
        }

        _userConfiguration.SetCloudUploaderName(sanitized);
    }

    private void ApplyPlayerIdentityToUiAndCloudStore()
    {
        SetUsernameDisplay(_viewModel?.PlayerName);

        if (_cloudModlistStore is not null)
        {
            ApplyPlayerIdentityToCloudStore(_cloudModlistStore);
        }
    }

    private void ApplyPlayerIdentityToCloudStore(FirebaseModlistStore? store)
    {
        if (store is null)
        {
            return;
        }

        store.SetPlayerIdentity(_viewModel?.PlayerUid, _viewModel?.PlayerName);
    }

    private void SaveUploaderName()
    {
        if (_userConfiguration is null)
        {
            return;
        }

        string? uploader = UsernameTextbox?.Text;
        SetUsernameDisplay(uploader);
    }

    private void CacheAllVersionsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
        {
            return;
        }

        _userConfiguration.SetCacheAllVersionsLocally(menuItem.IsChecked);
        menuItem.IsChecked = _userConfiguration.CacheAllVersionsLocally;
    }

    private void RequireExactVsVersionMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
        {
            return;
        }

        _userConfiguration.SetRequireExactVsVersionMatch(menuItem.IsChecked);
        menuItem.IsChecked = _userConfiguration.RequireExactVsVersionMatch;
    }

    private void EnableDebugLoggingMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
        {
            return;
        }

        _userConfiguration.SetEnableDebugLogging(menuItem.IsChecked);
        bool isEnabled = _userConfiguration.EnableDebugLogging;
        menuItem.IsChecked = isEnabled;
        StatusLogService.IsLoggingEnabled = isEnabled;
    }

    private async void ExperimentalModDebuggingMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedMod is not ModListItemViewModel selectedMod)
        {
            WpfMessageBox.Show(
                "Please select a mod before using Experimental Mod Debugging.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string modId = selectedMod.ModId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(modId))
        {
            WpfMessageBox.Show(
                "The selected mod does not specify a mod ID to search for in the logs.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_dataDirectory))
        {
            WpfMessageBox.Show(
                "The manager data directory is not available. Please configure the Vintage Story data folder and try again.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string logsDirectory = Path.Combine(_dataDirectory, "Logs");
        if (!Directory.Exists(logsDirectory))
        {
            WpfMessageBox.Show(
                "No log files were found in the manager's Logs folder.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        List<ExperimentalModDebugLogLine> logLines;
        IDisposable? busyScope = _viewModel?.EnterBusyScope();
        try
        {
            logLines = await Task.Run(() => CollectExperimentalModDebugLines(logsDirectory, modId)).ConfigureAwait(true);
        }
        finally
        {
            busyScope?.Dispose();
        }
        if (logLines.Count == 0)
        {
            logLines.Add(ExperimentalModDebugLogLine.FromPlainText(
                $"No log entries referencing '{modId}' were found in client-debug, client-main, server-debug, or server-main logs."));
        }

        var dialog = new ExperimentalModDebugDialog(modId, logLines)
        {
            Owner = this
        };

        _ = dialog.ShowDialog();
    }

    private async void ExperimentalAllModsDebuggingMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            WpfMessageBox.Show(
                "The installed mod list is not available. Please wait for the manager to finish loading and try again.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        IReadOnlyList<ModListItemViewModel> installedMods = _viewModel.GetInstalledModsSnapshot();
        var modIdentifiers = new List<InstalledModLogIdentifier>();
        var seenModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ModListItemViewModel mod in installedMods)
        {
            if (mod is null)
            {
                continue;
            }

            string? modId = mod.ModId;
            if (string.IsNullOrWhiteSpace(modId))
            {
                continue;
            }

            string trimmedModId = modId.Trim();
            if (!seenModIds.Add(trimmedModId))
            {
                continue;
            }

            string? displayName = mod.DisplayName;
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                displayName = displayName.Trim();
            }

            string displayLabel = string.IsNullOrWhiteSpace(displayName)
                ? trimmedModId
                : $"{displayName} ({trimmedModId})";

            modIdentifiers.Add(new InstalledModLogIdentifier(trimmedModId, displayLabel));
        }

        if (modIdentifiers.Count == 0)
        {
            WpfMessageBox.Show(
                "No installed mods with a valid mod ID were found to search for in the logs.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_dataDirectory))
        {
            WpfMessageBox.Show(
                "The manager data directory is not available. Please configure the Vintage Story data folder and try again.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string logsDirectory = Path.Combine(_dataDirectory, "Logs");
        if (!Directory.Exists(logsDirectory))
        {
            WpfMessageBox.Show(
                "No log files were found in the manager's Logs folder.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        List<ExperimentalModDebugLogLine> logLines;
        IDisposable? busyScope = _viewModel.EnterBusyScope();
        try
        {
            await Task.Yield();
            logLines = await Task.Run(() => CollectInstalledModDebugLines(logsDirectory, modIdentifiers)).ConfigureAwait(true);
        }
        finally
        {
            busyScope.Dispose();
        }
        if (logLines.Count == 0)
        {
            logLines.Add(ExperimentalModDebugLogLine.FromPlainText(
                "No log entries referencing the installed mods were found in client-debug, client-main, server-debug, or server-main logs."));
        }

        var dialog = new ExperimentalModDebugDialog(
            "Log entries referencing installed mods",
            "No log entries referencing installed mods were found.",
            logLines)
        {
            Owner = this
        };

        _ = dialog.ShowDialog();
    }

    private static List<ExperimentalModDebugLogLine> CollectExperimentalModDebugLines(string logsDirectory, string modId)
    {
        var logLines = new List<ExperimentalModDebugLogLine>();
        foreach (string filePath in GetExperimentalModDebugFilePaths(logsDirectory))
        {
            AppendExperimentalModDebugLines(logLines, filePath, modId);
        }

        return logLines;
    }

    private static List<ExperimentalModDebugLogLine> CollectInstalledModDebugLines(
        string logsDirectory,
        IReadOnlyList<InstalledModLogIdentifier> modIdentifiers)
    {
        var logLines = new List<ExperimentalModDebugLogLine>();
        if (modIdentifiers.Count == 0)
        {
            return logLines;
        }

        foreach (string filePath in GetExperimentalModDebugFilePaths(logsDirectory))
        {
            AppendInstalledModDebugLines(logLines, filePath, modIdentifiers);
        }

        return logLines;
    }

    private static List<string> GetExperimentalModDebugFilePaths(string logsDirectory)
    {
        var filePaths = new List<string>();
        var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string prefix in ExperimentalModDebugLogPrefixes)
        {
            foreach (string extension in ExperimentalModDebugLogExtensions)
            {
                string pattern = extension.StartsWith('.') ? $"{prefix}*{extension}" : $"{prefix}*.{extension}";
                try
                {
                    foreach (string path in Directory.EnumerateFiles(logsDirectory, pattern, SearchOption.TopDirectoryOnly))
                    {
                        if (processedFiles.Add(path))
                        {
                            filePaths.Add(path);
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            string directPath = Path.Combine(logsDirectory, prefix);
            if (File.Exists(directPath) && processedFiles.Add(directPath))
            {
                filePaths.Add(directPath);
            }
        }

        filePaths.Sort(StringComparer.OrdinalIgnoreCase);
        return filePaths;
    }

    private static void AppendExperimentalModDebugLines(
        List<ExperimentalModDebugLogLine> logLines,
        string filePath,
        string modId)
    {
        try
        {
            string fileName = Path.GetFileName(filePath);
            var matchedLines = new List<(string Line, int LineNumber)>();
            int lineNumber = 0;
            foreach (string line in File.ReadLines(filePath))
            {
                lineNumber++;
                if (ShouldIgnoreExperimentalModDebugLine(line))
                {
                    continue;
                }

                if (line.IndexOf(modId, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matchedLines.Add((line, lineNumber));
                }
            }

            AppendExperimentalModDebugFileSectionWithModId(logLines, filePath, fileName, matchedLines, modId);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void AppendInstalledModDebugLines(
        List<ExperimentalModDebugLogLine> logLines,
        string filePath,
        IReadOnlyList<InstalledModLogIdentifier> modIdentifiers)
    {
        try
        {
            string fileName = Path.GetFileName(filePath);
            var matchedLines = new List<(string Line, string ModName, int LineNumber)>();
            int lineNumber = 0;
            foreach (string line in File.ReadLines(filePath))
            {
                lineNumber++;
                if (ShouldIgnoreExperimentalModDebugLine(line))
                {
                    continue;
                }

                foreach (InstalledModLogIdentifier identifier in modIdentifiers)
                {
                    if (line.IndexOf(identifier.SearchValue, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matchedLines.Add((line, identifier.DisplayLabel, lineNumber));
                        break;
                    }
                }
            }

            AppendExperimentalModDebugFileSectionWithMods(logLines, filePath, fileName, matchedLines);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void AppendExperimentalModDebugFileSection(
        List<ExperimentalModDebugLogLine> logLines,
        string fileName,
        List<string> matchedLines)
    {
        if (matchedLines.Count == 0)
        {
            return;
        }

        List<string> processedLines = SummarizePatchMissingLines(matchedLines);
        if (processedLines.Count == 0)
        {
            return;
        }

        logLines.Add(ExperimentalModDebugLogLine.FromPlainText($"**{fileName}**"));
        foreach (string line in processedLines)
        {
            logLines.Add(ExperimentalModDebugLogLine.FromLogEntry(line));
        }
    }

    private static void AppendExperimentalModDebugFileSectionWithModId(
        List<ExperimentalModDebugLogLine> logLines,
        string filePath,
        string fileName,
        List<(string Line, int LineNumber)> matchedLines,
        string modId)
    {
        if (matchedLines.Count == 0)
        {
            return;
        }

        List<(string Line, int LineNumber)> processedLines = SummarizePatchMissingLinesWithLineNumbers(matchedLines);
        if (processedLines.Count == 0)
        {
            return;
        }

        logLines.Add(ExperimentalModDebugLogLine.FromPlainText($"**{fileName}**"));

        foreach (var (line, lineNumber) in processedLines)
        {
            logLines.Add(ExperimentalModDebugLogLine.FromLogEntry(line, modId, filePath, lineNumber));
        }
    }

    private static void AppendExperimentalModDebugFileSectionWithMods(
        List<ExperimentalModDebugLogLine> logLines,
        string filePath,
        string fileName,
        List<(string Line, string ModName, int LineNumber)> matchedLines)
    {
        if (matchedLines.Count == 0)
        {
            return;
        }

        List<(string Line, string ModName, int LineNumber)> processedLines = SummarizePatchMissingLinesWithModAndLineNumbers(matchedLines);
        if (processedLines.Count == 0)
        {
            return;
        }

        logLines.Add(ExperimentalModDebugLogLine.FromPlainText($"**{fileName}**"));

        foreach (var (line, modName, lineNumber) in processedLines)
        {
            logLines.Add(ExperimentalModDebugLogLine.FromLogEntry(line, modName, filePath, lineNumber));
        }
    }

    private static bool ShouldIgnoreExperimentalModDebugLine(string line)
    {
        foreach (string phrase in ExperimentalModDebugIgnoredLinePhrases)
        {
            if (line.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> SummarizePatchMissingLines(List<string> matchedLines)
    {
        if (matchedLines.Count == 0)
        {
            return matchedLines;
        }

        var summarized = new List<string>(matchedLines.Count);
        var lineSummaries = new Dictionary<string, (int Index, int HiddenCount)>(StringComparer.OrdinalIgnoreCase);

        foreach (string line in matchedLines)
        {
            // Try to match "Patch X in [mod]" pattern
            Match patchMatch = PatchAssetMissingRegex.Match(line);
            if (patchMatch.Success)
            {
                string modId = patchMatch.Groups["mod"].Value;
                if (!string.IsNullOrEmpty(modId))
                {
                    string summaryKey = $"{SummaryKeyPatchModPrefix}{modId.Trim()}";
                    AddOrIncrementSummary(summarized, lineSummaries, line, summaryKey);
                    continue;
                }
            }

            // Try to match summarizable prefixes
            string? matchedPrefix = null;
            foreach (string prefix in SummarizableLinePrefixes)
            {
                if (line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matchedPrefix = prefix;
                    break;
                }
            }

            if (matchedPrefix != null)
            {
                // Create a summary key based on the prefix
                string summaryKey = $"{SummaryKeyLinePrefix}{matchedPrefix}";
                AddOrIncrementSummary(summarized, lineSummaries, line, summaryKey);
                continue;
            }

            // No pattern matched, add line as-is
            summarized.Add(line);
        }

        // Append hidden count to summarized lines
        foreach ((int Index, int HiddenCount) value in lineSummaries.Values)
        {
            if (value.HiddenCount <= 0)
            {
                continue;
            }

            int index = value.Index;
            if (index >= 0 && index < summarized.Count)
            {
                summarized[index] = $"{summarized[index]} ({value.HiddenCount} similar lines hidden...)";
            }
        }

        return summarized;
    }

    private static List<(string Line, int LineNumber)> SummarizePatchMissingLinesWithLineNumbers(
        List<(string Line, int LineNumber)> matchedLines)
    {
        if (matchedLines.Count == 0)
        {
            return matchedLines;
        }

        var summarized = new List<(string Line, int LineNumber)>(matchedLines.Count);
        var lineSummaries = new Dictionary<string, (int Index, int HiddenCount)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (line, lineNumber) in matchedLines)
        {
            // Try to match "Patch X in [mod]" pattern
            Match patchMatch = PatchAssetMissingRegex.Match(line);
            if (patchMatch.Success)
            {
                string modId = patchMatch.Groups["mod"].Value;
                if (!string.IsNullOrEmpty(modId))
                {
                    string summaryKey = $"{SummaryKeyPatchModPrefix}{modId.Trim()}";
                    AddOrIncrementSummaryWithLineNumber(summarized, lineSummaries, line, lineNumber, summaryKey);
                    continue;
                }
            }

            // Try to match summarizable prefixes
            string? matchedPrefix = null;
            foreach (string prefix in SummarizableLinePrefixes)
            {
                if (line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matchedPrefix = prefix;
                    break;
                }
            }

            if (matchedPrefix != null)
            {
                // Create a summary key based on the prefix
                string summaryKey = $"{SummaryKeyLinePrefix}{matchedPrefix}";
                AddOrIncrementSummaryWithLineNumber(summarized, lineSummaries, line, lineNumber, summaryKey);
                continue;
            }

            // No pattern matched, add line as-is with its line number
            summarized.Add((line, lineNumber));
        }

        // Append hidden count to summarized lines
        foreach ((int Index, int HiddenCount) value in lineSummaries.Values)
        {
            if (value.HiddenCount <= 0)
            {
                continue;
            }

            int index = value.Index;
            if (index >= 0 && index < summarized.Count)
            {
                var (line, lineNumber) = summarized[index];
                summarized[index] = ($"{line} ({value.HiddenCount} similar lines hidden...)", lineNumber);
            }
        }

        return summarized;
    }

    private static void AddOrIncrementSummary(
        List<string> summarized,
        Dictionary<string, (int Index, int HiddenCount)> summaries,
        string line,
        string summaryKey)
    {
        if (summaries.TryGetValue(summaryKey, out var entry))
        {
            summaries[summaryKey] = (entry.Index, entry.HiddenCount + 1);
        }
        else
        {
            summaries[summaryKey] = (summarized.Count, 0);
            summarized.Add(line);
        }
    }

    private static void AddOrIncrementSummaryWithLineNumber(
        List<(string Line, int LineNumber)> summarized,
        Dictionary<string, (int Index, int HiddenCount)> summaries,
        string line,
        int lineNumber,
        string summaryKey)
    {
        if (summaries.TryGetValue(summaryKey, out var entry))
        {
            summaries[summaryKey] = (entry.Index, entry.HiddenCount + 1);
        }
        else
        {
            summaries[summaryKey] = (summarized.Count, 0);
            summarized.Add((line, lineNumber));
        }
    }

    private static List<(string Line, string ModName, int LineNumber)> SummarizePatchMissingLinesWithModAndLineNumbers(
        List<(string Line, string ModName, int LineNumber)> matchedLines)
    {
        if (matchedLines.Count == 0)
        {
            return matchedLines;
        }

        var summarized = new List<(string Line, string ModName, int LineNumber)>(matchedLines.Count);
        var lineSummaries = new Dictionary<string, (int Index, int HiddenCount)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (line, modName, lineNumber) in matchedLines)
        {
            // Try to match "Patch X in [mod]" pattern
            Match patchMatch = PatchAssetMissingRegex.Match(line);
            if (patchMatch.Success)
            {
                string modId = patchMatch.Groups["mod"].Value;
                if (!string.IsNullOrEmpty(modId))
                {
                    string summaryKey = $"{SummaryKeyPatchModPrefix}{modId.Trim()}";
                    AddOrIncrementSummaryWithModAndLineNumber(summarized, lineSummaries, line, modName, lineNumber, summaryKey);
                    continue;
                }
            }

            // Try to match summarizable prefixes
            string? matchedPrefix = null;
            foreach (string prefix in SummarizableLinePrefixes)
            {
                if (line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matchedPrefix = prefix;
                    break;
                }
            }

            if (matchedPrefix != null)
            {
                // Create a summary key based on the prefix
                string summaryKey = $"{SummaryKeyLinePrefix}{matchedPrefix}";
                AddOrIncrementSummaryWithModAndLineNumber(summarized, lineSummaries, line, modName, lineNumber, summaryKey);
                continue;
            }

            // No pattern matched, add line as-is with its mod name and line number
            summarized.Add((line, modName, lineNumber));
        }

        // Append hidden count to summarized lines
        foreach ((int Index, int HiddenCount) value in lineSummaries.Values)
        {
            if (value.HiddenCount <= 0)
            {
                continue;
            }

            int index = value.Index;
            if (index >= 0 && index < summarized.Count)
            {
                var (line, modName, lineNumber) = summarized[index];
                summarized[index] = ($"{line} ({value.HiddenCount} similar lines hidden...)", modName, lineNumber);
            }
        }

        return summarized;
    }

    private static void AddOrIncrementSummaryWithModAndLineNumber(
        List<(string Line, string ModName, int LineNumber)> summarized,
        Dictionary<string, (int Index, int HiddenCount)> summaries,
        string line,
        string modName,
        int lineNumber,
        string summaryKey)
    {
        if (summaries.TryGetValue(summaryKey, out var entry))
        {
            summaries[summaryKey] = (entry.Index, entry.HiddenCount + 1);
        }
        else
        {
            summaries[summaryKey] = (summarized.Count, 0);
            summarized.Add((line, modName, lineNumber));
        }
    }

    private void InitializeViewModel()
    {
        if (string.IsNullOrWhiteSpace(_dataDirectory))
        {
            throw new InvalidOperationException("The data directory is not set.");
        }

        _viewModel = new MainViewModel(
            _dataDirectory,
            _userConfiguration,
            _userConfiguration.ModDatabaseSearchResultLimit,
            _userConfiguration.ModDatabaseNewModsRecentMonths,
            _userConfiguration.ModDatabaseAutoLoadMode,
            gameDirectory: _gameDirectory,
            excludeInstalledModDatabaseResults: _userConfiguration.ExcludeInstalledModDatabaseResults,
            onlyShowCompatibleModDatabaseResults: _userConfiguration.OnlyShowCompatibleModDatabaseResults)
        {
            IsCompactView = _userConfiguration.IsCompactView,
            UseModDbDesignView = _userConfiguration.UseModDbDesignView
        };
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        DataContext = _viewModel;
        ApplyPlayerIdentityToUiAndCloudStore();
        _cloudModlistsLoaded = false;
        _selectedCloudModlist = null;
        ApplyCompactViewState(_viewModel.IsCompactView);
        UpdateSearchColumnVisibility(_viewModel.SearchModDatabase);
        AttachToModsView(_viewModel.CurrentModsView);
        RestoreSortPreference();
        UpdateGameVersionMenuItem(_viewModel.InstalledGameVersion);
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedSortOption))
        {
            if (_viewModel != null)
            {
                Dispatcher.Invoke(() =>
                {
                    if (_viewModel != null)
                    {
                        UpdateSortPreferenceFromSelectedOption(!_suppressSortPreferenceSave);
                    }
                });
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.IsCompactView))
        {
            if (_viewModel != null)
            {
                _userConfiguration.SetCompactViewMode(_viewModel.IsCompactView);

                Dispatcher.Invoke(() =>
                {
                    if (_viewModel != null)
                    {
                        ApplyCompactViewState(_viewModel.IsCompactView);
                    }
                });
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.UseModDbDesignView))
        {
            if (_viewModel != null)
            {
                _userConfiguration.SetModDbDesignViewMode(_viewModel.UseModDbDesignView);

                Dispatcher.Invoke(() =>
                {
                    _modsScrollViewer = null;
                    _modDatabaseCardsScrollViewer = null;
                });

                Dispatcher.InvokeAsync(UpdateLoadMoreScrollThresholdState, DispatcherPriority.Background);
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.ExcludeInstalledModDatabaseResults))
        {
            if (_viewModel != null)
            {
                _userConfiguration.SetExcludeInstalledModDatabaseResults(
                    _viewModel.ExcludeInstalledModDatabaseResults);
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.OnlyShowCompatibleModDatabaseResults))
        {
            if (_viewModel != null)
            {
                _userConfiguration.SetOnlyShowCompatibleModDatabaseResults(
                    _viewModel.OnlyShowCompatibleModDatabaseResults);
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedModDatabaseFetchLimit))
        {
            if (_viewModel != null)
            {
                _userConfiguration.SetModDatabaseSearchResultLimit(
                    _viewModel.SelectedModDatabaseFetchLimit);
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.SearchModDatabase))
        {
            if (_viewModel != null)
            {
                Dispatcher.Invoke(() =>
                {
                    if (_viewModel != null)
                    {
                        UpdateSearchColumnVisibility(_viewModel.SearchModDatabase);
                        _modsScrollViewer = null;
                        _modDatabaseCardsScrollViewer = null;
                    }
                });

                Dispatcher.InvokeAsync(UpdateLoadMoreScrollThresholdState, DispatcherPriority.Background);
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.ModDatabaseAutoLoadMode))
        {
            if (_viewModel != null)
            {
                _userConfiguration.SetModDatabaseAutoLoadMode(_viewModel.ModDatabaseAutoLoadMode);
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.IsViewingCloudModlists))
        {
            if (_viewModel != null)
            {
                Dispatcher.Invoke(() =>
                {
                    if (_viewModel != null)
                    {
                        HandleCloudModlistsVisibilityChanged(_viewModel.IsViewingCloudModlists);
                    }
                });
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.IsLoadMoreModDatabaseButtonVisible))
        {
            Dispatcher.Invoke(UpdateLoadMoreScrollThresholdState);
        }
        else if (e.PropertyName == nameof(MainViewModel.CurrentModsView))
        {
            if (_viewModel != null)
            {
                Dispatcher.Invoke(() =>
                {
                    if (_viewModel != null)
                    {
                        AttachToModsView(_viewModel.CurrentModsView);
                    }
                });
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.IsLoadingMods)
                 || e.PropertyName == nameof(MainViewModel.IsLoadingModDetails))
        {
            Dispatcher.Invoke(() =>
            {
                RefreshHoverOverlayState();
                ScheduleRefreshAfterModlistLoadIfReady();
            });
        }
        else if (e.PropertyName == nameof(MainViewModel.StatusMessage))
        {
            string? statusMessage = _viewModel?.StatusMessage;
            if (ShouldRefreshAfterDependencyResolution(statusMessage))
            {
                Dispatcher.InvokeAsync(async () =>
                {
                    await RefreshModsAfterDependencyResolutionAsync().ConfigureAwait(true);
                }, DispatcherPriority.Background);
            }

            Dispatcher.Invoke(ScheduleRefreshAfterModlistLoadIfReady);
        }
    }

    private void ApplyCompactViewState(bool isCompactView)
    {
        UpdateColumnVisibility();
    }

    private void UpdateSearchColumnVisibility(bool isSearchingModDatabase)
    {
        _isSearchingModDatabase = isSearchingModDatabase;
        UpdateColumnVisibility();
        UpdateSearchSortingBehavior(isSearchingModDatabase);
    }

    private void HandleCloudModlistsVisibilityChanged(bool isVisible)
    {
        if (isVisible)
        {
            if (!EnsureCloudModlistsConsent())
            {
                _viewModel?.ShowInstalledModsCommand.Execute(null);
                return;
            }

            _ = RefreshCloudModlistsAsync(force: !_cloudModlistsLoaded);
            return;
        }

        SetCloudModlistSelection(null);
        if (CloudModlistsDataGrid != null)
        {
            CloudModlistsDataGrid.SelectedItem = null;
        }
    }

    private void InternetAccessManager_OnInternetAccessChanged(object? sender, EventArgs e)
    {
        void Update()
        {
            UpdateCloudModlistControlsEnabledState();
            _ = RefreshManagerUpdateLinkAsync();
        }

        if (Dispatcher.CheckAccess())
        {
            Update();
        }
        else
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)Update);
        }
    }

    private bool EnsureCloudModlistsConsent()
    {
        string message =
            "In this tab you can easily save and load Modlists from an online database (Google Firebase), for free." +
            Environment.NewLine + Environment.NewLine +
            "When you continue, Simple VS Manager will create a firebase-auth.json (basically just a code that identifies you as the owner of your uploaded modlists) file in the AppData/Local/Simple VS Manager folder. " +
            "If you lose this file you will not be able to delete or modify your uploaded online modlists." +
            Environment.NewLine + Environment.NewLine +
            "You will not need to sign in or provide any account information or do anything really :) Press OK to continue and never show this again!";

        return EnsureFirebaseAuthConsent(message);
    }

    private bool EnsureFirebaseAuthConsent(string message)
    {
        string stateFilePath = FirebaseAnonymousAuthenticator.GetStateFilePath();
        if (string.IsNullOrWhiteSpace(stateFilePath))
        {
            return true;
        }

        if (File.Exists(stateFilePath))
        {
            EnsureFirebaseAuthBackedUpIfAvailable();
            return true;
        }

        var buttonOverrides = new MessageDialogButtonContentOverrides
        {
            Cancel = "No thanks"
        };

        MessageBoxResult result = WpfMessageBox.Show(
            this,
            message,
            "Simple VS Manager",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information,
            buttonContentOverrides: buttonOverrides);

        return result == MessageBoxResult.OK;
    }

    private bool EnsureUserReportVotingConsent()
    {
        string message =
            "To enable voting, Simple VS Manager will create a firebase-auth.json (basically just a code that identifies you as the owner of your mod compatibility votes) file in the AppData/Local/Simple VS Manager folder. " +
            "If you lose this file you will not be able to manage or remove your mod compatibility votes." +
            Environment.NewLine + Environment.NewLine +
            "You will not need to sign in or provide any account information or do anything really :) Press OK to continue and never show this again!";

        return EnsureFirebaseAuthConsent(message);
    }

    private void EnsureFirebaseAuthBackedUpIfAvailable()
    {
        string stateFilePath = FirebaseAnonymousAuthenticator.GetStateFilePath();
        if (string.IsNullOrWhiteSpace(stateFilePath) || !File.Exists(stateFilePath))
        {
            return;
        }

        string? dataDirectory = _dataDirectory;
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            return;
        }

        string modDataDirectory = Path.Combine(dataDirectory, "ModData");
        string backupDirectory = Path.Combine(modDataDirectory, "SimpleVSManager");
        string backupPath = Path.Combine(backupDirectory, "firebase-auth.json");

        try
        {
            Directory.CreateDirectory(backupDirectory);
            File.Copy(stateFilePath, backupPath, overwrite: true);
        }
        catch (IOException ex)
        {
            StatusLogService.AppendStatus($"Failed to back up Firebase auth state: {ex.Message}", true);
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusLogService.AppendStatus($"Failed to back up Firebase auth state: {ex.Message}", true);
        }
        catch (NotSupportedException ex)
        {
            StatusLogService.AppendStatus($"Failed to back up Firebase auth state: {ex.Message}", true);
        }
    }

    private void DeleteFirebaseAuthFiles()
    {
        string stateFilePath = FirebaseAnonymousAuthenticator.GetStateFilePath();
        if (!string.IsNullOrWhiteSpace(stateFilePath))
        {
            TryDeleteFirebaseAuthFile(stateFilePath);
        }

        string? dataDirectory = _dataDirectory;
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            return;
        }

        string backupDirectory = Path.Combine(dataDirectory, "ModData", "SimpleVSManager");
        string backupPath = Path.Combine(backupDirectory, "firebase-auth.json");
        TryDeleteFirebaseAuthFile(backupPath);
    }

    private static void TryDeleteFirebaseAuthFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            StatusLogService.AppendStatus($"Failed to delete Firebase auth file {path}: {ex.Message}", true);
        }
    }

    private void UpdateSearchSortingBehavior(bool isSearchingModDatabase)
    {
        if (ModsDataGrid == null)
        {
            return;
        }

        if (isSearchingModDatabase)
        {
            CacheCurrentSortState();
            ModsDataGrid.CanUserSortColumns = false;

            ICollectionView? view = _viewModel?.SearchResultsView;
            if (view != null && view.SortDescriptions.Count > 0)
            {
                view.SortDescriptions.Clear();
            }

            if (ModsDataGrid.Items.SortDescriptions.Count > 0)
            {
                ModsDataGrid.Items.SortDescriptions.Clear();
            }
            return;
        }

        ModsDataGrid.CanUserSortColumns = true;
        RestoreCachedSortState();
        Dispatcher.BeginInvoke(new Action(EnsureModsViewSortStateIsApplied), DispatcherPriority.Background);
    }

    private void CacheCurrentSortState()
    {
        _cachedSortMemberPath = null;
        _cachedSortDirection = null;
        _cachedSortOption = _viewModel?.SelectedSortOption;

        if (ModsDataGrid != null)
        {
            foreach (DataGridColumn column in ModsDataGrid.Columns)
            {
                if (!column.SortDirection.HasValue)
                {
                    continue;
                }

                string? memberPath = column.SortMemberPath;
                if (string.IsNullOrWhiteSpace(memberPath))
                {
                    continue;
                }

                _cachedSortMemberPath = NormalizeSortMemberPath(memberPath);
                _cachedSortDirection = column.SortDirection;
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(_cachedSortMemberPath) && _cachedSortDirection.HasValue)
        {
            return;
        }

        if (_viewModel?.SelectedSortOption is { SortDescriptions: { Count: > 0 } sorts })
        {
            var primary = sorts[0];
            _cachedSortMemberPath = NormalizeSortMemberPath(primary.Property);
            _cachedSortDirection = primary.Direction;
            return;
        }

        if (ModsDataGrid?.Items.SortDescriptions.Count > 0)
        {
            SortDescription description = ModsDataGrid.Items.SortDescriptions[0];
            _cachedSortMemberPath = NormalizeSortMemberPath(description.PropertyName);
            _cachedSortDirection = description.Direction;
        }
    }

    private static bool ShouldRefreshAfterDependencyResolution(string? statusMessage)
    {
        return !string.IsNullOrWhiteSpace(statusMessage)
            && statusMessage.StartsWith("Resolved dependencies for ", StringComparison.Ordinal);
    }

    private async Task RefreshModsAfterDependencyResolutionAsync()
    {
        if (_isDependencyResolutionRefreshPending)
        {
            return;
        }

        if (_viewModel?.RefreshCommand == null)
        {
            return;
        }

        _isDependencyResolutionRefreshPending = true;

        try
        {
            await RefreshModsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"The mod list could not be refreshed after resolving dependencies:{Environment.NewLine}{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isDependencyResolutionRefreshPending = false;
        }
    }

    private void ScheduleRefreshAfterModlistLoadIfReady()
    {
        if (!_refreshAfterModlistLoadPending || _isRefreshingAfterModlistLoad)
        {
            return;
        }

        var viewModel = _viewModel;
        if (viewModel?.RefreshCommand == null)
        {
            return;
        }

        if (viewModel.IsLoadingMods || viewModel.IsLoadingModDetails)
        {
            return;
        }

        _isRefreshingAfterModlistLoad = true;

        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await RefreshModsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"Failed to refresh mods after loading the modlist:{Environment.NewLine}{ex.Message}",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _refreshAfterModlistLoadPending = false;
                _isRefreshingAfterModlistLoad = false;
            }
        }, DispatcherPriority.Background);
    }

    private void RestoreCachedSortState()
    {
        var viewModel = _viewModel;
        if (viewModel is null)
        {
            return;
        }

        SortOption? cachedOption = _cachedSortOption;
        string? cachedMemberPath = _cachedSortMemberPath;
        ListSortDirection? cachedDirection = _cachedSortDirection;
        _cachedSortMemberPath = null;
        _cachedSortDirection = null;
        _cachedSortOption = null;

        if (cachedOption is not null)
        {
            ApplySortOption(cachedOption, persistPreference: false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(cachedMemberPath) && cachedDirection.HasValue)
        {
            ApplyModListSort(cachedMemberPath, cachedDirection.Value, persistPreference: false);
            return;
        }

        if (viewModel.SelectedSortOption is { } selectedOption)
        {
            selectedOption.Apply(viewModel.ModsView);
            UpdateSortPreferenceFromSelectedOption(persistPreference: false);
            return;
        }

        RestoreSortPreference();
    }

    private void RestoreSortPreference()
    {
        var viewModel = _viewModel;
        if (viewModel is null)
        {
            return;
        }

        var preference = _userConfiguration.GetModListSortPreference();
        string? sortMemberPath = preference.SortMemberPath;
        if (!string.IsNullOrWhiteSpace(sortMemberPath))
        {
            ApplyModListSort(sortMemberPath, preference.Direction, persistPreference: false);
        }
        else
        {
            UpdateSortPreferenceFromSelectedOption(persistPreference: false);
        }
    }

    private void ApplyModListSort(string sortMemberPath, ListSortDirection direction, bool persistPreference)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(sortMemberPath))
        {
            return;
        }

        sortMemberPath = NormalizeSortMemberPath(sortMemberPath.Trim());

        SortOption? option = FindMatchingSortOption(sortMemberPath, direction);
        if (option is null)
        {
            var sorts = BuildSortDescriptions(sortMemberPath, direction);
            string displayName = BuildSortDisplayName(sortMemberPath, direction);
            option = new SortOption(displayName, sorts);
        }

        ApplySortOption(option, persistPreference);
    }

    private void ApplySortOption(SortOption option, bool persistPreference)
    {
        if (_viewModel is null)
        {
            return;
        }

        bool changed = !ReferenceEquals(_viewModel.SelectedSortOption, option);
        bool previousSuppression = _suppressSortPreferenceSave;
        _suppressSortPreferenceSave = !persistPreference;

        try
        {
            if (changed)
            {
                _viewModel.SelectedSortOption = option;
            }
            else
            {
                option.Apply(_viewModel.ModsView);
                UpdateSortPreferenceFromSelectedOption(persistPreference);
            }
        }
        finally
        {
            _suppressSortPreferenceSave = previousSuppression;
        }
    }

    private static bool IsActiveSortMember(string? sortMemberPath)
    {
        if (string.IsNullOrWhiteSpace(sortMemberPath))
        {
            return false;
        }

        return string.Equals(sortMemberPath, nameof(ModListItemViewModel.IsActive), StringComparison.OrdinalIgnoreCase)
            || string.Equals(sortMemberPath, nameof(ModListItemViewModel.ActiveSortOrder), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSortMemberPath(string sortMemberPath)
    {
        if (string.IsNullOrWhiteSpace(sortMemberPath))
        {
            return sortMemberPath;
        }

        return IsActiveSortMember(sortMemberPath)
            ? nameof(ModListItemViewModel.ActiveSortOrder)
            : sortMemberPath;
    }

    private static bool SortMemberMatches(string? columnSortMemberPath, string sortMemberPath)
    {
        if (string.IsNullOrWhiteSpace(columnSortMemberPath))
        {
            return false;
        }

        return string.Equals(
            NormalizeSortMemberPath(columnSortMemberPath),
            NormalizeSortMemberPath(sortMemberPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private SortOption? FindMatchingSortOption(string sortMemberPath, ListSortDirection direction)
    {
        if (_viewModel is null)
        {
            return null;
        }

        sortMemberPath = NormalizeSortMemberPath(sortMemberPath);

        foreach (var option in _viewModel.SortOptions)
        {
            if (SortOptionMatches(option, sortMemberPath, direction))
            {
                return option;
            }
        }

        if (_viewModel.SelectedSortOption != null
            && SortOptionMatches(_viewModel.SelectedSortOption, sortMemberPath, direction))
        {
            return _viewModel.SelectedSortOption;
        }

        return null;
    }

    private static bool SortOptionMatches(SortOption option, string sortMemberPath, ListSortDirection direction)
    {
        if (option.SortDescriptions.Count == 0)
        {
            return false;
        }

        var primary = option.SortDescriptions[0];
        if (!string.Equals(primary.Property, sortMemberPath, StringComparison.OrdinalIgnoreCase)
            || primary.Direction != direction)
        {
            return false;
        }

        if (IsActiveSortMember(sortMemberPath))
        {
            if (option.SortDescriptions.Count < 2)
            {
                return false;
            }

            var secondary = option.SortDescriptions[1];
            return string.Equals(secondary.Property, nameof(ModListItemViewModel.DisplayName), StringComparison.OrdinalIgnoreCase)
                && secondary.Direction == ListSortDirection.Ascending;
        }

        return true;
    }

    private static (string Property, ListSortDirection Direction)[] BuildSortDescriptions(string sortMemberPath, ListSortDirection direction)
    {
        if (IsActiveSortMember(sortMemberPath))
        {
            return new[]
            {
                (nameof(ModListItemViewModel.ActiveSortOrder), direction),
                (nameof(ModListItemViewModel.DisplayName), ListSortDirection.Ascending)
            };
        }

        if (string.Equals(sortMemberPath, nameof(ModListItemViewModel.LatestVersionSortKey), StringComparison.OrdinalIgnoreCase))
        {
            return new[]
            {
                (nameof(ModListItemViewModel.LatestVersionSortKey), direction),
                (nameof(ModListItemViewModel.DisplayName), ListSortDirection.Ascending)
            };
        }

        return new[] { (sortMemberPath, direction) };
    }

    private static string BuildSortDisplayName(string sortMemberPath, ListSortDirection direction)
    {
        if (IsActiveSortMember(sortMemberPath))
        {
            return direction == ListSortDirection.Ascending
                ? "Active (Active → Inactive)"
                : "Active (Inactive → Active)";
        }

        if (string.Equals(sortMemberPath, nameof(ModListItemViewModel.DisplayName), StringComparison.OrdinalIgnoreCase))
        {
            return direction == ListSortDirection.Ascending
                ? "Name (A → Z)"
                : "Name (Z → A)";
        }

        if (string.Equals(sortMemberPath, nameof(ModListItemViewModel.LatestVersionSortKey), StringComparison.OrdinalIgnoreCase))
        {
            return direction == ListSortDirection.Ascending
                ? "Latest Version (Updates First)"
                : "Latest Version (Updates Last)";
        }

        return $"{sortMemberPath} ({(direction == ListSortDirection.Ascending ? "Ascending" : "Descending")})";
    }

    private void UpdateSortPreferenceFromSelectedOption(bool persistPreference)
    {
        if (_viewModel?.SelectedSortOption is not { } option)
        {
            ClearColumnSortIndicators();
            if (persistPreference)
            {
                _userConfiguration.SetModListSortPreference(null, ListSortDirection.Ascending);
            }

            return;
        }

        if (option.SortDescriptions.Count == 0)
        {
            ClearColumnSortIndicators();
            if (persistPreference)
            {
                _userConfiguration.SetModListSortPreference(null, ListSortDirection.Ascending);
            }

            return;
        }

        var primary = option.SortDescriptions[0];
        UpdateColumnSortVisuals(primary.Property, primary.Direction);

        if (persistPreference)
        {
            _userConfiguration.SetModListSortPreference(primary.Property, primary.Direction);
        }
    }

    private void UpdateColumnSortVisuals(string sortMemberPath, ListSortDirection direction)
    {
        if (ModsDataGrid == null)
        {
            return;
        }

        foreach (var column in ModsDataGrid.Columns)
        {
            if (SortMemberMatches(column.SortMemberPath, sortMemberPath))
            {
                column.SortDirection = direction;
            }
            else
            {
                column.SortDirection = null;
            }
        }
    }

    private void ClearColumnSortIndicators()
    {
        if (ModsDataGrid == null)
        {
            return;
        }

        foreach (var column in ModsDataGrid.Columns)
        {
            column.SortDirection = null;
        }
    }

    private void EnsureModsViewSortStateIsApplied()
    {
        if (_viewModel is null || _viewModel.SearchModDatabase || ModsDataGrid is null)
        {
            return;
        }

        ICollectionView view = _viewModel.ModsView;

        if (view.SortDescriptions.Count == 0)
        {
            _viewModel.SelectedSortOption?.Apply(view);
        }

        if (view.SortDescriptions.Count > 0)
        {
            SortDescription sort = view.SortDescriptions[0];
            UpdateColumnSortVisuals(sort.PropertyName, sort.Direction);
        }
        else
        {
            ClearColumnSortIndicators();
        }

        view.Refresh();
    }

    private async Task InitializeViewModelAsync(MainViewModel viewModel)
    {
        if (_isInitializing)
        {
            return;
        }

        _isInitializing = true;
        bool initialized = false;
        try
        {
            await viewModel.InitializeAsync();
            initialized = true;
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Failed to load mods:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isInitializing = false;
        }

        if (initialized)
        {
            await AutoAssignMissingModConfigPathsAsync(viewModel);
            StartModsWatcher();
            StartGameSessionMonitor();
            await TryShowModUsagePromptAsync().ConfigureAwait(true);
        }
    }

    private void DisposeCurrentViewModel()
    {
        if (_viewModel is null)
        {
            return;
        }

        MainViewModel? current = _viewModel;
        _viewModel = null;
        DataContext = null;
        StopGameSessionMonitor();
        DisposeViewModel(current);
    }

    private void DisposeViewModel(MainViewModel? viewModel)
    {
        if (viewModel is null)
        {
            return;
        }

        viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        viewModel.Dispose();
    }

    private void StartModsWatcher()
    {
        if (_viewModel is null)
        {
            return;
        }

        StopModsWatcher();

        _modsWatcherTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _modsWatcherTimer.Tick += ModsWatcherTimerOnTick;
        _modsWatcherTimer.Start();
    }

    private void StartGameSessionMonitor()
    {
        if (string.IsNullOrWhiteSpace(_dataDirectory) || _viewModel is null)
        {
            return;
        }

        if (!_userConfiguration.IsModUsageTrackingEnabled)
        {
            return;
        }

        StopGameSessionMonitor();

        string logsDirectory = Path.Combine(_dataDirectory, "Logs");

        try
        {
            _gameSessionMonitor = new GameSessionMonitor(
                logsDirectory,
                Dispatcher,
                _userConfiguration,
                () => _viewModel.GetActiveModUsageSnapshot());
            _gameSessionMonitor.PromptRequired += GameSessionMonitor_OnPromptRequired;
            _gameSessionMonitor.RefreshPromptState();

            if (_userConfiguration.HasPendingModUsagePrompt)
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Func<Task>(TryShowModUsagePromptAsync));
            }
        }
        catch (Exception ex)
        {
            StatusLogService.AppendStatus(
                string.Format(CultureInfo.CurrentCulture, "Failed to initialize log monitor: {0}", ex.Message),
                true);
        }
    }

    private void StopGameSessionMonitor()
    {
        if (_gameSessionMonitor is null)
        {
            return;
        }

        _gameSessionMonitor.PromptRequired -= GameSessionMonitor_OnPromptRequired;
        _gameSessionMonitor.Dispose();
        _gameSessionMonitor = null;
    }

    private void GameSessionMonitor_OnPromptRequired(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Func<Task>(TryShowModUsagePromptAsync));
    }

    private Task AutoAssignMissingModConfigPathsAsync(MainViewModel viewModel)
    {
        return AutoAssignMissingModConfigPathsAsync(viewModel, (IReadOnlyCollection<string>?)null);
    }

    private async Task AutoAssignMissingModConfigPathsAsync(MainViewModel viewModel, IReadOnlyCollection<string>? modIds)
    {
        if (viewModel is null)
        {
            return;
        }

        IReadOnlyList<ModListItemViewModel> candidateMods;
        if (modIds is null)
        {
            candidateMods = viewModel.GetInstalledModsSnapshot();
        }
        else
        {
            var normalizedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mods = new List<ModListItemViewModel>();
            foreach (string id in modIds)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                string trimmedId = id.Trim();
                if (!normalizedIds.Add(trimmedId))
                {
                    continue;
                }

                ModListItemViewModel? installedMod = viewModel.FindInstalledModById(trimmedId);
                if (installedMod != null)
                {
                    mods.Add(installedMod);
                }
            }

            if (mods.Count == 0)
            {
                return;
            }

            candidateMods = mods;
        }

        await AutoAssignMissingModConfigPathsAsync(viewModel, candidateMods).ConfigureAwait(true);
    }

    private async Task AutoAssignMissingModConfigPathsAsync(
        MainViewModel viewModel,
        IReadOnlyList<ModListItemViewModel> candidateMods)
    {
        if (string.IsNullOrWhiteSpace(_dataDirectory) || candidateMods.Count == 0)
        {
            return;
        }

        try
        {
            string configDirectory = Path.Combine(_dataDirectory, "ModConfig");
            if (!Directory.Exists(configDirectory))
            {
                return;
            }

            var missingMods = new List<(string ModId, string DisplayName)>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModListItemViewModel mod in candidateMods)
            {
                if (mod is null)
                {
                    continue;
                }

                string? modId = mod.ModId;
                if (string.IsNullOrWhiteSpace(modId))
                {
                    continue;
                }

                string trimmedId = modId.Trim();
                if (!seenIds.Add(trimmedId))
                {
                    continue;
                }

                if (_userConfiguration.TryGetModConfigPath(trimmedId, out string? path)
                    && !string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                missingMods.Add((trimmedId, mod.DisplayName));
            }

            if (missingMods.Count == 0)
            {
                return;
            }

            string[] configFiles = Directory.GetFiles(configDirectory, "*.json", SearchOption.TopDirectoryOnly);
            if (configFiles.Length == 0)
            {
                return;
            }

            List<(string ModId, string ConfigPath)> matches =
                await Task.Run(() => FindConfigMatches(missingMods, configFiles)).ConfigureAwait(true);
            if (matches.Count == 0)
            {
                return;
            }

            bool assignedAny = false;
            foreach ((string ModId, string ConfigPath) match in matches)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(match.ConfigPath) || !File.Exists(match.ConfigPath))
                    {
                        continue;
                    }
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                _userConfiguration.SetModConfigPath(match.ModId, match.ConfigPath);
                assignedAny = true;
            }

            if (assignedAny)
            {
                UpdateSelectedModEditConfigButton(viewModel.SelectedMod);
            }
        }
        catch (Exception ex)
        {
            StatusLogService.AppendStatus($"Failed to automatically locate mod configuration files: {ex.Message}", true);
        }
    }

    private static List<(string ModId, string ConfigPath)> FindConfigMatches(
        IReadOnlyList<(string ModId, string DisplayName)> mods,
        IReadOnlyList<string> configPaths)
    {
        var results = new List<(string ModId, string ConfigPath)>();
        if (mods.Count == 0 || configPaths.Count == 0)
        {
            return results;
        }

        var candidates = configPaths
            .Select(path => (Path: path, Tokens: BuildSearchTokens(Path.GetFileNameWithoutExtension(path) ?? string.Empty)))
            .Where(candidate => candidate.Tokens.Count > 0)
            .ToList();

        if (candidates.Count == 0)
        {
            return results;
        }

        foreach ((string ModId, string DisplayName) mod in mods)
        {
            var words = BuildSearchTokens(mod.DisplayName);
            if (words.Count == 0)
            {
                continue;
            }

            string? bestPath = null;
            int bestScore = int.MaxValue;
            int bestCandidateIndex = -1;

            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (!TryCalculateMatchScore(words, candidate.Tokens, out int score))
                {
                    continue;
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    bestPath = candidate.Path;
                    bestCandidateIndex = i;

                    if (score == 0)
                    {
                        break;
                    }
                }
            }

            // Require at least one word to score better than the maximum allowed distance to avoid
            // weak matches that only satisfy the fallback threshold.
            if (bestPath is not null
                && bestCandidateIndex >= 0
                && bestScore < words.Count * AutomaticConfigMaxWordDistance)
            {
                results.Add((mod.ModId, bestPath));
                candidates.RemoveAt(bestCandidateIndex);

                if (candidates.Count == 0)
                {
                    break;
                }
            }
        }

        return results;
    }

    private static bool TryCalculateMatchScore(
        IReadOnlyList<string> words,
        IReadOnlyList<string> candidateTokens,
        out int score)
    {
        score = 0;
        if (words.Count == 0 || candidateTokens.Count == 0)
        {
            return false;
        }

        foreach (string word in words)
        {
            int bestDistance = int.MaxValue;
            foreach (string token in candidateTokens)
            {
                int distance = CalculateBestDistance(token, word, AutomaticConfigMaxWordDistance);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    if (bestDistance == 0)
                    {
                        break;
                    }
                }
            }

            if (bestDistance > AutomaticConfigMaxWordDistance)
            {
                score = int.MaxValue;
                return false;
            }

            score += bestDistance;
        }

        return true;
    }

    private static int CalculateBestDistance(string token, string word, int maxDistance)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(word))
        {
            return int.MaxValue;
        }

        ReadOnlySpan<char> tokenSpan = token.AsSpan();
        ReadOnlySpan<char> wordSpan = word.AsSpan();

        return CalculateLevenshteinDistance(wordSpan, tokenSpan, maxDistance);
    }

    private static int CalculateLevenshteinDistance(ReadOnlySpan<char> source, ReadOnlySpan<char> target, int maxDistance)
    {
        if (Math.Abs(source.Length - target.Length) > maxDistance)
        {
            return maxDistance + 1;
        }

        int targetLength = target.Length;
        Span<int> previous = stackalloc int[targetLength + 1];
        Span<int> current = stackalloc int[targetLength + 1];

        for (int j = 0; j <= targetLength; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= source.Length; i++)
        {
            current[0] = i;
            int minInRow = current[0];
            char sourceChar = source[i - 1];

            for (int j = 1; j <= targetLength; j++)
            {
                int cost = sourceChar == target[j - 1] ? 0 : 1;
                int deletion = previous[j] + 1;
                int insertion = current[j - 1] + 1;
                int substitution = previous[j - 1] + cost;
                int value = Math.Min(Math.Min(deletion, insertion), substitution);
                current[j] = value;

                if (value < minInRow)
                {
                    minInRow = value;
                }
            }

            if (minInRow > maxDistance)
            {
                return maxDistance + 1;
            }

            Span<int> temp = previous;
            previous = current;
            current = temp;
        }

        return previous[targetLength];
    }

    private static List<string> BuildSearchTokens(string value)
    {
        List<string> tokens = ExtractWords(value);
        if (tokens.Count == 0 && !string.IsNullOrWhiteSpace(value))
        {
            tokens.Add(value.ToLowerInvariant());
        }

        if (tokens.Count > 1)
        {
            string combined = string.Concat(tokens);
            if (!string.IsNullOrEmpty(combined) && !tokens.Contains(combined))
            {
                tokens.Add(combined);
            }
        }

        return tokens;
    }

    private static List<string> ExtractWords(string value)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return results;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        bool hasPrevious = false;
        char previousChar = '\0';

        void FlushBuilder()
        {
            if (builder.Length == 0)
            {
                return;
            }

            string word = builder.ToString();
            if (seen.Add(word))
            {
                results.Add(word);
            }

            builder.Clear();
        }

        for (int i = 0; i < value.Length; i++)
        {
            char current = value[i];
            if (char.IsLetterOrDigit(current))
            {
                if (builder.Length > 0
                    && char.IsUpper(current)
                    && hasPrevious
                    && char.IsLetter(previousChar)
                    && !char.IsUpper(previousChar))
                {
                    FlushBuilder();
                }

                builder.Append(char.ToLowerInvariant(current));
                hasPrevious = true;
                previousChar = current;
            }
            else
            {
                FlushBuilder();
                hasPrevious = false;
                previousChar = '\0';
            }
        }

        FlushBuilder();

        return results;
    }

    private void StopModsWatcher()
    {
        if (_modsWatcherTimer is null)
        {
            return;
        }

        _modsWatcherTimer.Stop();
        _modsWatcherTimer.Tick -= ModsWatcherTimerOnTick;
        _modsWatcherTimer = null;
        _isAutomaticRefreshRunning = false;
    }

    private async void ModsWatcherTimerOnTick(object? sender, EventArgs e)
    {
        if (_viewModel is null || _viewModel.IsBusy || _isInitializing || _isAutomaticRefreshRunning)
        {
            return;
        }

        bool hasChanges = await _viewModel.CheckForModStateChangesAsync();
        if (!hasChanges)
        {
            return;
        }

        if (_viewModel.RefreshCommand == null)
        {
            return;
        }

        _isAutomaticRefreshRunning = true;
        try
        {
            await RefreshModsAsync();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Failed to refresh mods automatically:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isAutomaticRefreshRunning = false;
        }
    }

    private async Task RefreshModsAsync(IReadOnlyCollection<string>? autoAssignModIds = null)
    {
        if (_viewModel?.RefreshCommand == null)
        {
            return;
        }

        ScrollViewer? scrollViewer = GetModsScrollViewer();
        double? targetOffset = scrollViewer?.VerticalOffset;

        List<string>? selectedSourcePaths = null;
        string? anchorSourcePath = null;

        if (_viewModel.SearchModDatabase != true && _selectedMods.Count > 0)
        {
            var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            selectedSourcePaths = new List<string>(_selectedMods.Count);

            foreach (ModListItemViewModel selected in _selectedMods)
            {
                string? sourcePath = selected.SourcePath;
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    continue;
                }

                if (dedup.Add(sourcePath))
                {
                    selectedSourcePaths.Add(sourcePath);
                }
            }

            if (selectedSourcePaths.Count > 0 && _selectionAnchor is { } anchor)
            {
                anchorSourcePath = anchor.SourcePath;
            }
        }

        await _viewModel.RefreshCommand.ExecuteAsync(null);

        if (selectedSourcePaths is { Count: > 0 })
        {
            RestoreSelectionFromSourcePaths(selectedSourcePaths, anchorSourcePath);
        }

        if (_viewModel is { } viewModel)
        {
            await AutoAssignMissingModConfigPathsAsync(viewModel, autoAssignModIds).ConfigureAwait(true);
        }

        if (scrollViewer != null && targetOffset.HasValue)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                scrollViewer.UpdateLayout();
                double clampedOffset = Math.Max(0, Math.Min(targetOffset.Value, scrollViewer.ScrollableHeight));
                scrollViewer.ScrollToVerticalOffset(clampedOffset);
            }, DispatcherPriority.Background);
        }
    }

    private bool TryInitializePaths()
    {
        bool dataResolved = TryResolveDataDirectory();
        bool gameResolved = TryResolveGameDirectory();
        TryResolveCustomShortcut();

        if (dataResolved)
        {
            _userConfiguration.SetDataDirectory(_dataDirectory!);
            DeveloperProfileManager.UpdateOriginalProfile(_dataDirectory!);
        }

        if (gameResolved)
        {
            _userConfiguration.SetGameDirectory(_gameDirectory!);
        }

        return dataResolved && gameResolved;
    }

    private bool TryResolveDataDirectory()
    {
        string? storedPath = _userConfiguration.DataDirectory;
        if (TryValidateDataDirectory(storedPath, out _dataDirectory, out _))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(storedPath))
        {
            _userConfiguration.ClearDataDirectory();
        }

        string defaultPath = DataDirectoryLocator.Resolve();
        if (TryValidateDataDirectory(defaultPath, out _dataDirectory, out _))
        {
            return true;
        }

        WpfMessageBox.Show("The Vintage Story data folder could not be located. Please select it to enable mod management.",
            "Simple VS Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        _dataDirectory = PromptForDirectory(
            "Select your VintagestoryData folder",
            _userConfiguration.DataDirectory ?? defaultPath,
            TryValidateDataDirectory,
            allowCancel: true);

        if (_dataDirectory is null)
        {
            WpfMessageBox.Show(
                "Mods cannot be managed until a VintagestoryData folder is selected. You can set it later from File > Set Data Folder.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private bool TryResolveGameDirectory()
    {
        string? storedPath = _userConfiguration.GameDirectory;
        if (TryValidateGameDirectory(storedPath, out _gameDirectory, out _))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(storedPath))
        {
            _userConfiguration.ClearGameDirectory();
        }

        string defaultPath = GameDirectoryLocator.Resolve();
        if (!string.IsNullOrWhiteSpace(defaultPath) && TryValidateGameDirectory(defaultPath, out _gameDirectory, out _))
        {
            return true;
        }

        WpfMessageBox.Show("The Vintage Story installation folder could not be located. Please select it to enable game-related features.",
            "Simple VS Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        _gameDirectory = PromptForDirectory(
            "Select your Vintage Story installation folder",
            _userConfiguration.GameDirectory ?? (string.IsNullOrWhiteSpace(defaultPath) ? null : defaultPath),
            TryValidateGameDirectory,
            allowCancel: true);

        if (_gameDirectory is null)
        {
            WpfMessageBox.Show(
                "Game-related features will be unavailable until a Vintage Story installation folder is selected. You can set it later from File > Set Game Folder.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private void TryResolveCustomShortcut()
    {
        string? storedPath = _userConfiguration.CustomShortcutPath;
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            _customShortcutPath = null;
            return;
        }

        if (File.Exists(storedPath))
        {
            _customShortcutPath = storedPath;
            return;
        }

        _customShortcutPath = null;
        _userConfiguration.ClearCustomShortcutPath();

        WpfMessageBox.Show(
            "The previously selected Vintage Story shortcut could not be found and has been cleared.",
            "Simple VS Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void HandleViewModelInitializationFailure(Exception exception)
    {
        DisposeCurrentViewModel();

        if (_dataDirectory != null)
        {
            _userConfiguration.ClearDataDirectory();
        }

        _dataDirectory = null;

        string message = $"Failed to initialize the mod manager:\n{exception.Message}\n\n" +
            "You can set the Vintage Story folders from the File menu once the application has loaded.";

        WpfMessageBox.Show(message,
            "Simple VS Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private async Task ReloadViewModelAsync()
    {
        if (string.IsNullOrWhiteSpace(_dataDirectory))
        {
            await RefreshDeleteCachedModsMenuHeaderAsync();
            return;
        }

        StopModsWatcher();
        StopGameSessionMonitor();

        MainViewModel? previousViewModel = _viewModel;
        if (previousViewModel is not null)
        {
            previousViewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        }

        MainViewModel? newViewModel = null;

        try
        {
            newViewModel = new MainViewModel(
                _dataDirectory,
                _userConfiguration,
                _userConfiguration.ModDatabaseSearchResultLimit,
                _userConfiguration.ModDatabaseNewModsRecentMonths,
                _userConfiguration.ModDatabaseAutoLoadMode,
                gameDirectory: _gameDirectory,
                excludeInstalledModDatabaseResults: _userConfiguration.ExcludeInstalledModDatabaseResults,
                onlyShowCompatibleModDatabaseResults: _userConfiguration.OnlyShowCompatibleModDatabaseResults);
            newViewModel.PropertyChanged += ViewModelOnPropertyChanged;
            _viewModel = newViewModel;
            UpdateGameVersionMenuItem(newViewModel.InstalledGameVersion);
            DataContext = newViewModel;
            ApplyPlayerIdentityToUiAndCloudStore();
            AttachToModsView(newViewModel.CurrentModsView);
            await InitializeViewModelAsync(newViewModel);

            DisposeViewModel(previousViewModel);
        }
        catch (Exception ex)
        {
            DisposeViewModel(newViewModel);

            if (previousViewModel is not null)
            {
                _viewModel = previousViewModel;
                previousViewModel.PropertyChanged += ViewModelOnPropertyChanged;
                DataContext = previousViewModel;
                ApplyPlayerIdentityToUiAndCloudStore();
                AttachToModsView(previousViewModel.CurrentModsView);
                StartModsWatcher();
            }

            WpfMessageBox.Show($"Failed to reload mods:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        await RefreshDeleteCachedModsMenuHeaderAsync();
    }

    private delegate bool PathValidator(string? path, out string? normalizedPath, out string? errorMessage);

    private string? PromptForDirectory(string description, string? initialPath, PathValidator validator, bool allowCancel)
    {
        string? candidate = initialPath;

        while (true)
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = description,
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };

            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                dialog.SelectedPath = candidate;
            }

            WinForms.DialogResult result = dialog.ShowDialog();
            if (result != WinForms.DialogResult.OK)
            {
                if (allowCancel)
                {
                    return null;
                }

                MessageBoxResult exit = WpfMessageBox.Show(
                    "You must select a folder to continue. Do you want to exit the application?",
                    "Simple VS Manager",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (exit == MessageBoxResult.Yes)
                {
                    return null;
                }

                continue;
            }

            candidate = dialog.SelectedPath;
            if (validator(candidate, out string? normalized, out string? errorMessage))
            {
                return normalized;
            }

            WpfMessageBox.Show(errorMessage ?? "The selected folder is not valid.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private string? PromptForConfigFile(ModListItemViewModel mod, string? previousPath)
    {
        string? initialDirectory = GetInitialConfigDirectory(previousPath);

        using var dialog = new WinForms.OpenFileDialog
        {
            Title = $"Select config file for {mod.DisplayName}",
            Filter = "Config files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        if (!string.IsNullOrWhiteSpace(previousPath))
        {
            dialog.FileName = Path.GetFileName(previousPath);
        }

        WinForms.DialogResult result = dialog.ShowDialog();
        if (result != WinForms.DialogResult.OK)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(dialog.FileName);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The selected configuration file path is invalid.", nameof(previousPath), ex);
        }
    }

    private string? GetInitialConfigDirectory(string? previousPath)
    {
        if (!string.IsNullOrWhiteSpace(previousPath))
        {
            try
            {
                string? directory = Path.GetDirectoryName(previousPath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    return directory;
                }
            }
            catch (Exception)
            {
                // Ignore invalid stored paths and fall back to the default directory.
            }
        }

        if (!string.IsNullOrWhiteSpace(_dataDirectory))
        {
            string configDirectory = Path.Combine(_dataDirectory, "ModConfig");
            if (Directory.Exists(configDirectory))
            {
                return configDirectory;
            }

            return _dataDirectory;
        }

        return null;
    }

    private static bool TryValidateDataDirectory(string? path, out string? normalizedPath, out string? errorMessage)
    {
        normalizedPath = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            errorMessage = "No folder was selected.";
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            errorMessage = "The folder path is invalid.";
            return false;
        }

        if (!Directory.Exists(normalizedPath))
        {
            errorMessage = "The folder does not exist.";
            return false;
        }

        bool hasClientSettings = File.Exists(Path.Combine(normalizedPath, "clientsettings.json"));
        bool hasMods = Directory.Exists(Path.Combine(normalizedPath, "Mods"));
        bool hasConfig = Directory.Exists(Path.Combine(normalizedPath, "ModConfig"));

        if (!hasClientSettings && !hasMods && !hasConfig)
        {
            errorMessage = "The folder does not appear to be a VintagestoryData directory.";
            return false;
        }

        return true;
    }

    private static bool TryValidateGameDirectory(string? path, out string? normalizedPath, out string? errorMessage)
    {
        normalizedPath = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            errorMessage = "No folder was selected.";
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            errorMessage = "The folder path is invalid.";
            return false;
        }

        if (File.Exists(candidate))
        {
            string? directory = Path.GetDirectoryName(candidate);
            if (string.IsNullOrWhiteSpace(directory))
            {
                errorMessage = "The folder path is invalid.";
                return false;
            }

            candidate = directory;
        }

        if (!Directory.Exists(candidate))
        {
            errorMessage = "The folder does not exist.";
            return false;
        }

        string? executable = GameDirectoryLocator.FindExecutable(candidate);
        if (executable is null)
        {
            errorMessage = "The folder does not contain a Vintage Story executable.";
            return false;
        }

        normalizedPath = candidate;
        return true;
    }

    private void ModsDataGrid_OnSorting(object sender, DataGridSortingEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_viewModel.SearchModDatabase)
        {
            e.Handled = true;
            return;
        }

        string? sortMemberPath = e.Column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(sortMemberPath))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;

        ListSortDirection direction = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        ApplyModListSort(sortMemberPath, direction, persistPreference: true);
    }

    private void ModsDataGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid dataGrid)
        {
            dataGrid.SelectedIndex = -1;
            dataGrid.UnselectAll();
            dataGrid.UnselectAllCells();
        }
    }

    private async void ModsDataGrid_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (await TryHandleModListKeyDownAsync(e))
        {
            e.Handled = true;
        }
    }

    private async void MainWindow_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (await TryHandleModListKeyDownAsync(e))
        {
            e.Handled = true;
        }
    }

    private async void UserReportsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { DataContext: ModListItemViewModel mod })
        {
            return;
        }

        e.Handled = true;

        if (_viewModel is null)
        {
            return;
        }

        if (!mod.CanSubmitUserReport)
        {
            WpfMessageBox.Show(
                "User reports are unavailable because the mod version or Vintage Story version could not be determined.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!EnsureUserReportVotingConsent())
        {
            return;
        }

        _viewModel.EnableUserReportFetching();

        try
        {
            ModVersionVoteSummary? summary = await _viewModel
                .RefreshUserReportAsync(mod)
                .ConfigureAwait(true);

            summary ??= mod.UserReportSummary;

            if (summary is null)
            {
                summary = new ModVersionVoteSummary(
                    mod.ModId,
                    mod.Version ?? string.Empty,
                    _viewModel.InstalledGameVersion,
                    ModVersionVoteCounts.Empty,
                    ModVersionVoteComments.Empty,
                    null,
                    null);
            }

            var dialog = new ModVoteDialog(
                mod,
                summary,
                (option, comment) => _viewModel.SubmitUserReportVoteAsync(mod, option, comment));

            dialog.Owner = this;
            dialog.ShowDialog();
        }
        catch (InternetAccessDisabledException ex)
        {
            WpfMessageBox.Show(
                ex.Message,
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(
                $"Failed to load user reports:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task<bool> TryHandleModListKeyDownAsync(System.Windows.Input.KeyEventArgs e)
    {
        if (_isApplyingPreset)
        {
            return true;
        }

        if (_viewModel?.SearchModDatabase == true)
        {
            return false;
        }

        if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (ModsDataGrid?.IsVisible == true)
            {
                SelectAllModsInCurrentView();
                return true;
            }

            return false;
        }

        if (e.Key == Key.Delete)
        {
            ModifierKeys modifiers = Keyboard.Modifiers;
            if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == ModifierKeys.None &&
                _selectedMods.Count > 0)
            {
                await DeleteSelectedModsAsync();
                return true;
            }
        }

        return false;
    }

    private void ModsDataGrid_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isApplyingPreset)
        {
            e.Handled = true;
            return;
        }

        if (e.Handled)
        {
            return;
        }

        if (sender is not DataGrid)
        {
            return;
        }

        if (ShouldIgnoreRowSelection(e.OriginalSource as DependencyObject))
        {
            return;
        }

        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (FindAncestor<DataGridRow>(source) != null)
        {
            return;
        }

        if (FindAncestor<DataGridColumnHeader>(source) != null)
        {
            return;
        }

        if (FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) != null)
        {
            return;
        }

        ClearSelection(resetAnchor: true);
    }

    private void ModDatabaseCardsListView_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListView listView)
        {
            listView.SelectedIndex = -1;
            listView.UnselectAll();
        }
    }

    private void ModDatabaseCardsListView_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateLoadMoreScrollThresholdState(e.VerticalOffset, e.ViewportHeight, e.ExtentHeight);
    }

    private void UpdateLoadMoreScrollThresholdState()
    {
        if (_viewModel?.SearchModDatabase != true || !_viewModel.UseModDbDesignView)
        {
            return;
        }

        ScrollViewer? scrollViewer = GetModsScrollViewer();
        if (scrollViewer == null)
        {
            return;
        }

        UpdateLoadMoreScrollThresholdState(scrollViewer.VerticalOffset, scrollViewer.ViewportHeight, scrollViewer.ExtentHeight);
    }

    private void UpdateLoadMoreScrollThresholdState(double verticalOffset, double viewportHeight, double extentHeight)
    {
        if (_viewModel?.SearchModDatabase != true || !_viewModel.UseModDbDesignView)
        {
            return;
        }

        double scrollableHeight = extentHeight - viewportHeight;
        bool isNearBottom = scrollableHeight <= 0 || verticalOffset / scrollableHeight >= LoadMoreScrollThreshold;
        _viewModel.IsLoadMoreModDatabaseScrollThresholdReached = isNearBottom;
    }

    private void ModsDataGridRow_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isApplyingPreset)
        {
            e.Handled = true;
            return;
        }

        if (ShouldIgnoreRowSelection(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (sender is not DataGridRow row || row.DataContext is not ModListItemViewModel mod)
        {
            return;
        }

        row.Focus();
        HandleModRowSelection(mod);
        e.Handled = true;
    }

    private void ModDatabaseCard_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isApplyingPreset)
        {
            e.Handled = true;
            return;
        }

        if (ShouldIgnoreRowSelection(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (sender is not System.Windows.Controls.ListViewItem item || item.DataContext is not ModListItemViewModel mod)
        {
            return;
        }

        item.Focus();
        HandleModRowSelection(mod);
        e.Handled = true;
    }

    private void ModsDataGridRow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            row.DataContextChanged -= ModsDataGridRow_OnDataContextChanged;
            row.DataContextChanged += ModsDataGridRow_OnDataContextChanged;
            UpdateRowModSubscription(row, row.DataContext as ModListItemViewModel);
            SetRowIsHovered(row, row.IsMouseOver);
            ResetRowOverlays(row);
        }
    }

    private void ModsDataGridRow_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            UpdateRowModSubscription(row, e.NewValue as ModListItemViewModel);
            ResetRowOverlays(row);
        }
    }

    private void ModsDataGridRow_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            row.DataContextChanged -= ModsDataGridRow_OnDataContextChanged;
            UpdateRowModSubscription(row, null);
            ClearRowOverlayValues(row);
            SetRowIsHovered(row, false);
        }
    }

    private void ModsDataGridRow_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            SetRowIsHovered(row, true);
            ResetRowOverlays(row);
        }
    }

    private void ModsDataGridRow_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            SetRowIsHovered(row, false);
            ResetRowOverlays(row);
        }
    }

    private static void ResetRowOverlays(DataGridRow row)
    {
        row.ApplyTemplate();

        Border? selectionOverlay = row.Template?.FindName("SelectionOverlay", row) as Border;
        Border? hoverOverlay = row.Template?.FindName("HoverOverlay", row) as Border;

        if (selectionOverlay == null && hoverOverlay == null)
        {
            return;
        }

        if (row.DataContext is not ModListItemViewModel mod)
        {
            selectionOverlay?.ClearValue(UIElement.OpacityProperty);
            hoverOverlay?.ClearValue(UIElement.OpacityProperty);
            return;
        }

        bool isModSelected = mod.IsSelected || row.IsSelected;
        bool isHovered = GetRowIsHovered(row);

        if (selectionOverlay != null)
        {
            double targetOpacity = isModSelected ? SelectionOverlayOpacity : 0;
            selectionOverlay.Opacity = targetOpacity;
        }

        if (hoverOverlay != null)
        {
            bool shouldShowHover = isHovered && !isModSelected && !AreHoverOverlaysSuppressed(row);
            hoverOverlay.Opacity = shouldShowHover ? HoverOverlayOpacity : 0;
        }
    }

    private bool AreHoverOverlaysSuppressed()
    {
        return _viewModel?.IsLoadingMods == true || _viewModel?.IsLoadingModDetails == true;
    }

    private static bool AreHoverOverlaysSuppressed(DataGridRow row)
    {
        return Window.GetWindow(row) is MainWindow mainWindow && mainWindow.AreHoverOverlaysSuppressed();
    }

    private static void ClearRowOverlayValues(DataGridRow row)
    {
        row.ApplyTemplate();

        if (row.Template?.FindName("SelectionOverlay", row) is Border selectionOverlay)
        {
            selectionOverlay.ClearValue(UIElement.OpacityProperty);
        }

        if (row.Template?.FindName("HoverOverlay", row) is Border hoverOverlay)
        {
            hoverOverlay.ClearValue(UIElement.OpacityProperty);
        }
    }

    private void RefreshHoverOverlayState()
    {
        RefreshRowHoverOverlays(ModsDataGrid);
        RefreshRowHoverOverlays(CloudModlistsDataGrid);
    }

    private static void RefreshRowHoverOverlays(DataGrid? dataGrid)
    {
        if (dataGrid == null)
        {
            return;
        }

        ItemContainerGenerator generator = dataGrid.ItemContainerGenerator;
        foreach (object item in dataGrid.Items)
        {
            if (generator.ContainerFromItem(item) is DataGridRow row)
            {
                ResetRowOverlays(row);
            }
        }
    }

    private static void UpdateRowModSubscription(DataGridRow row, ModListItemViewModel? newMod)
    {
        if (GetBoundMod(row) is { } oldMod && GetBoundModHandler(row) is { } oldHandler)
        {
            oldMod.PropertyChanged -= oldHandler;
        }

        if (newMod is { })
        {
            PropertyChangedEventHandler handler = (_, args) =>
            {
                if (args.PropertyName == nameof(ModListItemViewModel.IsSelected))
                {
                    if (row.Dispatcher.CheckAccess())
                    {
                        ResetRowOverlays(row);
                    }
                    else
                    {
                        row.Dispatcher.Invoke(() => ResetRowOverlays(row));
                    }
                }
            };

            newMod.PropertyChanged += handler;
            SetBoundModHandler(row, handler);
        }
        else
        {
            SetBoundModHandler(row, null);
        }

        SetBoundMod(row, newMod);
    }

    private static void SetBoundMod(DataGridRow row, ModListItemViewModel? mod)
    {
        row.SetValue(BoundModProperty, mod);
    }

    private static ModListItemViewModel? GetBoundMod(DataGridRow row)
    {
        return (ModListItemViewModel?)row.GetValue(BoundModProperty);
    }

    private static void SetBoundModHandler(DataGridRow row, PropertyChangedEventHandler? handler)
    {
        row.SetValue(BoundModHandlerProperty, handler);
    }

    private static PropertyChangedEventHandler? GetBoundModHandler(DataGridRow row)
    {
        return (PropertyChangedEventHandler?)row.GetValue(BoundModHandlerProperty);
    }

    private static void SetRowIsHovered(DataGridRow row, bool value)
    {
        row.SetValue(RowIsHoveredProperty, value);
    }

    private static bool GetRowIsHovered(DataGridRow row)
    {
        return (bool)row.GetValue(RowIsHoveredProperty);
    }

    private void ModDatabasePageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { DataContext: ModListItemViewModel mod }
            && mod.OpenModDatabasePageCommand is ICommand command
            && command.CanExecute(null))
        {
            command.Execute(null);
        }

        e.Handled = true;
    }

    private void EditConfigButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { DataContext: ModListItemViewModel mod })
        {
            return;
        }

        e.Handled = true;

        if (string.IsNullOrWhiteSpace(mod.ModId))
        {
            return;
        }

        string? configPath = null;
        string? storedPath = null;

        try
        {
            if (_userConfiguration.TryGetModConfigPath(mod.ModId, out string? existing) && !string.IsNullOrWhiteSpace(existing))
            {
                storedPath = existing;
                if (File.Exists(existing))
                {
                    configPath = existing;
                }
                else
                {
                    _userConfiguration.RemoveModConfigPath(mod.ModId);
                    UpdateSelectedModEditConfigButton(mod);
                }
            }

            if (configPath is null)
            {
                configPath = PromptForConfigFile(mod, storedPath);
                if (configPath is null)
                {
                    return;
                }

                _userConfiguration.SetModConfigPath(mod.ModId, configPath);
                UpdateSelectedModEditConfigButton(mod);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            WpfMessageBox.Show($"Failed to store the configuration path:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        try
        {
            var editorViewModel = new ModConfigEditorViewModel(mod.DisplayName, configPath);
            var editorWindow = new ModConfigEditorWindow(editorViewModel)
            {
                Owner = this
            };

            bool? result = editorWindow.ShowDialog();
            if (result == true)
            {
                if (!string.Equals(configPath, editorViewModel.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        _userConfiguration.SetModConfigPath(mod.ModId, editorViewModel.FilePath);
                        UpdateSelectedModEditConfigButton(mod);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        WpfMessageBox.Show($"Failed to store the configuration path:\n{ex.Message}",
                            "Simple VS Manager",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }

                _viewModel?.ReportStatus($"Saved config for {mod.DisplayName}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            WpfMessageBox.Show($"Failed to open the configuration file:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _userConfiguration.RemoveModConfigPath(mod.ModId);
            UpdateSelectedModEditConfigButton(mod);
        }
    }

    private async void DeleteModButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SearchModDatabase == true)
        {
            return;
        }

        if (sender is not WpfButton button)
        {
            return;
        }

        if (button.DataContext is ModListItemViewModel mod)
        {
            e.Handled = true;
            await DeleteSingleModAsync(mod);
            return;
        }

        if (_selectedMods.Count == 0)
        {
            return;
        }

        e.Handled = true;
        await DeleteSelectedModsAsync();
    }

    private async Task DeleteSelectedModsAsync()
    {
        if (_selectedMods.Count == 0)
        {
            return;
        }

        if (_selectedMods.Count == 1)
        {
            await DeleteSingleModAsync(_selectedMods[0]);
            return;
        }

        List<ModListItemViewModel> modsToDelete = _selectedMods.ToList();
        await DeleteMultipleModsAsync(modsToDelete);
    }

    private async Task DeleteSingleModAsync(ModListItemViewModel mod)
    {
        if (!TryGetManagedModPath(mod, out string modPath, out string? errorMessage))
        {
            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                WpfMessageBox.Show(errorMessage!,
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return;
        }

        MessageBoxResult confirmation = WpfMessageBox.Show(
            $"Are you sure you want to delete {mod.DisplayName}? This will remove the mod from disk.",
            "Simple VS Manager",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await CreateAutomaticBackupAsync("ModsDeleted").ConfigureAwait(true);

        bool removed = TryDeleteModAtPath(mod, modPath);

        if (_viewModel?.RefreshCommand != null)
        {
            try
            {
                await RefreshModsAsync();
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"The mod list could not be refreshed:{Environment.NewLine}{ex.Message}",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        if (removed)
        {
            _viewModel?.ReportStatus($"Deleted {mod.DisplayName}.");
        }
    }

    private async Task DeleteMultipleModsAsync(IReadOnlyList<ModListItemViewModel> mods)
    {
        if (mods.Count == 0)
        {
            return;
        }

        List<(ModListItemViewModel Mod, string Path)> deletable = new();
        foreach (ModListItemViewModel mod in mods)
        {
            if (!TryGetManagedModPath(mod, out string modPath, out string? errorMessage))
            {
                if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    WpfMessageBox.Show(errorMessage!,
                        "Simple VS Manager",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                continue;
            }

            deletable.Add((mod, modPath));
        }

        if (deletable.Count == 0)
        {
            return;
        }

        StringBuilder confirmationBuilder = new();
        confirmationBuilder.Append($"Are you sure you want to delete {deletable.Count} mods? This will remove them from disk.");
        confirmationBuilder.AppendLine();
        confirmationBuilder.AppendLine();

        const int maxListedMods = 10;
        int listedCount = 0;
        foreach ((ModListItemViewModel mod, _) in deletable)
        {
            if (listedCount >= maxListedMods)
            {
                break;
            }

            confirmationBuilder.AppendLine($"• {mod.DisplayName}");
            listedCount++;
        }

        if (deletable.Count > maxListedMods)
        {
            confirmationBuilder.AppendLine("• …");
        }

        MessageBoxResult confirmation = WpfMessageBox.Show(
            confirmationBuilder.ToString(),
            "Simple VS Manager",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await CreateAutomaticBackupAsync("ModsDeleted").ConfigureAwait(true);

        int removedCount = 0;
        foreach ((ModListItemViewModel mod, string path) in deletable)
        {
            if (TryDeleteModAtPath(mod, path))
            {
                removedCount++;
            }
        }

        if (_viewModel?.RefreshCommand != null)
        {
            try
            {
                await RefreshModsAsync();
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"The mod list could not be refreshed:{Environment.NewLine}{ex.Message}",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        if (removedCount > 0)
        {
            _viewModel?.ReportStatus($"Deleted {removedCount} mod{(removedCount == 1 ? string.Empty : "s")}.");
        }
    }

    private bool TryDeleteModAtPath(ModListItemViewModel mod, string modPath)
    {
        bool removed = false;
        try
        {
            if (Directory.Exists(modPath))
            {
                Directory.Delete(modPath, recursive: true);
                removed = true;
            }
            else if (File.Exists(modPath))
            {
                File.Delete(modPath);
                removed = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WpfMessageBox.Show($"Failed to delete {mod.DisplayName}:{Environment.NewLine}{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        if (!removed)
        {
            WpfMessageBox.Show($"The mod could not be found at:{Environment.NewLine}{modPath}{Environment.NewLine}It may have already been removed.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        _userConfiguration.RemoveModConfigPath(mod.ModId, preserveHistory: true);
        return true;
    }

    private async void FixModButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isModUpdateInProgress)
        {
            return;
        }

        if (_viewModel is null || _viewModel.SearchModDatabase)
        {
            return;
        }

        if (sender is not WpfButton { DataContext: ModListItemViewModel mod })
        {
            return;
        }

        e.Handled = true;

        IReadOnlyList<ModDependencyInfo> dependencies = mod.Dependencies;
        if (dependencies.Count == 0)
        {
            WpfMessageBox.Show("This mod does not declare dependencies that can be fixed automatically.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        IReadOnlyCollection<string> errorSourcePathsBeforeFix =
            _viewModel.GetSourcePathsForModsWithErrors();
        var modsToRefresh = new HashSet<string>(errorSourcePathsBeforeFix, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(mod.SourcePath))
        {
            modsToRefresh.Add(mod.SourcePath);
        }

        _isModUpdateInProgress = true;
        UpdateSelectedModButtons();

        var failures = new List<string>();
        bool anySuccess = false;
        var processedDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var dependency in dependencies)
            {
                if (dependency.IsGameOrCoreDependency || !processedDependencies.Add(dependency.ModId))
                {
                    continue;
                }

                ModListItemViewModel? installedDependency = _viewModel.FindInstalledModById(dependency.ModId);

                bool isMissing = mod.MissingDependencies.Any(d => string.Equals(d.ModId, dependency.ModId, StringComparison.OrdinalIgnoreCase));
                if (!isMissing && installedDependency is null)
                {
                    isMissing = true;
                }

                if (!isMissing && installedDependency != null)
                {
                    bool satisfies = VersionStringUtility.SatisfiesMinimumVersion(dependency.Version, installedDependency.Version);
                    if (!satisfies)
                    {
                        isMissing = true;
                    }
                }

                if (isMissing)
                {
                    var result = await InstallOrUpdateDependencyAsync(dependency, installedDependency).ConfigureAwait(true);
                    if (!result.Success)
                    {
                        failures.Add($"{dependency.Display}: {result.Message}");
                        _viewModel.ReportStatus($"Failed to install dependency {dependency.Display}: {result.Message}", true);
                    }
                    else
                    {
                        anySuccess = true;
                        _viewModel.ReportStatus(result.Message);
                    }

                    continue;
                }

                if (installedDependency != null && !installedDependency.IsActive)
                {
                    installedDependency.IsActive = true;
                    anySuccess = true;
                    _viewModel.ReportStatus($"Activated dependency {installedDependency.DisplayName}.");
                }
            }
        }
        finally
        {
            _isModUpdateInProgress = false;
            UpdateSelectedModButtons();
        }

        if (_viewModel is { } viewModel && modsToRefresh.Count > 0)
        {
            try
            {
                await viewModel.RefreshModsWithErrorsAsync(modsToRefresh).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"The mods with errors could not be refreshed after fixing dependencies:{Environment.NewLine}{ex.Message}",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            UpdateSelectedModButtons();
        }

        if (failures.Count > 0)
        {
            string message = string.Join(Environment.NewLine, failures);
            WpfMessageBox.Show($"Some dependencies could not be resolved:{Environment.NewLine}{message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _viewModel.ReportStatus($"Failed to resolve all dependencies for {mod.DisplayName}.", true);
        }
        else if (anySuccess)
        {
            _viewModel.ReportStatus($"Resolved dependencies for {mod.DisplayName}.");
        }
        else
        {
            _viewModel.ReportStatus($"Dependencies for {mod.DisplayName} are already satisfied.");
        }
    }

    private async void InstallModButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isModUpdateInProgress)
        {
            return;
        }

        if (_viewModel?.SearchModDatabase != true)
        {
            return;
        }

        if (sender is not WpfButton { DataContext: ModListItemViewModel mod })
        {
            return;
        }

        e.Handled = true;

        if (!mod.HasDownloadableRelease)
        {
            WpfMessageBox.Show("No downloadable releases are available for this mod.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        ModReleaseInfo? release = SelectReleaseForInstall(mod);
        if (release is null)
        {
            WpfMessageBox.Show("No downloadable releases are available for this mod.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!TryGetInstallTargetPath(mod, release, out string targetPath, out string? errorMessage))
        {
            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                WpfMessageBox.Show(errorMessage!,
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return;
        }

        await CreateAutomaticBackupAsync("ModsUpdated").ConfigureAwait(true);

        _isModUpdateInProgress = true;
        UpdateSelectedModButtons();

        try
        {
            var descriptor = new ModUpdateDescriptor(
                mod.ModId,
                mod.DisplayName,
                release.DownloadUri,
                targetPath,
                false,
                release.FileName,
                release.Version,
                mod.Version);

            var progress = new Progress<ModUpdateProgress>(p =>
                _viewModel?.ReportStatus($"{mod.DisplayName}: {p.Message}"));

            ModUpdateResult result = await _modUpdateService
                .UpdateAsync(descriptor, _userConfiguration.CacheAllVersionsLocally, progress)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                string message = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "The installation failed."
                    : result.ErrorMessage!;
                _viewModel?.ReportStatus($"Failed to install {mod.DisplayName}: {message}", true);
                WpfMessageBox.Show($"Failed to install {mod.DisplayName}:{Environment.NewLine}{message}",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            string versionText = string.IsNullOrWhiteSpace(release.Version) ? string.Empty : $" {release.Version}";
            _viewModel?.ReportStatus($"Installed {mod.DisplayName}{versionText}.");

            string? installedModId = string.IsNullOrWhiteSpace(mod.ModId) ? null : mod.ModId.Trim();
            IReadOnlyCollection<string>? autoAssignTargets = installedModId is null
                ? null
                : new[] { installedModId };

            await RefreshModsAsync(autoAssignTargets).ConfigureAwait(true);

            if (mod.IsSelected)
            {
                RemoveFromSelection(mod);
            }

            _viewModel?.RemoveSearchResult(mod);
        }
        catch (OperationCanceledException)
        {
            _viewModel?.ReportStatus("Installation cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _viewModel?.ReportStatus($"Failed to install {mod.DisplayName}: {ex.Message}", true);
            WpfMessageBox.Show($"Failed to install {mod.DisplayName}:{Environment.NewLine}{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isModUpdateInProgress = false;
            UpdateSelectedModButtons();
        }
    }

    private async Task<(bool Success, string Message)> InstallOrUpdateDependencyAsync(
        ModDependencyInfo dependency,
        ModListItemViewModel? installedMod)
    {
        try
        {
            ModDatabaseInfo? info = await _modDatabaseService
                .TryLoadDatabaseInfoAsync(dependency.ModId, installedMod?.Version, _viewModel?.InstalledGameVersion, _userConfiguration.RequireExactVsVersionMatch)
                .ConfigureAwait(true);

            if (info is null)
            {
                return (false, "Mod not found on the mod database.");
            }

            ModReleaseInfo? release = SelectReleaseForDependency(dependency, info);
            if (release is null)
            {
                return (false, "No compatible releases were found.");
            }

            string targetPath;
            bool targetIsDirectory;
            string? existingPath = null;

            if (installedMod != null)
            {
                if (!TryGetManagedModPath(installedMod, out targetPath, out string? pathError))
                {
                    return (false, pathError ?? "The mod path could not be determined.");
                }

                targetIsDirectory = Directory.Exists(targetPath);
                if (!targetIsDirectory && !File.Exists(targetPath) && installedMod.SourceKind == ModSourceKind.Folder)
                {
                    targetIsDirectory = true;
                }

                if (!targetIsDirectory)
                {
                    if (!TryGetUpdateTargetPath(installedMod, release, targetPath, out string resolvedPath, out string? targetError))
                    {
                        return (false, targetError ?? "The mod path could not be determined.");
                    }

                    existingPath = targetPath;
                    targetPath = resolvedPath;
                }
            }
            else
            {
                if (!TryGetDependencyInstallTargetPath(dependency.ModId, release, out targetPath, out string? errorMessage))
                {
                    return (false, errorMessage ?? "The Mods folder is not available.");
                }

                targetIsDirectory = false;
            }

            bool wasActive = installedMod?.IsActive == true;

            var descriptor = new ModUpdateDescriptor(
                dependency.ModId,
                dependency.Display,
                release.DownloadUri,
                targetPath,
                targetIsDirectory,
                release.FileName,
                release.Version,
                installedMod?.Version)
            {
                ExistingPath = existingPath
            };

            var progress = new Progress<ModUpdateProgress>(p =>
                _viewModel?.ReportStatus($"{dependency.ModId}: {p.Message}"));

            ModUpdateResult updateResult = await _modUpdateService
                .UpdateAsync(descriptor, _userConfiguration.CacheAllVersionsLocally, progress)
                .ConfigureAwait(true);

            if (!updateResult.Success)
            {
                string message = string.IsNullOrWhiteSpace(updateResult.ErrorMessage)
                    ? "The installation failed."
                    : updateResult.ErrorMessage!;
                return (false, message);
            }

            if (installedMod != null && _viewModel != null)
            {
                await _viewModel.PreserveActivationStateAsync(
                    dependency.ModId,
                    installedMod.Version,
                    release.Version,
                    wasActive).ConfigureAwait(true);
            }

            string action = installedMod != null ? "Updated" : "Installed";
            string versionSuffix = string.IsNullOrWhiteSpace(release.Version) ? string.Empty : $" {release.Version}";
            return (true, $"{action} dependency {dependency.Display}{versionSuffix}.");
        }
        catch (OperationCanceledException)
        {
            return (false, "The operation was cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (false, ex.Message);
        }
    }

    private static ModReleaseInfo? SelectReleaseForDependency(ModDependencyInfo dependency, ModDatabaseInfo info)
    {
        if (info is null)
        {
            return null;
        }

        IReadOnlyList<ModReleaseInfo> releases = info.Releases ?? Array.Empty<ModReleaseInfo>();
        if (releases.Count == 0)
        {
            return null;
        }

        foreach (var release in releases)
        {
            if (release.IsCompatibleWithInstalledGame
                && VersionStringUtility.SatisfiesMinimumVersion(dependency.Version, release.Version))
            {
                return release;
            }
        }

        foreach (var release in releases)
        {
            if (VersionStringUtility.SatisfiesMinimumVersion(dependency.Version, release.Version))
            {
                return release;
            }
        }

        ModReleaseInfo fallback = releases.FirstOrDefault(r => r.IsCompatibleWithInstalledGame)
            ?? releases[0];

        string availableVersion = string.IsNullOrWhiteSpace(fallback.Version)
            ? "the latest available release"
            : $"version {fallback.Version}";

        string requirement = string.IsNullOrWhiteSpace(dependency.Version)
            ? dependency.ModId
            : $"{dependency.ModId} {dependency.Version} or newer";

        string message =
            $"No release that satisfies the required minimum version for {dependency.Display} could be found.{Environment.NewLine}{Environment.NewLine}" +
            $"The mod database only provides {availableVersion}, which may not resolve the dependency requirement for {requirement}.{Environment.NewLine}{Environment.NewLine}" +
            "Do you want to install this older release anyway?";

        MessageBoxResult confirmation = WpfMessageBox.Show(
            message,
            "Simple VS Manager",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return confirmation == MessageBoxResult.Yes ? fallback : null;
    }

    private static ModReleaseInfo? SelectReleaseForInstall(ModListItemViewModel mod)
    {
        if (mod.LatestRelease?.IsCompatibleWithInstalledGame == true)
        {
            return mod.LatestRelease;
        }

        if (mod.LatestCompatibleRelease != null)
        {
            return mod.LatestCompatibleRelease;
        }

        return mod.LatestRelease;
    }

    private async void UpdateModButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isModUpdateInProgress)
        {
            return;
        }

        if (sender is not WpfButton { DataContext: ModListItemViewModel mod })
        {
            return;
        }

        e.Handled = true;

        IReadOnlyDictionary<ModListItemViewModel, ModReleaseInfo>? overrides = null;
        if (mod.SelectedVersionOption is { Release: { } selectedRelease, IsInstalled: false })
        {
            overrides = new Dictionary<ModListItemViewModel, ModReleaseInfo>
            {
                [mod] = selectedRelease
            };
        }

        await UpdateModsAsync(new[] { mod }, isBulk: false, overrides);
    }

    private async void SelectedModVersionComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isModUpdateInProgress)
        {
            return;
        }

        if (sender is not System.Windows.Controls.ComboBox comboBox)
        {
            return;
        }

        if (!comboBox.IsDropDownOpen && !comboBox.IsKeyboardFocusWithin)
        {
            return;
        }

        if (_viewModel?.SelectedMod is not ModListItemViewModel mod)
        {
            return;
        }

        if (comboBox.SelectedItem is not ModVersionOptionViewModel option)
        {
            return;
        }

        if (option.IsInstalled || option.Release is null)
        {
            return;
        }

        if (string.Equals(mod.Version, option.Version, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var overrides = new Dictionary<ModListItemViewModel, ModReleaseInfo>
        {
            [mod] = option.Release
        };

        await UpdateModsAsync(new[] { mod }, isBulk: false, overrides);
    }

    private void SelectedModVersionComboBox_OnDropDownOpened(object sender, EventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox comboBox)
        {
            return;
        }

        void ScrollToTop()
        {
            ScrollViewer? scrollViewer = null;

            if (comboBox.Template?.FindName("Popup", comboBox) is Popup popup)
            {
                scrollViewer = FindDescendantScrollViewer(popup.Child);
            }

            scrollViewer ??= FindDescendantScrollViewer(comboBox);
            if (scrollViewer != null)
            {
                scrollViewer.ScrollToHome();
                scrollViewer.ScrollToVerticalOffset(0);
            }
        }

        comboBox.Dispatcher.BeginInvoke((Action)ScrollToTop, DispatcherPriority.Background);
    }

    private async void RebuildButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isApplyingPreset)
        {
            return;
        }

        if (_isFullRefreshInProgress)
        {
            return;
        }

        var viewModel = _viewModel;
        if (viewModel?.RefreshCommand == null)
        {
            return;
        }

        if (viewModel.IsBusy)
        {
            WpfMessageBox.Show(
                "Please wait for the current operation to finish before refreshing.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        ModlistLoadMode? loadMode = GetRebuildModlistLoadMode();
        if (loadMode is not ModlistLoadMode resolvedLoadMode)
        {
            return;
        }

        if (!await EnsureModDatabaseReachableForRebuildAsync().ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await EnsureInstalledModsCachedAsync(viewModel, ignoreUserSetting: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(
                $"Failed to cache the currently installed mods before rebuilding:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string requestedModlistName = $"Rebuilt_{timestamp}";
        string savedModlistName = string.Empty;
        string savedModlistPath = string.Empty;

        _isFullRefreshInProgress = true;
        bool cachesCleared = false;

        try
        {
            if (!TrySaveAutomaticModlist(requestedModlistName, out savedModlistName, out savedModlistPath))
            {
                return;
            }

            try
            {
                await Task.Run(() => ClearManagerCaches(preserveModCache: true)).ConfigureAwait(true);
                cachesCleared = true;
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(
                    $"Failed to clear cached mod data:\n{ex.Message}",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            await RefreshDeleteCachedModsMenuHeaderAsync().ConfigureAwait(true);

            if (!cachesCleared)
            {
                return;
            }

            (bool Success, int RemovedCount) deletionResult = await DeleteAllInstalledModsForRebuildAsync().ConfigureAwait(true);
            if (!deletionResult.Success)
            {
                WpfMessageBox.Show(
                    "Rebuild cancelled because some mods could not be removed.",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            PresetLoadOptions loadOptions = GetModlistLoadOptions(resolvedLoadMode);

            if (!TryLoadPresetFromFile(savedModlistPath, "Modlist", loadOptions, out ModPreset? preset, out string? errorMessage))
            {
                string message = string.IsNullOrWhiteSpace(errorMessage)
                    ? "The saved rebuild modlist could not be loaded."
                    : errorMessage!;
                WpfMessageBox.Show(
                    $"Failed to load the rebuild modlist:\n{message}",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            await ApplyPresetAsync(preset!).ConfigureAwait(true);

            string status = resolvedLoadMode == ModlistLoadMode.Replace
                ? $"Rebuilt mods from \"{savedModlistName}\"."
                : $"Rebuilt mods from \"{savedModlistName}\" (added mods).";
            viewModel.ReportStatus(status);
        }
        finally
        {
            _isFullRefreshInProgress = false;
        }
    }

    private async void UpdateAllModsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isApplyingPreset)
        {
            return;
        }

        if (_isModUpdateInProgress || _viewModel?.ModsView == null)
        {
            return;
        }

        var mods = _viewModel.ModsView.Cast<ModListItemViewModel>()
            .Where(mod => mod.CanUpdate)
            .ToList();

        Dictionary<ModListItemViewModel, ModReleaseInfo>? overrides = null;
        foreach (ModListItemViewModel mod in mods)
        {
            if (mod.SelectedVersionOption is { Release: { } selectedRelease, IsInstalled: false })
            {
                overrides ??= new Dictionary<ModListItemViewModel, ModReleaseInfo>();
                overrides[mod] = selectedRelease;
            }
        }

        if (mods.Count == 0)
        {
            WpfMessageBox.Show("All mods are already up to date.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new UpdateModsDialog(_userConfiguration, mods, overrides)
        {
            Owner = this
        };

        bool? dialogResult = dialog.ShowDialog();
        if (dialogResult != true)
        {
            return;
        }

        IReadOnlyList<ModListItemViewModel> selectedMods = dialog.SelectedMods;
        if (selectedMods.Count == 0)
        {
            return;
        }

        Dictionary<ModListItemViewModel, ModReleaseInfo>? selectedOverrides = null;
        if (overrides != null)
        {
            foreach (ModListItemViewModel mod in selectedMods)
            {
                if (overrides.TryGetValue(mod, out ModReleaseInfo? release) && release != null)
                {
                    selectedOverrides ??= new Dictionary<ModListItemViewModel, ModReleaseInfo>();
                    selectedOverrides[mod] = release;
                }
            }
        }

        await CreateAutomaticBackupAsync("ModsUpdated").ConfigureAwait(true);
        await UpdateModsAsync(selectedMods, isBulk: true, selectedOverrides).ConfigureAwait(true);
    }

    private async void CheckModsCompatibilityMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var viewModel = _viewModel;
        if (viewModel is null)
        {
            return;
        }

        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            WpfMessageBox.Show(
                "Enable Internet Access in the File menu to check mod compatibility.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        IReadOnlyList<string> recentVersions;
        try
        {
            recentVersions = await VintageStoryGameVersionService
                .GetRecentReleaseVersionsAsync(10)
                .ConfigureAwait(true);
        }
        catch (HttpRequestException ex)
        {
            WpfMessageBox.Show(
                $"Failed to retrieve Vintage Story versions:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }
        catch (TaskCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(
                $"Failed to retrieve Vintage Story versions:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (recentVersions is not { Count: > 0 })
        {
            WpfMessageBox.Show(
                "Could not determine recent Vintage Story versions.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var versionSelectionDialog = new VintageStoryVersionSelectionDialog(
            this,
            recentVersions,
            viewModel.InstalledGameVersion);
        bool? selectionResult = versionSelectionDialog.ShowDialog();
        if (selectionResult != true)
        {
            return;
        }

        string? targetVersion = versionSelectionDialog.SelectedVersion;
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            return;
        }

        targetVersion = targetVersion.Trim();

        IReadOnlyList<ModListItemViewModel> mods = viewModel.GetInstalledModsSnapshot();
        if (mods.Count == 0)
        {
            WpfMessageBox.Show(
                $"Vintage Story version: {targetVersion}.\n\nNo installed mods were found.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var incompatible = new List<string>();
        var unknown = new List<string>();

        foreach (ModListItemViewModel mod in mods)
        {
            if (mod is null)
            {
                continue;
            }

            string displayName = string.IsNullOrWhiteSpace(mod.DisplayName)
                ? (mod.ModId ?? "Unknown mod")
                : mod.DisplayName!;

            CompatibilityEvaluation evaluation = EvaluateCompatibility(mod, targetVersion, displayName, _userConfiguration.RequireExactVsVersionMatch);
            if (evaluation.IsCompatible)
            {
                continue;
            }

            if (evaluation.IsUnknown)
            {
                unknown.Add(displayName);
            }
            else
            {
                incompatible.Add(displayName);
            }
        }

        incompatible.Sort(StringComparer.CurrentCultureIgnoreCase);
        unknown.Sort(StringComparer.CurrentCultureIgnoreCase);

        var resultsDialog = new CompatibilityResultsDialog(this, targetVersion, incompatible, unknown);
        _ = resultsDialog.ShowDialog();
    }

    private static CompatibilityEvaluation EvaluateCompatibility(
        ModListItemViewModel mod,
        string targetVersion,
        string displayName,
        bool requireExactMatch)
    {
        string installedVersion = string.IsNullOrWhiteSpace(mod.Version)
            ? "Unknown"
            : mod.Version!;

        ModVersionOptionViewModel? installedOption = mod.VersionOptions
            .FirstOrDefault(option => option is { IsInstalled: true });
        ModReleaseInfo? installedRelease = installedOption?.Release;

        List<ModReleaseInfo> releases = mod.VersionOptions
            .Select(option => option.Release)
            .Where(release => release != null)
            .GroupBy(release => release!.Version, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()!)
            .ToList();

        ModReleaseInfo? compatibleRelease = releases
            .FirstOrDefault(release => ReleaseSupportsVersion(release, targetVersion, requireExactMatch));

        if (installedRelease != null)
        {
            if (ReleaseSupportsVersion(installedRelease, targetVersion, requireExactMatch))
            {
                return CompatibilityEvaluation.Compatible;
            }

            if (compatibleRelease != null
                && !string.Equals(compatibleRelease.Version, installedRelease.Version, StringComparison.OrdinalIgnoreCase))
            {
                string message =
                    $"{displayName}: Installed version {installedRelease.Version} is not marked as compatible with Vintage Story {targetVersion}. Update to version {compatibleRelease.Version} or later.";
                return CompatibilityEvaluation.Incompatible(message);
            }

            if (installedRelease.GameVersionTags is { Count: > 0 })
            {
                string message =
                    $"{displayName}: Installed version {installedVersion} is not marked as compatible with Vintage Story {targetVersion}. No compatible update was found.";
                return CompatibilityEvaluation.Incompatible(message);
            }
        }

        if (compatibleRelease != null)
        {
            string message =
                $"{displayName}: Installed version {installedVersion} is not marked as compatible with Vintage Story {targetVersion}. Update to version {compatibleRelease.Version} or later.";
            return CompatibilityEvaluation.Incompatible(message);
        }

        IReadOnlyList<ModDependencyInfo> dependencies = mod.Dependencies ?? Array.Empty<ModDependencyInfo>();
        bool hasGameDependency = false;

        foreach (ModDependencyInfo dependency in dependencies)
        {
            if (dependency is null || !dependency.IsGameOrCoreDependency || string.IsNullOrWhiteSpace(dependency.Version))
            {
                continue;
            }

            hasGameDependency = true;
            if (!VersionStringUtility.SatisfiesMinimumVersion(dependency.Version, targetVersion))
            {
                string message =
                    $"{displayName}: Requires Vintage Story {dependency.Version} or newer.";
                return CompatibilityEvaluation.Incompatible(message);
            }
        }

        if (hasGameDependency)
        {
            return CompatibilityEvaluation.Compatible;
        }

        string unknownMessage =
            $"{displayName}: No compatibility metadata is available for Vintage Story {targetVersion}.";
        return CompatibilityEvaluation.Unknown(unknownMessage);
    }

    private static bool ReleaseSupportsVersion(ModReleaseInfo release, string targetVersion, bool requireExactMatch)
    {
        if (release.GameVersionTags is not { Count: > 0 })
        {
            return false;
        }

        foreach (string tag in release.GameVersionTags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            if (VersionStringUtility.SupportsVersion(tag, targetVersion, requireExactMatch))
            {
                return true;
            }
        }

        return false;
    }

    private readonly struct CompatibilityEvaluation
    {
        private CompatibilityEvaluation(bool isCompatible, bool isUnknown, string? message)
        {
            IsCompatible = isCompatible;
            IsUnknown = isUnknown;
            Message = message;
        }

        public bool IsCompatible { get; }

        public bool IsUnknown { get; }

        public string? Message { get; }

        public static CompatibilityEvaluation Compatible { get; } = new(true, false, null);

        public static CompatibilityEvaluation Incompatible(string message) => new(false, false, message);

        public static CompatibilityEvaluation Unknown(string message) => new(false, true, message);
    }

    private async void ModsMenuItem_OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        await RefreshDeleteCachedModsMenuHeaderAsync();
    }

    private async void DeleteCachedModsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        MessageBoxResult result = WpfMessageBox.Show(
            "This will only delete the managers cached mods to save some disk space, it will not affect your installed mods.",
            "Simple VS Manager",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        string? cachedModsDirectory = GetCachedModsDirectory();
        if (string.IsNullOrWhiteSpace(cachedModsDirectory))
        {
            WpfMessageBox.Show(
                "Could not determine the cached mods directory.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            await RefreshDeleteCachedModsMenuHeaderAsync();
            return;
        }

        if (!Directory.Exists(cachedModsDirectory))
        {
            WpfMessageBox.Show(
                "No cached mods were found.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            await RefreshDeleteCachedModsMenuHeaderAsync();
            return;
        }

        try
        {
            foreach (string directory in Directory.GetDirectories(cachedModsDirectory))
            {
                Directory.Delete(directory, recursive: true);
            }

            WpfMessageBox.Show(
                "Cached mods deleted successfully.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(
                $"Failed to delete cached mods:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        await RefreshDeleteCachedModsMenuHeaderAsync();
    }

    private void ManagerDataFolderMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        string? directory = GetManagerDataDirectory();
        if (string.IsNullOrWhiteSpace(directory))
        {
            WpfMessageBox.Show(
                "The manager data folder is not available on this system.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(
                $"Failed to open the manager data folder:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        OpenFolder(directory, "manager data");
    }

    private void ManagerModDbPageMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        OpenManagerModDatabasePage();
    }
    private void HelpMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        string managerDirectory = _userConfiguration.GetConfigurationDirectory();
        string? cachedModsDirectory = ModCacheLocator.GetCachedModsDirectory();

        var dialog = new HelpDialogWindow(managerDirectory, cachedModsDirectory)
        {
            Owner = this
        };

        _ = dialog.ShowDialog();
    }
    private void GuideMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        string managerDirectory = _userConfiguration.GetConfigurationDirectory();
        string configurationFilePath = Path.Combine(managerDirectory, "SimpleVSManagerConfiguration.json");
        string? cachedModsDirectory = ModCacheLocator.GetCachedModsDirectory();

        var dialog = new GuideDialogWindow(managerDirectory, cachedModsDirectory, configurationFilePath)
        {
            Owner = this
        };

        _ = dialog.ShowDialog();
    }

    private void ManagerUpdateLink_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        OpenManagerModDatabasePage();
    }

    private async void ModUsagePromptLink_OnClick(object sender, RoutedEventArgs e)
    {
        if (e is not null)
        {
            e.Handled = true;
        }

        await ShowModUsagePromptDialogAsync().ConfigureAwait(true);
    }

    private void OpenManagerModDatabasePage()
    {
        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            WpfMessageBox.Show(
                "Internet access is disabled. Enable Internet Access in the File menu to open the mod database page.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ManagerModDatabaseUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(
                $"Failed to open the mod database page:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void ExperimentalCompReviewMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedMod is not ModListItemViewModel selectedMod)
        {
            WpfMessageBox.Show(
                "Select a mod first!",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string modSlug = ResolveExperimentalCompReviewIdentifier(selectedMod);
        string? latestVersion = string.IsNullOrWhiteSpace(_viewModel?.InstalledGameVersion)
            ? null
            : _viewModel!.InstalledGameVersion;

        try
        {
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

            ModCompatibilityCommentsService.ExperimentalCompReviewResult result = await _modCompatibilityCommentsService
                .GetTop3CommentsAsync(modSlug, latestVersion)
                .ConfigureAwait(true);

            string messageText = BuildExperimentalCompReviewMessage(result);
            if (string.IsNullOrWhiteSpace(messageText))
            {
                messageText = result.Reason ?? "No relevant comments were found.";
            }

            string title = string.Format(
                CultureInfo.CurrentCulture,
                "Compatibility comments for {0}",
                selectedMod.DisplayName);

            WpfMessageBox.Show(
                messageText,
                title,
                MessageBoxButton.OK,
                result.Top3.Count > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (InternetAccessDisabledException ex)
        {
            WpfMessageBox.Show(
                ex.Message,
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(
                $"The experimental compatibility review failed:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private static string BuildExperimentalCompReviewMessage(
        ModCompatibilityCommentsService.ExperimentalCompReviewResult result)
    {
        if (result.Top3 is not { Count: > 0 })
        {
            return result.Reason ?? string.Empty;
        }

        var builder = new StringBuilder();
        for (int index = 0; index < result.Top3.Count; index++)
        {
            ModCompatibilityCommentsService.ExperimentalCompReviewComment comment = result.Top3[index];
            double totalScore = comment.ScoreBreakdown?.Values.Sum() ?? 0;
            string scoreText = FormatExperimentalCompReviewScore(totalScore);

            builder.Append(index + 1);
            builder.Append(". [");
            builder.Append(scoreText);
            builder.Append("] ");
            builder.AppendLine(comment.Snippet);
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatExperimentalCompReviewScore(double score)
    {
        double rounded = Math.Round(score, 2);
        return rounded.ToString("+0.##;-0.##;0", CultureInfo.CurrentCulture);
    }

    private async void DeleteCloudAuthMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        const string confirmationMessage =
            "This will remove all your online modlists and delete your authorization - good for resetting if something has gone wrong. Visit the Modlists (Beta) tab again to get a fresh firebase-auth";

        MessageBoxResult result = WpfMessageBox.Show(
            this,
            confirmationMessage,
            "Simple VS Manager",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        await ExecuteCloudOperationAsync(
            store => DeleteAllCloudModlistsAndAuthorizationAsync(store),
            "delete all cloud modlists and Firebase authorization");
    }

    private static string ResolveExperimentalCompReviewIdentifier(ModListItemViewModel selectedMod)
    {
        string? fromUrl = TryExtractModSlug(selectedMod.ModDatabasePageUrl);
        if (!string.IsNullOrWhiteSpace(fromUrl))
        {
            return fromUrl!;
        }

        if (!string.IsNullOrWhiteSpace(selectedMod.ModDatabaseAssetId))
        {
            return selectedMod.ModDatabaseAssetId!;
        }

        return selectedMod.ModId;
    }

    private static string? TryExtractModSlug(string? modDatabasePageUrl)
    {
        if (string.IsNullOrWhiteSpace(modDatabasePageUrl))
        {
            return null;
        }

        if (Uri.TryCreate(modDatabasePageUrl, UriKind.Absolute, out Uri? uri))
        {
            string? fromUri = ExtractSlugFromPath(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(fromUri))
            {
                return fromUri;
            }
        }

        return ExtractSlugFromPath(modDatabasePageUrl);

        static string? ExtractSlugFromPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string trimmed = path.Trim();

            int fragmentIndex = trimmed.IndexOf('#');
            if (fragmentIndex >= 0)
            {
                trimmed = trimmed[..fragmentIndex];
            }

            int queryIndex = trimmed.IndexOf('?');
            if (queryIndex >= 0)
            {
                trimmed = trimmed[..queryIndex];
            }

            trimmed = trimmed.Trim('/');

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return null;
            }

            int lastSlash = trimmed.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                trimmed = trimmed[(lastSlash + 1)..];
            }

            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }
    }

    private void ClearAllCachesMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        const string confirmationMessage =
            "This will delete all cache folders used by Simple VS Manager:\n\n" +
            "• Cached Mods\n" +
            "• Mod Database Cache\n" +
            "• Mod Metadata\n" +
            "• Modlists (Cloud Cache)\n\n" +
            "Your settings and installed mods will NOT be affected.\n\n" +
            "This is useful when experiencing problems with the mod database or cached data.\n\n" +
            "Continue?";

        MessageBoxResult confirmation = WpfMessageBox.Show(
            this,
            confirmationMessage,
            "Simple VS Manager",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            var deletedFolders = new List<string>();
            var failedFolders = new List<string>();

            // Get manager data directory
            string? managerDataDir = ModCacheLocator.GetManagerDataDirectory();
            if (string.IsNullOrWhiteSpace(managerDataDir))
            {
                WpfMessageBox.Show(
                    this,
                    "Could not locate the Simple VS Manager data directory.",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            // Define cache folders to delete
            var cacheFolders = new[]
            {
                ("Cached Mods", Path.Combine(managerDataDir, "Cached Mods")),
                ("Mod Database Cache", Path.Combine(managerDataDir, "Mod Database Cache")),
                ("Mod Metadata", Path.Combine(managerDataDir, "Mod Metadata")),
                ("Modlists (Cloud Cache)", Path.Combine(managerDataDir, "Modlists (Cloud Cache)"))
            };

            // Delete each cache folder
            foreach (var (name, path) in cacheFolders)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                        deletedFolders.Add(name);
                    }
                }
                catch (Exception ex)
                {
                    failedFolders.Add($"{name}: {ex.Message}");
                }
            }

            // Show results
            var messageBuilder = new StringBuilder();

            if (deletedFolders.Count > 0)
            {
                messageBuilder.AppendLine("Successfully deleted the following cache folders:");
                foreach (string folder in deletedFolders)
                {
                    messageBuilder.AppendLine($"• {folder}");
                }
            }
            else if (failedFolders.Count == 0)
            {
                messageBuilder.AppendLine("No cache folders were found to delete.");
            }

            if (failedFolders.Count > 0)
            {
                if (messageBuilder.Length > 0)
                {
                    messageBuilder.AppendLine();
                }
                messageBuilder.AppendLine("Failed to delete the following cache folders:");
                foreach (string error in failedFolders)
                {
                    messageBuilder.AppendLine($"• {error}");
                }
            }

            WpfMessageBox.Show(
                this,
                messageBuilder.ToString(),
                "Simple VS Manager",
                MessageBoxButton.OK,
                failedFolders.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(
                this,
                $"An error occurred while clearing caches:\n\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void DeleteAllManagerFilesMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        const string confirmationMessage =
            "This will move every file Simple VS Manager created to the Recycle Bin, including its AppData/Simple VS Manager folder, any ModData backups, cached mods, presets, and Firebase authentication tokens.\n\n" +
            "You can restore them from the Recycle Bin if needed. Continue?";

        MessageBoxResult confirmation = WpfMessageBox.Show(
            this,
            confirmationMessage,
            "Simple VS Manager",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        await ExecuteCloudOperationAsync(
            store => DeleteAllCloudModlistsAndAuthorizationAsync(store, showCompletionMessage: false),
            "delete the Firebase user and cloud data");

        string? dataDirectory = _dataDirectory;
        ManagerDeletionResult deletionResult = await Task.Run(() => DeleteAllManagerFiles(dataDirectory)).ConfigureAwait(true);

        if (deletionResult.DeletedPaths.Count == 0 && deletionResult.FailedPaths.Count == 0)
        {
            WpfMessageBox.Show(
                this,
                "No Simple VS Manager files were found.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            SwitchToInstalledModsTab();
            return;
        }

        var builder = new StringBuilder();

        if (deletionResult.DeletedPaths.Count > 0)
        {
            builder.AppendLine("Moved the following locations to the Recycle Bin:");
            foreach (string path in deletionResult.DeletedPaths)
            {
                builder.AppendLine($"• {path}");
            }
        }

        if (deletionResult.FailedPaths.Count == 0)
        {
            string message = builder.Length > 0
                ? builder.ToString()
                : "Finished moving Simple VS Manager files to the Recycle Bin.";

            WpfMessageBox.Show(
                this,
                message,
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            SwitchToInstalledModsTab();
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.AppendLine("The following locations could not be moved to the Recycle Bin. Please remove them manually:");
        foreach (string path in deletionResult.FailedPaths)
        {
            builder.AppendLine($"• {path}");
        }

        WpfMessageBox.Show(
            this,
            builder.ToString(),
            "Simple VS Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        SwitchToInstalledModsTab();
    }

    private void SwitchToInstalledModsTab()
    {
        if (_viewModel?.ShowInstalledModsCommand?.CanExecute(null) == true)
        {
            _viewModel.ShowInstalledModsCommand.Execute(null);
        }
    }

    private void PrepareForModlistLoad()
    {
        SwitchToInstalledModsTab();
        ClearSelection(resetAnchor: true);
    }

    private void UpdateModlistLoadingUiState()
    {
        bool isEnabled = !_isApplyingPreset;

        if (UpdateAllButton != null)
        {
            UpdateAllButton.IsEnabled = isEnabled;
        }

        if (RebuildButton != null)
        {
            RebuildButton.IsEnabled = isEnabled;
        }

        if (LaunchGameButton != null)
        {
            LaunchGameButton.IsEnabled = isEnabled;
        }

        if (ModDatabaseTabButton != null)
        {
            ModDatabaseTabButton.IsEnabled = isEnabled;
        }

        if (ModlistsTabButton != null)
        {
            ModlistsTabButton.IsEnabled = isEnabled;
        }

        if (PresetsAndModlistsMenuItem != null)
        {
            PresetsAndModlistsMenuItem.IsEnabled = isEnabled;
        }

        if (UpdateAllModsMenuItem != null)
        {
            UpdateAllModsMenuItem.IsEnabled = isEnabled;
        }

        if (ModsDataGrid != null)
        {
            ModsDataGrid.IsEnabled = isEnabled;
        }

        if (ModDatabaseCardsListView != null)
        {
            ModDatabaseCardsListView.IsEnabled = isEnabled;
        }
    }

    private async Task RefreshDeleteCachedModsMenuHeaderAsync()
    {
        if (DeleteCachedModsMenuItem is null)
        {
            return;
        }

        const string baseHeader = "_Delete Cached Mods";
        string header = baseHeader;

        string? cachedModsDirectory = GetCachedModsDirectory();
        if (!string.IsNullOrWhiteSpace(cachedModsDirectory) && Directory.Exists(cachedModsDirectory))
        {
            long cacheSize = await Task.Run(() => CalculateDirectorySize(cachedModsDirectory));
            long cacheSizeInMegabytes = (long)Math.Round(cacheSize / (1024d * 1024d), MidpointRounding.AwayFromZero);
            if (cacheSizeInMegabytes < 0)
            {
                cacheSizeInMegabytes = 0;
            }

            header = string.Format(CultureInfo.InvariantCulture, "{0} ({1}MB)", baseHeader, cacheSizeInMegabytes);
        }

        DeleteCachedModsMenuItem.Header = header;
    }

    private static long CalculateDirectorySize(string rootDirectory)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootDirectory);
        long totalBytes = 0;

        while (pendingDirectories.Count > 0)
        {
            string currentDirectory = pendingDirectories.Pop();
            if (string.IsNullOrWhiteSpace(currentDirectory) || !Directory.Exists(currentDirectory))
            {
                continue;
            }

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(currentDirectory))
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        totalBytes += fileInfo.Length;
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch (System.Security.SecurityException)
                    {
                    }
                }
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (System.Security.SecurityException)
            {
                continue;
            }

            try
            {
                foreach (string directoryPath in Directory.EnumerateDirectories(currentDirectory))
                {
                    pendingDirectories.Push(directoryPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }
        }

        return totalBytes;
    }

    private static string? GetCachedModsDirectory() => ModCacheLocator.GetCachedModsDirectory();

    private static string? GetManagerDataDirectory() => ModCacheLocator.GetManagerDataDirectory();

    private static ManagerDeletionResult DeleteAllManagerFiles(string? dataDirectory)
    {
        var directoryCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddCandidateDirectory(directoryCandidates, ModCacheLocator.GetManagerDataDirectory());
        AddCandidateDirectory(directoryCandidates, TryCombineSpecialFolder(Environment.SpecialFolder.MyDocuments, "Simple VS Manager"));
        AddCandidateDirectory(directoryCandidates, TryCombineSpecialFolder(Environment.SpecialFolder.Personal, "Simple VS Manager"));
        AddCandidateDirectory(directoryCandidates, TryCombineSpecialFolder(Environment.SpecialFolder.ApplicationData, "Simple VS Manager"));
        AddCandidateDirectory(directoryCandidates, TryCombineSpecialFolder(Environment.SpecialFolder.LocalApplicationData, "Simple VS Manager"));
        AddCandidateDirectory(directoryCandidates, TryCombineSpecialFolder(Environment.SpecialFolder.UserProfile, ".simple-vs-manager"));
        AddCandidateDirectory(directoryCandidates, Path.Combine(AppContext.BaseDirectory, "Simple VS Manager"));
        AddCandidateDirectory(directoryCandidates, Path.Combine(Environment.CurrentDirectory, "Simple VS Manager"));

        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            AddCandidateDirectory(directoryCandidates, Path.Combine(dataDirectory!, "ModData", "SimpleVSManager"));
        }

        AddCandidateFile(fileCandidates, FirebaseAnonymousAuthenticator.GetStateFilePath());
        AddCandidateFile(fileCandidates, Path.Combine(AppContext.BaseDirectory, "SimpleVSManagerStatus.log"));
        AddCandidateFile(fileCandidates, Path.Combine(Environment.CurrentDirectory, "SimpleVSManagerStatus.log"));

        var deletedPaths = new List<string>();
        var failedPaths = new List<string>();

        foreach (string file in fileCandidates)
        {
            try
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                FileSystem.DeleteFile(file, FileUIOption.OnlyErrorDialogs, FileRecycleOption.SendToRecycleBin);
                deletedPaths.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or PathTooLongException or ArgumentException)
            {
                failedPaths.Add($"{file} ({ex.Message})");
            }
        }

        foreach (string directory in directoryCandidates.OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                FileSystem.DeleteDirectory(directory, FileUIOption.OnlyErrorDialogs, FileRecycleOption.SendToRecycleBin);
                deletedPaths.Add(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or PathTooLongException or ArgumentException)
            {
                failedPaths.Add($"{directory} ({ex.Message})");
            }
        }

        deletedPaths.Sort(StringComparer.OrdinalIgnoreCase);
        failedPaths.Sort(StringComparer.OrdinalIgnoreCase);

        return new ManagerDeletionResult(deletedPaths, failedPaths);
    }

    private static void AddCandidateDirectory(ISet<string> directories, string? path)
    {
        string? normalized = TryNormalizePath(path);
        if (normalized is null)
        {
            return;
        }

        directories.Add(normalized);
    }

    private static void AddCandidateFile(ISet<string> files, string? path)
    {
        string? normalized = TryNormalizePath(path);
        if (normalized is null)
        {
            return;
        }

        files.Add(normalized);
    }

    private static string? TryCombineSpecialFolder(Environment.SpecialFolder folder, string relativePath)
    {
        string? root = TryGetSpecialFolderPath(folder);
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(root!, relativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? TryGetSpecialFolderPath(Environment.SpecialFolder folder)
    {
        try
        {
            string path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Environment.GetFolderPath(folder);
            }

            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? TryNormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private async Task RefreshModsWithErrorHandlingAsync()
    {
        if (_viewModel?.RefreshCommand == null)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
        else
        {
            await Task.Yield();
        }

        try
        {
            await RefreshModsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(
                $"Failed to refresh mods:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static void ClearManagerCaches(bool preserveModCache)
    {
        var errors = new List<string>();

        try
        {
            ModDatabaseCacheService.ClearCacheDirectory();
        }
        catch (Exception ex)
        {
            errors.Add(BuildCacheClearErrorMessage("Mod database cache", ex));
        }

        try
        {
            ModManifestCacheService.ClearCache();
        }
        catch (Exception ex)
        {
            errors.Add(BuildCacheClearErrorMessage("Mod metadata cache", ex));
        }

        string? cachedModsDirectory = ModCacheLocator.GetCachedModsDirectory();
        if (!preserveModCache && !string.IsNullOrWhiteSpace(cachedModsDirectory))
        {
            try
            {
                ClearCachedModsDirectory(cachedModsDirectory);
            }
            catch (Exception ex)
            {
                errors.Add(BuildCacheClearErrorMessage($"Cached mods at {cachedModsDirectory}", ex));
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine + Environment.NewLine, errors));
        }
    }

    private static void ClearCachedModsDirectory(string cachedModsDirectory)
    {
        if (!Directory.Exists(cachedModsDirectory))
        {
            return;
        }

        Directory.Delete(cachedModsDirectory, recursive: true);
    }

    private static string BuildCacheClearErrorMessage(string context, Exception ex)
    {
        var builder = new StringBuilder();
        builder.Append(context);
        builder.Append(':');
        builder.AppendLine();
        builder.Append(ex.Message);

        Exception? inner = ex.InnerException;
        while (inner is not null)
        {
            builder.AppendLine();
            builder.Append(inner.Message);
            inner = inner.InnerException;
        }

        return builder.ToString();
    }

    private async void SelectDataFolderMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        string? selected = PromptForDirectory(
            "Select your VintagestoryData folder",
            _dataDirectory,
            TryValidateDataDirectory,
            allowCancel: true);

        if (selected is null)
        {
            return;
        }

        if (string.Equals(selected, _dataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _dataDirectory = selected;
        _userConfiguration.SetDataDirectory(selected);
        DeveloperProfileManager.UpdateOriginalProfile(selected);
        RefreshDeveloperProfilesMenuEntries();
        await ReloadViewModelAsync();
    }

    private void SelectGameFolderMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        string? selected = PromptForDirectory(
            "Select your Vintage Story installation folder",
            _gameDirectory,
            TryValidateGameDirectory,
            allowCancel: true);

        if (selected is null)
        {
            return;
        }

        if (string.Equals(selected, _gameDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _gameDirectory = selected;
        _userConfiguration.SetGameDirectory(selected);
        UpdateGameVersionMenuItem(VintageStoryVersionLocator.GetInstalledVersion(_gameDirectory));
    }

    private void RefreshDeveloperProfilesMenuEntries()
    {
        if (DeveloperProfilesMenuItem is null)
        {
            return;
        }

        foreach (MenuItem item in _developerProfileMenuItems)
        {
            item.Click -= DeveloperProfileMenuItem_OnClick;
        }

        _developerProfileMenuItems.Clear();
        DeveloperProfilesMenuItem.Items.Clear();

        if (!DeveloperProfileManager.DevDebug)
        {
            DeveloperProfilesMenuItem.Visibility = Visibility.Collapsed;
            return;
        }

        IReadOnlyList<DeveloperProfile> profiles = DeveloperProfileManager.GetProfiles();
        if (profiles.Count == 0)
        {
            DeveloperProfilesMenuItem.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (DeveloperProfile profile in profiles)
        {
            var menuItem = new MenuItem
            {
                Header = profile.DisplayName,
                Tag = profile,
                IsCheckable = true
            };

            menuItem.Click += DeveloperProfileMenuItem_OnClick;
            DeveloperProfilesMenuItem.Items.Add(menuItem);
            _developerProfileMenuItems.Add(menuItem);
        }

        DeveloperProfilesMenuItem.Visibility = Visibility.Visible;
        UpdateDeveloperProfileMenuChecks();
    }

    private void UpdateDeveloperProfileMenuChecks()
    {
        if (!DeveloperProfileManager.DevDebug)
        {
            return;
        }

        DeveloperProfile? current = DeveloperProfileManager.CurrentProfile;

        foreach (MenuItem menuItem in _developerProfileMenuItems)
        {
            if (menuItem.Tag is not DeveloperProfile profile)
            {
                menuItem.IsChecked = false;
                continue;
            }

            bool isSelected = current is not null
                && string.Equals(profile.Id, current.Id, StringComparison.OrdinalIgnoreCase);
            menuItem.IsChecked = isSelected;
        }
    }

    private async void DeveloperProfileMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: DeveloperProfile profile })
        {
            return;
        }

        bool changed = DeveloperProfileManager.TrySetCurrentProfile(profile.Id);
        if (!changed)
        {
            UpdateDeveloperProfileMenuChecks();
            return;
        }

        string profileDirectory = profile.DataDirectory;

        if (string.Equals(_dataDirectory, profileDirectory, StringComparison.OrdinalIgnoreCase))
        {
            UpdateDeveloperProfileMenuChecks();
            return;
        }

        _dataDirectory = profileDirectory;

        if (profile.IsOriginal)
        {
            _userConfiguration.SetDataDirectory(profileDirectory);
            DeveloperProfileManager.UpdateOriginalProfile(profileDirectory);
        }

        _cloudModlistStore = null;
        await ReloadViewModelAsync();
        UpdateDeveloperProfileMenuChecks();
    }

    private void DeveloperProfileManager_OnCurrentProfileChanged(object? sender, DeveloperProfileChangedEventArgs e)
    {
        if (!DeveloperProfileManager.DevDebug)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => DeveloperProfileManager_OnCurrentProfileChanged(sender, e));
            return;
        }

        if (e.ProfilesUpdated)
        {
            RefreshDeveloperProfilesMenuEntries();
        }
        else
        {
            UpdateDeveloperProfileMenuChecks();
        }
    }

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void LaunchGameButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isApplyingPreset)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_customShortcutPath))
        {
            if (!File.Exists(_customShortcutPath))
            {
                WpfMessageBox.Show(
                    "The custom Vintage Story shortcut could not be found. Please set it again from File > Set custom Vintage Story shortcut.",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                _userConfiguration.ClearCustomShortcutPath();
                _customShortcutPath = null;
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _customShortcutPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"Failed to launch Vintage Story using the shortcut:\n{ex.Message}",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(_dataDirectory) || !Directory.Exists(_dataDirectory))
        {
            WpfMessageBox.Show(
                "The VintagestoryData folder could not be located. Please verify it from File > Set Data Folder before launching the game.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string? executable = GameDirectoryLocator.FindExecutable(_gameDirectory);
        if (executable is null)
        {
            WpfMessageBox.Show("The Vintage Story executable could not be found. Verify the game folder in File > Set Game Folder.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("--dataPath");
            startInfo.ArgumentList.Add(_dataDirectory);

            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Failed to launch Vintage Story:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SetCustomShortcutMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        string? initialDirectory = null;
        string? initialFileName = null;

        if (!string.IsNullOrWhiteSpace(_customShortcutPath))
        {
            try
            {
                initialDirectory = Path.GetDirectoryName(_customShortcutPath);
                initialFileName = Path.GetFileName(_customShortcutPath);
            }
            catch (Exception)
            {
                initialDirectory = null;
                initialFileName = null;
            }
        }

        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "Select Vintage Story shortcut",
            Filter = "Shortcut files (*.lnk)|*.lnk|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        if (!string.IsNullOrWhiteSpace(initialFileName))
        {
            dialog.FileName = initialFileName;
        }

        WinForms.DialogResult result = dialog.ShowDialog();
        if (result == WinForms.DialogResult.OK)
        {
            string selected = dialog.FileName;
            if (!File.Exists(selected))
            {
                WpfMessageBox.Show(
                    "The selected shortcut could not be found.",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                _userConfiguration.SetCustomShortcutPath(selected);
                _customShortcutPath = _userConfiguration.CustomShortcutPath;
            }
            catch (ArgumentException ex)
            {
                WpfMessageBox.Show(
                    $"The selected shortcut is not valid:\n{ex.Message}",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(_customShortcutPath))
        {
            return;
        }

        MessageBoxResult clear = WpfMessageBox.Show(
            "Do you want to clear the custom Vintage Story shortcut?",
            "Simple VS Manager",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (clear != MessageBoxResult.Yes)
        {
            return;
        }

        _userConfiguration.ClearCustomShortcutPath();
        _customShortcutPath = null;
    }

    private void RestoreBackupMenuItem_OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
        {
            return;
        }

        menuItem.Items.Clear();

        string directory;
        try
        {
            directory = EnsureBackupDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Failed to access backup directory: {0}", ex.Message);
            menuItem.Items.Add(new MenuItem
            {
                Header = "Backups unavailable",
                IsEnabled = false
            });
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Failed to enumerate backups: {0}", ex.Message);
            menuItem.Items.Add(new MenuItem
            {
                Header = "Backups unavailable",
                IsEnabled = false
            });
            return;
        }

        if (files.Length == 0)
        {
            menuItem.Items.Add(new MenuItem
            {
                Header = "No backups available",
                IsEnabled = false
            });
            return;
        }

        Array.Sort(files, (left, right) =>
            File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left)));

        bool appStartedAdded = false;

        foreach (string file in files)
        {
            bool isAppStarted = IsAppStartedBackup(file);
            if (isAppStarted)
            {
                if (appStartedAdded)
                {
                    continue;
                }

                appStartedAdded = true;
            }

            string displayName = Path.GetFileNameWithoutExtension(file);
            var item = new MenuItem
            {
                Header = displayName,
                Tag = file
            };
            item.Click += RestoreBackupMenuItem_OnBackupClick;
            menuItem.Items.Add(item);
        }
    }

    private async void RestoreBackupMenuItem_OnBackupClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not string filePath)
        {
            return;
        }

        var confirmationDialog = new RestoreBackupDialog
        {
            Owner = this
        };

        bool? confirmation = confirmationDialog.ShowDialog();
        if (confirmation != true)
        {
            return;
        }

        await RestoreBackupAsync(filePath, confirmationDialog.RestoreConfigurations).ConfigureAwait(true);
    }

    private async Task RestoreBackupAsync(string backupPath, bool restoreConfigurations)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (!File.Exists(backupPath))
        {
            WpfMessageBox.Show(
                "The selected backup could not be found.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!TryLoadPresetFromFile(backupPath,
                "Backup",
                ModListLoadOptions,
                out ModPreset? preset,
                out string? errorMessage))
        {
            string message = string.IsNullOrWhiteSpace(errorMessage)
                ? "The selected backup is not valid."
                : errorMessage!;
            WpfMessageBox.Show(
                $"Failed to restore the backup:\n{message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        ModPreset loadedPreset = preset!;
        await ApplyPresetAsync(loadedPreset, restoreConfigurations).ConfigureAwait(true);
        _viewModel.ReportStatus($"Restored backup \"{loadedPreset.Name}\".");
    }

    private void OpenModFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenFolder(_dataDirectory is null ? null : Path.Combine(_dataDirectory, "Mods"), "mods");
    }

    private void OpenConfigFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenFolder(_dataDirectory is null ? null : Path.Combine(_dataDirectory, "ModConfig"), "config");
    }

    private void OpenLogsFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenFolder(_dataDirectory is null ? null : Path.Combine(_dataDirectory, "Logs"), "logs");
    }

    private static void OpenFolder(string? path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            WpfMessageBox.Show($"The {description} folder is not available. Please verify the VintagestoryData folder from File > Set Data Folder.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!Directory.Exists(path))
        {
            WpfMessageBox.Show($"The {description} folder could not be found at:\n{path}\nPlease verify the VintagestoryData folder from File > Set Data Folder.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            OpenFolderWithShell(path);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Failed to open the {description} folder:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static void OpenFolderWithShell(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var explorerStartInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            };
            explorerStartInfo.ArgumentList.Add(path);
            Process.Start(explorerStartInfo);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private bool TryGetManagedModPath(ModListItemViewModel mod, out string fullPath, out string? errorMessage)
    {
        fullPath = string.Empty;
        errorMessage = null;

        if (_dataDirectory is null)
        {
            errorMessage = "The VintagestoryData folder is not available. Please verify it from File > Set Data Folder.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(mod.SourcePath))
        {
            errorMessage = "This mod does not have a known source path and cannot be deleted automatically.";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(mod.SourcePath);
        }
        catch (Exception)
        {
            errorMessage = "The mod path is invalid and cannot be deleted automatically.";
            return false;
        }

        if (!IsPathWithinManagedMods(fullPath))
        {
            errorMessage = $"This mod is located outside of the Mods folder and cannot be deleted automatically.{Environment.NewLine}{Environment.NewLine}Location:{Environment.NewLine}{fullPath}";
            return false;
        }

        if (!TryEnsureManagedModTargetIsSafe(fullPath, out errorMessage))
        {
            return false;
        }

        return true;
    }

    private bool TryGetDependencyInstallTargetPath(string modId, ModReleaseInfo release, out string fullPath, out string? errorMessage)
    {
        fullPath = string.Empty;
        errorMessage = null;

        if (_dataDirectory is null)
        {
            errorMessage = "The VintagestoryData folder is not available. Please verify it from File > Set Data Folder.";
            return false;
        }

        string modsDirectory = Path.Combine(_dataDirectory, "Mods");

        try
        {
            Directory.CreateDirectory(modsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            errorMessage = $"The Mods folder could not be accessed:{Environment.NewLine}{ex.Message}";
            return false;
        }

        string defaultName = string.IsNullOrWhiteSpace(modId) ? "mod" : modId;
        string versionPart = string.IsNullOrWhiteSpace(release.Version) ? "latest" : release.Version!;
        string fallbackFileName = $"{defaultName}-{versionPart}.zip";

        string? releaseFileName = release.FileName;
        if (!string.IsNullOrWhiteSpace(releaseFileName))
        {
            releaseFileName = Path.GetFileName(releaseFileName);
        }

        string sanitizedFileName = SanitizeFileName(releaseFileName, fallbackFileName);
        if (string.IsNullOrWhiteSpace(Path.GetExtension(sanitizedFileName)))
        {
            sanitizedFileName += ".zip";
        }

        string candidatePath = Path.Combine(modsDirectory, sanitizedFileName);
        fullPath = EnsureUniqueFilePath(candidatePath);
        return true;
    }

    private bool TryGetInstallTargetPath(ModListItemViewModel mod, ModReleaseInfo release, out string fullPath, out string? errorMessage)
    {
        fullPath = string.Empty;
        errorMessage = null;

        if (_dataDirectory is null)
        {
            errorMessage = "The VintagestoryData folder is not available. Please verify it from File > Set Data Folder.";
            return false;
        }

        string modsDirectory = Path.Combine(_dataDirectory, "Mods");

        try
        {
            Directory.CreateDirectory(modsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            errorMessage = $"The Mods folder could not be accessed:{Environment.NewLine}{ex.Message}";
            return false;
        }

        string defaultName = string.IsNullOrWhiteSpace(mod.ModId) ? "mod" : mod.ModId;
        string versionPart = string.IsNullOrWhiteSpace(release.Version) ? "latest" : release.Version!;
        string fallbackFileName = $"{defaultName}-{versionPart}.zip";

        string? releaseFileName = release.FileName;
        if (!string.IsNullOrWhiteSpace(releaseFileName))
        {
            releaseFileName = Path.GetFileName(releaseFileName);
        }

        string sanitizedFileName = SanitizeFileName(releaseFileName, fallbackFileName);
        if (string.IsNullOrWhiteSpace(Path.GetExtension(sanitizedFileName)))
        {
            sanitizedFileName += ".zip";
        }

        string candidatePath = Path.Combine(modsDirectory, sanitizedFileName);
        fullPath = EnsureUniqueFilePath(candidatePath);
        return true;
    }

    private bool TryGetUpdateTargetPath(
        ModListItemViewModel mod,
        ModReleaseInfo release,
        string existingPath,
        out string fullPath,
        out string? errorMessage)
    {
        fullPath = string.Empty;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(existingPath))
        {
            errorMessage = "The mod path could not be determined.";
            return false;
        }

        string? directory;
        try
        {
            directory = Path.GetDirectoryName(existingPath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            errorMessage = $"The mod path is invalid:{Environment.NewLine}{ex.Message}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            errorMessage = "The mod path could not be determined.";
            return false;
        }

        string defaultName = string.IsNullOrWhiteSpace(mod.ModId) ? "mod" : mod.ModId;
        string versionPart = string.IsNullOrWhiteSpace(release.Version) ? "latest" : release.Version!;
        string fallbackFileName = $"{defaultName}-{versionPart}.zip";

        string? releaseFileName = release.FileName;
        if (!string.IsNullOrWhiteSpace(releaseFileName))
        {
            releaseFileName = Path.GetFileName(releaseFileName);
        }

        string sanitizedFileName = SanitizeFileName(releaseFileName, fallbackFileName);
        if (string.IsNullOrWhiteSpace(Path.GetExtension(sanitizedFileName)))
        {
            sanitizedFileName += ".zip";
        }

        fullPath = Path.Combine(directory, sanitizedFileName);
        return true;
    }

    private bool IsPathWithinManagedMods(string fullPath)
    {
        if (_dataDirectory is null)
        {
            return false;
        }

        string modsDirectory = Path.Combine(_dataDirectory, "Mods");
        string modsByServerDirectory = Path.Combine(_dataDirectory, "ModsByServer");
        return IsPathUnderDirectory(fullPath, modsDirectory) || IsPathUnderDirectory(fullPath, modsByServerDirectory);
    }

    private static bool IsPathUnderDirectory(string path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            string normalizedPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (normalizedPath.Length < normalizedDirectory.Length)
            {
                return false;
            }

            if (!normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (normalizedPath.Length == normalizedDirectory.Length)
            {
                return true;
            }

            char separator = normalizedPath[normalizedDirectory.Length];
            return separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string SanitizeFileName(string? fileName, string fallback)
    {
        string name = string.IsNullOrWhiteSpace(fileName) ? fallback : fileName;
        char[] invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (char c in name)
        {
            builder.Append(Array.IndexOf(invalidChars, c) >= 0 ? '_' : c);
        }

        string sanitized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private static string EnsureUniqueFilePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string? directory = Path.GetDirectoryName(path);
        string fileName = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Directory.GetCurrentDirectory();
        }

        int counter = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(directory, $"{fileName} ({counter}){extension}");
            counter++;
        }
        while (File.Exists(candidate));

        return candidate;
    }

    private bool TryEnsureManagedModTargetIsSafe(string fullPath, out string? errorMessage)
    {
        errorMessage = null;

        FileSystemInfo? info = null;

        if (Directory.Exists(fullPath))
        {
            info = new DirectoryInfo(fullPath);
        }
        else if (File.Exists(fullPath))
        {
            info = new FileInfo(fullPath);
        }

        if (info is null)
        {
            return true;
        }

        if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return true;
        }

        try
        {
            FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);

            if (target is null)
            {
                errorMessage = $"This mod is a symbolic link and its target could not be resolved. It will not be deleted automatically.{Environment.NewLine}{Environment.NewLine}Location:{Environment.NewLine}{fullPath}";
                return false;
            }

            string resolvedFullPath = Path.GetFullPath(target.FullName);

            if (!IsPathWithinManagedMods(resolvedFullPath))
            {
                errorMessage = $"This mod is a symbolic link that points outside of the Mods folder and cannot be deleted automatically.{Environment.NewLine}{Environment.NewLine}Link target:{Environment.NewLine}{resolvedFullPath}";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PlatformNotSupportedException)
        {
            errorMessage = $"This mod is a symbolic link that could not be validated for automatic deletion.{Environment.NewLine}{Environment.NewLine}Location:{Environment.NewLine}{fullPath}{Environment.NewLine}{Environment.NewLine}Reason:{Environment.NewLine}{ex.Message}";
            return false;
        }
    }

    private bool TrySaveSnapshot(
        string directory,
        string title,
        string filter,
        string folderWarningMessage,
        string fallbackName,
        Func<string?>? suggestedNameProvider,
        Action<string>? onSuccess,
        string failureContext,
        bool includeModVersions,
        bool exclusive)
    {
        if (_viewModel is null)
        {
            return false;
        }

        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = directory
        };

        dialog.FileOk += (_, args) =>
        {
            if (IsPathWithinDirectory(directory, dialog.FileName))
            {
                return;
            }

            WpfMessageBox.Show(folderWarningMessage,
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Cancel = true;
        };

        string? suggestedName = suggestedNameProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(suggestedName))
        {
            dialog.FileName = BuildSuggestedFileName(suggestedName, fallbackName);
        }

        bool? result = dialog.ShowDialog(this);
        if (result != true)
        {
            return false;
        }

        string filePath = dialog.FileName;
        string entryName = BuildSuggestedFileName(Path.GetFileNameWithoutExtension(filePath), fallbackName);
        if (!string.Equals(entryName, Path.GetFileNameWithoutExtension(filePath), StringComparison.Ordinal))
        {
            filePath = Path.Combine(directory, entryName + ".json");
        }

        var serializable = BuildSerializablePreset(entryName, includeModVersions, exclusive);

        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            string json = JsonSerializer.Serialize(serializable, options);
            File.WriteAllText(filePath, json);

            onSuccess?.Invoke(entryName);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WpfMessageBox.Show($"Failed to save the {failureContext}:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private SerializablePreset BuildSerializablePreset(
        string entryName,
        bool includeModVersions,
        bool exclusive,
        IReadOnlyDictionary<string, ModConfigurationSnapshot>? includedConfigurations = null)
    {
        if (_viewModel is null)
        {
            throw new InvalidOperationException("View model is not initialized.");
        }

        IReadOnlyList<ModPresetModState> states = _viewModel.GetCurrentModStates();

        var mods = new List<SerializablePresetModState>(states.Count);
        foreach (ModPresetModState state in states)
        {
            if (state is null)
            {
                continue;
            }

            string? trimmedId = string.IsNullOrWhiteSpace(state.ModId) ? state.ModId : state.ModId.Trim();
            var serializableState = new SerializablePresetModState
            {
                ModId = trimmedId,
                Version = includeModVersions && !string.IsNullOrWhiteSpace(state.Version)
                    ? state.Version!.Trim()
                    : null,
                IsActive = state.IsActive
            };

            if (!string.IsNullOrWhiteSpace(trimmedId))
            {
                string normalizedId = trimmedId!;

                if (includedConfigurations != null
                    && includedConfigurations.TryGetValue(normalizedId, out ModConfigurationSnapshot? snapshot)
                    && snapshot is not null)
                {
                    serializableState.ConfigurationFileName = snapshot.FileName;
                    serializableState.ConfigurationContent = snapshot.Content;
                }
                else if (!string.IsNullOrWhiteSpace(state.ConfigurationContent))
                {
                    serializableState.ConfigurationFileName = GetSafeConfigFileName(state.ConfigurationFileName, normalizedId);
                    serializableState.ConfigurationContent = state.ConfigurationContent;
                }
            }

            mods.Add(serializableState);
        }

        return new SerializablePreset
        {
            Name = entryName,
            IncludeModStatus = true,
            IncludeModVersions = includeModVersions ? true : null,
            Exclusive = exclusive ? true : null,
            Mods = mods
        };
    }

    private async Task<(bool Success, int RemovedCount)> DeleteAllInstalledModsForRebuildAsync()
    {
        if (_viewModel?.ModsView is null)
        {
            return (true, 0);
        }

        var installedMods = _viewModel.ModsView.Cast<ModListItemViewModel>()
            .Where(mod => mod.IsInstalled)
            .ToList();

        if (installedMods.Count == 0)
        {
            return (true, 0);
        }

        var pathErrors = new List<string>();
        int removedCount = 0;
        bool hadDeletionFailure = false;

        foreach (var mod in installedMods)
        {
            if (!TryGetManagedModPath(mod, out string modPath, out string? errorMessage))
            {
                if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    pathErrors.Add($"{mod.DisplayName}: {errorMessage}");
                }

                hadDeletionFailure = true;
                continue;
            }

            if (TryDeleteModAtPath(mod, modPath))
            {
                removedCount++;
            }
            else
            {
                hadDeletionFailure = true;
            }
        }

        if (_viewModel.RefreshCommand != null)
        {
            try
            {
                await RefreshModsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"The mod list could not be refreshed:{Environment.NewLine}{ex.Message}",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return (false, removedCount);
            }
        }

        if (pathErrors.Count > 0)
        {
            string message = string.Join(Environment.NewLine + Environment.NewLine, pathErrors);
            WpfMessageBox.Show($"Some mods could not be removed automatically:{Environment.NewLine}{Environment.NewLine}{message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        bool success = !hadDeletionFailure && pathErrors.Count == 0;
        if (success)
        {
            string status = removedCount == 0
                ? "No installed mods were found to delete."
                : $"Removed {removedCount} mod{(removedCount == 1 ? string.Empty : "s")} before rebuild.";
            _viewModel.ReportStatus(status);
        }

        return (success, removedCount);
    }

    private void SavePresetMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        string presetDirectory = EnsurePresetDirectory();
        TrySaveSnapshot(
            presetDirectory,
            "Save Mod Preset",
            "Preset files (*.json)|*.json|All files (*.*)|*.*",
            "Presets must be saved inside the Presets folder.",
            "Preset",
            () => _userConfiguration.GetLastSelectedPresetName(),
            name =>
            {
                _userConfiguration.SetLastSelectedPresetName(name);
                _viewModel?.ReportStatus($"Saved preset \"{name}\".");
            },
            "preset",
            includeModVersions: false,
            exclusive: false);
    }

    private bool TrySaveModlist()
    {
        string modListDirectory = EnsureModListDirectory();
        return TrySaveSnapshot(
            modListDirectory,
            "Save Modlist",
            "Modlist files (*.json)|*.json|All files (*.*)|*.*",
            "Modlists must be saved inside the Modlists folder.",
            "Modlist",
            suggestedNameProvider: null,
            onSuccess: name => _viewModel?.ReportStatus($"Saved modlist \"{name}\"."),
            failureContext: "modlist",
            includeModVersions: true,
            exclusive: true);
    }

    private bool TrySaveAutomaticModlist(string requestedName, out string savedName, out string filePath)
    {
        savedName = string.Empty;
        filePath = string.Empty;

        if (_viewModel is null)
        {
            return false;
        }

        string modListDirectory = EnsureModListDirectory();
        savedName = BuildSuggestedFileName(requestedName, "Modlist");
        filePath = Path.Combine(modListDirectory, savedName + ".json");

        var serializable = BuildSerializablePreset(savedName, includeModVersions: true, exclusive: true);

        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            string json = JsonSerializer.Serialize(serializable, options);
            File.WriteAllText(filePath, json);

            _viewModel.ReportStatus($"Saved modlist \"{savedName}\".");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WpfMessageBox.Show($"Failed to save the modlist:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            savedName = string.Empty;
            filePath = string.Empty;
            return false;
        }
    }

    private ModlistLoadMode? GetRebuildModlistLoadMode()
    {
        MessageBoxResult confirmation = WpfMessageBox.Show(
            this,
            "This will remove all cache and reinstall all current mods, proceed?",
            "Rebuild Mods",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return confirmation == MessageBoxResult.Yes ? ModlistLoadMode.Replace : null;
    }

    private async Task<bool> EnsureModDatabaseReachableForRebuildAsync()
    {
        if (_viewModel is null)
        {
            return false;
        }

        IReadOnlyList<ModPresetModState> states = _viewModel.GetCurrentModStates();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ModPresetModState>(5);

        foreach (ModPresetModState state in states)
        {
            if (state is null || string.IsNullOrWhiteSpace(state.ModId))
            {
                continue;
            }

            string trimmedId = state.ModId.Trim();
            if (!seenIds.Add(trimmedId))
            {
                continue;
            }

            candidates.Add(state);
            if (candidates.Count >= 5)
            {
                break;
            }
        }

        if (candidates.Count == 0)
        {
            try
            {
                await _modDatabaseService.GetMostDownloadedModsAsync(1).ConfigureAwait(true);
                return true;
            }
            catch (InternetAccessDisabledException ex)
            {
                WpfMessageBox.Show(
                    ex.Message,
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
            catch (HttpRequestException)
            {
                WpfMessageBox.Show(
                    ModDatabaseUnavailableMessage,
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
            catch (TaskCanceledException)
            {
                WpfMessageBox.Show(
                    ModDatabaseUnavailableMessage,
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(
                    $"{ModDatabaseUnavailableMessage}.{Environment.NewLine}{ex.Message}",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        string? installedGameVersion = _viewModel.InstalledGameVersion;
        foreach (ModPresetModState candidate in candidates)
        {
            try
            {
                if (await TryTestRebuildModConnectivityAsync(candidate, installedGameVersion).ConfigureAwait(true))
                {
                    return true;
                }
            }
            catch (InternetAccessDisabledException ex)
            {
                WpfMessageBox.Show(
                    ex.Message,
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
        }

        WpfMessageBox.Show(
            "Aborted because couldn't reach mod DB, check connection.",
            "Simple VS Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        return false;
    }

    private async Task<bool> TryTestRebuildModConnectivityAsync(ModPresetModState state, string? installedGameVersion)
    {
        string modId = state.ModId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(modId))
        {
            return false;
        }

        string? desiredVersion = string.IsNullOrWhiteSpace(state.Version) ? null : state.Version!.Trim();

        ModDatabaseInfo? info = await _modDatabaseService
            .TryLoadDatabaseInfoAsync(modId, desiredVersion, installedGameVersion, _userConfiguration.RequireExactVsVersionMatch)
            .ConfigureAwait(true);

        if (info is null)
        {
            Trace.TraceWarning("Connectivity test failed to load metadata for mod {0}.", modId);
            return false;
        }

        ModReleaseInfo? release = TrySelectReleaseForConnectivityTest(info, desiredVersion);
        if (release?.DownloadUri is null)
        {
            Trace.TraceWarning("Connectivity test could not find a downloadable release for mod {0}.", modId);
            return false;
        }

        bool success = await TryProbeModDownloadAsync(release.DownloadUri).ConfigureAwait(true);
        if (!success)
        {
            Trace.TraceWarning("Connectivity test failed to reach download URI for mod {0} (version {1}).", modId, release.Version);
        }

        return success;
    }

    private static ModReleaseInfo? TrySelectReleaseForConnectivityTest(ModDatabaseInfo info, string? desiredVersion)
    {
        if (!string.IsNullOrWhiteSpace(desiredVersion))
        {
            string trimmedVersion = desiredVersion.Trim();
            string? normalizedDesired = VersionStringUtility.Normalize(desiredVersion);

            foreach (ModReleaseInfo release in info.Releases ?? Array.Empty<ModReleaseInfo>())
            {
                if (!string.IsNullOrWhiteSpace(release.Version)
                    && string.Equals(release.Version.Trim(), trimmedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return release;
                }

                if (normalizedDesired is not null
                    && !string.IsNullOrWhiteSpace(release.NormalizedVersion)
                    && string.Equals(release.NormalizedVersion, normalizedDesired, StringComparison.OrdinalIgnoreCase))
                {
                    return release;
                }
            }
        }

        IReadOnlyList<ModReleaseInfo> releases = info.Releases ?? Array.Empty<ModReleaseInfo>();

        return info.LatestCompatibleRelease
            ?? info.LatestRelease
            ?? (releases.Count > 0 ? releases[0] : null);
    }

    private static async Task<bool> TryProbeModDownloadAsync(Uri downloadUri)
    {
        try
        {
            InternetAccessManager.ThrowIfInternetAccessDisabled();

            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            using HttpResponseMessage response = await ConnectivityTestHttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(true);

            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException ex)
        {
            Trace.TraceWarning("Connectivity test download probe failed for {0}: {1}", downloadUri, ex.Message);
            return false;
        }
        catch (TaskCanceledException ex)
        {
            Trace.TraceWarning("Connectivity test download probe timed out for {0}: {1}", downloadUri, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Connectivity test download probe encountered an unexpected error for {0}: {1}", downloadUri, ex.Message);
            return false;
        }
    }

    private ModlistLoadMode? PromptModlistLoadMode()
    {
        ModlistAutoLoadBehavior behavior = _userConfiguration.ModlistAutoLoadBehavior;
        switch (behavior)
        {
            case ModlistAutoLoadBehavior.Replace:
                return ModlistLoadMode.Replace;
            case ModlistAutoLoadBehavior.Add:
                return ModlistLoadMode.Add;
        }

        var buttonOverrides = new MessageDialogButtonContentOverrides
        {
            Yes = "Only Modlist mods",
            No = "Add Modlist mods"
        };

        MessageBoxResult result = WpfMessageBox.Show(
            this,
            "How would you like to load the modlist?" +
            "\n\nOnly Modlist mods: Delete your current mods and install only the mods from the modlist." +
            "\nAdd Modlist mods: Keep your current mods and add any missing mods from the modlist." +
            "\nCancel: Do nothing.",
            "Load Modlist",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            buttonContentOverrides: buttonOverrides);

        return result switch
        {
            MessageBoxResult.Yes => ModlistLoadMode.Replace,
            MessageBoxResult.No => ModlistLoadMode.Add,
            _ => null
        };
    }

    private PresetLoadOptions GetModlistLoadOptions(ModlistLoadMode mode)
    {
        if (mode == ModlistLoadMode.Replace)
        {
            return ModListLoadOptions;
        }

        return new PresetLoadOptions(ModListLoadOptions.ApplyModStatus, ModListLoadOptions.ApplyModVersions, false);
    }

    private bool EnsureModlistBackupBeforeLoad()
    {
        MessageBoxResult prompt;
        if (_userConfiguration.SuppressModlistSavePrompt)
        {
            prompt = MessageBoxResult.No;
        }
        else
        {
            var suppressButton = new MessageDialogExtraButton(
                "No, don't ask again",
                MessageBoxResult.No,
                onClick: () => _userConfiguration.SetSuppressModlistSavePrompt(true));

            prompt = WpfMessageBox.Show(
                "Would you like to backup your current mods as a Modlist before loading the selected Modlist? Your current mods will be deleted! ",
                "Simple VS Manager",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                suppressButton);
        }

        if (prompt == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (prompt == MessageBoxResult.Yes)
        {
            return TrySaveModlist();
        }

        return true;
    }

    private bool TryBuildCurrentModlistJson(
        string modlistName,
        string? description,
        string? version,
        string uploader,
        IReadOnlyDictionary<string, ModConfigurationSnapshot>? includedConfigurations,
        out string json)
    {
        json = string.Empty;

        string? trimmedName = string.IsNullOrWhiteSpace(modlistName) ? null : modlistName.Trim();
        if (string.IsNullOrEmpty(trimmedName) || _viewModel is null)
        {
            return false;
        }

        var serializable = BuildSerializablePreset(trimmedName, includeModVersions: true, exclusive: true, includedConfigurations);
        serializable.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        serializable.Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        serializable.Uploader = string.IsNullOrWhiteSpace(uploader) ? null : uploader.Trim();

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        json = JsonSerializer.Serialize(serializable, options);
        return true;
    }

    private Task CreateAutomaticBackupAsync(string trigger)
    {
        return CreateBackupAsync(
            trigger,
            fallbackFileName: "Backup",
            pruneAutomaticBackups: true,
            pruneAppStartedBackups: false);
    }

    private Task CreateAppStartedBackupAsync()
    {
        return CreateBackupAsync(
            "AppStarted",
            fallbackFileName: "Backup_AppStarted",
            pruneAutomaticBackups: false,
            pruneAppStartedBackups: true);
    }

    private async Task CreateBackupAsync(
        string trigger,
        string fallbackFileName,
        bool pruneAutomaticBackups,
        bool pruneAppStartedBackups)
    {
        if (_viewModel is null)
        {
            return;
        }

        await _backupSemaphore.WaitAsync().ConfigureAwait(true);
        try
        {
            IReadOnlyList<ModListItemViewModel> mods = _viewModel.GetInstalledModsSnapshot();
            int modCount = mods.Count;

            DateTime timestamp = DateTime.Now;
            string formattedTimestamp = timestamp.ToString("dd MMM yyyy '•' HH.mm '•' ss's'", CultureInfo.InvariantCulture);

            string normalizedTrigger = string.IsNullOrWhiteSpace(trigger)
                ? "Automatic"
                : trigger.Trim();
            string modLabel = modCount == 1 ? "1 mod" : $"{modCount} mods";
            string displayName = $"{formattedTimestamp} -- {normalizedTrigger} ({modLabel})";

            string directory;
            try
            {
                directory = EnsureBackupDirectory();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Failed to prepare backup directory: {0}", ex.Message);
                return;
            }

            string fileName = SanitizeFileName(displayName, fallbackFileName);
            string filePath = Path.Combine(directory, $"{fileName}.json");

            IReadOnlyDictionary<string, ModConfigurationSnapshot>? includedConfigurations =
                CaptureConfigurationsForBackup(mods);

            SerializablePreset serializable = BuildSerializablePreset(
                displayName,
                includeModVersions: true,
                exclusive: true,
                includedConfigurations);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            string json = JsonSerializer.Serialize(serializable, options);

            try
            {
                await File.WriteAllTextAsync(filePath, json).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Failed to write backup {0}: {1}", filePath, ex.Message);
                return;
            }

            if (pruneAutomaticBackups)
            {
                PruneAutomaticBackups(directory);
            }

            if (pruneAppStartedBackups)
            {
                PruneAppStartedBackups(directory);
            }
        }
        finally
        {
            _backupSemaphore.Release();
        }
    }

    private IReadOnlyDictionary<string, ModConfigurationSnapshot>? CaptureConfigurationsForBackup(
        IReadOnlyList<ModListItemViewModel> mods)
    {
        if (mods is null || mods.Count == 0)
        {
            return null;
        }

        var includedConfigurations = new Dictionary<string, ModConfigurationSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (ModListItemViewModel mod in mods)
        {
            if (mod is null || string.IsNullOrWhiteSpace(mod.ModId))
            {
                continue;
            }

            string normalizedId = mod.ModId.Trim();
            if (includedConfigurations.ContainsKey(normalizedId))
            {
                continue;
            }

            if (!_userConfiguration.TryGetModConfigPath(normalizedId, out string? path)
                || string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string normalizedPath = path.Trim();
            if (!File.Exists(normalizedPath))
            {
                continue;
            }

            try
            {
                string content = File.ReadAllText(normalizedPath);
                string fileName = GetSafeConfigFileName(Path.GetFileName(normalizedPath), normalizedId);
                includedConfigurations[normalizedId] = new ModConfigurationSnapshot(fileName, content);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                Trace.TraceWarning(
                    "Failed to include configuration file {0} for mod {1} in backup: {2}",
                    normalizedPath,
                    normalizedId,
                    ex.Message);
            }
        }

        return includedConfigurations.Count > 0 ? includedConfigurations : null;
    }

    private static bool IsAppStartedBackup(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith("_AppStarted", StringComparison.OrdinalIgnoreCase)
            || name.Contains("-- AppStarted", StringComparison.OrdinalIgnoreCase);
    }

    private static void PruneAutomaticBackups(string directory)
    {
        try
        {
            string[] files = Directory.GetFiles(directory, "*.json");
            string[] regularBackups = files
                .Where(file => !IsAppStartedBackup(file))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();

            if (regularBackups.Length <= 10)
            {
                return;
            }

            for (int index = 10; index < regularBackups.Length; index++)
            {
                string candidate = regularBackups[index];
                try
                {
                    File.Delete(candidate);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning("Failed to delete backup {0}: {1}", candidate, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Failed to prune backups in {0}: {1}", directory, ex.Message);
        }
    }

    private static void PruneAppStartedBackups(string directory)
    {
        try
        {
            string[] files = Directory.GetFiles(directory, "*.json");
            string[] appStartedBackups = files
                .Where(IsAppStartedBackup)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();

            if (appStartedBackups.Length <= 10)
            {
                return;
            }

            for (int index = 10; index < appStartedBackups.Length; index++)
            {
                string candidate = appStartedBackups[index];
                try
                {
                    File.Delete(candidate);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning("Failed to delete backup {0}: {1}", candidate, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Failed to prune backups in {0}: {1}", directory, ex.Message);
        }
    }

    private static string BuildCloudModlistName()
    {
        return $"Modlist {DateTime.Now:yyyy-MM-dd HH:mm}";
    }

    private string DetermineUploaderName(FirebaseModlistStore store)
    {
        string? playerName = _viewModel?.PlayerName;
        if (!string.IsNullOrWhiteSpace(playerName))
        {
            string trimmed = playerName.Trim();
            SetUsernameDisplay(trimmed);
            return trimmed;
        }

        string? suffixSource = _viewModel?.PlayerUid;
        if (string.IsNullOrWhiteSpace(suffixSource))
        {
            suffixSource = store?.CurrentUserId;
        }

        string suffix = "0000";
        if (!string.IsNullOrWhiteSpace(suffixSource))
        {
            string trimmedId = suffixSource.Trim();
            suffix = trimmedId.Length <= 4
                ? trimmedId
                : trimmedId.Substring(trimmedId.Length - 4, 4);
        }

        string fallback = $"Anonymous{suffix}";
        SetUsernameDisplay(fallback);
        return fallback;
    }

    private void SaveModlistMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        TrySaveModlist();
    }

    private Task SaveModlistToCloudAsync()
    {
        return ExecuteCloudOperationAsync(async store =>
        {
            string suggestedName = BuildCloudModlistName();
            List<CloudModConfigOption> configOptions = BuildCloudModConfigOptions();
            var detailsDialog = new CloudModlistDetailsDialog(this, suggestedName, configOptions);
            bool? dialogResult = detailsDialog.ShowDialog();
            if (dialogResult != true)
            {
                return;
            }

            string uploader = DetermineUploaderName(store);

            string modlistName = detailsDialog.ModlistName;
            string? description = detailsDialog.ModlistDescription;
            string? version = detailsDialog.ModlistVersion;

            Dictionary<string, ModConfigurationSnapshot>? includedConfigurations = null;
            IReadOnlyList<CloudModConfigOption> selectedConfigOptions = detailsDialog.GetSelectedConfigOptions();
            if (selectedConfigOptions.Count > 0)
            {
                includedConfigurations = new Dictionary<string, ModConfigurationSnapshot>(StringComparer.OrdinalIgnoreCase);
                var readErrors = new List<string>();

                foreach (CloudModConfigOption option in selectedConfigOptions)
                {
                    try
                    {
                        string content = File.ReadAllText(option.ConfigPath);
                        string fileName = GetSafeConfigFileName(Path.GetFileName(option.ConfigPath), option.ModId);
                        includedConfigurations[option.ModId] = new ModConfigurationSnapshot(fileName, content);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        readErrors.Add($"{option.DisplayName}: {ex.Message}");
                    }
                }

                if (readErrors.Count > 0)
                {
                    WpfMessageBox.Show(
                        "Some configuration files could not be included:\n" + string.Join("\n", readErrors),
                        "Simple VS Manager",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                if (includedConfigurations.Count == 0)
                {
                    includedConfigurations = null;
                }
            }

            if (!TryBuildCurrentModlistJson(modlistName, description, version, uploader, includedConfigurations, out string json))
            {
                return;
            }

            var slots = await GetCloudModlistSlotsAsync(store, includeEmptySlots: true, captureContent: false);
            string trimmedModlistName = modlistName.Trim();

            CloudModlistSlot? replacementSlot = null;
            string? slotKey = null;

            CloudModlistSlot? matchingSlot = slots.FirstOrDefault(slot =>
                slot.IsOccupied
                && string.Equals((slot.Name ?? string.Empty).Trim(), trimmedModlistName, StringComparison.OrdinalIgnoreCase));

            if (matchingSlot is not null)
            {
                string slotLabel = FormatCloudSlotLabel(matchingSlot.SlotKey);
                MessageBoxResult replaceExisting = WpfMessageBox.Show(
                    $"A cloud modlist named \"{trimmedModlistName}\" already exists in {slotLabel}. Do you want to replace it?",
                    "Simple VS Manager",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (replaceExisting != MessageBoxResult.Yes)
                {
                    return;
                }

                replacementSlot = matchingSlot;
                slotKey = matchingSlot.SlotKey;
            }

            CloudModlistSlot? freeSlot = null;
            if (slotKey is null)
            {
                freeSlot = slots.FirstOrDefault(slot => !slot.IsOccupied);
                slotKey = freeSlot?.SlotKey;
            }

            if (slotKey is null)
            {
                replacementSlot = PromptForCloudSaveReplacement(slots);
                if (replacementSlot is null)
                {
                    return;
                }

                slotKey = replacementSlot.SlotKey;
            }

            await store.SaveAsync(slotKey, json);

            if (replacementSlot is not null)
            {
                string replacedName = replacementSlot.Name ?? "existing modlist";
                string? replacedVersion = replacementSlot.Version;
                if (!string.IsNullOrWhiteSpace(replacedVersion))
                {
                    replacedName = $"{replacedName} (v{replacedVersion})";
                }

                _viewModel?.ReportStatus($"Replaced cloud modlist \"{replacedName}\" with \"{modlistName}\".");
            }
            else
            {
                _viewModel?.ReportStatus($"Saved cloud modlist \"{modlistName}\" to the cloud.");
            }
        }, "save the modlist to the cloud");
    }

    private static string? NormalizeCloudVersion(string? version)
    {
        return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
    }

    private List<CloudModConfigOption> BuildCloudModConfigOptions()
    {
        var options = new List<CloudModConfigOption>();

        if (_viewModel is null)
        {
            return options;
        }

        IReadOnlyList<ModListItemViewModel> mods = _viewModel.GetInstalledModsSnapshot();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ModListItemViewModel mod in mods)
        {
            if (mod is null || string.IsNullOrWhiteSpace(mod.ModId))
            {
                continue;
            }

            string normalizedId = mod.ModId.Trim();
            if (!seenIds.Add(normalizedId))
            {
                continue;
            }

            if (_userConfiguration.TryGetModConfigPath(normalizedId, out string? path)
                && !string.IsNullOrWhiteSpace(path)
                && File.Exists(path))
            {
                options.Add(new CloudModConfigOption(normalizedId, mod.DisplayName, path, isSelected: false));
            }
        }

        options.Sort((left, right) => string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase));
        return options;
    }

    private static string GetSafeConfigFileName(string? candidate, string? modId)
    {
        string sanitizedModId = SanitizeForFileName(string.IsNullOrWhiteSpace(modId) ? "modconfig" : modId!.Trim());
        if (string.IsNullOrWhiteSpace(sanitizedModId))
        {
            sanitizedModId = "modconfig";
        }

        string? trimmedCandidate = string.IsNullOrWhiteSpace(candidate) ? null : candidate.Trim();
        string fileName = string.IsNullOrWhiteSpace(trimmedCandidate)
            ? sanitizedModId + ".json"
            : Path.GetFileName(trimmedCandidate);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = sanitizedModId + ".json";
        }

        string sanitizedFileName = SanitizeForFileName(fileName);
        if (string.IsNullOrWhiteSpace(sanitizedFileName))
        {
            sanitizedFileName = sanitizedModId + ".json";
        }

        if (!sanitizedFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            sanitizedFileName += ".json";
        }

        return sanitizedFileName;
    }

    private static string SanitizeForFileName(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (char ch in value)
        {
            builder.Append(Array.IndexOf(invalidChars, ch) >= 0 ? '_' : ch);
        }

        return builder.ToString();
    }

    private void BrowseDownloadsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        IRelayCommand command = viewModel.ShowDownloadsSortingOptionsCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void BrowseActivityButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        IRelayCommand command = viewModel.ShowActivitySortingOptionsCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void ModlistsTabButton_OnClick(object sender, RoutedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_viewModel?.IsViewingCloudModlists == true)
            {
                _ = RefreshCloudModlistsAsync(force: true);
            }
        }, DispatcherPriority.Background);
    }

    private async void SaveModlistToCloudMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        await SaveModlistToCloudAsync();
        if (_viewModel?.IsViewingCloudModlists == true)
        {
            await RefreshCloudModlistsAsync(force: true);
        }
        else
        {
            _cloudModlistsLoaded = false;
        }
    }

    private async void SaveCloudModlistButton_OnClick(object sender, RoutedEventArgs e)
    {
        await SaveModlistToCloudAsync();
        if (_viewModel?.IsViewingCloudModlists == true)
        {
            await RefreshCloudModlistsAsync(force: true);
        }
    }

    private async void ModifyCloudModlistsButton_OnClick(object sender, RoutedEventArgs e)
    {
        await ExecuteCloudOperationAsync(async store =>
        {
            await ShowCloudModlistManagementDialogAsync(store);
        }, "manage your cloud modlists");
    }

    private async Task<bool> IsCloudUploaderNameAvailableAsync(FirebaseModlistStore store, string uploader)
    {
        if (string.IsNullOrWhiteSpace(uploader))
        {
            return true;
        }

        string trimmedUploader = uploader.Trim();
        string? currentUserId = store.CurrentUserId;
        IReadOnlyList<CloudModlistRegistryEntry> registryEntries = await store.GetRegistryEntriesAsync();

        foreach (CloudModlistRegistryEntry entry in registryEntries)
        {
            if (!string.IsNullOrEmpty(currentUserId) &&
                string.Equals(entry.OwnerId, currentUserId, StringComparison.Ordinal))
            {
                continue;
            }

            CloudModlistMetadata metadata = ExtractCloudModlistMetadata(entry.ContentJson);
            if (metadata.Uploader is not null &&
                string.Equals(metadata.Uploader, trimmedUploader, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private async void RefreshCloudModlistsButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshCloudModlistsAsync(force: true);
    }

    private void CloudModlistsDataGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CloudModlistsDataGrid?.SelectedItem is CloudModlistListEntry entry)
        {
            SetCloudModlistSelection(entry);
        }
        else
        {
            SetCloudModlistSelection(null);
        }
    }

    private async void InstallCloudModlistButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _selectedCloudModlist is not CloudModlistListEntry entry)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.ContentJson))
        {
            WpfMessageBox.Show("The selected cloud modlist has no content.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string cacheDirectory;
        try
        {
            cacheDirectory = EnsureCloudModListCacheDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WpfMessageBox.Show($"Failed to prepare the cloud modlist cache:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        string cacheFileName = BuildSuggestedFileName(entry.Name ?? entry.DisplayName, "Cloud Modlist");
        string cacheFilePath = GetUniqueFilePath(cacheDirectory, cacheFileName, ".json");

        try
        {
            await File.WriteAllTextAsync(cacheFilePath, entry.ContentJson);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WpfMessageBox.Show($"Failed to cache the selected modlist:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        ModlistLoadMode? loadMode = PromptModlistLoadMode();
        if (loadMode is not ModlistLoadMode mode)
        {
            return;
        }

        if (mode == ModlistLoadMode.Replace && !EnsureModlistBackupBeforeLoad())
        {
            return;
        }

        PrepareForModlistLoad();

        PresetLoadOptions loadOptions = GetModlistLoadOptions(mode);
        string fallbackName = entry.Name ?? entry.DisplayName ?? "Modlist";

        if (!TryLoadPresetFromFile(cacheFilePath,
                fallbackName,
                loadOptions,
                out ModPreset? preset,
                out string? errorMessage))
        {
            string message = string.IsNullOrWhiteSpace(errorMessage)
                ? "Failed to load the downloaded cloud modlist."
                : errorMessage!;
            WpfMessageBox.Show(message,
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (preset is null)
        {
            return;
        }

        await CreateAutomaticBackupAsync("ModlistLoaded").ConfigureAwait(true);
        await ApplyPresetAsync(preset);
        string status = mode == ModlistLoadMode.Replace
            ? $"Installed cloud modlist \"{preset.Name}\"."
            : $"Added mods from cloud modlist \"{preset.Name}\".";
        _viewModel.ReportStatus(status);
    }

    private async void LoadModlistFromCloudMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        await ExecuteCloudOperationAsync(async store =>
        {
            var slots = await GetCloudModlistSlotsAsync(store, includeEmptySlots: false, captureContent: true);
            if (slots.Count == 0)
            {
                WpfMessageBox.Show("No cloud modlists are available.",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dialog = new CloudSlotSelectionDialog(this,
                slots,
                "Load Cloud Modlist",
                "Select a cloud modlist to load.");
            bool? dialogResult = dialog.ShowDialog();
            if (dialogResult != true || dialog.SelectedSlot is not CloudModlistSlot selectedSlot)
            {
                return;
            }

            ModlistLoadMode? loadMode = PromptModlistLoadMode();
            if (loadMode is not ModlistLoadMode mode)
            {
                return;
            }

            if (mode == ModlistLoadMode.Replace && !EnsureModlistBackupBeforeLoad())
            {
                return;
            }

            PrepareForModlistLoad();

            PresetLoadOptions loadOptions = GetModlistLoadOptions(mode);
            string? json = selectedSlot.CachedContent;
            if (string.IsNullOrWhiteSpace(json))
            {
                json = await store.LoadAsync(selectedSlot.SlotKey);
                if (string.IsNullOrWhiteSpace(json))
                {
                    WpfMessageBox.Show("The selected cloud modlist is empty.",
                        "Simple VS Manager",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }

            string? sourceName = selectedSlot.Name ?? FormatCloudSlotLabel(selectedSlot.SlotKey);
            if (!TryLoadPresetFromJson(json,
                    "Modlist",
                    loadOptions,
                    out ModPreset? preset,
                    out string? errorMessage,
                    sourceName))
            {
                string message = string.IsNullOrWhiteSpace(errorMessage)
                    ? "The selected cloud modlist is not valid."
                    : errorMessage!;
                WpfMessageBox.Show($"Failed to load the modlist:\n{message}",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            ModPreset loadedModlist = preset!;
            await CreateAutomaticBackupAsync("ModlistLoaded").ConfigureAwait(true);
            await ApplyPresetAsync(loadedModlist);
            string slotLabel = FormatCloudSlotLabel(selectedSlot.SlotKey);
            string status = mode == ModlistLoadMode.Replace
                ? $"Loaded cloud modlist \"{loadedModlist.Name}\" from {slotLabel}."
                : $"Added mods from cloud modlist \"{loadedModlist.Name}\" from {slotLabel}.";
            _viewModel?.ReportStatus(status);
        }, "load the modlist from the cloud");
    }

    private async void DeleteCloudModlistMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        await ExecuteCloudOperationAsync(async store =>
        {
            var slots = await GetCloudModlistSlotsAsync(store, includeEmptySlots: false, captureContent: false);
            if (slots.Count == 0)
            {
                WpfMessageBox.Show("No cloud modlists are available to delete.",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dialog = new CloudSlotSelectionDialog(this,
                slots,
                "Delete Cloud Modlist",
                "Select the cloud modlist you want to delete.");
            bool? dialogResult = dialog.ShowDialog();
            if (dialogResult != true || dialog.SelectedSlot is not CloudModlistSlot selectedSlot)
            {
                return;
            }

            string slotLabel = FormatCloudSlotLabel(selectedSlot.SlotKey);
            string displayName = string.IsNullOrWhiteSpace(selectedSlot.Name)
                ? slotLabel
                : $"{slotLabel} (\"{selectedSlot.Name}\")";

            MessageBoxResult confirmation = WpfMessageBox.Show(
                $"Are you sure you want to delete {displayName}? This action cannot be undone.",
                "Simple VS Manager",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            await store.DeleteAsync(selectedSlot.SlotKey);
            _viewModel?.ReportStatus($"Deleted cloud modlist from {slotLabel}.");
        }, "delete the cloud modlist");

        if (_viewModel?.IsViewingCloudModlists == true)
        {
            await RefreshCloudModlistsAsync(force: true);
        }
        else
        {
            _cloudModlistsLoaded = false;
        }
    }

    private void LoadPresetMenuItem_OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
        {
            return;
        }

        for (int index = menuItem.Items.Count - 1; index >= 1; index--)
        {
            menuItem.Items.RemoveAt(index);
        }

        string directory;
        try
        {
            directory = EnsurePresetDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Failed to access preset directory: {0}", ex.Message);
            menuItem.Items.Add(new MenuItem
            {
                Header = "Presets unavailable",
                IsEnabled = false
            });
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Failed to enumerate presets: {0}", ex.Message);
            menuItem.Items.Add(new MenuItem
            {
                Header = "Presets unavailable",
                IsEnabled = false
            });
            return;
        }

        if (files.Length == 0)
        {
            menuItem.Items.Add(new MenuItem
            {
                Header = "No presets available",
                IsEnabled = false
            });
            return;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        menuItem.Items.Add(new Separator());

        foreach (string file in files)
        {
            string displayName = Path.GetFileNameWithoutExtension(file);
            var item = new MenuItem
            {
                Header = displayName,
                Tag = file
            };
            item.Click += LoadPresetMenuItem_OnPresetClick;
            menuItem.Items.Add(item);
        }
    }

    private async void LoadPresetMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        string presetDirectory = EnsurePresetDirectory();
        var dialog = new OpenFileDialog
        {
            Title = "Load Mod Preset",
            Filter = "Preset files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            InitialDirectory = presetDirectory,
            Multiselect = false
        };

        dialog.FileOk += (_, args) =>
        {
            if (IsPathWithinDirectory(presetDirectory, dialog.FileName))
            {
                return;
            }

            WpfMessageBox.Show("Please select a preset from the Presets folder.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Cancel = true;
        };

        bool? result = dialog.ShowDialog(this);
        if (result != true)
        {
            return;
        }

        await LoadPresetFromFileAsync(dialog.FileName).ConfigureAwait(true);
    }

    private async void LoadPresetMenuItem_OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not string filePath)
        {
            return;
        }

        await LoadPresetFromFileAsync(filePath).ConfigureAwait(true);
    }

    private async Task LoadPresetFromFileAsync(string filePath)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (!File.Exists(filePath))
        {
            WpfMessageBox.Show(
                "The selected preset could not be found.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!TryLoadPresetFromFile(filePath, "Preset", StandardPresetLoadOptions, out ModPreset? preset, out string? errorMessage))
        {
            string message = string.IsNullOrWhiteSpace(errorMessage)
                ? "The selected file is not a valid preset."
                : errorMessage!;
            WpfMessageBox.Show($"Failed to load the preset:\n{message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        ModPreset loadedPreset = preset!;
        _userConfiguration.SetLastSelectedPresetName(loadedPreset.Name);
        await ApplyPresetAsync(loadedPreset).ConfigureAwait(true);
        _viewModel?.ReportStatus($"Loaded preset \"{loadedPreset.Name}\".");
    }

    private async void LoadModlistMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        string modListDirectory = EnsureModListDirectory();
        var dialog = new OpenFileDialog
        {
            Title = "Load Modlist",
            Filter = "Modlist files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            InitialDirectory = modListDirectory,
            Multiselect = false
        };

        dialog.FileOk += (_, args) =>
        {
            if (IsPathWithinDirectory(modListDirectory, dialog.FileName))
            {
                return;
            }

            WpfMessageBox.Show("Please select a modlist from the Modlists folder.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Cancel = true;
        };

        bool? dialogResult = dialog.ShowDialog(this);
        if (dialogResult != true)
        {
            return;
        }

        ModlistLoadMode? loadMode = PromptModlistLoadMode();
        if (loadMode is not ModlistLoadMode mode)
        {
            return;
        }

        if (mode == ModlistLoadMode.Replace && !EnsureModlistBackupBeforeLoad())
        {
            return;
        }

        PrepareForModlistLoad();

        PresetLoadOptions loadOptions = GetModlistLoadOptions(mode);

        if (!TryLoadPresetFromFile(dialog.FileName, "Modlist", loadOptions, out ModPreset? preset, out string? errorMessage))
        {
            string message = string.IsNullOrWhiteSpace(errorMessage)
                ? "The selected file is not a valid modlist."
                : errorMessage!;
            WpfMessageBox.Show($"Failed to load the modlist:\n{message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        ModPreset loadedModlist = preset!;
        await CreateAutomaticBackupAsync("ModlistLoaded").ConfigureAwait(true);
        await ApplyPresetAsync(loadedModlist);
        string status = mode == ModlistLoadMode.Replace
            ? $"Loaded modlist \"{loadedModlist.Name}\"."
            : $"Added mods from modlist \"{loadedModlist.Name}\".";
        _viewModel?.ReportStatus(status);
    }

    private bool TryLoadPresetFromFile(string filePath, string fallbackName, PresetLoadOptions options, out ModPreset? preset, out string? errorMessage)
    {
        preset = null;
        errorMessage = null;

        try
        {
            if (!File.Exists(filePath))
            {
                errorMessage = "The selected file could not be found.";
                return false;
            }

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            using FileStream stream = File.OpenRead(filePath);
            SerializablePreset? data = JsonSerializer.Deserialize<SerializablePreset>(stream, jsonOptions);
            if (data is null)
            {
                errorMessage = "The selected file was empty.";
                return false;
            }

            string snapshotName = GetSnapshotNameFromFilePath(filePath, fallbackName);
            return TryBuildPresetFromSerializable(data, fallbackName, options, out preset, out errorMessage, snapshotName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private bool TryLoadPresetFromJson(
        string json,
        string fallbackName,
        PresetLoadOptions options,
        out ModPreset? preset,
        out string? errorMessage,
        string? sourceName = null)
    {
        preset = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            errorMessage = "The selected file was empty.";
            return false;
        }

        try
        {
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            SerializablePreset? data = JsonSerializer.Deserialize<SerializablePreset>(json, jsonOptions);
            if (data is null)
            {
                errorMessage = "The selected file was empty.";
                return false;
            }

            return TryBuildPresetFromSerializable(data, fallbackName, options, out preset, out errorMessage, sourceName);
        }
        catch (JsonException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private bool TryBuildPresetFromSerializable(
        SerializablePreset data,
        string fallbackName,
        PresetLoadOptions options,
        out ModPreset? preset,
        out string? errorMessage,
        string? fallbackNameFromSource = null)
    {
        preset = null;
        errorMessage = null;

        string name = !string.IsNullOrWhiteSpace(data.Name)
            ? data.Name!.Trim()
            : (!string.IsNullOrWhiteSpace(fallbackNameFromSource)
                ? fallbackNameFromSource!.Trim()
                : fallbackName);

        var disabledEntries = new List<string>();
        var seenDisabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (data.DisabledEntries != null)
        {
            foreach (string entry in data.DisabledEntries)
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                string trimmed = entry.Trim();
                if (seenDisabled.Add(trimmed))
                {
                    disabledEntries.Add(trimmed);
                }
            }
        }

        bool presetIndicatesStatus = data.IncludeModStatus
            ?? (data.Mods?.Any(entry => entry?.IsActive is not null) ?? false);
        bool presetIndicatesVersions = data.IncludeModVersions
            ?? (data.Mods?.Any(entry => !string.IsNullOrWhiteSpace(entry?.Version)) ?? false);
        bool includeStatus = options.ApplyModStatus && presetIndicatesStatus;
        bool includeVersions = options.ApplyModVersions && presetIndicatesVersions;
        bool exclusive = options.ForceExclusive;

        var modStates = new List<ModPresetModState>();
        if (data.Mods != null)
        {
            foreach (var mod in data.Mods)
            {
                if (mod is null || string.IsNullOrWhiteSpace(mod.ModId))
                {
                    continue;
                }

                string modId = mod.ModId.Trim();
                string? version = string.IsNullOrWhiteSpace(mod.Version)
                    ? null
                    : mod.Version!.Trim();
                string? configurationFileName = string.IsNullOrWhiteSpace(mod.ConfigurationFileName)
                    ? null
                    : mod.ConfigurationFileName!.Trim();
                string? configurationContent = string.IsNullOrWhiteSpace(mod.ConfigurationContent)
                    ? null
                    : mod.ConfigurationContent;

                modStates.Add(new ModPresetModState(modId, version, mod.IsActive, configurationFileName, configurationContent));
            }
        }

        preset = new ModPreset(name, disabledEntries, modStates, includeStatus, includeVersions, exclusive);
        return true;
    }

    private async Task ExecuteCloudOperationAsync(Func<FirebaseModlistStore, Task> operation, string actionDescription)
    {
        try
        {
            InternetAccessManager.ThrowIfInternetAccessDisabled();
        }
        catch (InternetAccessDisabledException ex)
        {
            WpfMessageBox.Show(ex.Message,
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        FirebaseModlistStore store;
        try
        {
            store = await EnsureCloudStoreInitializedAsync();
        }
        catch (Exception ex)
        {
            StatusLogService.AppendStatus($"Failed to initialize cloud storage: {ex}", true);
            WpfMessageBox.Show($"Failed to initialize cloud storage:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        try
        {
            await operation(store);
            EnsureFirebaseAuthBackedUpIfAvailable();
        }
        catch (InternetAccessDisabledException ex)
        {
            WpfMessageBox.Show(ex.Message,
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            StatusLogService.AppendStatus($"Network error while attempting to {actionDescription}: {ex.Message}", true);
            WpfMessageBox.Show($"Failed to {actionDescription}:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (InvalidOperationException ex)
        {
            StatusLogService.AppendStatus($"Cloud operation failed while attempting to {actionDescription}: {ex.Message}", true);
            WpfMessageBox.Show($"Failed to {actionDescription}:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            StatusLogService.AppendStatus($"Unexpected error while attempting to {actionDescription}: {ex}", true);
            WpfMessageBox.Show($"An unexpected error occurred while attempting to {actionDescription}:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task<FirebaseModlistStore> EnsureCloudStoreInitializedAsync()
    {
        if (_cloudModlistStore is { } existingStore)
        {
            ApplyPlayerIdentityToCloudStore(existingStore);
            return existingStore;
        }

        await _cloudStoreLock.WaitAsync();
        try
        {
            if (_cloudModlistStore is { } cached)
            {
                ApplyPlayerIdentityToCloudStore(cached);
                return cached;
            }

            var store = new FirebaseModlistStore();
            ApplyPlayerIdentityToCloudStore(store);
            _cloudModlistStore = store;
            return store;
        }
        finally
        {
            _cloudStoreLock.Release();
        }
    }

    private CloudModlistSlot? PromptForCloudSaveReplacement(IReadOnlyList<CloudModlistSlot> slots)
    {
        if (slots is null)
        {
            return null;
        }

        var occupiedSlots = slots.Where(slot => slot.IsOccupied).ToList();
        if (occupiedSlots.Count == 0)
        {
            return null;
        }

        var dialog = new CloudSlotSelectionDialog(this,
            occupiedSlots,
            "Replace Cloud Modlist",
            "Select a cloud modlist to replace.");
        bool? dialogResult = dialog.ShowDialog();
        return dialogResult == true ? dialog.SelectedSlot : null;
    }

    private async Task ShowCloudModlistManagementDialogAsync(FirebaseModlistStore store)
    {
        IReadOnlyList<CloudModlistManagementEntry> entries = await BuildCloudModlistManagementEntriesAsync(store);
        if (entries.Count == 0)
        {
            WpfMessageBox.Show(
                "You do not have any cloud modlists saved.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new CloudModlistManagementDialog(
            this,
            entries,
            () => BuildCloudModlistManagementEntriesAsync(store),
            (entry, newName) => RenameCloudModlistAsync(store, entry, newName),
            entry => DeleteCloudModlistAsync(store, entry));

        dialog.ShowDialog();
    }

    private async Task<IReadOnlyList<CloudModlistManagementEntry>> BuildCloudModlistManagementEntriesAsync(
        FirebaseModlistStore store)
    {
        var slots = await GetCloudModlistSlotsAsync(store, includeEmptySlots: false, captureContent: true);
        var list = new List<CloudModlistManagementEntry>(slots.Count);

        foreach (CloudModlistSlot slot in slots)
        {
            string slotLabel = FormatCloudSlotLabel(slot.SlotKey);
            list.Add(new CloudModlistManagementEntry(
                slot.SlotKey,
                slotLabel,
                slot.Name,
                slot.Version,
                slot.DisplayName,
                slot.CachedContent));
        }

        return list;
    }

    private async Task<bool> RenameCloudModlistAsync(
        FirebaseModlistStore store,
        CloudModlistManagementEntry entry,
        string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return false;
        }

        string trimmedName = newName.Trim();
        string? json = entry.CachedContent;

        if (string.IsNullOrWhiteSpace(json))
        {
            try
            {
                json = await store.LoadAsync(entry.SlotKey);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                StatusLogService.AppendStatus($"Failed to load cloud modlist for rename: {ex.Message}", true);
                WpfMessageBox.Show($"Failed to load the cloud modlist before renaming:\n{ex.Message}",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            WpfMessageBox.Show(
                "The selected cloud modlist could not be loaded.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        string updatedJson;
        try
        {
            updatedJson = ReplaceCloudModlistName(json, trimmedName);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            StatusLogService.AppendStatus($"Failed to update cloud modlist name: {ex.Message}", true);
            WpfMessageBox.Show($"The cloud modlist data is invalid and could not be renamed:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        try
        {
            await store.SaveAsync(entry.SlotKey, updatedJson);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            StatusLogService.AppendStatus($"Failed to rename cloud modlist: {ex.Message}", true);
            WpfMessageBox.Show($"Failed to rename the cloud modlist:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        string slotLabel = FormatCloudSlotLabel(entry.SlotKey);
        _viewModel?.ReportStatus($"Renamed cloud modlist in {slotLabel} to \"{trimmedName}\".");

        await UpdateCloudModlistsAfterChangeAsync();
        return true;
    }

    private async Task<bool> DeleteCloudModlistAsync(FirebaseModlistStore store, CloudModlistManagementEntry entry)
    {
        try
        {
            await store.DeleteAsync(entry.SlotKey);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            StatusLogService.AppendStatus($"Failed to delete cloud modlist: {ex.Message}", true);
            WpfMessageBox.Show($"Failed to delete the cloud modlist:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            StatusLogService.AppendStatus($"Invalid request while deleting cloud modlist: {ex.Message}", true);
            WpfMessageBox.Show($"Failed to delete the cloud modlist:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        string slotLabel = FormatCloudSlotLabel(entry.SlotKey);
        _viewModel?.ReportStatus($"Deleted cloud modlist from {slotLabel}.");

        await UpdateCloudModlistsAfterChangeAsync();
        return true;
    }

    private async Task DeleteAllCloudModlistsAndAuthorizationAsync(FirebaseModlistStore store, bool showCompletionMessage = true)
    {
        await store.DeleteAllUserDataAsync();
        await store.Authenticator.DeleteAccountAsync(CancellationToken.None);

        DeleteFirebaseAuthFiles();

        _cloudModlistStore = null;
        _cloudModlistsLoaded = false;

        SetCloudModlistSelection(null);
        _viewModel?.ReplaceCloudModlists(null);
        if (CloudModlistsDataGrid is not null)
        {
            CloudModlistsDataGrid.SelectedItem = null;
        }

        StatusLogService.AppendStatus("Deleted all cloud modlists and Firebase authorization.", false);
        _viewModel?.ReportStatus("Deleted all cloud modlists and Firebase authorization.");

        if (showCompletionMessage)
        {
            WpfMessageBox.Show(
                this,
                "Cloud modlists and Firebase authorization have been deleted.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private async Task UpdateCloudModlistsAfterChangeAsync()
    {
        if (_viewModel?.IsViewingCloudModlists == true)
        {
            await RefreshCloudModlistsAsync(force: true);
        }
        else
        {
            _cloudModlistsLoaded = false;
        }
    }

    private static string ReplaceCloudModlistName(string json, string newName)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The cloud modlist content is not a valid object.");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            bool nameWritten = false;

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("name"))
                {
                    writer.WriteString("name", newName);
                    nameWritten = true;
                }
                else
                {
                    property.WriteTo(writer);
                }
            }

            if (!nameWritten)
            {
                writer.WriteString("name", newName);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task<List<CloudModlistSlot>> GetCloudModlistSlotsAsync(
        FirebaseModlistStore store,
        bool includeEmptySlots,
        bool captureContent)
    {
        IReadOnlyList<string> existing = await store.ListSlotsAsync();
        var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var result = new List<CloudModlistSlot>(FirebaseModlistStore.SlotKeys.Count);

        foreach (string slotKey in FirebaseModlistStore.SlotKeys)
        {
            bool isOccupied = existingSet.Contains(slotKey);
            if (!includeEmptySlots && !isOccupied)
            {
                continue;
            }

            string? json = null;
            CloudModlistMetadata metadata = CloudModlistMetadata.Empty;
            if (isOccupied)
            {
                try
                {
                    json = await store.LoadAsync(slotKey);
                    metadata = ExtractCloudModlistMetadata(json);
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
                {
                    StatusLogService.AppendStatus($"Failed to retrieve cloud modlist for {slotKey}: {ex.Message}", true);
                }
            }

            string display = BuildCloudSlotDisplay(slotKey, metadata, isOccupied);
            string? cachedContent = captureContent ? json : null;
            result.Add(new CloudModlistSlot(slotKey, isOccupied, display, metadata.Name, metadata.Version, cachedContent));
        }

        return result;
    }

    private async Task RefreshCloudModlistsAsync(bool force)
    {
        if (_isCloudModlistRefreshInProgress)
        {
            return;
        }

        if (!force && _cloudModlistsLoaded)
        {
            return;
        }

        _isCloudModlistRefreshInProgress = true;
        UpdateCloudModlistControlsEnabledState();

        try
        {
            await ExecuteCloudOperationAsync(async store =>
            {
                IReadOnlyList<CloudModlistRegistryEntry> registryEntries = await store.GetRegistryEntriesAsync();
                IReadOnlyList<CloudModlistListEntry> listEntries = BuildCloudModlistEntries(registryEntries);

                await Dispatcher.InvokeAsync(() =>
                {
                    _viewModel?.ReplaceCloudModlists(listEntries);
                    _cloudModlistsLoaded = true;
                    SetCloudModlistSelection(null);
                    if (CloudModlistsDataGrid != null)
                    {
                        CloudModlistsDataGrid.SelectedItem = null;
                    }
                }, DispatcherPriority.Background);
            }, "load cloud modlists");
        }
        finally
        {
            _isCloudModlistRefreshInProgress = false;
            UpdateCloudModlistControlsEnabledState();
        }
    }

    private IReadOnlyList<CloudModlistListEntry> BuildCloudModlistEntries(IEnumerable<CloudModlistRegistryEntry> registryEntries)
    {
        var list = new List<CloudModlistListEntry>();
        if (registryEntries is null)
        {
            return list;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in registryEntries)
        {
            if (entry is null)
            {
                continue;
            }

            if (!seen.Add(entry.RegistryKey))
            {
                continue;
            }

            string slotLabel = FormatCloudSlotLabel(entry.SlotKey);
            CloudModlistMetadata metadata = ExtractCloudModlistMetadata(entry.ContentJson);
            list.Add(new CloudModlistListEntry(
                entry.OwnerId,
                entry.SlotKey,
                slotLabel,
                metadata.Name,
                metadata.Description,
                metadata.Version,
                metadata.Uploader,
                metadata.Mods,
                entry.ContentJson,
                entry.DateAdded));
        }

        list.Sort((left, right) =>
        {
            int compare = string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
            if (compare != 0)
            {
                return compare;
            }

            compare = string.Compare(left.OwnerId, right.OwnerId, StringComparison.OrdinalIgnoreCase);
            if (compare != 0)
            {
                return compare;
            }

            return string.Compare(left.SlotKey, right.SlotKey, StringComparison.OrdinalIgnoreCase);
        });

        return list;
    }

    private void SetCloudModlistSelection(CloudModlistListEntry? entry)
    {
        _selectedCloudModlist = entry;

        if (SelectedModlistTitle is not null)
        {
            SelectedModlistTitle.Text = entry?.DisplayName ?? string.Empty;
        }

        if (SelectedModlistDescription is not null)
        {
            SelectedModlistDescription.Text = entry?.Description ?? string.Empty;
        }

        if (InstallCloudModlistButton is not null)
        {
            if (entry is null)
            {
                InstallCloudModlistButton.Visibility = Visibility.Collapsed;
                InstallCloudModlistButton.ToolTip = null;
            }
            else
            {
                InstallCloudModlistButton.Visibility = Visibility.Visible;
                InstallCloudModlistButton.ToolTip = $"Install \"{entry.DisplayName}\"";
            }
        }

        UpdateCloudModlistControlsEnabledState();
    }

    private void UpdateCloudModlistControlsEnabledState()
    {
        bool internetEnabled = !InternetAccessManager.IsInternetAccessDisabled;

        if (SaveCloudModlistButton is not null)
        {
            SaveCloudModlistButton.IsEnabled = internetEnabled;
        }

        if (ModifyCloudModlistsButton is not null)
        {
            ModifyCloudModlistsButton.IsEnabled = internetEnabled;
        }

        if (RefreshCloudModlistsButton is not null)
        {
            RefreshCloudModlistsButton.IsEnabled = internetEnabled && !_isCloudModlistRefreshInProgress;
        }

        if (InstallCloudModlistButton is not null)
        {
            bool hasSelection = _selectedCloudModlist is not null;
            InstallCloudModlistButton.IsEnabled = internetEnabled && hasSelection;
        }
    }

    private async Task RefreshManagerUpdateLinkAsync()
    {
        if (ManagerUpdateLinkTextBlock is null)
        {
            return;
        }

        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            ManagerUpdateLinkTextBlock.Visibility = Visibility.Collapsed;
            return;
        }

        string? currentVersion = GetManagerInformationalVersion();
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            ManagerUpdateLinkTextBlock.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            // Use relaxed mode for manager updates to allow more flexible compatibility
            ModDatabaseInfo? info = await _modDatabaseService
                .TryLoadDatabaseInfoAsync(ManagerModDatabaseModId, currentVersion, null, requireExactVersionMatch: false)
                .ConfigureAwait(true);

            bool hasUpdate = info?.LatestVersion is string latestVersion
                && VersionStringUtility.IsCandidateVersionNewer(latestVersion, currentVersion);

            ManagerUpdateLinkTextBlock.Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            ManagerUpdateLinkTextBlock.Visibility = Visibility.Collapsed;
        }
    }

    private static string? GetManagerInformationalVersion()
    {
        try
        {
            Assembly? assembly = typeof(MainWindow).Assembly;
            if (assembly is null)
            {
                return null;
            }

            string? informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
                ?? assembly.GetName().Version?.ToString();

            if (string.IsNullOrWhiteSpace(informationalVersion))
            {
                return null;
            }

            int buildMetadataSeparatorIndex = informationalVersion.IndexOf('+');
            if (buildMetadataSeparatorIndex >= 0)
            {
                informationalVersion = informationalVersion[..buildMetadataSeparatorIndex];
            }

            return informationalVersion.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string BuildCloudSlotDisplay(string slotKey, CloudModlistMetadata metadata, bool isOccupied)
    {
        if (!isOccupied)
        {
            return $"{FormatCloudSlotLabel(slotKey)} (Empty)";
        }

        string name = metadata.Name ?? "Unnamed Modlist";
        return string.IsNullOrWhiteSpace(metadata.Version)
            ? name
            : $"{name} (v{metadata.Version})";
    }

    private static string FormatCloudSlotLabel(string slotKey)
    {
        if (string.Equals(slotKey, "public", StringComparison.OrdinalIgnoreCase))
        {
            return "Public Entry";
        }

        if (slotKey.Length > 4 && slotKey.StartsWith("slot", StringComparison.OrdinalIgnoreCase))
        {
            return $"Slot {slotKey.Substring(4)}";
        }

        return slotKey;
    }

    private static string? ExtractModlistName(string? json)
    {
        return ExtractCloudModlistMetadata(json).Name;
    }

    private static CloudModlistMetadata ExtractCloudModlistMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return CloudModlistMetadata.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return CloudModlistMetadata.Empty;
            }

            JsonElement root = document.RootElement;
            string? name = TryGetTrimmedProperty(root, "name");
            string? description = TryGetTrimmedProperty(root, "description");
            string? version = TryGetTrimmedProperty(root, "version");
            string? uploader = TryGetTrimmedProperty(root, "uploader")
                ?? TryGetTrimmedProperty(root, "uploaderName");

            var mods = new List<string>();
            if (root.TryGetProperty("mods", out JsonElement modsElement) && modsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement modElement in modsElement.EnumerateArray())
                {
                    if (modElement.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string? modId = TryGetTrimmedProperty(modElement, "modId");
                    if (string.IsNullOrWhiteSpace(modId))
                    {
                        continue;
                    }

                    string? modVersion = TryGetTrimmedProperty(modElement, "version");
                    string display = modId;
                    if (!string.IsNullOrWhiteSpace(modVersion))
                    {
                        display += $" ({modVersion})";
                    }

                    mods.Add(display);
                }
            }

            return new CloudModlistMetadata(name, description, version, uploader, mods);
        }
        catch (JsonException)
        {
            return CloudModlistMetadata.Empty;
        }
    }

    private static string? TryGetTrimmedProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out JsonElement property))
        {
            if (property.ValueKind == JsonValueKind.String)
            {
                string? value = property.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }

        return null;
    }

    private string EnsurePresetDirectory()
    {
        string baseDirectory = _userConfiguration.GetConfigurationDirectory();
        string presetDirectory = Path.Combine(baseDirectory, PresetDirectoryName);
        Directory.CreateDirectory(presetDirectory);
        return presetDirectory;
    }

    private string EnsureBackupDirectory()
    {
        string baseDirectory = _userConfiguration.GetConfigurationDirectory();
        string backupDirectory = Path.Combine(baseDirectory, BackupDirectoryName);
        Directory.CreateDirectory(backupDirectory);
        return backupDirectory;
    }

    private string EnsureLocalModBackupRootDirectory()
    {
        string backupDirectory = EnsureBackupDirectory();
        string localModsDirectory = Path.Combine(backupDirectory, "Backup Local Mods");
        Directory.CreateDirectory(localModsDirectory);
        return localModsDirectory;
    }

    private string CreateLocalModBackupSessionDirectory()
    {
        string rootDirectory = EnsureLocalModBackupRootDirectory();
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string sessionName = $"Local Mods {timestamp}";
        string sessionDirectory = Path.Combine(rootDirectory, sessionName);
        sessionDirectory = EnsureUniqueDirectoryPath(sessionDirectory);
        Directory.CreateDirectory(sessionDirectory);
        return sessionDirectory;
    }

    private static string EnsureUniqueDirectoryPath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return path;
        }

        string basePath = path;
        int counter = 1;
        string candidate;
        do
        {
            candidate = $"{basePath} ({counter++})";
        }
        while (Directory.Exists(candidate) || File.Exists(candidate));

        return candidate;
    }

    private static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (string directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, directory);
            string targetDirectory = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(targetDirectory);
        }

        foreach (string file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, file);
            string targetPath = Path.Combine(destinationDirectory, relativePath);
            string? targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            File.Copy(file, targetPath, overwrite: false);
        }
    }

    private string GetLocalModBackupEntryDirectory(string sessionDirectory, ModListItemViewModel mod)
    {
        string fallbackName = string.IsNullOrWhiteSpace(mod.ModId) ? "Mod" : mod.ModId;
        string displayName = string.IsNullOrWhiteSpace(mod.DisplayName) ? fallbackName : mod.DisplayName;
        string sanitized = SanitizeFileName(displayName, fallbackName);
        string entryDirectory = Path.Combine(sessionDirectory, sanitized);
        return EnsureUniqueDirectoryPath(entryDirectory);
    }

    private static void BackupLocalModAtPath(string sourcePath, string destinationDirectory)
    {
        if (Directory.Exists(sourcePath))
        {
            CopyDirectoryContents(sourcePath, destinationDirectory);
            return;
        }

        if (File.Exists(sourcePath))
        {
            Directory.CreateDirectory(destinationDirectory);
            string fileName = Path.GetFileName(sourcePath);
            string targetPath = Path.Combine(destinationDirectory, fileName);
            targetPath = EnsureUniqueFilePath(targetPath);
            File.Copy(sourcePath, targetPath, overwrite: false);
        }
    }

    private string EnsureModListDirectory()
    {
        string baseDirectory = _userConfiguration.GetConfigurationDirectory();
        string modListDirectory = Path.Combine(baseDirectory, ModListDirectoryName);
        Directory.CreateDirectory(modListDirectory);
        return modListDirectory;
    }

    private string EnsureCloudModListCacheDirectory()
    {
        string baseDirectory = _userConfiguration.GetConfigurationDirectory();
        string cacheDirectory = Path.Combine(baseDirectory, CloudModListCacheDirectoryName);
        Directory.CreateDirectory(cacheDirectory);
        return cacheDirectory;
    }

    private static bool IsPathWithinDirectory(string directory, string candidatePath)
    {
        try
        {
            string normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))
                + Path.DirectorySeparatorChar;
            string normalizedPath = Path.GetFullPath(candidatePath);
            return normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string BuildSuggestedFileName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        string sanitized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private static string GetUniqueFilePath(string directory, string baseFileName, string extension)
    {
        string safeBaseName = string.IsNullOrWhiteSpace(baseFileName) ? "Modlist" : baseFileName;
        string fileName = safeBaseName + extension;
        string path = Path.Combine(directory, fileName);
        int counter = 1;

        while (File.Exists(path))
        {
            fileName = $"{safeBaseName} ({counter}){extension}";
            path = Path.Combine(directory, fileName);
            counter++;
        }

        return path;
    }

    private static string GetSnapshotNameFromFilePath(string filePath, string fallback)
    {
        string name = Path.GetFileNameWithoutExtension(filePath);
        return string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
    }

    private sealed class CloudModlistMetadata
    {
        public static readonly CloudModlistMetadata Empty = new(null, null, null, null, Array.Empty<string>());

        public CloudModlistMetadata(string? name, string? description, string? version, string? uploader, IReadOnlyList<string> mods)
        {
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
            Uploader = string.IsNullOrWhiteSpace(uploader) ? null : uploader.Trim();
            Mods = mods ?? Array.Empty<string>();
        }

        public string? Name { get; }

        public string? Description { get; }

        public string? Version { get; }

        public string? Uploader { get; }

        public IReadOnlyList<string> Mods { get; }
    }

    private sealed record ModConfigurationSnapshot(string FileName, string Content);

    private sealed class SerializablePreset
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Version { get; set; }
        public string? Uploader { get; set; }
        public List<string>? DisabledEntries { get; set; }
        public List<SerializablePresetModState>? Mods { get; set; }
        public bool? IncludeModStatus { get; set; }
        public bool? IncludeModVersions { get; set; }
        public bool? Exclusive { get; set; }
    }

    private sealed class SerializablePresetModState
    {
        public string? ModId { get; set; }
        public string? Version { get; set; }
        public bool? IsActive { get; set; }
        public string? ConfigurationFileName { get; set; }
        public string? ConfigurationContent { get; set; }
    }

    private async Task ApplyPresetAsync(ModPreset preset, bool importConfigurations = true)
    {
        MainViewModel? viewModel = _viewModel;
        if (viewModel is null || _isApplyingPreset)
        {
            return;
        }

        using IDisposable busyScope = viewModel.EnterBusyScope();

        _recentLocalModBackupDirectory = null;
        _recentLocalModBackupModNames = null;

        bool scheduleRefreshAfterLoad = false;
        _isApplyingPreset = true;
        UpdateModlistLoadingUiState();
        try
        {
            if (preset.IncludesModVersions && preset.ModStates.Count > 0)
            {
                scheduleRefreshAfterLoad = await ApplyPresetModVersionsAsync(preset).ConfigureAwait(true);
            }

            bool applied = await viewModel.ApplyPresetAsync(preset).ConfigureAwait(true);
            if (applied)
            {
                if (preset.IsExclusive)
                {
                    await ApplyExclusivePresetAsync(preset).ConfigureAwait(true);
                }

                viewModel.SelectedSortOption?.Apply(viewModel.ModsView);
                viewModel.ModsView.Refresh();
            }

            if (importConfigurations)
            {
                await ImportPresetConfigsAsync(preset).ConfigureAwait(true);
            }
        }
        finally
        {
            _isApplyingPreset = false;
            UpdateModlistLoadingUiState();

            if (scheduleRefreshAfterLoad)
            {
                _refreshAfterModlistLoadPending = true;
                ScheduleRefreshAfterModlistLoadIfReady();
            }
        }
    }

    private async Task ImportPresetConfigsAsync(ModPreset preset)
    {
        if (preset.ModStates.Count == 0)
        {
            return;
        }

        var configs = new List<(string ModId, string? FileName, string Content)>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ModPresetModState state in preset.ModStates)
        {
            if (state is null
                || string.IsNullOrWhiteSpace(state.ModId)
                || string.IsNullOrWhiteSpace(state.ConfigurationContent))
            {
                continue;
            }

            string trimmedId = state.ModId.Trim();
            if (!seenIds.Add(trimmedId))
            {
                continue;
            }

            configs.Add((trimmedId, state.ConfigurationFileName, state.ConfigurationContent!));
        }

        if (configs.Count == 0)
        {
            return;
        }

        var modDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var promptNames = new List<string>(configs.Count);
        foreach (var config in configs)
        {
            string displayName = config.ModId;
            if (_viewModel?.TryGetInstalledModDisplayName(config.ModId, out string? resolvedName) == true
                && !string.IsNullOrWhiteSpace(resolvedName))
            {
                displayName = resolvedName.Trim();
            }

            if (!modDisplayNames.ContainsKey(config.ModId))
            {
                modDisplayNames.Add(config.ModId, displayName);
            }

            promptNames.Add(displayName);
        }

        promptNames = promptNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string summary = promptNames.Count == 0
            ? ""
            : string.Join("\n", promptNames.Select(name => $"• {name}"));

        string message = "This modlist includes configuration files for the following mods:";
        if (!string.IsNullOrEmpty(summary))
        {
            message += $"\n\n{summary}";
        }

        message += "\n\nImporting these configurations will overwrite your existing settings for these mods if they are already installed. Do you want to import them?";

        MessageBoxResult prompt = WpfMessageBox.Show(
            this,
            message,
            "Import Mod Configurations",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (prompt != MessageBoxResult.Yes)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_dataDirectory))
        {
            WpfMessageBox.Show(
                "The Vintage Story data directory is not set, so the configuration files could not be imported.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string configDirectory = Path.Combine(_dataDirectory, "ModConfig");
        try
        {
            Directory.CreateDirectory(configDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WpfMessageBox.Show(
                $"Failed to prepare the configuration directory:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        int importedCount = 0;

        foreach (var config in configs)
        {
            string fileName = GetSafeConfigFileName(config.FileName, config.ModId);
            string uniqueFileName = fileName;
            int counter = 1;

            while (!usedFileNames.Add(uniqueFileName))
            {
                string baseName = Path.GetFileNameWithoutExtension(fileName);
                string extension = Path.GetExtension(fileName);
                uniqueFileName = $"{baseName}_{counter++}{extension}";
            }

            string targetPath = Path.Combine(configDirectory, uniqueFileName);
            try
            {
                await File.WriteAllTextAsync(targetPath, config.Content).ConfigureAwait(true);
                _userConfiguration.SetModConfigPath(config.ModId, targetPath);
                importedCount++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                string displayName = modDisplayNames.TryGetValue(config.ModId, out string? name) && !string.IsNullOrWhiteSpace(name)
                    ? name
                    : config.ModId;
                errors.Add($"{displayName}: {ex.Message}");
            }
        }

        if (importedCount > 0)
        {
            _viewModel?.ReportStatus($"Imported configuration files for {importedCount} mod(s).");
            UpdateSelectedModButtons();
        }

        if (errors.Count > 0)
        {
            WpfMessageBox.Show(
                "Some configuration files could not be imported:\n" + string.Join("\n", errors),
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task<bool> ApplyPresetModVersionsAsync(ModPreset preset)
    {
        if (_viewModel?.ModsView is null)
        {
            return false;
        }

        var mods = _viewModel.ModsView.Cast<ModListItemViewModel>().ToList();
        var modLookup = mods.ToDictionary(mod => mod.ModId, StringComparer.OrdinalIgnoreCase);
        var overrides = new Dictionary<ModListItemViewModel, ModReleaseInfo>();
        var missingVersions = new List<string>();
        var missingMods = new List<string>();
        var installFailures = new List<string>();
        var installCandidates = new List<ModPresetModState>();

        foreach (var state in preset.ModStates)
        {
            if (!modLookup.TryGetValue(state.ModId, out ModListItemViewModel? mod))
            {
                installCandidates.Add(state);
                continue;
            }

            string? desiredVersion = string.IsNullOrWhiteSpace(state.Version) ? null : state.Version!.Trim();
            if (string.IsNullOrWhiteSpace(desiredVersion))
            {
                continue;
            }

            string? installedVersion = string.IsNullOrWhiteSpace(mod.Version) ? null : mod.Version!.Trim();

            if (VersionsMatch(desiredVersion, installedVersion))
            {
                continue;
            }

            string? desiredNormalized = VersionStringUtility.Normalize(desiredVersion);

            ModVersionOptionViewModel? option = mod.VersionOptions.FirstOrDefault(opt =>
                (!string.IsNullOrWhiteSpace(desiredVersion)
                    && string.Equals(opt.Version, desiredVersion, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(desiredNormalized)
                    && !string.IsNullOrWhiteSpace(opt.NormalizedVersion)
                    && string.Equals(opt.NormalizedVersion, desiredNormalized, StringComparison.OrdinalIgnoreCase)));

            if (option is null)
            {
                missingVersions.Add($"{mod.DisplayName} ({desiredVersion})");
                continue;
            }

            if (option.IsInstalled)
            {
                continue;
            }

            if (!option.HasRelease || option.Release is null)
            {
                string display = !string.IsNullOrWhiteSpace(option.Version)
                    ? option.Version
                    : desiredVersion ?? "Unknown";
                missingVersions.Add($"{mod.DisplayName} ({display})");
                continue;
            }

            overrides[mod] = option.Release;
        }

        bool installedAnyMods = false;
        List<string>? installedModIds = null;
        if (installCandidates.Count > 0)
        {
            foreach (var candidate in installCandidates)
            {
                var installResult = await TryInstallPresetModAsync(candidate).ConfigureAwait(true);
                if (installResult.Success)
                {
                    installedAnyMods = true;
                    if (!string.IsNullOrWhiteSpace(candidate.ModId))
                    {
                        installedModIds ??= new List<string>();
                        installedModIds.Add(candidate.ModId!.Trim());
                    }
                    continue;
                }

                string desiredVersion = string.IsNullOrWhiteSpace(candidate.Version)
                    ? "Unknown"
                    : candidate.Version!.Trim();

                if (installResult.ModMissing)
                {
                    string modDisplay = string.IsNullOrWhiteSpace(candidate.ModId)
                        ? "<unknown mod>"
                        : candidate.ModId!;
                    string display = string.IsNullOrWhiteSpace(installResult.ErrorMessage)
                        ? modDisplay
                        : $"{modDisplay} — {installResult.ErrorMessage}";
                    missingMods.Add(display);
                    continue;
                }

                if (installResult.VersionMissing)
                {
                    string modDisplay = string.IsNullOrWhiteSpace(candidate.ModId)
                        ? "<unknown mod>"
                        : candidate.ModId!;
                    missingVersions.Add($"{modDisplay} ({desiredVersion})");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(installResult.ErrorMessage))
                {
                    installFailures.Add($"{candidate.ModId}: {installResult.ErrorMessage}");
                }
            }

            if (installedAnyMods)
            {
                await RefreshModsAsync(installedModIds).ConfigureAwait(true);
            }
        }

        if (missingMods.Count > 0 || missingVersions.Count > 0 || installFailures.Count > 0)
        {
            var builder = new StringBuilder();

            if (missingMods.Count > 0)
            {
                builder.AppendLine("The following mods from the preset could not be installed:");
                foreach (string modId in missingMods.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    builder.AppendLine($" • {modId}");
                }
            }

            if (missingVersions.Count > 0)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.AppendLine("The following mod versions could not be located:");
                foreach (string entry in missingVersions.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    builder.AppendLine($" • {entry}");
                }
            }

            if (installFailures.Count > 0)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.AppendLine("Some mods failed to install:");
                foreach (string failure in installFailures.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    builder.AppendLine($" • {failure}");
                }
            }

            if (missingMods.Count > 0
                && !string.IsNullOrWhiteSpace(_recentLocalModBackupDirectory)
                && _recentLocalModBackupModNames is { Count: > 0 })
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine();
                }

                builder.AppendLine("Local copies of mods that are not on the mod database were saved to:");
                builder.AppendLine($" • {_recentLocalModBackupDirectory}");

                var distinctBackups = _recentLocalModBackupModNames
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (distinctBackups.Count > 0)
                {
                    builder.AppendLine("Backed up mods:");
                    foreach (string backupName in distinctBackups)
                    {
                        builder.AppendLine($"   • {backupName}");
                    }
                }
            }

            string message = builder.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(message))
            {
                WpfMessageBox.Show(message,
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        bool hasOverrides = overrides.Count > 0;
        if (hasOverrides)
        {
            await UpdateModsAsync(overrides.Keys.ToList(), isBulk: true, overrides, showSummary: false)
                .ConfigureAwait(true);
        }

        return installedAnyMods || hasOverrides;
    }

    private readonly record struct PresetModInstallResult(bool Success, bool ModMissing, bool VersionMissing, string? ErrorMessage);

    private async Task<PresetModInstallResult> TryInstallPresetModAsync(ModPresetModState state)
    {
        if (_viewModel is null)
        {
            return new PresetModInstallResult(false, false, false, "The mod view model is not available.");
        }

        string modId = state.ModId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(modId))
        {
            return new PresetModInstallResult(false, false, false, "The preset entry is missing a mod identifier.");
        }

        string? desiredVersion = string.IsNullOrWhiteSpace(state.Version) ? null : state.Version!.Trim();
        if (string.IsNullOrWhiteSpace(desiredVersion))
        {
            return new PresetModInstallResult(false, false, true, "No version was recorded for this mod.");
        }

        ModDatabaseInfo? info = await _modDatabaseService
            .TryLoadDatabaseInfoAsync(modId, desiredVersion, _viewModel.InstalledGameVersion, _userConfiguration.RequireExactVsVersionMatch)
            .ConfigureAwait(true);

        if (info is null)
        {
            return new PresetModInstallResult(false, true, false, "Mod not found on the mod database.");
        }

        string? desiredNormalized = VersionStringUtility.Normalize(desiredVersion);
        IReadOnlyList<ModReleaseInfo> releases = info.Releases ?? Array.Empty<ModReleaseInfo>();
        ModReleaseInfo? release = releases.FirstOrDefault(r =>
            (!string.IsNullOrWhiteSpace(r.Version)
                && string.Equals(r.Version.Trim(), desiredVersion, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(desiredNormalized)
                && !string.IsNullOrWhiteSpace(r.NormalizedVersion)
                && string.Equals(r.NormalizedVersion, desiredNormalized, StringComparison.OrdinalIgnoreCase)));

        if (release is null)
        {
            return new PresetModInstallResult(false, false, true, "The specified version could not be found on the mod database.");
        }

        if (!TryGetDependencyInstallTargetPath(modId, release, out string targetPath, out string? pathError))
        {
            return new PresetModInstallResult(false, false, false, pathError);
        }

        var descriptor = new ModUpdateDescriptor(
            modId,
            modId,
            release.DownloadUri,
            targetPath,
            false,
            release.FileName,
            release.Version,
            null);

        var progress = new Progress<ModUpdateProgress>(p =>
            _viewModel.ReportStatus($"{modId}: {p.Message}"));

        ModUpdateResult result = await _modUpdateService
            .UpdateAsync(descriptor, _userConfiguration.CacheAllVersionsLocally, progress)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            string message = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "The installation failed."
                : result.ErrorMessage!;
            return new PresetModInstallResult(false, false, false, message);
        }

        string versionSuffix = string.IsNullOrWhiteSpace(release.Version) ? string.Empty : $" {release.Version}";
        _viewModel.ReportStatus($"Installed {modId}{versionSuffix}.");

        return new PresetModInstallResult(true, false, false, null);
    }


    private async Task ApplyExclusivePresetAsync(ModPreset preset)
    {
        if (_viewModel?.ModsView is null)
        {
            return;
        }

        if (preset.ModStates.Count == 0)
        {
            return;
        }

        var keepSet = new HashSet<string>(
            preset.ModStates.Select(state => state.ModId),
            StringComparer.OrdinalIgnoreCase);

        if (keepSet.Count == 0)
        {
            return;
        }

        var installedMods = _viewModel.ModsView.Cast<ModListItemViewModel>().ToList();
        if (installedMods.Count == 0)
        {
            return;
        }

        var failures = new List<string>();
        int removedCount = 0;
        string? localBackupSessionDirectory = null;
        List<string>? backedUpModNames = null;
        bool localBackupInitializationFailed = false;

        foreach (var mod in installedMods)
        {
            if (!mod.IsInstalled)
            {
                continue;
            }

            if (keepSet.Contains(mod.ModId))
            {
                continue;
            }

            if (!TryGetManagedModPath(mod, out string modPath, out string? errorMessage))
            {
                if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    failures.Add($"{mod.DisplayName}: {errorMessage}");
                }

                continue;
            }

            bool sourceExists = Directory.Exists(modPath) || File.Exists(modPath);
            if (!mod.HasModDatabasePageLink && sourceExists && !localBackupInitializationFailed)
            {
                if (string.IsNullOrWhiteSpace(localBackupSessionDirectory))
                {
                    try
                    {
                        localBackupSessionDirectory = CreateLocalModBackupSessionDirectory();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
                    {
                        failures.Add($"Failed to prepare the local mod backup directory: {ex.Message}");
                        localBackupInitializationFailed = true;
                    }
                }

                if (!localBackupInitializationFailed && !string.IsNullOrWhiteSpace(localBackupSessionDirectory))
                {
                    try
                    {
                        string entryDirectory = GetLocalModBackupEntryDirectory(localBackupSessionDirectory, mod);
                        BackupLocalModAtPath(modPath, entryDirectory);

                        string? name = string.IsNullOrWhiteSpace(mod.DisplayName)
                            ? mod.ModId
                            : mod.DisplayName;

                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            backedUpModNames ??= new List<string>();
                            backedUpModNames.Add(name.Trim());
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
                    {
                        failures.Add($"{mod.DisplayName}: Failed to backup the local copy before deletion — {ex.Message}");
                    }
                }
            }

            try
            {
                if (Directory.Exists(modPath))
                {
                    Directory.Delete(modPath, recursive: true);
                    removedCount++;
                }
                else if (File.Exists(modPath))
                {
                    File.Delete(modPath);
                    removedCount++;
                }
                else
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{mod.DisplayName}: {ex.Message}");
                continue;
            }

            _userConfiguration.RemoveModConfigPath(mod.ModId, preserveHistory: true);
        }

        if (!string.IsNullOrWhiteSpace(localBackupSessionDirectory) && backedUpModNames is { Count: > 0 })
        {
            _recentLocalModBackupDirectory = localBackupSessionDirectory;
            _recentLocalModBackupModNames = backedUpModNames;
        }

        if (removedCount > 0)
        {
            await RefreshModsAsync().ConfigureAwait(true);

            string status = removedCount == 1
                ? "Removed 1 mod not in the preset."
                : $"Removed {removedCount} mods not in the preset.";
            _viewModel?.ReportStatus(status);
        }

        if (failures.Count > 0)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Some mods could not be removed:");
            foreach (string failure in failures.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($" • {failure}");
            }

            WpfMessageBox.Show(builder.ToString().Trim(),
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static bool VersionsMatch(string? desiredVersion, string? installedVersion)
    {
        if (string.IsNullOrWhiteSpace(desiredVersion) && string.IsNullOrWhiteSpace(installedVersion))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(desiredVersion) && !string.IsNullOrWhiteSpace(installedVersion))
        {
            if (string.Equals(desiredVersion.Trim(), installedVersion.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string? desiredNormalized = VersionStringUtility.Normalize(desiredVersion);
            string? installedNormalized = VersionStringUtility.Normalize(installedVersion);
            if (!string.IsNullOrWhiteSpace(desiredNormalized)
                && !string.IsNullOrWhiteSpace(installedNormalized)
                && string.Equals(desiredNormalized, installedNormalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void LatestVersionTextBlock_OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement frameworkElement)
        {
            return;
        }

        if (frameworkElement.ToolTip is not WpfToolTip toolTip)
        {
            return;
        }

        toolTip.PreviewMouseWheel -= ChangelogToolTip_OnPreviewMouseWheel;
        toolTip.PreviewMouseWheel += ChangelogToolTip_OnPreviewMouseWheel;
    }

    private void LatestVersionTextBlock_OnToolTipClosing(object sender, ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement frameworkElement)
        {
            return;
        }

        if (frameworkElement.ToolTip is not WpfToolTip toolTip)
        {
            return;
        }

        toolTip.PreviewMouseWheel -= ChangelogToolTip_OnPreviewMouseWheel;
    }

    private void ChangelogToolTip_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || sender is not WpfToolTip toolTip)
        {
            return;
        }

        if (toolTip.Content is not ScrollViewer scrollViewer)
        {
            return;
        }

        double lines = Math.Max(1, SystemParameters.WheelScrollLines);
        double deltaMultiplier = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine;
        double offsetChange = deltaMultiplier * lines * GetCurrentScrollMultiplier();
        if (Math.Abs(offsetChange) < double.Epsilon)
        {
            return;
        }

        double targetOffset = scrollViewer.VerticalOffset - offsetChange;
        double clampedOffset = Math.Max(0, Math.Min(targetOffset, scrollViewer.ScrollableHeight));
        scrollViewer.ScrollToVerticalOffset(clampedOffset);
        e.Handled = true;
    }

    private void ModsDataGrid_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject originalSource && IsDescendantOfToolTip(originalSource))
        {
            return;
        }

        if (sender is not DependencyObject dependencyObject)
        {
            return;
        }

        ScrollViewer? scrollViewer = ReferenceEquals(dependencyObject, ModsDataGrid)
            ? GetModsScrollViewer()
            : ReferenceEquals(dependencyObject, ModDatabaseCardsListView)
                ? GetModsScrollViewer()
                : FindDescendantScrollViewer(dependencyObject);

        if (scrollViewer is null)
        {
            scrollViewer = GetModsScrollViewer();
        }

        if (scrollViewer is null)
        {
            return;
        }

        double lines = Math.Max(1, SystemParameters.WheelScrollLines);
        double deltaMultiplier = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine;
        double offsetChange = deltaMultiplier * lines * GetCurrentScrollMultiplier();
        if (Math.Abs(offsetChange) < double.Epsilon)
        {
            return;
        }

        double targetOffset = scrollViewer.VerticalOffset - offsetChange;
        double clampedOffset = Math.Max(0, Math.Min(targetOffset, scrollViewer.ScrollableHeight));
        scrollViewer.ScrollToVerticalOffset(clampedOffset);
        e.Handled = true;
    }

    private static bool IsDescendantOfToolTip(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is System.Windows.Controls.ToolTip || source is Popup)
            {
                return true;
            }

            source = GetParent(source);
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        if (current is Visual visual)
        {
            DependencyObject? parent = VisualTreeHelper.GetParent(visual);
            if (parent != null)
            {
                return parent;
            }

            if (visual is FrameworkElement frameworkElement)
            {
                return frameworkElement.Parent;
            }
        }

        if (current is FrameworkContentElement contentElement)
        {
            return contentElement.Parent ?? contentElement.TemplatedParent;
        }

        return LogicalTreeHelper.GetParent(current);
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T match)
            {
                return match;
            }

            source = GetParent(source);
        }

        return null;
    }

    private double GetCurrentScrollMultiplier()
    {
        if (_viewModel?.SearchModDatabase == true && _viewModel.UseModDbDesignView)
        {
            return ModDbDesignScrollMultiplier;
        }

        return ModListScrollMultiplier;
    }

    private void AttachToModsView(ICollectionView modsView)
    {
        if (_modsCollection != null)
        {
            _modsCollection.CollectionChanged -= ModsView_OnCollectionChanged;
            _modsCollection = null;
        }

        _modsScrollViewer = null;
        _modDatabaseCardsScrollViewer = null;

        if (modsView is INotifyCollectionChanged notify)
        {
            _modsCollection = notify;
            notify.CollectionChanged += ModsView_OnCollectionChanged;
        }

        ClearSelection(resetAnchor: true);
    }

    private void ModsView_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.Invoke(() => ClearSelection(resetAnchor: true));
    }

    private void HandleModRowSelection(ModListItemViewModel mod)
    {
        if (_isApplyingPreset)
        {
            return;
        }

        bool isShiftPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool isCtrlPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (isShiftPressed)
        {
            if (_selectionAnchor is not { } anchor)
            {
                if (!isCtrlPressed)
                {
                    ClearSelection();
                }

                AddToSelection(mod);
                _selectionAnchor = mod;
                return;
            }

            bool anchorApplied = ApplyRangeSelection(anchor, mod, isCtrlPressed);
            if (!anchorApplied)
            {
                _selectionAnchor = mod;
            }

            return;
        }

        if (isCtrlPressed)
        {
            if (_selectedMods.Contains(mod))
            {
                RemoveFromSelection(mod);
                _selectionAnchor = mod;
            }
            else
            {
                AddToSelection(mod);
                _selectionAnchor = mod;
            }

            return;
        }

        ClearSelection();
        AddToSelection(mod);
        _selectionAnchor = mod;
    }

    private void SelectAllModsInCurrentView()
    {
        if (_isApplyingPreset)
        {
            return;
        }

        if (_viewModel?.SearchModDatabase == true)
        {
            return;
        }

        List<ModListItemViewModel> mods = GetModsInViewOrder();
        ClearSelection(resetAnchor: true);

        if (mods.Count == 0)
        {
            return;
        }

        foreach (ModListItemViewModel mod in mods)
        {
            AddToSelection(mod);
        }

        _selectionAnchor = mods[mods.Count - 1];
    }

    private bool ApplyRangeSelection(ModListItemViewModel start, ModListItemViewModel end, bool preserveExisting)
    {
        List<ModListItemViewModel> mods = GetModsInViewOrder();
        int startIndex = mods.IndexOf(start);
        int endIndex = mods.IndexOf(end);

        if (startIndex < 0 || endIndex < 0)
        {
            if (!preserveExisting)
            {
                ClearSelection();
            }

            AddToSelection(end);
            return false;
        }

        if (!preserveExisting)
        {
            ClearSelection();
        }

        if (startIndex > endIndex)
        {
            (startIndex, endIndex) = (endIndex, startIndex);
        }

        for (int i = startIndex; i <= endIndex; i++)
        {
            AddToSelection(mods[i]);
        }

        return true;
    }

    private List<ModListItemViewModel> GetModsInViewOrder()
    {
        ICollectionView? view = _viewModel?.CurrentModsView;
        if (view == null)
        {
            return new List<ModListItemViewModel>();
        }

        return view.Cast<ModListItemViewModel>().ToList();
    }

    private void AddToSelection(ModListItemViewModel mod)
    {
        if (_selectedMods.Contains(mod))
        {
            return;
        }

        _selectedMods.Add(mod);
        SubscribeToSelectedMod(mod);
        mod.IsSelected = true;
        UpdateSelectedModButtons();
    }

    private void RemoveFromSelection(ModListItemViewModel mod)
    {
        if (!_selectedMods.Remove(mod))
        {
            return;
        }

        mod.IsSelected = false;
        UnsubscribeFromSelectedMod(mod);
        UpdateSelectedModButtons();
    }

    private void ClearSelection(bool resetAnchor = false)
    {
        if (_selectedMods.Count > 0)
        {
            foreach (var mod in _selectedMods)
            {
                mod.IsSelected = false;
                UnsubscribeFromSelectedMod(mod);
            }

            _selectedMods.Clear();
        }

        if (resetAnchor)
        {
            _selectionAnchor = null;
        }

        UpdateSelectedModButtons();
    }

    private void RestoreSelectionFromSourcePaths(IReadOnlyList<string> sourcePaths, string? anchorSourcePath)
    {
        if (_viewModel is null || _viewModel.SearchModDatabase == true)
        {
            return;
        }

        var resolved = new List<ModListItemViewModel>(sourcePaths.Count);
        foreach (string path in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            ModListItemViewModel? current = _viewModel.FindModBySourcePath(path);
            if (current != null && !resolved.Contains(current))
            {
                resolved.Add(current);
            }
        }

        bool selectionChanged = resolved.Count != _selectedMods.Count;
        if (!selectionChanged)
        {
            for (int i = 0; i < resolved.Count; i++)
            {
                if (!ReferenceEquals(resolved[i], _selectedMods[i]))
                {
                    selectionChanged = true;
                    break;
                }
            }
        }

        if (!selectionChanged)
        {
            UpdateSelectionAnchorAfterRestore(resolved, anchorSourcePath);
            return;
        }

        foreach (var mod in _selectedMods)
        {
            mod.IsSelected = false;
            UnsubscribeFromSelectedMod(mod);
        }

        _selectedMods.Clear();

        foreach (var mod in resolved)
        {
            _selectedMods.Add(mod);
            mod.IsSelected = true;
            SubscribeToSelectedMod(mod);
        }

        UpdateSelectionAnchorAfterRestore(resolved, anchorSourcePath);
        UpdateSelectedModButtons();
    }

    private void UpdateSelectionAnchorAfterRestore(IReadOnlyList<ModListItemViewModel> selection, string? anchorSourcePath)
    {
        if (selection.Count == 0)
        {
            _selectionAnchor = null;
            return;
        }

        if (!string.IsNullOrWhiteSpace(anchorSourcePath))
        {
            foreach (var mod in selection)
            {
                if (string.Equals(mod.SourcePath, anchorSourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    _selectionAnchor = mod;
                    return;
                }
            }
        }

        _selectionAnchor = selection[selection.Count - 1];
    }

    private void SubscribeToSelectedMod(ModListItemViewModel mod)
    {
        if (_selectedModPropertyHandlers.ContainsKey(mod))
        {
            return;
        }

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (string.IsNullOrEmpty(args.PropertyName)
                || args.PropertyName == nameof(ModListItemViewModel.CanFixDependencyIssues)
                || args.PropertyName == nameof(ModListItemViewModel.HasDependencyIssues)
                || args.PropertyName == nameof(ModListItemViewModel.MissingDependencies)
                || args.PropertyName == nameof(ModListItemViewModel.DependencyHasErrors))
            {
                if (Dispatcher.CheckAccess())
                {
                    RefreshSelectedModFixButton(mod);
                }
                else
                {
                    Dispatcher.Invoke(() => RefreshSelectedModFixButton(mod));
                }
            }
        };

        mod.PropertyChanged += handler;
        _selectedModPropertyHandlers[mod] = handler;
    }

    private void UnsubscribeFromSelectedMod(ModListItemViewModel mod)
    {
        if (_selectedModPropertyHandlers.TryGetValue(mod, out var handler))
        {
            mod.PropertyChanged -= handler;
            _selectedModPropertyHandlers.Remove(mod);
        }
    }

    private void RefreshSelectedModFixButton(ModListItemViewModel mod)
    {
        if (_viewModel?.SearchModDatabase == true)
        {
            return;
        }

        if (_selectedMods.Count == 1 && ReferenceEquals(_selectedMods[0], mod))
        {
            UpdateSelectedModFixButton(mod);
        }
    }

    private void UpdateSelectedModButtons()
    {
        int selectionCount = _selectedMods.Count;
        ModListItemViewModel? singleSelection = selectionCount == 1 ? _selectedMods[0] : null;
        bool hasMultipleSelection = selectionCount > 1;

        if (hasMultipleSelection)
        {
            UpdateSelectedModInstallButton(null);
            UpdateSelectedModButton(SelectedModDatabasePageButton, null, requireModDatabaseLink: true);
            UpdateSelectedModButton(SelectedModUpdateButton, null, requireModDatabaseLink: false, requireUpdate: true);
            UpdateSelectedModEditConfigButton(null);
            UpdateSelectedModFixButton(null);

            if (SelectedModDeleteButton is not null)
            {
                bool allowDeletion = _viewModel?.SearchModDatabase != true;
                SelectedModDeleteButton.DataContext = null;
                SelectedModDeleteButton.Visibility = allowDeletion ? Visibility.Visible : Visibility.Collapsed;
                SelectedModDeleteButton.IsEnabled = allowDeletion;
            }
        }
        else if (_viewModel?.SearchModDatabase == true)
        {
            UpdateSelectedModButton(SelectedModDatabasePageButton, singleSelection, requireModDatabaseLink: true);
            UpdateSelectedModButton(SelectedModUpdateButton, null, requireModDatabaseLink: false, requireUpdate: true);
            UpdateSelectedModEditConfigButton(null);
            UpdateSelectedModButton(SelectedModDeleteButton, null, requireModDatabaseLink: false);
            UpdateSelectedModInstallButton(singleSelection);
            UpdateSelectedModFixButton(null);
        }
        else
        {
            UpdateSelectedModInstallButton(null);
            UpdateSelectedModButton(SelectedModDatabasePageButton, singleSelection, requireModDatabaseLink: true);
            UpdateSelectedModButton(SelectedModUpdateButton, singleSelection, requireModDatabaseLink: false, requireUpdate: true);
            UpdateSelectedModEditConfigButton(singleSelection);
            UpdateSelectedModButton(SelectedModDeleteButton, singleSelection, requireModDatabaseLink: false);
            UpdateSelectedModFixButton(singleSelection);
        }

        _viewModel?.SetSelectedMod(singleSelection, selectionCount);
    }

    private void UpdateSelectedModInstallButton(ModListItemViewModel? mod)
    {
        if (SelectedModInstallButton is null)
        {
            return;
        }

        if (_viewModel?.SearchModDatabase != true || mod is null)
        {
            SelectedModInstallButton.DataContext = null;
            SelectedModInstallButton.Visibility = Visibility.Collapsed;
            SelectedModInstallButton.IsEnabled = false;
            return;
        }

        SelectedModInstallButton.DataContext = mod;
        SelectedModInstallButton.Visibility = Visibility.Visible;
        SelectedModInstallButton.IsEnabled = mod.HasDownloadableRelease && !_isModUpdateInProgress;
    }

    private void UpdateSelectedModFixButton(ModListItemViewModel? mod)
    {
        if (SelectedModFixButton is null)
        {
            return;
        }

        if (mod is null || !mod.CanFixDependencyIssues || _isModUpdateInProgress)
        {
            SelectedModFixButton.DataContext = null;
            SelectedModFixButton.Visibility = Visibility.Collapsed;
            SelectedModFixButton.IsEnabled = false;
            return;
        }

        SelectedModFixButton.DataContext = mod;
        SelectedModFixButton.Visibility = Visibility.Visible;
        SelectedModFixButton.IsEnabled = true;
    }

    private static void UpdateSelectedModButton(WpfButton? button, ModListItemViewModel? mod, bool requireModDatabaseLink, bool requireUpdate = false)
    {
        if (button is null)
        {
            return;
        }

        if (mod is null
            || (requireModDatabaseLink && !mod.HasModDatabasePageLink)
            || (requireUpdate && !mod.CanUpdate))
        {
            button.DataContext = null;
            button.Visibility = Visibility.Collapsed;
            button.IsEnabled = false;
            return;
        }

        button.DataContext = mod;
        button.Visibility = Visibility.Visible;
        button.IsEnabled = true;
    }

    private void UpdateSelectedModEditConfigButton(ModListItemViewModel? mod)
    {
        if (SelectedModEditConfigButton is null)
        {
            return;
        }

        UpdateSelectedModButton(SelectedModEditConfigButton, mod, requireModDatabaseLink: false);

        if (SelectedModEditConfigButton.DataContext is not ModListItemViewModel context)
        {
            SelectedModEditConfigButton.ToolTip = null;
            SelectedModEditConfigButton.Content = "Edit Config";
            return;
        }

        bool hasConfigPath = !string.IsNullOrWhiteSpace(context.ModId)
            && _userConfiguration.TryGetModConfigPath(context.ModId, out string? path)
            && !string.IsNullOrWhiteSpace(path);

        SelectedModEditConfigButton.Content = hasConfigPath ? "Edit Config" : "Set Config...";
        SelectedModEditConfigButton.ToolTip = hasConfigPath
            ? $"Edit Config for {context.DisplayName}"
            : $"Set Config for {context.DisplayName}";
    }

    private bool ShouldIgnoreRowSelection(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase or ToggleButton || source is ToggleSwitch)
            {
                return true;
            }

            if (source is FrameworkElement { TemplatedParent: ToggleSwitch })
            {
                return true;
            }

            if (source is DataGridCell cell && ReferenceEquals(cell.Column, ActiveColumn))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private ScrollViewer? GetModsScrollViewer()
    {
        if (_viewModel?.SearchModDatabase == true && _viewModel.UseModDbDesignView)
        {
            if (_modDatabaseCardsScrollViewer != null)
            {
                return _modDatabaseCardsScrollViewer;
            }

            if (ModDatabaseCardsListView == null)
            {
                return null;
            }

            _modDatabaseCardsScrollViewer = FindDescendantScrollViewer(ModDatabaseCardsListView);
            return _modDatabaseCardsScrollViewer;
        }

        if (_modsScrollViewer != null)
        {
            return _modsScrollViewer;
        }

        if (ModsDataGrid == null)
        {
            return null;
        }

        _modsScrollViewer = FindDescendantScrollViewer(ModsDataGrid);
        return _modsScrollViewer;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject? current)
    {
        if (current is null)
        {
            return null;
        }

        if (current is ScrollViewer viewer)
        {
            return viewer;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(current);
        for (int i = 0; i < childCount; i++)
        {
            ScrollViewer? result = FindDescendantScrollViewer(VisualTreeHelper.GetChild(current, i));
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private async Task UpdateModsAsync(
        IReadOnlyList<ModListItemViewModel> mods,
        bool isBulk,
        IReadOnlyDictionary<ModListItemViewModel, ModReleaseInfo>? releaseOverrides = null,
        bool showSummary = true)
    {
        if (_viewModel is null || mods.Count == 0)
        {
            await RefreshDeleteCachedModsMenuHeaderAsync();
            return;
        }

        _isModUpdateInProgress = true;
        UpdateSelectedModButtons();

        try
        {
            var results = new List<ModUpdateOperationResult>();
            ModUpdateReleasePreference? bulkPreference = null;
            bool abortRequested = false;
            bool requiresRefresh = false;

            foreach (ModListItemViewModel mod in mods)
            {
                ModReleaseInfo? overrideRelease = null;
                bool hasOverride = releaseOverrides != null
                    && releaseOverrides.TryGetValue(mod, out overrideRelease);

                if (!hasOverride && !mod.CanUpdate)
                {
                    continue;
                }

                if (!TryGetManagedModPath(mod, out string modPath, out string? pathError))
                {
                    string message = string.IsNullOrWhiteSpace(pathError)
                        ? "The mod location could not be determined."
                        : pathError!;
                    results.Add(ModUpdateOperationResult.Failure(mod, message));
                    requiresRefresh = true;
                    continue;
                }

                int previousResultCount = results.Count;
                ModReleaseInfo? release = hasOverride
                    ? overrideRelease
                    : SelectReleaseForMod(mod, isBulk, ref bulkPreference, results, ref abortRequested);
                if (abortRequested)
                {
                    requiresRefresh = true;
                    break;
                }

                if (release is null)
                {
                    if (results.Count > previousResultCount)
                    {
                        requiresRefresh = true;
                    }
                    continue;
                }

                bool targetIsDirectory = Directory.Exists(modPath);

                if (!targetIsDirectory && !File.Exists(modPath) && mod.SourceKind == ModSourceKind.Folder)
                {
                    targetIsDirectory = true;
                }

                string targetPath = modPath;
                string? existingPath = null;

                if (!targetIsDirectory)
                {
                    if (!TryGetUpdateTargetPath(mod, release, modPath, out string resolvedPath, out string? targetError))
                    {
                        string failureMessage = string.IsNullOrWhiteSpace(targetError)
                            ? "The mod location could not be determined."
                            : targetError!;
                        results.Add(ModUpdateOperationResult.Failure(mod, failureMessage));
                        requiresRefresh = true;
                        continue;
                    }

                    targetPath = resolvedPath;
                    existingPath = modPath;
                }

                var descriptor = new ModUpdateDescriptor(
                    mod.ModId,
                    mod.DisplayName,
                    release.DownloadUri,
                    targetPath,
                    targetIsDirectory,
                    release.FileName,
                    release.Version,
                    mod.Version)
                {
                    ExistingPath = existingPath
                };

                var progress = new Progress<ModUpdateProgress>(p =>
                    _viewModel.ReportStatus($"{mod.DisplayName}: {p.Message}"));

                ModUpdateResult updateResult = await _modUpdateService
                    .UpdateAsync(descriptor, _userConfiguration.CacheAllVersionsLocally, progress)
                    .ConfigureAwait(true);

                if (!updateResult.Success)
                {
                    string failureMessage = string.IsNullOrWhiteSpace(updateResult.ErrorMessage)
                        ? "The update failed."
                        : updateResult.ErrorMessage!;
                    _viewModel.ReportStatus($"Failed to update {mod.DisplayName}: {failureMessage}", true);
                    results.Add(ModUpdateOperationResult.Failure(mod, failureMessage));
                    requiresRefresh = true;
                    continue;
                }

                requiresRefresh = true;
                _viewModel.ReportStatus($"Updated {mod.DisplayName} to {release.Version}.");
                await _viewModel.PreserveActivationStateAsync(mod.ModId, mod.Version, release.Version, mod.IsActive).ConfigureAwait(true);
                IReadOnlyList<ModListItemViewModel.ReleaseChangelog> appliedChangelogEntries =
                    mod.GetChangelogEntriesForUpgrade(release.Version);
                string? changelogSummary = BuildChangelogSummary(appliedChangelogEntries);
                results.Add(ModUpdateOperationResult.SuccessResult(mod, release.Version, mod.Version, changelogSummary));
            }

            if (requiresRefresh && _viewModel.RefreshCommand != null)
            {
                try
                {
                    await RefreshModsAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    WpfMessageBox.Show($"The mod list could not be refreshed after updating mods:{Environment.NewLine}{ex.Message}",
                        "Simple VS Manager",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }

            if (abortRequested)
            {
                _viewModel.ReportStatus(isBulk ? "Bulk update cancelled." : "Update cancelled.");
            }

            if (results.Count > 0 && showSummary)
            {
                if (isBulk)
                {
                    ShowBulkUpdateChangelogDialog(results);
                }

                ShowUpdateSummary(results, isBulk, abortRequested);
            }
            else if (abortRequested && showSummary)
            {
                WpfMessageBox.Show(isBulk ? "Bulk update cancelled." : "Update cancelled.",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        finally
        {
            _isModUpdateInProgress = false;
            UpdateSelectedModButtons();
        }

        await RefreshDeleteCachedModsMenuHeaderAsync();
    }

    private ModReleaseInfo? SelectReleaseForMod(
        ModListItemViewModel mod,
        bool isBulk,
        ref ModUpdateReleasePreference? bulkPreference,
        List<ModUpdateOperationResult> results,
        ref bool abortRequested)
    {
        ModReleaseInfo? latest = mod.LatestRelease;
        if (latest is null)
        {
            results.Add(ModUpdateOperationResult.SkippedResult(mod, "No downloadable release was found."));
            return null;
        }

        if (!mod.RequiresCompatibilitySelection)
        {
            if (!latest.IsCompatibleWithInstalledGame && mod.LatestCompatibleRelease == null)
            {
                if (bulkPreference.HasValue && bulkPreference.Value == ModUpdateReleasePreference.Latest)
                {
                    return latest;
                }

                if (!PromptInstallIncompatibleLatest(mod, isBulk, ref bulkPreference, out bool skipped, out bool aborted))
                {
                    if (aborted)
                    {
                        abortRequested = true;
                    }

                    if (skipped)
                    {
                        results.Add(ModUpdateOperationResult.SkippedResult(mod, "Skipped by user."));
                    }

                    return null;
                }
            }

            return latest;
        }

        if (bulkPreference.HasValue)
        {
            if (bulkPreference.Value == ModUpdateReleasePreference.LatestCompatible)
            {
                if (mod.LatestCompatibleRelease != null)
                {
                    return mod.LatestCompatibleRelease;
                }

                if (!PromptInstallIncompatibleLatest(mod, isBulk, ref bulkPreference, out bool skipped, out bool aborted))
                {
                    if (aborted)
                    {
                        abortRequested = true;
                    }

                    if (skipped)
                    {
                        results.Add(ModUpdateOperationResult.SkippedResult(mod, "Skipped by user."));
                    }

                    return null;
                }

                return latest;
            }

            return latest;
        }

        CompatibilityDecisionKind decision = PromptCompatibilityDecision(mod, isBulk);

        switch (decision)
        {
            case CompatibilityDecisionKind.Latest:
                return latest;
            case CompatibilityDecisionKind.LatestForAll:
                bulkPreference = ModUpdateReleasePreference.Latest;
                return latest;
            case CompatibilityDecisionKind.LatestCompatible:
                if (mod.LatestCompatibleRelease != null)
                {
                    return mod.LatestCompatibleRelease;
                }

                results.Add(ModUpdateOperationResult.Failure(mod, "No compatible release is available."));
                return null;
            case CompatibilityDecisionKind.LatestCompatibleForAll:
                if (mod.LatestCompatibleRelease != null)
                {
                    bulkPreference = ModUpdateReleasePreference.LatestCompatible;
                    return mod.LatestCompatibleRelease;
                }

                results.Add(ModUpdateOperationResult.Failure(mod, "No compatible release is available."));
                return null;
            case CompatibilityDecisionKind.Skip:
                results.Add(ModUpdateOperationResult.SkippedResult(mod, "Skipped by user."));
                return null;
            case CompatibilityDecisionKind.Abort:
                abortRequested = true;
                return null;
            default:
                return null;
        }
    }

    private CompatibilityDecisionKind PromptCompatibilityDecision(ModListItemViewModel mod, bool isBulk)
    {
        ModReleaseInfo? latest = mod.LatestRelease;
        ModReleaseInfo? compatible = mod.LatestCompatibleRelease;

        if (latest is null)
        {
            return CompatibilityDecisionKind.Skip;
        }

        bool hasCompatible = compatible != null
            && !string.Equals(latest.Version, compatible.Version, StringComparison.OrdinalIgnoreCase);

        string message = $"The latest release ({latest.Version}) for {mod.DisplayName} is not marked as compatible with your Vintage Story version.";

        if (hasCompatible)
        {
            string compatibleMessage = isBulk
                ? $"Choose whether to install the latest release ({latest.Version}) or the latest compatible release ({compatible!.Version})."
                : $"Select Yes to install the latest release or No to install the latest compatible release ({compatible!.Version}).";

            message = string.Concat(
                message,
                Environment.NewLine,
                Environment.NewLine,
                compatibleMessage);
        }
        else
        {
            string noCompatibleMessage = isBulk
                ? "No compatible alternative was found. Choose whether to install the latest release anyway or skip this mod."
                : "No compatible alternative was found. Do you want to install the latest release anyway?";

            message = string.Concat(
                message,
                Environment.NewLine,
                Environment.NewLine,
                noCompatibleMessage);
        }

        if (isBulk)
        {
            message = string.Concat(
                message,
                Environment.NewLine,
                Environment.NewLine,
                "Choose an option below. Selections that apply to all mods will be used for the remaining updates in this session.");

            var prompt = new BulkCompatibilityPromptWindow(
                message,
                latest.Version,
                hasCompatible ? compatible!.Version : null,
                hasCompatible);

            prompt.Owner = this;

            bool? dialogResult = prompt.ShowDialog();

            return dialogResult == true
                ? prompt.Decision
                : CompatibilityDecisionKind.Skip;
        }

        MessageBoxResult result = WpfMessageBox.Show(
            message,
            "Simple VS Manager",
            hasCompatible ? MessageBoxButton.YesNoCancel : MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        return result switch
        {
            MessageBoxResult.Yes => CompatibilityDecisionKind.Latest,
            MessageBoxResult.No => hasCompatible ? CompatibilityDecisionKind.LatestCompatible : CompatibilityDecisionKind.Skip,
            MessageBoxResult.Cancel => CompatibilityDecisionKind.Abort,
            _ => CompatibilityDecisionKind.Skip
        };
    }

    private bool PromptInstallIncompatibleLatest(
        ModListItemViewModel mod,
        bool isBulk,
        ref ModUpdateReleasePreference? bulkPreference,
        out bool skipped,
        out bool aborted)
    {
        skipped = false;
        aborted = false;

        ModReleaseInfo? latest = mod.LatestRelease;
        if (latest is null)
        {
            skipped = true;
            return false;
        }

        string message = $"The latest release ({latest.Version}) for {mod.DisplayName} is not marked as compatible with your Vintage Story version, and no compatible alternative was found.";

        if (isBulk)
        {
            string bulkMessage = string.Concat(
                message,
                Environment.NewLine,
                Environment.NewLine,
                "Choose whether to install the latest release anyway or skip this mod.",
                Environment.NewLine,
                Environment.NewLine,
                "Choose an option below. Selections that apply to all mods will be used for the remaining updates in this session.");

            var prompt = new BulkCompatibilityPromptWindow(bulkMessage, latest.Version, null, false);
            prompt.Owner = this;

            bool? dialogResult = prompt.ShowDialog();

            if (dialogResult == true)
            {
                switch (prompt.Decision)
                {
                    case CompatibilityDecisionKind.Latest:
                        return true;
                    case CompatibilityDecisionKind.LatestForAll:
                        bulkPreference = ModUpdateReleasePreference.Latest;
                        return true;
                    case CompatibilityDecisionKind.Skip:
                        skipped = true;
                        return false;
                    case CompatibilityDecisionKind.Abort:
                        aborted = true;
                        return false;
                }
            }

            skipped = true;
            return false;
        }

        string promptMessage = string.Concat(
            message,
            Environment.NewLine,
            Environment.NewLine,
            "Select Yes to install the latest release, No to skip this mod, or Cancel to stop updating.");

        MessageBoxResult result = WpfMessageBox.Show(
            promptMessage,
            "Simple VS Manager",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        switch (result)
        {
            case MessageBoxResult.Yes:
                return true;
            case MessageBoxResult.No:
                skipped = true;
                return false;
            case MessageBoxResult.Cancel:
                aborted = true;
                return false;
            default:
                skipped = true;
                return false;
        }
    }

    private void ShowBulkUpdateChangelogDialog(IReadOnlyList<ModUpdateOperationResult> results)
    {
        if (results is not { Count: > 0 })
        {
            return;
        }

        var items = new List<BulkUpdateChangelogWindow.BulkUpdateChangelogItem>();

        foreach (ModUpdateOperationResult result in results)
        {
            if (!result.Success)
            {
                continue;
            }

            string fromVersion = string.IsNullOrWhiteSpace(result.OldVersion)
                ? "Unknown"
                : result.OldVersion!;
            string toVersion = string.IsNullOrWhiteSpace(result.NewVersion)
                ? "Unknown"
                : result.NewVersion!;
            string title = $"{result.Mod.DisplayName} ({fromVersion} → {toVersion})";
            string changelog = string.IsNullOrWhiteSpace(result.ChangelogSummary)
                ? "No changelog entries were provided for this update."
                : result.ChangelogSummary!;
            items.Add(new BulkUpdateChangelogWindow.BulkUpdateChangelogItem(title, changelog));
        }

        if (items.Count == 0)
        {
            return;
        }

        var dialog = new BulkUpdateChangelogWindow(items)
        {
            Owner = this
        };

        dialog.ShowDialog();
    }

    private static string? BuildChangelogSummary(IReadOnlyList<ModListItemViewModel.ReleaseChangelog> changelogEntries)
    {
        if (changelogEntries is not { Count: > 0 })
        {
            return null;
        }

        var builder = new StringBuilder();

        for (int i = 0; i < changelogEntries.Count; i++)
        {
            ModListItemViewModel.ReleaseChangelog entry = changelogEntries[i];
            if (i > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine($"{entry.Version}:");
            builder.AppendLine(entry.Changelog);
        }

        return builder.ToString().TrimEnd();
    }

    private static void ShowUpdateSummary(IReadOnlyList<ModUpdateOperationResult> results, bool isBulk, bool aborted)
    {
        if (results.Count == 0)
        {
            return;
        }

        int successCount = results.Count(result => result.Success);
        int failureCount = results.Count(result => !result.Success && !result.Skipped);
        int skippedCount = results.Count(result => result.Skipped);

        if (!isBulk && failureCount == 0 && skippedCount == 0)
        {
            return;
        }

        if (isBulk && failureCount == 0 && skippedCount == 0 && !aborted)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine(isBulk ? "Bulk update completed." : "Update completed.");
        if (aborted)
        {
            builder.AppendLine("The operation was cancelled.");
        }

        builder.AppendLine($"Updated: {successCount}");

        if (failureCount > 0)
        {
            builder.AppendLine($"Failed: {failureCount}");
        }

        if (skippedCount > 0)
        {
            builder.AppendLine($"Skipped: {skippedCount}");
        }

        if (failureCount > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Failures:");
            foreach (var failure in results.Where(result => !result.Success && !result.Skipped))
            {
                builder.AppendLine($" • {failure.Mod.DisplayName}: {failure.Message}");
            }
        }

        if (skippedCount > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Skipped:");
            foreach (var skipped in results.Where(result => result.Skipped))
            {
                builder.AppendLine($" • {skipped.Mod.DisplayName}: {skipped.Message}");
            }
        }

        MessageBoxImage icon;
        if (isBulk)
        {
            icon = MessageBoxImage.None;
        }
        else
        {
            icon = failureCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information;
        }
        WpfMessageBox.Show(builder.ToString(), "Simple VS Manager", MessageBoxButton.OK, icon);
    }

    private enum ModUpdateReleasePreference
    {
        Latest,
        LatestCompatible
    }

    private readonly record struct ModUpdateOperationResult(
        ModListItemViewModel Mod,
        bool Success,
        bool Skipped,
        string Message,
        string? OldVersion,
        string? NewVersion,
        string? ChangelogSummary)
    {
        public static ModUpdateOperationResult SuccessResult(
            ModListItemViewModel mod,
            string newVersion,
            string? previousVersion,
            string? changelogSummary) =>
            new(mod, true, false, $"Updated to {newVersion}.", previousVersion, newVersion, changelogSummary);

        public static ModUpdateOperationResult Failure(ModListItemViewModel mod, string message) =>
            new(mod, false, false, message, mod.Version, null, null);

        public static ModUpdateOperationResult SkippedResult(ModListItemViewModel mod, string message) =>
            new(mod, false, true, message, mod.Version, null, null);
    }

    private void ActiveToggle_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ToggleSwitch toggleSwitch)
        {
            return;
        }

        if (!toggleSwitch.IsEnabled)
        {
            return;
        }

        e.Handled = true;

        toggleSwitch.Focus();
        toggleSwitch.IsOn = !toggleSwitch.IsOn;
    }

    private void ActiveToggle_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ToggleSwitch)
        {
            e.Handled = true;
        }
    }

    private void ActiveToggle_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not ToggleSwitch || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        e.Handled = true;
    }

    private void ActiveToggle_OnToggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingMultiToggle)
        {
            return;
        }

        if (sender is not ToggleSwitch { DataContext: ModListItemViewModel mod })
        {
            return;
        }

        if (!_selectedMods.Contains(mod) || _selectedMods.Count <= 1)
        {
            return;
        }

        bool desiredState = mod.IsActive;

        try
        {
            _isApplyingMultiToggle = true;

            foreach (var selected in _selectedMods)
            {
                if (ReferenceEquals(selected, mod))
                {
                    continue;
                }

                if (!selected.CanToggle || selected.IsActive == desiredState)
                {
                    continue;
                }

                selected.IsActive = desiredState;
            }
        }
        finally
        {
            _isApplyingMultiToggle = false;
        }
    }

    private void CheckBox_Checked(object sender, RoutedEventArgs e)
    {

    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {

    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        if (_isWindowActive)
        {
            return;
        }

        _isWindowActive = true;
        _viewModel?.TriggerLightweightModUpdateCheck();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        _isWindowActive = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        StopModsWatcher();
        base.OnClosed(e);
    }
}
