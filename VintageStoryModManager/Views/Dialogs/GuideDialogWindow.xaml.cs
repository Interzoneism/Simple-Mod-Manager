using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using VintageStoryModManager.Services;

using WpfMessageBox = VintageStoryModManager.Services.ModManagerMessageBox;

namespace VintageStoryModManager.Views.Dialogs;

public partial class GuideDialogWindow : Window
{
    public GuideDialogWindow(string managerDirectory, string? cachedModsDirectory, string configurationFilePath)
    {
        InitializeComponent();

        ConfigureHyperlink(ManagerFolderHyperlink, managerDirectory, ensureDirectory: true);
        ConfigureHyperlink(CachedModsHyperlink, cachedModsDirectory, ensureDirectory: true);
        ConfigureHyperlink(ConfigurationFileHyperlink, Path.GetDirectoryName(configurationFilePath), ensureDirectory: true);
    }

    private void ConfigureHyperlink(Hyperlink hyperlink, string? path, bool ensureDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            hyperlink.IsEnabled = false;
            hyperlink.Tag = null;
            hyperlink.NavigateUri = null;
            hyperlink.ToolTip = "Location not available";
            hyperlink.Foreground = System.Windows.SystemColors.GrayTextBrush;
            hyperlink.TextDecorations = null;
            return;
        }

        if (ensureDirectory)
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception)
            {
                // Ignore failures; the navigation handler will surface errors if needed.
            }
        }

        hyperlink.Tag = path;
        hyperlink.NavigateUri = TryCreateUri(path);
        hyperlink.ToolTip = path;
    }

    private static Uri? TryCreateUri(string path)
    {
        return Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) ? uri : null;
    }

    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;

        if (sender is not Hyperlink hyperlink || hyperlink.Tag is not string target || string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        string? destination = ResolveDestinationPath(target);
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
        if (Directory.Exists(path) || File.Exists(path))
        {
            return path;
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            return directory;
        }

        return null;
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
