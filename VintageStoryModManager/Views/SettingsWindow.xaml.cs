using ModernWpf.Controls;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VintageStoryModManager.Services;

namespace VintageStoryModManager.Views;

/// <summary>
/// A modern, centralized settings window. Each control drives the matching option in the
/// main window's menus so that all persistence and validation logic stays in a single place.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MainWindow _mainWindow;
    private readonly Dictionary<ToggleSwitch, MenuItem> _toggleMap = new();
    private bool _isSyncing;

    public SettingsWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));

        InitializeComponent();

        // Share the main view model so the compact-view toggle binds to the same state.
        DataContext = mainWindow.DataContext;

        InitializeState();
    }

    private void InitializeState()
    {
        _isSyncing = true;

        var config = _mainWindow.UserConfiguration;

        Register(SwDisableAutoRefresh, _mainWindow.DisableAutoRefreshMenuItem, config.DisableAutoRefresh);
        Register(SwFasterThumbnails, _mainWindow.UseFasterThumbnailsMenuItem, config.UseFasterThumbnails);
        Register(SwCacheModDb, _mainWindow.CacheModDatabaseResultsMenuItem, config.CacheModDatabaseResults);
        Register(SwCacheAllVersions, _mainWindow.CacheAllVersionsMenuItem, config.CacheAllVersionsLocally);
        Register(SwDisableHover, _mainWindow.DisableHoverEffectsMenuItem, config.DisableHoverEffects);
        Register(SwRequireExactVs, _mainWindow.RequireExactVsVersionMenuItem, config.RequireExactVsVersionMatch);
        Register(SwAutoDataBackup, _mainWindow.AutomaticDataBackupsMenuItem, config.AutomaticDataBackupsEnabled);
        Register(SwCloseManagerAfterGameLaunch, _mainWindow.CloseManagerAfterGameLaunchMenuItem,
            config.CloseManagerAfterGameLaunch);
        Register(SwAlwaysClear, _mainWindow.AlwaysClearModlistsMenuItem,
            config.ModlistAutoLoadBehavior == ModlistAutoLoadBehavior.Replace);
        Register(SwAlwaysAdd, _mainWindow.AlwaysAddModlistsMenuItem,
            config.ModlistAutoLoadBehavior == ModlistAutoLoadBehavior.Add);
        Register(SwDisableInternet, _mainWindow.DisableInternetAccessMenuItem, config.DisableInternetAccess);
        Register(SwEnableServerOptions, _mainWindow.EnableServerOptionsMenuItem, config.EnableServerOptions);
        Register(SwLogUpdate, _mainWindow.LogModUpdateMenuItem, config.LogModUpdates);
        Register(SwLogInstall, _mainWindow.LogModInstallMenuItem, config.LogModInstalls);
        Register(SwLogDeletion, _mainWindow.LogModDeletionMenuItem, config.LogModDeletions);
        Register(SwLogAppLifecycle, _mainWindow.LogAppLifecycleMenuItem, config.LogAppLaunchAndExit);
        Register(SwLogErrors, _mainWindow.LogErrorsAndExceptionsMenuItem, config.LogErrorsAndExceptions);

        switch (config.ColorTheme)
        {
            case ColorTheme.VintageStory:
                RbThemeVintage.IsChecked = true;
                break;
            case ColorTheme.Dark:
                RbThemeDark.IsChecked = true;
                break;
            case ColorTheme.Light:
                RbThemeLight.IsChecked = true;
                break;
        }

        VersionTextBlock.Text = _mainWindow.ManagerVersionMenuItem?.Header?.ToString() ?? "Version: --";

        _isSyncing = false;
    }

    private void Register(ToggleSwitch toggle, MenuItem menuItem, bool value)
    {
        _toggleMap[toggle] = menuItem;
        toggle.IsOn = value;
    }

    // The custom ToggleSwitch template used across the app omits ModernWpf's built-in
    // interactive parts, so a click is turned into a state change manually (the installed
    // mods grid does the same). Flipping IsOn then raises the Toggled event below.
    private void SettingToggle_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ToggleSwitch toggleSwitch || !toggleSwitch.IsEnabled) return;

        e.Handled = true;
        toggleSwitch.Focus();
        toggleSwitch.IsOn = !toggleSwitch.IsOn;
    }

    private void Toggle_OnToggled(object sender, RoutedEventArgs e)
    {
        if (_isSyncing) return;
        if (sender is not ToggleSwitch toggle) return;
        if (!_toggleMap.TryGetValue(toggle, out var menuItem)) return;

        // Drive the existing menu handler so persistence and validation are reused.
        menuItem.IsChecked = toggle.IsOn;
        InvokeMenuItem(menuItem);

        // The handler may adjust related state (reverts, mutual exclusion). Reflect it back.
        ResyncToggles();
    }

    private void ResyncToggles()
    {
        _isSyncing = true;

        foreach (var pair in _toggleMap)
            pair.Key.IsOn = pair.Value.IsChecked;

        _isSyncing = false;
    }

    private void ThemeRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_isSyncing) return;
        if (sender is not System.Windows.Controls.RadioButton radioButton) return;

        MenuItem? menuItem = radioButton.Name switch
        {
            nameof(RbThemeVintage) => _mainWindow.VintageStoryThemeMenuItem,
            nameof(RbThemeDark) => _mainWindow.DarkThemeMenuItem,
            nameof(RbThemeLight) => _mainWindow.LightThemeMenuItem,
            _ => null
        };

        if (menuItem is null) return;

        menuItem.IsChecked = true;
        InvokeMenuItem(menuItem);
    }

    private void BtnEditTheme_OnClick(object sender, RoutedEventArgs e) =>
        InvokeMenuItem(_mainWindow.EditThemeMenuItem);

    private void BtnSetDataFolder_OnClick(object sender, RoutedEventArgs e) =>
        InvokeMenuItem(_mainWindow.SelectDataFolderMenuItem);

    private void BtnSetGameFolder_OnClick(object sender, RoutedEventArgs e) =>
        InvokeMenuItem(_mainWindow.SelectGameFolderMenuItem);

    private void BtnChangeManagerFolder_OnClick(object sender, RoutedEventArgs e) =>
        InvokeMenuItem(_mainWindow.ChangeManagerFolderMenuItem);

    private void BtnSetShortcut_OnClick(object sender, RoutedEventArgs e) =>
        InvokeMenuItem(_mainWindow.SetCustomShortcutMenuItem);

    private void BtnMenuGuide_OnClick(object sender, RoutedEventArgs e) =>
        InvokeMenuItem(_mainWindow.GuideMenuItem);

    private void BtnHelp_OnClick(object sender, RoutedEventArgs e) =>
        InvokeMenuItem(_mainWindow.HelpMenuItem);

    private void BtnModDbPage_OnClick(object sender, RoutedEventArgs e) =>
        InvokeMenuItem(_mainWindow.ManagerModDbPageMenuItem);

    private void BtnClearCache_OnClick(object sender, RoutedEventArgs e) =>
        InvokeMenuItem(_mainWindow.ClearAllCachesMenuItem);

    private void BtnClose_OnClick(object sender, RoutedEventArgs e) => Close();

    private static void InvokeMenuItem(MenuItem? menuItem)
    {
        menuItem?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menuItem));
    }
}
