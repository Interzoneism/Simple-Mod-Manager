using System.Collections.ObjectModel;
using System.Windows;

namespace VintageStoryModManager.Views;

public partial class BulkUpdateChangelogWindow : Window
{
    public BulkUpdateChangelogWindow(IEnumerable<BulkUpdateChangelogItem> items)
    {
        InitializeComponent();
        var list = items?.ToList() ?? new List<BulkUpdateChangelogItem>();
        ChangelogItems = new ReadOnlyCollection<BulkUpdateChangelogItem>(list);
        DataContext = this;
    }

    public IReadOnlyList<BulkUpdateChangelogItem> ChangelogItems { get; }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    public sealed record BulkUpdateChangelogItem(string Title, string Changelog);
}