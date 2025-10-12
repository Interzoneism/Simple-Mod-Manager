using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using System.Windows.Threading;

using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Media.Animation;
using ModernWpf.Controls;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

using VintageStoryModManager.Models;
using VintageStoryModManager.Services;
using VintageStoryModManager.ViewModels;
using VintageStoryModManager.Views.Dialogs;
using WinForms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;
using WpfButton = System.Windows.Controls.Button;
using WpfMessageBox = VintageStoryModManager.Services.ModManagerMessageBox;
using WpfToolTip = System.Windows.Controls.ToolTip;

namespace VintageStoryModManager.Views;

public partial class MainWindow : Window
{
    private const double ModListScrollMultiplier = 0.5;
    private const double ModDbDesignScrollMultiplier = 20.0;
    private const double HoverOverlayOpacity = 0.1;
    private const double SelectionOverlayOpacity = 0.25;
    private const string ManagerModDatabaseUrl = "https://mods.vintagestory.at/simplevsmanager";
    private const string PresetDirectoryName = "Presets";
    private const string ModListDirectoryName = "Modlists";

    private readonly record struct PresetLoadOptions(bool ApplyModStatus, bool ApplyModVersions, bool ForceExclusive);

    private static readonly PresetLoadOptions StandardPresetLoadOptions = new(true, false, false);
    private static readonly PresetLoadOptions ModListLoadOptions = new(true, true, true);

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

    private readonly UserConfigurationService _userConfiguration;
    private MainViewModel? _viewModel;
    private string? _dataDirectory;
    private string? _gameDirectory;
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

        if (!TryInitializePaths())
        {
            WpfApplication.Current?.Shutdown();
            return;
        }

        try
        {
            InitializeViewModel();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Failed to initialize the mod manager:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            WpfApplication.Current?.Shutdown();
            return;
        }

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_OnClosing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        if (_viewModel != null)
        {
            await InitializeViewModelAsync(_viewModel);
        }

