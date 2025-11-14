using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;
using VintageStoryModManager.Services;
using SystemColors = System.Windows.SystemColors;
// For DevConfig
// For InternetAccessManager
using WpfMessageBox = VintageStoryModManager.Services.ModManagerMessageBox;

namespace VintageStoryModManager.Views.Dialogs;

public partial class HelpDialogWindow : Window
{
    public HelpDialogWindow(string managerDirectory, string? cachedModsDirectory)
    {
        InitializeComponent();

        ConfigureCachedModsHyperlink(ModDBlink, cachedModsDirectory, true);
        ConfigureFirebaseHyperlink(FirebaseFileHyperlink, managerDirectory, true);
        ConfigureHyperlinkCommon(BackupLink, DevConfig.FirebaseBackupDirectory, true);
    }

    private void ConfigureCachedModsHyperlink(Hyperlink hyperlink, string? path, bool ensureDirectory)
    {
        ConfigureHyperlinkCommon(hyperlink, path, ensureDirectory);
    }

    private void ConfigureFirebaseHyperlink(Hyperlink hyperlink, string? path, bool ensureDirectory)
    {
        ConfigureHyperlinkCommon(hyperlink, path, ensureDirectory);
    }

    private void ConfigureHyperlinkCommon(Hyperlink hyperlink, string? path, bool ensureDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            hyperlink.IsEnabled = false;
            hyperlink.Tag = null;
            hyperlink.NavigateUri = null;
            hyperlink.ToolTip = "Location not available";
            hyperlink.Foreground = SystemColors.GrayTextBrush;
            hyperlink.TextDecorations = null;
            return;
        }

        if (ensureDirectory)
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception)
            {
                // Ignore failures; the navigation handler will surface errors if needed.
            }

        hyperlink.Tag = path;
        hyperlink.NavigateUri = TryCreateUri(path);
        hyperlink.ToolTip = path;
    }

    private static Uri? TryCreateUri(string path)
    {
        return Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri : null;
    }

    private void OnFirebaseHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        HandleHyperlinkNavigation(sender, e);
    }

    private void OnBackupHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        HandleHyperlinkNavigation(sender, e);
    }

    private void OnCachedModsHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        HandleHyperlinkNavigation(sender, e);
    }

    // NEW: Open the manager's Mod DB page exactly like the Help menu item.
    private void OnModDBlinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;

        var url = DevConfig.ManagerModDatabaseUrl;

        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            WpfMessageBox.Show(this,
                "Internet access is disabled. Enable Internet Access in the File menu to open web links.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this,
                $"Failed to open the web page:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void HandleHyperlinkNavigation(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;

        if (sender is not Hyperlink hyperlink || hyperlink.Tag is not string target ||
            string.IsNullOrWhiteSpace(target)) return;

        var destination = ResolveDestinationPath(target);
        if (destination is null)
        {
            WpfMessageBox.Show(this,
                $"The location could not be found:\n{target}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = destination,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this,
                $"Failed to open the location:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static string? ResolveDestinationPath(string path)
    {
        if (Directory.Exists(path) || File.Exists(path)) return path;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) return directory;

        return null;
    }

    private void OnHelpCloseButtonClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}