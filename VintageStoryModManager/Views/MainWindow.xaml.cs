using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

using System.Windows.Threading;

using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Media.Animation;
using ModernWpf.Controls;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

using SimpleVsManager.Cloud;
using VintageStoryModManager.Models;
using VintageStoryModManager.Services;
using VintageStoryModManager.ViewModels;
using VintageStoryModManager.Views.Dialogs;
using WinForms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;
using WpfButton = System.Windows.Controls.Button;
using WpfMessageBox = VintageStoryModManager.Services.ModManagerMessageBox;
using WpfToolTip = System.Windows.Controls.ToolTip;
using CloudModConfigOption = VintageStoryModManager.Views.Dialogs.CloudModlistDetailsDialog.CloudModConfigOption;
using FileRecycleOption = Microsoft.VisualBasic.FileIO.RecycleOption;
using FileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using FileUIOption = Microsoft.VisualBasic.FileIO.UIOption;

namespace VintageStoryModManager.Views;

public partial class MainWindow : Window
{
    private const double ModListScrollMultiplier = 0.5;
    private const double ModDbDesignScrollMultiplier = 20.0;
    private const double LoadMoreScrollThreshold = 0.98;
    private const double HoverOverlayOpacity = 0.1;
    private const double SelectionOverlayOpacity = 0.25;
    private const string ManagerModDatabaseUrl = "https://mods.vintagestory.at/simplevsmanager";
    private const string ManagerModDatabaseModId = "5545";
    private const string PresetDirectoryName = "Presets";
    private const string ModListDirectoryName = "Modlists";
    private const string CloudModListCacheDirectoryName = "Modlists (Cloud Cache)";
    private const string BackupDirectoryName = "Backups";
    private const int AutomaticConfigMaxWordDistance = 2;

    private readonly record struct PresetLoadOptions(bool ApplyModStatus, bool ApplyModVersions, bool ForceExclusive);

    private enum ModlistLoadMode
    {
        Replace,
        Add
    }

    private static readonly PresetLoadOptions StandardPresetLoadOptions = new(true, false, false);
    private static readonly PresetLoadOptions ModListLoadOptions = new(true, true, true);
    private readonly record struct ManagerDeletionResult(List<string> DeletedPaths, List<string> FailedPaths);

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
    private MainViewModel? _viewModel;
    private string? _dataDirectory;
    private string? _gameDirectory;
    private string? _customShortcutPath;
    private bool _isInitializing;
    private bool _isApplyingPreset;

    private DispatcherTimer? _modsWatcherTimer;
    private bool _isAutomaticRefreshRunning;

    private readonly List<ModListItemViewModel> _selectedMods = new();
    private readonly Dictionary<ModListItemViewModel, PropertyChangedEventHandler> _selectedModPropertyHandlers = new();
    private ModListItemViewModel? _selectionAnchor;
    private INotifyCollectionChanged? _modsCollection;
    private bool _isApplyingMultiToggle;
    private readonly ModDatabaseService _modDatabaseService = new();
    private readonly ModUpdateService _modUpdateService = new();
    private bool _isModUpdateInProgress;
    private bool _isDependencyResolutionRefreshPending;
    private ScrollViewer? _modsScrollViewer;
    private ScrollViewer? _modDatabaseCardsScrollViewer;
    private bool _suppressSortPreferenceSave;
    private string? _cachedSortMemberPath;
    private ListSortDirection? _cachedSortDirection;
    private SortOption? _cachedSortOption;
    private readonly SemaphoreSlim _backupSemaphore = new(1, 1);
    private readonly SemaphoreSlim _cloudStoreLock = new(1, 1);
    private FirebaseModlistStore? _cloudModlistStore;
    private bool _cloudModlistsLoaded;
    private bool _isCloudModlistRefreshInProgress;
    private CloudModlistListEntry? _selectedCloudModlist;


    public MainWindow()
    {
        InitializeComponent();

        _userConfiguration = new UserConfigurationService();
        ApplyStoredWindowDimensions();
        CacheAllVersionsMenuItem.IsChecked = _userConfiguration.CacheAllVersionsLocally;
        DisableInternetAccessMenuItem.IsChecked = _userConfiguration.DisableInternetAccess;
        InternetAccessManager.SetInternetAccessDisabled(_userConfiguration.DisableInternetAccess);
        EnableDebugLoggingMenuItem.IsChecked = _userConfiguration.EnableDebugLogging;
        StatusLogService.IsLoggingEnabled = _userConfiguration.EnableDebugLogging;

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

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        _userConfiguration.EnablePersistence();

        if (_viewModel != null)
        {
            await InitializeViewModelAsync(_viewModel).ConfigureAwait(true);
            await EnsureInstalledModsCachedAsync(_viewModel).ConfigureAwait(true);
            await CreateAppStartedBackupAsync().ConfigureAwait(true);
        }

        await RefreshDeleteCachedModsMenuHeaderAsync();
        await RefreshManagerUpdateLinkAsync();
    }

