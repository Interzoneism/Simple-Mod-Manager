using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
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
using WpfMessageBox = VintageStoryModManager.Services.ModManagerMessageBox;

namespace VintageStoryModManager.Views.Dialogs;

public partial class ThemePaletteEditorDialog : Window
{
    private readonly UserConfigurationService _configuration;

    public ThemePaletteEditorDialog(UserConfigurationService configuration)
    {
        InitializeComponent();
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        PaletteItems = new ObservableCollection<PaletteColorEntry>();
        DataContext = this;
        LoadPalette();
    }

    public ObservableCollection<PaletteColorEntry> PaletteItems { get; }

    private void LoadPalette()
    {
        PaletteItems.Clear();

        foreach (var pair in _configuration.GetThemePaletteColors().OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            PaletteItems.Add(new PaletteColorEntry(pair.Key, pair.Value, PickColor));
        }
    }

    private void PickColor(PaletteColorEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        using var dialog = new FormsColorDialog
        {
            FullOpen = true,
            AnyColor = true
        };

        if (TryParseColor(entry.HexValue, out Color currentColor))
        {
            dialog.Color = DrawingColor.FromArgb(currentColor.A, currentColor.R, currentColor.G, currentColor.B);
        }

        IntPtr ownerHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        FormsDialogResult result = ownerHandle != IntPtr.Zero
            ? dialog.ShowDialog(new Win32Window(ownerHandle))
            : dialog.ShowDialog();

        if (result != FormsDialogResult.OK)
        {
            return;
        }

        DrawingColor selected = dialog.Color;
        string hex = $"#{selected.A:X2}{selected.R:X2}{selected.G:X2}{selected.B:X2}";

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
        IReadOnlyDictionary<string, string> palette = _configuration.GetThemePaletteColors();
        App.ApplyTheme(_configuration.ColorTheme, palette.Count > 0 ? palette : null);
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        color = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            object? converted = MediaColorConverter.ConvertFromString(value);
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

    private void ResetButton_OnClick(object sender, RoutedEventArgs e)
    {
        _configuration.ResetThemePalette();
        LoadPalette();
        ApplyTheme();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

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
    private MediaBrush _previewBrush;
    private string _hexValue;

    public PaletteColorEntry(string key, string hexValue, Action<PaletteColorEntry> selectColorAction)
    {
        Key = key;
        _hexValue = hexValue;
        _selectColorAction = selectColorAction ?? throw new ArgumentNullException(nameof(selectColorAction));
        _previewBrush = CreateBrush(hexValue);
        SelectColorCommand = new RelayCommand(() => _selectColorAction(this));
    }

    public string Key { get; }

    public string DisplayName => Key;

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
            object? converted = MediaColorConverter.ConvertFromString(hexValue);
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
