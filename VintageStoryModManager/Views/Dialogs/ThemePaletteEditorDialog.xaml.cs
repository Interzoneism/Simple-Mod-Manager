using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VintageStoryModManager.Services;
using Color = System.Windows.Media.Color;
using DrawingColor = System.Drawing.Color;
using FormsColorDialog = System.Windows.Forms.ColorDialog;
using FormsDialogResult = System.Windows.Forms.DialogResult;
using FormsIWin32Window = System.Windows.Forms.IWin32Window;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using WpfMessageBox = VintageStoryModManager.Services.ModManagerMessageBox;

namespace VintageStoryModManager.Views.Dialogs;

public partial class ThemePaletteEditorDialog : Window, INotifyPropertyChanged
{
    private static readonly IReadOnlyDictionary<string, PaletteDisplayInfo> PaletteDisplayInfos =
        new Dictionary<string, PaletteDisplayInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["Palette.BaseSurface.Shadowed"] =
                new("Palette.BaseSurface.Shadowed", "Main background", 33),
            ["Palette.Text.Primary"] = new("Palette.Text.Primary", "Primary text", 24),
            ["Palette.Accent.Primary"] = new("Palette.Accent.Primary", "Accent color", 24),
            ["Palette.BaseSurface.Raised"] = new("Palette.BaseSurface.Raised", "Panel background", 19),
            ["Palette.BaseSurface.HoverGlow"] = new("Palette.BaseSurface.HoverGlow", "Hover highlight", 14),
            ["Palette.Interactive.Surface"] = new("Palette.Interactive.Surface", "Active controls", 13),
            ["Palette.Interactive.DisabledSurface"] =
                new("Palette.Interactive.DisabledSurface", "Disabled controls", 11),
            ["Palette.Bevel.Highlight"] = new("Palette.Bevel.Highlight", "Highlight edges", 11),
            ["Palette.BaseSurface.Brighter"] = new("Palette.BaseSurface.Brighter", "Secondary background", 7),
            ["Palette.Bevel.Shadow"] = new("Palette.Bevel.Shadow", "Shadow edges", 7),
            ["Palette.White"] = new("Palette.White", "Bright highlight", 6),
            ["Palette.DarkGrey"] = new("Palette.DarkGrey", "Dark neutral", 6),
            ["Palette.Overlay.HoverTint"] = new("Palette.Overlay.HoverTint", "Hover overlay", 6),
            ["Palette.Text.Link"] = new("Palette.Text.Link", "Link text", 5),
            ["Palette.Grey"] = new("Palette.Grey", "Neutral grey", 5),
            ["Palette.Error"] = new("Palette.Error", "Error state", 5)
        };

    private readonly UserConfigurationService _configuration;
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _themePaletteSnapshots =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _isInitializing;
    private bool _isSwitchingAfterSave;
    private ThemeOption? _selectedThemeOption;

    public ThemePaletteEditorDialog(UserConfigurationService configuration)
    {
        InitializeComponent();
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        PaletteItems = new ObservableCollection<PaletteColorEntry>();
        ThemeOptions = new ObservableCollection<ThemeOption>();
        DataContext = this;
        _isInitializing = true;
        RefreshThemeOptions();
        CaptureThemePaletteSnapshots();
        SelectedThemeOption ??= ThemeOptions.FirstOrDefault();
        LoadPalette();
        UpdateResetAvailability();
        _isInitializing = false;
    }

    public ObservableCollection<PaletteColorEntry> PaletteItems { get; }

    public ObservableCollection<ThemeOption> ThemeOptions { get; }

    public ThemeOption? SelectedThemeOption
    {
        get => _selectedThemeOption;
        set
        {
            if (!SetProperty(ref _selectedThemeOption, value)) return;

            UpdateResetAvailability();
        }
    }

    private void CaptureThemePaletteSnapshots()
    {
        _themePaletteSnapshots.Clear();

        foreach (var name in _configuration.GetAllThemeNames())
            if (_configuration.TryGetThemePalette(name, out var palette))
                _themePaletteSnapshots[name] =
                    new Dictionary<string, string>(palette, StringComparer.OrdinalIgnoreCase);

        var currentName = _configuration.GetCurrentThemeName();
        if (!_themePaletteSnapshots.ContainsKey(currentName)
            && _configuration.TryGetThemePalette(currentName, out var currentPalette))
            _themePaletteSnapshots[currentName] =
                new Dictionary<string, string>(currentPalette, StringComparer.OrdinalIgnoreCase);
    }

    private void LoadPalette()
    {
        PaletteItems.Clear();

        foreach (var pair in _configuration.GetThemePaletteColors()
                     .OrderByDescending(entry => GetPaletteUsageCount(entry.Key))
                     .ThenBy(entry => GetPaletteDisplayName(entry.Key), StringComparer.OrdinalIgnoreCase))
        {
            var displayName = GetPaletteDisplayName(pair.Key);
            PaletteItems.Add(new PaletteColorEntry(pair.Key, displayName, pair.Value, PickColor));
        }
    }

    private void RefreshThemeOptions()
    {
        _isInitializing = true;
        var currentName = SelectedThemeOption?.Name ?? _configuration.GetCurrentThemeName();

        ThemeOptions.Clear();

        // Add built-in themes (these might have custom overrides)
        ThemeOptions.Add(new ThemeOption(
            UserConfigurationService.GetThemeDisplayName(ColorTheme.VintageStory),
            ColorTheme.VintageStory,
            _configuration));
        ThemeOptions.Add(new ThemeOption(
            UserConfigurationService.GetThemeDisplayName(ColorTheme.Dark), 
            ColorTheme.Dark,
            _configuration));
        ThemeOptions.Add(new ThemeOption(
            UserConfigurationService.GetThemeDisplayName(ColorTheme.Light), 
            ColorTheme.Light,
            _configuration));

        // Add custom themes that don't match built-in theme names
        foreach (var name in _configuration.GetCustomThemeNames())
        {
            if (!ThemeOption.IsBuiltInThemeName(name))
                ThemeOptions.Add(new ThemeOption(name, null, _configuration));
        }

        if (_configuration.ColorTheme == ColorTheme.Custom
            && ThemeOptions.All(option => !string.Equals(option.Name, currentName, StringComparison.OrdinalIgnoreCase)))
            ThemeOptions.Add(new ThemeOption(currentName, null, _configuration));

        SelectedThemeOption = ThemeOptions.FirstOrDefault(option =>
                                  string.Equals(option.Name, currentName, StringComparison.OrdinalIgnoreCase))
                              ?? SelectedThemeOption;
        _isInitializing = false;
        UpdateResetAvailability();
    }

    private void PickColor(PaletteColorEntry? entry)
    {
        if (entry is null) return;

        using var dialog = new FormsColorDialog
        {
            FullOpen = true,
            AnyColor = true
        };

        if (TryParseColor(entry.HexValue, out var currentColor))
            dialog.Color = DrawingColor.FromArgb(currentColor.A, currentColor.R, currentColor.G, currentColor.B);

        var ownerHandle = new WindowInteropHelper(this).Handle;
        var result = ownerHandle != IntPtr.Zero
            ? dialog.ShowDialog(new Win32Window(ownerHandle))
            : dialog.ShowDialog();

        if (result != FormsDialogResult.OK) return;

        var selected = dialog.Color;
        var hex = $"#{selected.A:X2}{selected.R:X2}{selected.G:X2}{selected.B:X2}";

        ApplyPaletteEntry(entry, hex);
    }

    private void ApplyPaletteEntry(PaletteColorEntry entry, string hexValue)
    {
        if (!_configuration.TrySetThemePaletteColor(entry.Key, hexValue))
        {
            WpfMessageBox.Show(
                this,
                "Please enter a colour in #RRGGBB or #AARRGGBB format.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        entry.UpdateFromHex(hexValue);
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        var palette = _configuration.GetThemePaletteColors();
        App.ApplyTheme(_configuration.ColorTheme, palette.Count > 0 ? palette : null);
    }

    private static string GetPaletteDisplayName(string key)
    {
        return PaletteDisplayInfos.TryGetValue(key, out var info) ? info.DisplayName : key;
    }

    private static int GetPaletteUsageCount(string key)
    {
        return PaletteDisplayInfos.TryGetValue(key, out var info) ? info.UsageCount : 0;
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        color = default;

        if (string.IsNullOrWhiteSpace(value)) return false;

        try
        {
            var converted = MediaColorConverter.ConvertFromString(value);
            if (converted is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private void ApplySelectedTheme()
    {
        if (_isInitializing || SelectedThemeOption is null) return;

        if (_configuration.TryActivateTheme(SelectedThemeOption.Name))
        {
            LoadPalette();
            ApplyTheme();
        }
    }

    private void ResetButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedThemeOption?.SupportsReset != true) return;

        // If this is a built-in theme with a custom override, delete the custom theme
        if (SelectedThemeOption.Theme.HasValue && SelectedThemeOption.HasCustomOverride)
        {
            _configuration.DeleteCustomTheme(SelectedThemeOption.Name);
            _themePaletteSnapshots.Remove(SelectedThemeOption.Name);
        }

        // Activate the theme (this will use the built-in version now if override was deleted)
        _configuration.TryActivateTheme(SelectedThemeOption.Name);
        _configuration.ResetThemePalette();
        
        // Refresh the theme options to update the HasCustomOverride flag
        RefreshThemeOptions();
        
        LoadPalette();
        ApplyTheme();
        
        // Capture new snapshot after reset
        CaptureThemePaletteSnapshots();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveUnsavedChanges(SelectedThemeOption, isClosing: true)) return;

        Close();
    }

    private void UpdateResetAvailability()
    {
        if (ResetButton is not null) ResetButton.IsEnabled = SelectedThemeOption?.SupportsReset == true;
    }

    private void PaletteComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || PaletteItems.Count == 0) return;

        var previousTheme = e.RemovedItems.OfType<ThemeOption>().FirstOrDefault();
        var requestedTheme = e.AddedItems.OfType<ThemeOption>().FirstOrDefault() ?? SelectedThemeOption;
        var requestedThemeName = requestedTheme?.Name;

        // Revert the selection immediately before checking for unsaved changes
        if (previousTheme is not null && !ReferenceEquals(SelectedThemeOption, previousTheme))
        {
            _isInitializing = true;
            SelectedThemeOption = previousTheme;
            _isInitializing = false;

            // Skip the unsaved changes check if we're switching after saving
            // Now check if we can proceed with the theme switch
            if (!_isSwitchingAfterSave && !TryResolveUnsavedChanges(previousTheme))
            {
                return;
            }
        }

        if (requestedThemeName is not null)
        {
            requestedTheme = ThemeOptions.FirstOrDefault(option =>
                string.Equals(option.Name, requestedThemeName, StringComparison.OrdinalIgnoreCase));
        }

        // User has approved switching, now update to the requested theme
        if (requestedTheme is not null && !ReferenceEquals(SelectedThemeOption, requestedTheme))
        {
            _isInitializing = true;
            SelectedThemeOption = requestedTheme;
            _isInitializing = false;
        }

        ApplySelectedTheme();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        TrySaveTheme(out _);
    }

    private void DeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedThemeOption is null || !SelectedThemeOption.IsCustom) return;

        var result = WpfMessageBox.Show(
            this,
            $"Delete the theme '{SelectedThemeOption.Name}'?",
            "Simple VS Manager",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        if (!_configuration.DeleteCustomTheme(SelectedThemeOption.Name)) return;

        _themePaletteSnapshots.Remove(SelectedThemeOption.Name);

        RefreshThemeOptions();
        
        // After deletion, sync the selection with the current theme from the service
        var currentThemeName = _configuration.GetCurrentThemeName();
        SelectedThemeOption = ThemeOptions.FirstOrDefault(option =>
            string.Equals(option.Name, currentThemeName, StringComparison.OrdinalIgnoreCase))
            ?? ThemeOptions.FirstOrDefault();
        
        ApplySelectedTheme();
        CaptureThemePaletteSnapshots();
    }

    private bool TryResolveUnsavedChanges(ThemeOption? activeTheme, bool isClosing = false)
    {
        if (activeTheme is null || !HasUnsavedChanges(activeTheme.Name)) return true;

        var prompt = isClosing
            ? "Save changes to the current theme before closing?"
            : $"Save changes to the theme '{activeTheme.Name}' before switching?";

        var result = WpfMessageBox.Show(
            this,
            prompt,
            "Simple VS Manager",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        switch (result)
        {
            case MessageBoxResult.Yes:
                return TrySaveTheme(out _, activeTheme.Name);
            case MessageBoxResult.No:
                RestoreThemeFromSnapshot(activeTheme);
                return true;
            default:
                return false;
        }
    }

    private bool HasUnsavedChanges(string themeName)
    {
        if (!_themePaletteSnapshots.TryGetValue(themeName, out var snapshot)) return false;

        var currentPalette = _configuration.GetThemePaletteColors();
        return !ArePalettesEqual(currentPalette, snapshot);
    }

    private void RestoreThemeFromSnapshot(ThemeOption themeOption)
    {
        if (!_themePaletteSnapshots.TryGetValue(themeOption.Name, out var palette)) return;

        var paletteCopy = new Dictionary<string, string>(palette, StringComparer.OrdinalIgnoreCase);
        var theme = themeOption.Theme ?? ColorTheme.Custom;

        _configuration.SetColorTheme(theme, paletteCopy);
        LoadPalette();
        ApplyTheme();
    }

    private bool TrySaveTheme(out string? savedThemeName, string? defaultThemeName = null)
    {
        savedThemeName = null;

        var defaultName = defaultThemeName ?? SelectedThemeOption?.Name ?? _configuration.GetCurrentThemeName();
        
        // If editing a built-in theme, prefill with "BUILTINNAME Custom"
        if (ThemeOption.IsBuiltInThemeName(defaultName))
        {
            defaultName = $"{defaultName} Custom";
        }
        
        var dialog = new ThemeNameDialog(defaultName, _configuration)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true) return false;

        if (!_configuration.SaveCustomTheme(dialog.ThemeName))
        {
            WpfMessageBox.Show(this, "Please enter a valid theme name.", "Simple VS Manager", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        savedThemeName = dialog.ThemeName;
        UpdateThemeSnapshot(savedThemeName);

        RefreshThemeOptions();
        
        // Set flag to skip unsaved changes prompt when switching to the newly saved theme
        _isSwitchingAfterSave = true;
        try
        {
            SelectedThemeOption = ThemeOptions.FirstOrDefault(option =>
                string.Equals(option.Name, dialog.ThemeName, StringComparison.OrdinalIgnoreCase));

            ApplySelectedTheme();
        }
        finally
        {
            _isSwitchingAfterSave = false;
        }
        
        return true;
    }

    private void UpdateThemeSnapshot(string themeName)
    {
        var palette = _configuration.GetThemePaletteColors();
        _themePaletteSnapshots[themeName] = new Dictionary<string, string>(palette, StringComparer.OrdinalIgnoreCase);
    }

    private static bool ArePalettesEqual(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
    {
        if (ReferenceEquals(first, second)) return true;

        if (first.Count != second.Count) return false;

        foreach (var pair in first)
        {
            if (!second.TryGetValue(pair.Key, out var value)) return false;

            if (!string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    public sealed class ThemeOption
    {
        public ThemeOption(string name, ColorTheme? theme, UserConfigurationService configuration)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Theme = theme;
            
            // A theme supports reset if:
            // 1. It's a built-in theme (VintageStory, Dark, or Light), OR
            // 2. It's a custom theme that overrides a built-in theme name
            var isBuiltInTheme = theme is ColorTheme.VintageStory or ColorTheme.Dark or ColorTheme.Light;
            var hasBuiltInName = IsBuiltInThemeName(name);
            
            SupportsReset = isBuiltInTheme || hasBuiltInName;
            
            // Check if this built-in theme has a custom override
            HasCustomOverride = false;
            if (isBuiltInTheme && configuration != null)
            {
                var customThemeNames = configuration.GetCustomThemeNames();
                HasCustomOverride = customThemeNames.Any(customName => 
                    string.Equals(customName, name, StringComparison.OrdinalIgnoreCase));
            }
        }

        public string Name { get; }

        public ColorTheme? Theme { get; }

        public bool IsCustom => Theme is null;

        public bool SupportsReset { get; }
        
        public bool HasCustomOverride { get; }
        
        public static bool IsBuiltInThemeName(string name)
        {
            return string.Equals(name, UserConfigurationService.GetThemeDisplayName(ColorTheme.VintageStory), StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, UserConfigurationService.GetThemeDisplayName(ColorTheme.Dark), StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, UserConfigurationService.GetThemeDisplayName(ColorTheme.Light), StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record PaletteDisplayInfo(string Key, string DisplayName, int UsageCount);

    private sealed class Win32Window : FormsIWin32Window
    {
        public Win32Window(IntPtr handle)
        {
            Handle = handle;
        }

        public IntPtr Handle { get; }
    }
}

public sealed class PaletteColorEntry : ObservableObject
{
    private readonly Action<PaletteColorEntry> _selectColorAction;
    private string _hexValue;
    private MediaBrush _previewBrush;

    public PaletteColorEntry(
        string key,
        string displayName,
        string hexValue,
        Action<PaletteColorEntry> selectColorAction)
    {
        Key = key;
        DisplayName = displayName;
        _hexValue = hexValue;
        _selectColorAction = selectColorAction ?? throw new ArgumentNullException(nameof(selectColorAction));
        _previewBrush = CreateBrush(hexValue);
        SelectColorCommand = new RelayCommand(() => _selectColorAction(this));
    }

    public string Key { get; }

    public string DisplayName { get; }

    public string HexValue
    {
        get => _hexValue;
        private set => SetProperty(ref _hexValue, value);
    }

    public MediaBrush PreviewBrush
    {
        get => _previewBrush;
        private set => SetProperty(ref _previewBrush, value);
    }

    public IRelayCommand SelectColorCommand { get; }

    public void UpdateFromHex(string hexValue)
    {
        HexValue = hexValue;
        PreviewBrush = CreateBrush(hexValue);
    }

    private static MediaBrush CreateBrush(string hexValue)
    {
        try
        {
            var converted = MediaColorConverter.ConvertFromString(hexValue);
            if (converted is Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }
        catch
        {
            // Ignore and fall through to transparent.
        }

        return MediaBrushes.Transparent;
    }
}