        await RefreshDeleteCachedModsMenuHeaderAsync();
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        SaveWindowDimensions();
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
            _userConfiguration.ModDatabaseNewModsRecentMonths)
        {
            IsCompactView = _userConfiguration.IsCompactView,
            UseModDbDesignView = _userConfiguration.UseModDbDesignView
        };
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        DataContext = _viewModel;
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
            }
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
            StartModsWatcher();
        }
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

    private async Task RefreshModsAsync()
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
        if (!TryValidateDataDirectory(_userConfiguration.DataDirectory, out _dataDirectory, out _))
        {
            TryValidateDataDirectory(DataDirectoryLocator.Resolve(), out _dataDirectory, out _);
        }

        if (_dataDirectory is null)
        {
            WpfMessageBox.Show("The Vintage Story data folder could not be located. Please select it to continue.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            _dataDirectory = PromptForDirectory(
                "Select your VintagestoryData folder",
                _userConfiguration.DataDirectory ?? DataDirectoryLocator.Resolve(),
                TryValidateDataDirectory,
                allowCancel: false);

            if (_dataDirectory is null)
            {
                return false;
            }
        }

        if (!TryValidateGameDirectory(_userConfiguration.GameDirectory, out _gameDirectory, out _))
        {
            TryValidateGameDirectory(GameDirectoryLocator.Resolve(), out _gameDirectory, out _);
        }

        if (_gameDirectory is null)
        {
            WpfMessageBox.Show("The Vintage Story installation folder could not be located. Please select it to continue.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            _gameDirectory = PromptForDirectory(
                "Select your Vintage Story installation folder",
                _userConfiguration.GameDirectory ?? GameDirectoryLocator.Resolve(),
                TryValidateGameDirectory,
                allowCancel: false);

            if (_gameDirectory is null)
            {
                return false;
            }
        }

        _userConfiguration.SetDataDirectory(_dataDirectory);
        _userConfiguration.SetGameDirectory(_gameDirectory);
        return true;
    }

    private async Task ReloadViewModelAsync()
    {
        if (string.IsNullOrWhiteSpace(_dataDirectory))
        {
            await RefreshDeleteCachedModsMenuHeaderAsync();
            return;
        }

        StopModsWatcher();

        try
        {
            var viewModel = new MainViewModel(
                _dataDirectory,
                _userConfiguration.ModDatabaseSearchResultLimit,
                _userConfiguration.ModDatabaseNewModsRecentMonths);
            _viewModel = viewModel;
            DataContext = viewModel;
            AttachToModsView(viewModel.CurrentModsView);
            await InitializeViewModelAsync(viewModel);
        }
        catch (Exception ex)
        {
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

    private void ModsDataGrid_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.A || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        if (_viewModel?.SearchModDatabase == true)
        {
            return;
        }

        SelectAllModsInCurrentView();
        e.Handled = true;
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
        }
    }

    private void ModsDataGridRow_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            ResetRowOverlays(row);
        }
    }

    private void ModsDataGridRow_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            ResetRowOverlays(row);
        }
    }

    private static void ResetRowOverlays(DataGridRow row)
    {
        row.ApplyTemplate();

        bool isModSelected = row.DataContext is ModListItemViewModel { IsSelected: true };

        if (row.Template?.FindName("SelectionOverlay", row) is Border selectionOverlay)
        {
            selectionOverlay.BeginAnimation(UIElement.OpacityProperty, null);

            double targetOpacity = isModSelected ? SelectionOverlayOpacity : 0;

            selectionOverlay.Opacity = targetOpacity;
        }

        if (row.Template?.FindName("HoverOverlay", row) is Border hoverOverlay)
        {
            hoverOverlay.BeginAnimation(UIElement.OpacityProperty, null);

            bool shouldShowHover = row.IsMouseOver && !isModSelected;
            hoverOverlay.Opacity = shouldShowHover ? HoverOverlayOpacity : 0;
        }
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

        IReadOnlyList<ModListItemViewModel> modsToDelete;

        if (button.DataContext is ModListItemViewModel mod)
        {
            modsToDelete = new[] { mod };
        }
        else if (_selectedMods.Count > 0)
        {
            modsToDelete = _selectedMods.ToList();
        }
        else
        {
            return;
        }

        e.Handled = true;

        if (modsToDelete.Count == 1)
        {
            await DeleteSingleModAsync(modsToDelete[0]);
        }
        else
        {
            await DeleteMultipleModsAsync(modsToDelete);
        }
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

        _userConfiguration.RemoveModConfigPath(mod.ModId);
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

        bool modHadLoadError = mod.HasLoadError;

        IReadOnlyList<ModDependencyInfo> dependencies = mod.Dependencies;
        if (dependencies.Count == 0)
        {
            WpfMessageBox.Show("This mod does not declare dependencies that can be fixed automatically.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _isModUpdateInProgress = true;
        UpdateSelectedModButtons();

        var failures = new List<string>();
        bool anySuccess = false;
        bool requiresRefresh = false;
        bool hasRefreshedAllMods = false;
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
                        requiresRefresh = true;
                        _viewModel.ReportStatus(result.Message);
                    }

                    continue;
                }

                if (installedDependency != null && !installedDependency.IsActive)
                {
                    installedDependency.IsActive = true;
                    anySuccess = true;
                    requiresRefresh = true;
                    _viewModel.ReportStatus($"Activated dependency {installedDependency.DisplayName}.");
                }
            }
        }
        finally
        {
            _isModUpdateInProgress = false;
            UpdateSelectedModButtons();
        }

        bool shouldRefreshErrorMods = !requiresRefresh && modHadLoadError && anySuccess;

        if (requiresRefresh)
        {
            try
            {
                hasRefreshedAllMods = true;
                await RefreshModsAsync();
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"The mod list could not be refreshed after fixing dependencies:{Environment.NewLine}{ex.Message}",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            UpdateSelectedModButtons();
        }
        else if (shouldRefreshErrorMods && _viewModel is { } viewModel)
        {
            try
            {
                await viewModel.RefreshModsWithErrorsAsync().ConfigureAwait(true);
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

        if (!hasRefreshedAllMods)
        {
            try
            {
                hasRefreshedAllMods = true;
                await RefreshModsAsync();
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"The mod list could not be refreshed after fixing dependencies:{Environment.NewLine}{ex.Message}",
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

            await RefreshModsAsync().ConfigureAwait(true);

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

        if (mods.Count == 0)
        {
            WpfMessageBox.Show("All mods are already up to date.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await UpdateModsAsync(mods, isBulk: true);
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
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Failed to launch Vintage Story:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
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

        IReadOnlyList<string> disabledEntries = _viewModel.GetCurrentDisabledEntries();
        IReadOnlyList<ModPresetModState> states = _viewModel.GetCurrentModStates();

        var serializable = new SerializablePreset
        {
            Name = entryName,
            DisabledEntries = disabledEntries.ToList(),
            IncludeModStatus = true,
            IncludeModVersions = includeModVersions ? true : null,
            Exclusive = exclusive ? true : null,
            Mods = states.Select(state => new SerializablePresetModState
            {
                ModId = state.ModId,
                Version = includeModVersions && !string.IsNullOrWhiteSpace(state.Version)
                    ? state.Version!.Trim()
                    : null,
                IsActive = state.IsActive
            }).ToList()
        };

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

    private void SaveModlistMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        TrySaveModlist();
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

        if (!TryLoadPresetFromFile(dialog.FileName, "Preset", StandardPresetLoadOptions, out ModPreset? preset, out string? errorMessage))
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
        await ApplyPresetAsync(loadedPreset);
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
            return;
        }

        if (prompt == MessageBoxResult.Yes)
        {
            if (!TrySaveModlist())
            {
                return;
            }
        }

        if (!TryLoadPresetFromFile(dialog.FileName, "Modlist", ModListLoadOptions, out ModPreset? preset, out string? errorMessage))
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
        await ApplyPresetAsync(loadedModlist);
        _viewModel?.ReportStatus($"Loaded modlist \"{loadedModlist.Name}\".");
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

            string name = !string.IsNullOrWhiteSpace(data.Name)
                ? data.Name!.Trim()
                : GetSnapshotNameFromFilePath(filePath, fallbackName);

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
                    modStates.Add(new ModPresetModState(modId, version, mod.IsActive));
                }
            }

            preset = new ModPreset(name, disabledEntries, modStates, includeStatus, includeVersions, exclusive);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private string EnsurePresetDirectory()
    {
        string baseDirectory = _userConfiguration.GetConfigurationDirectory();
        string presetDirectory = Path.Combine(baseDirectory, PresetDirectoryName);
        Directory.CreateDirectory(presetDirectory);
        return presetDirectory;
    }

    private string EnsureModListDirectory()
    {
        string baseDirectory = _userConfiguration.GetConfigurationDirectory();
        string modListDirectory = Path.Combine(baseDirectory, ModListDirectoryName);
        Directory.CreateDirectory(modListDirectory);
        return modListDirectory;
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

    private static string GetSnapshotNameFromFilePath(string filePath, string fallback)
    {
        string name = Path.GetFileNameWithoutExtension(filePath);
        return string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
    }

    private sealed class SerializablePreset
    {
        public string? Name { get; set; }
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
    }

    private async Task ApplyPresetAsync(ModPreset preset)
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
        }
        finally
        {
            _isApplyingPreset = false;
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
        if (installCandidates.Count > 0)
        {
            foreach (var candidate in installCandidates)
            {
                var installResult = await TryInstallPresetModAsync(candidate).ConfigureAwait(true);
                if (installResult.Success)
                {
                    installedAnyMods = true;
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
                await RefreshModsAsync().ConfigureAwait(true);
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

            _userConfiguration.RemoveModConfigPath(mod.ModId);
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
            UpdateSelectedModButton(SelectedModEditConfigButton, null, requireModDatabaseLink: false);
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
            UpdateSelectedModButton(SelectedModEditConfigButton, null, requireModDatabaseLink: false);
            UpdateSelectedModButton(SelectedModDeleteButton, null, requireModDatabaseLink: false);
            UpdateSelectedModInstallButton(singleSelection);
            UpdateSelectedModFixButton(null);
        }
        else
        {
            UpdateSelectedModInstallButton(null);
            UpdateSelectedModButton(SelectedModDatabasePageButton, singleSelection, requireModDatabaseLink: true);
            UpdateSelectedModButton(SelectedModUpdateButton, singleSelection, requireModDatabaseLink: false, requireUpdate: true);
            UpdateSelectedModButton(SelectedModEditConfigButton, singleSelection, requireModDatabaseLink: false);
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