    private Task EnsureInstalledModsCachedAsync(MainViewModel viewModel)
    {
        if (viewModel is null || !_userConfiguration.CacheAllVersionsLocally)
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
        SaveWindowDimensions();
        SaveUploaderName();
        DisposeCurrentViewModel();
        InternetAccessManager.InternetAccessChanged -= InternetAccessManager_OnInternetAccessChanged;
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

    private void InitializeViewModel()
    {
        if (string.IsNullOrWhiteSpace(_dataDirectory))
        {
            throw new InvalidOperationException("The data directory is not set.");
        }

        _viewModel = new MainViewModel(
            _dataDirectory,
            _userConfiguration.ModDatabaseSearchResultLimit,
            _userConfiguration.ModDatabaseNewModsRecentMonths,
            _userConfiguration.ModDatabaseAutoLoadMode,
            gameDirectory: _gameDirectory,
            excludeInstalledModDatabaseResults: _userConfiguration.ExcludeInstalledModDatabaseResults)
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
            Dispatcher.Invoke(RefreshHoverOverlayState);
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
        }
    }

    private void ApplyCompactViewState(bool isCompactView)
    {
        if (IconColumn == null)
        {
            return;
        }

        if (_viewModel?.SearchModDatabase == true)
        {
            IconColumn.Visibility = Visibility.Collapsed;
            return;
        }

        IconColumn.Visibility = isCompactView ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateSearchColumnVisibility(bool isSearchingModDatabase)
    {
        Visibility visibility = isSearchingModDatabase ? Visibility.Collapsed : Visibility.Visible;

        if (ActiveColumn != null)
        {
            ActiveColumn.Visibility = visibility;
        }

        if (StatusColumn != null)
        {
            StatusColumn.Visibility = visibility;
        }

        if (VersionColumn != null)
        {
            VersionColumn.Visibility = visibility;
        }

        if (InstalledColumn != null)
        {
            InstalledColumn.Visibility = isSearchingModDatabase ? Visibility.Visible : Visibility.Collapsed;
        }

        if (DownloadsColumn != null)
        {
            DownloadsColumn.Visibility = isSearchingModDatabase ? Visibility.Visible : Visibility.Collapsed;
        }

        ApplyCompactViewState(_viewModel?.IsCompactView ?? false);
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

        string message =
            "In this tab you can easily save and load Modlists from an online database (Google Firebase), for free." +
            Environment.NewLine + Environment.NewLine +
            "When you continue, Simple VS Manager will create a firebase-auth.json (basically just a code that identifies you as the owner of your uploaded modlists) file in the AppData/Local/Simple VS Manager folder. " +
            "If you lose this file you will not be able to delete or modify your uploaded online modlists." +
            Environment.NewLine + Environment.NewLine +
            "You will not need to sign in or provide any account information or do anything really :) Press OK to continue and never show this again!";

        MessageBoxResult result = WpfMessageBox.Show(
            this,
            message,
            "Simple VS Manager",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information,
            buttonContentOverrides: buttonOverrides);

        return result == MessageBoxResult.OK;
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

    private void RestoreCachedSortState()
    {
        if (_viewModel is null)
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

        if (_viewModel.SelectedSortOption is { } selectedOption)
        {
            selectedOption.Apply(_viewModel.ModsView);
            UpdateSortPreferenceFromSelectedOption(persistPreference: false);
            return;
        }

        RestoreSortPreference();
    }

    private void RestoreSortPreference()
    {
        if (_viewModel is null)
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
                _userConfiguration.ModDatabaseSearchResultLimit,
                _userConfiguration.ModDatabaseNewModsRecentMonths,
                _userConfiguration.ModDatabaseAutoLoadMode,
                gameDirectory: _gameDirectory,
                excludeInstalledModDatabaseResults: _userConfiguration.ExcludeInstalledModDatabaseResults);
            newViewModel.PropertyChanged += ViewModelOnPropertyChanged;
            _viewModel = newViewModel;
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

    private async Task<bool> TryHandleModListKeyDownAsync(System.Windows.Input.KeyEventArgs e)
    {
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
            ClearRowOverlayAnimations(row);
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

        selectionOverlay?.BeginAnimation(UIElement.OpacityProperty, null);
        hoverOverlay?.BeginAnimation(UIElement.OpacityProperty, null);

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

    private static void ClearRowOverlayAnimations(DataGridRow row)
    {
        row.ApplyTemplate();

        if (row.Template?.FindName("SelectionOverlay", row) is Border selectionOverlay)
        {
            selectionOverlay.BeginAnimation(UIElement.OpacityProperty, null);
        }

        if (row.Template?.FindName("HoverOverlay", row) is Border hoverOverlay)
        {
            hoverOverlay.BeginAnimation(UIElement.OpacityProperty, null);
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

        await CreateAutomaticBackupAsync().ConfigureAwait(true);

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

        await CreateAutomaticBackupAsync().ConfigureAwait(true);

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

        await CreateAutomaticBackupAsync().ConfigureAwait(true);

        _isModUpdateInProgress = true;
        UpdateSelectedModButtons();

        try
        {
            var descriptor = new ModUpdateDescriptor(
                mod.ModId,
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
                .TryLoadDatabaseInfoAsync(dependency.ModId, installedMod?.Version, _viewModel?.InstalledGameVersion)
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

            if (installedMod != null)
            {
                if (!TryGetManagedModPath(installedMod, out targetPath, out string? pathError))
                {
                    return (false, pathError ?? "The mod path could not be determined.");
                }

                targetIsDirectory = Directory.Exists(targetPath);
                bool targetIsFile = File.Exists(targetPath);
                if (!targetIsDirectory && !targetIsFile && installedMod.SourceKind == ModSourceKind.Folder)
                {
                    targetIsDirectory = true;
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
                release.DownloadUri,
                targetPath,
                targetIsDirectory,
                release.FileName,
                release.Version,
                installedMod?.Version);

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

    private async void UpdateAllModsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
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

        await CreateAutomaticBackupAsync().ConfigureAwait(true);
        await UpdateModsAsync(mods, isBulk: true, overrides);
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

    private async void DeleteAllManagerFilesMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        const string confirmationMessage =
            "This will move every file Simple VS Manager created to the Recycle Bin, including its Documents folder, any ModData backups, AppData entries, cached mods, presets, and Firebase authentication tokens.\n\n" +
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

    private async void RefreshModsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.RefreshCommand == null)
        {
            return;
        }

        try
        {
            await RefreshModsAsync();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Failed to refresh mods:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
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
    }

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void LaunchGameButton_OnClick(object sender, RoutedEventArgs e)
    {
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
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Failed to open the {description} folder:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
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

    private Task CreateAutomaticBackupAsync()
    {
        DateTime timestamp = DateTime.Now;
        string formattedTimestamp = timestamp.ToString("dd MMM yyyy '•' HH.mm '•' ss's'", CultureInfo.InvariantCulture);
        string displayName = $"Backup - {formattedTimestamp}";
        return CreateBackupAsync(
            displayName,
            fallbackFileName: "Backup",
            pruneAutomaticBackups: true,
            pruneAppStartedBackups: false);
    }

    private Task CreateAppStartedBackupAsync()
    {
        DateTime timestamp = DateTime.Now;
        string formattedTimestamp = timestamp.ToString("dd MMM yyyy '•' HH.mm '•' ss's'", CultureInfo.InvariantCulture);
        string displayName = $"Backup - {formattedTimestamp}_AppStarted";
        return CreateBackupAsync(
            displayName,
            fallbackFileName: "Backup_AppStarted",
            pruneAutomaticBackups: false,
            pruneAppStartedBackups: true);
    }

    private async Task CreateBackupAsync(
        string displayName,
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
                CaptureConfigurationsForBackup();

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

    private IReadOnlyDictionary<string, ModConfigurationSnapshot>? CaptureConfigurationsForBackup()
    {
        if (_viewModel is null)
        {
            return null;
        }

        IReadOnlyList<ModListItemViewModel> mods = _viewModel.GetInstalledModsSnapshot();
        if (mods.Count == 0)
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
        return name.EndsWith("_AppStarted", StringComparison.OrdinalIgnoreCase);
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
            string? trimmedVersion = NormalizeCloudVersion(version);

            CloudModlistSlot? replacementSlot = null;
            string? slotKey = null;

            CloudModlistSlot? matchingSlot = slots.FirstOrDefault(slot =>
                slot.IsOccupied
                && string.Equals((slot.Name ?? string.Empty).Trim(), trimmedModlistName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeCloudVersion(slot.Version), trimmedVersion, StringComparison.OrdinalIgnoreCase));

            if (matchingSlot is not null)
            {
                string slotLabel = FormatCloudSlotLabel(matchingSlot.SlotKey);
                string versionSuffix = trimmedVersion is null ? string.Empty : $" (v{trimmedVersion})";
                MessageBoxResult replaceExisting = WpfMessageBox.Show(
                    $"A cloud modlist named \"{trimmedModlistName}\"{versionSuffix} already exists in {slotLabel}. Do you want to replace it?",
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

        _viewModel.ShowInstalledModsCommand.Execute(null);

        ModlistLoadMode? loadMode = PromptModlistLoadMode();
        if (loadMode is not ModlistLoadMode mode)
        {
            return;
        }

        if (mode == ModlistLoadMode.Replace && !EnsureModlistBackupBeforeLoad())
        {
            return;
        }

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

        await CreateAutomaticBackupAsync().ConfigureAwait(true);
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
            await CreateAutomaticBackupAsync().ConfigureAwait(true);
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
        await CreateAutomaticBackupAsync().ConfigureAwait(true);
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
                entry.ContentJson));
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
            ModDatabaseInfo? info = await _modDatabaseService
                .TryLoadDatabaseInfoAsync(ManagerModDatabaseModId, currentVersion, null)
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
        if (_viewModel is null || _isApplyingPreset)
        {
            return;
        }

        _isApplyingPreset = true;
        try
        {
            if (preset.IncludesModVersions && preset.ModStates.Count > 0)
            {
                await ApplyPresetModVersionsAsync(preset);
            }

            bool applied = await _viewModel.ApplyPresetAsync(preset);
            if (applied)
            {
                if (preset.IsExclusive)
                {
                    await ApplyExclusivePresetAsync(preset);
                }

                _viewModel.SelectedSortOption?.Apply(_viewModel.ModsView);
                _viewModel.ModsView.Refresh();
            }

            if (importConfigurations)
            {
                await ImportPresetConfigsAsync(preset).ConfigureAwait(true);
            }
        }
        finally
        {
            _isApplyingPreset = false;
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

    private async Task ApplyPresetModVersionsAsync(ModPreset preset)
    {
        if (_viewModel?.ModsView is null)
        {
            return;
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

            string message = builder.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(message))
            {
                WpfMessageBox.Show(message,
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        if (overrides.Count == 0)
        {
            return;
        }

        await UpdateModsAsync(overrides.Keys.ToList(), isBulk: true, overrides, showSummary: false);
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
            .TryLoadDatabaseInfoAsync(modId, desiredVersion, _viewModel.InstalledGameVersion)
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
            bool anySuccess = false;

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
                    continue;
                }

                ModReleaseInfo? release = hasOverride
                    ? overrideRelease
                    : SelectReleaseForMod(mod, isBulk, ref bulkPreference, results, ref abortRequested);
                if (abortRequested)
                {
                    break;
                }

                if (release is null)
                {
                    continue;
                }

                bool targetIsDirectory = Directory.Exists(modPath);
                bool targetIsFile = File.Exists(modPath);

                if (!targetIsDirectory && !targetIsFile && mod.SourceKind == ModSourceKind.Folder)
                {
                    targetIsDirectory = true;
                }

                var descriptor = new ModUpdateDescriptor(
                    mod.ModId,
                    release.DownloadUri,
                    modPath,
                    targetIsDirectory,
                    release.FileName,
                    release.Version,
                    mod.Version);

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
                    continue;
                }

                anySuccess = true;
                _viewModel.ReportStatus($"Updated {mod.DisplayName} to {release.Version}.");
                await _viewModel.PreserveActivationStateAsync(mod.ModId, mod.Version, release.Version, mod.IsActive).ConfigureAwait(true);
                results.Add(ModUpdateOperationResult.SuccessResult(mod, release.Version));
            }

            if (anySuccess && _viewModel.RefreshCommand != null)
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

    private readonly record struct ModUpdateOperationResult(ModListItemViewModel Mod, bool Success, bool Skipped, string Message)
    {
        public static ModUpdateOperationResult SuccessResult(ModListItemViewModel mod, string version) =>
            new(mod, true, false, $"Updated to {version}.");

        public static ModUpdateOperationResult Failure(ModListItemViewModel mod, string message) =>
            new(mod, false, false, message);

        public static ModUpdateOperationResult SkippedResult(ModListItemViewModel mod, string message) =>
            new(mod, false, true, message);
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

    protected override void OnClosed(EventArgs e)
    {
        StopModsWatcher();
        base.OnClosed(e);
    }
}
