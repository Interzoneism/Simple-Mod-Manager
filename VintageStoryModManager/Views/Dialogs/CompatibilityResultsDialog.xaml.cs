using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VintageStoryModManager.Views.Dialogs;

public partial class CompatibilityResultsDialog : Window
{
    public CompatibilityResultsDialog(
        Window owner,
        string targetVersion,
        IReadOnlyList<string> incompatibleMods,
        IReadOnlyList<string> unknownMods)
    {
        InitializeComponent();

        Owner = owner?.IsVisible == true ? owner : null;
        WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;

        BuildMessage(targetVersion, incompatibleMods ?? Array.Empty<string>(), unknownMods ?? Array.Empty<string>());
        ConfigureIcon(incompatibleMods?.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void BuildMessage(
        string targetVersion,
        IReadOnlyList<string> incompatibleMods,
        IReadOnlyList<string> unknownMods)
    {
        MessageTextBlock.Inlines.Clear();

        var versionText = string.IsNullOrWhiteSpace(targetVersion)
            ? "Vintage Story version: Unknown."
            : $"Vintage Story version: {targetVersion.Trim()}.";
        MessageTextBlock.Inlines.Add(new Run(versionText));
        MessageTextBlock.Inlines.Add(new LineBreak());
        MessageTextBlock.Inlines.Add(new LineBreak());

        if (incompatibleMods.Count == 0)
        {
            MessageTextBlock.Inlines.Add(new Run("All installed mods appear to support this version."));
        }
        else
        {
            MessageTextBlock.Inlines.Add(new Run($"Incompatible mods for {targetVersion}:"));
            MessageTextBlock.Inlines.Add(new LineBreak());

            foreach (var mod in incompatibleMods.Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                MessageTextBlock.Inlines.Add(new Run("• "));
                MessageTextBlock.Inlines.Add(new Bold(new Run(mod.Trim())));
                MessageTextBlock.Inlines.Add(new LineBreak());
            }
        }

        if (unknownMods.Count > 0)
        {
            if (incompatibleMods.Count > 0) MessageTextBlock.Inlines.Add(new LineBreak());

            MessageTextBlock.Inlines.Add(new Run("Compatibility could not be determined for:"));
            MessageTextBlock.Inlines.Add(new LineBreak());

            foreach (var mod in unknownMods.Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                MessageTextBlock.Inlines.Add(new Run("• "));
                MessageTextBlock.Inlines.Add(new Run(mod.Trim()));
                MessageTextBlock.Inlines.Add(new LineBreak());
            }
        }
    }

    private void ConfigureIcon(MessageBoxImage icon)
    {
        var source = icon switch
        {
            MessageBoxImage.Error => ConvertIcon(SystemIcons.Error),
            MessageBoxImage.Warning => ConvertIcon(SystemIcons.Warning),
            MessageBoxImage.Information => ConvertIcon(SystemIcons.Information),
            MessageBoxImage.Question => ConvertIcon(SystemIcons.Question),
            _ => null
        };

        if (source == null)
        {
            IconImage.Source = null;
            IconImage.Visibility = Visibility.Collapsed;
        }
        else
        {
            IconImage.Source = source;
            IconImage.Visibility = Visibility.Visible;
        }
    }

    private static ImageSource? ConvertIcon(Icon icon)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(icon.Width, icon.Height));
        source.Freeze();
        return source;
    }

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}