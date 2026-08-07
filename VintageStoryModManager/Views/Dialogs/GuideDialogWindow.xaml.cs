using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;
using WpfMessageBox = VintageStoryModManager.Services.ModManagerMessageBox;

namespace VintageStoryModManager.Views.Dialogs;

public partial class GuideDialogWindow : Window
{
    public GuideDialogWindow(string managerDirectory, string? cachedModsDirectory, string configurationFilePath)
    {
        InitializeComponent();
    }

    private static Uri? TryCreateUri(string path)
    {
        return Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri : null;
    }

    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
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

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}