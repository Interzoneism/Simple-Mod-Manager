using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using System.Runtime.ExceptionServices;
using VintageStoryModManager.Services;
using VintageStoryModManager.Views.Dialogs;
using Application = System.Windows.Application;
using WpfMessageBox = VintageStoryModManager.Services.ModManagerMessageBox;
using Color = System.Windows.Media.Color;
using System.Configuration;
using System.Data;

namespace VintageStoryModManager;

public partial class App : Application
{
    private static readonly string SingleInstanceMutexName = DevConfig.SingleInstanceMutexName;
    private static readonly Uri DarkVsThemeUri = new("Resources/Themes/DarkVsTheme.xaml", UriKind.Relative);
    private ResourceDictionary? _activeTheme;
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        bool createdNew;
        _instanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
        _ownsMutex = createdNew;

        if (!createdNew)
        {
            ShowSingleInstanceWarning();
            ActivateExistingInstance();
            Current?.Shutdown();
            return;
        }

        ApplyPreferredTheme();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);

        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;

        if (_instanceMutex != null)
        {
            if (_ownsMutex) _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            _ownsMutex = false;
        }
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (e.Exception is not InvalidOperationException)
        {
            return;
        }

        try
        {
            Debug.WriteLine($"[FirstChance] InvalidOperationException: {e.Exception.Message}");
            Debug.WriteLine(e.Exception.StackTrace);
        }
        catch
        {
            // Debug logging should never block application startup.
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var message = $"An unexpected error occurred:\n{e.Exception.Message}";
        WpfMessageBox.Show(message, "Simple VS Manager", MessageBoxButton.OK, MessageBoxImage.Error);

        try
        {
            Current?.Shutdown(-1);
        }
        catch
        {
            // If shutdown fails we fall back to letting WPF terminate the process.
            e.Handled = false;
            return;
        }

        e.Handled = true;
    }

    private static void ActivateExistingInstance()
    {
        try
        {
            var currentProcess = Process.GetCurrentProcess();

            foreach (var process in Process.GetProcessesByName(currentProcess.ProcessName))
            {
                if (process.Id == currentProcess.Id) continue;

                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero) continue;

                WindowActivator.ShowAndActivate(handle);
                break;
            }
        }
        catch (Exception)
        {
            // If we cannot activate the existing instance we silently continue shutting down.
        }
    }

    private static void ShowSingleInstanceWarning()
    {
        try
        {
            var buttonOverrides = new MessageDialogButtonContentOverrides
            {
                Ok = "Abort"
            };

            WpfMessageBox.Show(
                "Simple VS Manager is already running. This launch will be aborted.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                buttonContentOverrides: buttonOverrides);
        }
        catch
        {
            // We ignore failures showing the warning so the application can shut down cleanly.
        }
    }

    private void ApplyPreferredTheme()
    {
        var theme = ColorTheme.VintageStory;
        IReadOnlyDictionary<string, string>? palette = null;

        try
        {
            var configuration = new UserConfigurationService();
            theme = configuration.ColorTheme;
            palette = configuration.GetThemePaletteColors();
        }
        catch (Exception)
        {
            // If the configuration fails to load we silently fall back to the default theme.
        }

        EnsureActiveThemeDictionary();
        ApplyTheme(theme, palette);
    }

    public static void ApplyTheme(ColorTheme theme, IReadOnlyDictionary<string, string>? paletteOverrides)
    {
        if (Current is not App app) return;

        app.EnsureActiveThemeDictionary();
        var overridesToApply = paletteOverrides is { Count: > 0 }
            ? paletteOverrides
            : null;
        app.ApplyThemeDictionary(ResolveThemeUri(theme), overridesToApply);
    }

    private void EnsureActiveThemeDictionary()
    {
        if (_activeTheme is not null || Resources is not { MergedDictionaries.Count: > 0 }) return;

        foreach (var dictionary in Resources.MergedDictionaries)
        {
            var source = dictionary.Source?.OriginalString;
            if (string.IsNullOrEmpty(source)) continue;

            if (source.Contains("Resources/Themes/", StringComparison.OrdinalIgnoreCase))
            {
                _activeTheme = dictionary;
                break;
            }
        }
    }

    private void ApplyThemeDictionary(Uri source, IReadOnlyDictionary<string, string>? paletteOverrides)
    {
        if (Resources is null) return;

        if (_activeTheme != null)
        {
            Resources.MergedDictionaries.Remove(_activeTheme);
            _activeTheme = null;
        }

        try
        {
            var dictionary = new ResourceDictionary { Source = source };
            if (paletteOverrides is not null) ApplyPaletteOverrides(dictionary, paletteOverrides);

            Resources.MergedDictionaries.Add(dictionary);
            _activeTheme = dictionary;
        }
        catch (Exception)
        {
            // If loading the preferred theme fails, fall back to the default theme.
            if (!ReferenceEquals(source, DarkVsThemeUri)) ApplyThemeDictionary(DarkVsThemeUri, paletteOverrides);
        }
    }

    private static Uri ResolveThemeUri(ColorTheme theme)
    {
        return DarkVsThemeUri;
    }

    private static void ApplyPaletteOverrides(ResourceDictionary dictionary,
        IReadOnlyDictionary<string, string> paletteOverrides)
    {
        foreach (var pair in paletteOverrides)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;

            if (!dictionary.Contains(pair.Key)) continue;

            if (!TryParseColor(pair.Value, out var color)) continue;

            dictionary[pair.Key] = color;
        }
    }

    private static bool TryParseColor(string value, out Color color)
    {
        color = default;

        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty || trimmed[0] != '#' || trimmed.Length <= 1) return false;

        var hex = trimmed.Slice(1);
        if (hex.Length == 6)
        {
            if (TryParseHexByte(hex.Slice(0, 2), out var r)
                && TryParseHexByte(hex.Slice(2, 2), out var g)
                && TryParseHexByte(hex.Slice(4, 2), out var b))
            {
                color = Color.FromRgb(r, g, b);
                return true;
            }

            return false;
        }

        if (hex.Length == 8)
        {
            if (TryParseHexByte(hex.Slice(0, 2), out var a)
                && TryParseHexByte(hex.Slice(2, 2), out var r)
                && TryParseHexByte(hex.Slice(4, 2), out var g)
                && TryParseHexByte(hex.Slice(6, 2), out var b))
            {
                color = Color.FromArgb(a, r, g, b);
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool TryParseHexByte(ReadOnlySpan<char> hex, out byte value)
    {
        return byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static class WindowActivator
    {
        private const int SwRestore = 9;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public static void ShowAndActivate(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero) return;

            ShowWindow(windowHandle, SwRestore);
            SetForegroundWindow(windowHandle);
        }
    }
}