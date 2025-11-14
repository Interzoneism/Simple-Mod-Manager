using System.Windows;

namespace VintageStoryModManager.Views;

public partial class BulkCompatibilityPromptWindow : Window
{
    public BulkCompatibilityPromptWindow(string message, string latestVersion, string? compatibleVersion,
        bool hasCompatible)
    {
        InitializeComponent();

        MessageTextBlock.Text = message;

        InstallLatestButton.Content = string.IsNullOrWhiteSpace(latestVersion)
            ? "Install latest release"
            : $"Install latest release ({latestVersion})";
        InstallLatestAllButton.Content = string.IsNullOrWhiteSpace(latestVersion)
            ? "Install latest release for all mods"
            : $"Install latest release for all mods ({latestVersion})";

        var showCompatible = hasCompatible
                             && !string.IsNullOrWhiteSpace(compatibleVersion)
                             && !string.Equals(latestVersion, compatibleVersion, StringComparison.OrdinalIgnoreCase);

        if (showCompatible)
        {
            InstallCompatibleButton.Content = $"Install latest compatible version ({compatibleVersion})";
            InstallCompatibleAllButton.Content =
                $"Install latest compatible version for all mods ({compatibleVersion})";
            InstallCompatibleButton.Visibility = Visibility.Visible;
            InstallCompatibleAllButton.Visibility = Visibility.Visible;
        }
        else
        {
            InstallCompatibleButton.Visibility = Visibility.Collapsed;
            InstallCompatibleAllButton.Visibility = Visibility.Collapsed;
        }

        SkipButton.Content = "Skip this mod";
        CancelButton.Content = "Cancel updates";

        Decision = CompatibilityDecisionKind.Skip;
    }

    internal CompatibilityDecisionKind Decision { get; private set; }

    private void InstallLatestButton_OnClick(object sender, RoutedEventArgs e)
    {
        Decision = CompatibilityDecisionKind.Latest;
        DialogResult = true;
    }

    private void InstallLatestAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        Decision = CompatibilityDecisionKind.LatestForAll;
        DialogResult = true;
    }

    private void InstallCompatibleButton_OnClick(object sender, RoutedEventArgs e)
    {
        Decision = CompatibilityDecisionKind.LatestCompatible;
        DialogResult = true;
    }

    private void InstallCompatibleAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        Decision = CompatibilityDecisionKind.LatestCompatibleForAll;
        DialogResult = true;
    }

    private void SkipButton_OnClick(object sender, RoutedEventArgs e)
    {
        Decision = CompatibilityDecisionKind.Skip;
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Decision = CompatibilityDecisionKind.Abort;
        DialogResult = true;
    }
}