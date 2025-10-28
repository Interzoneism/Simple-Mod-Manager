using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace VintageStoryModManager.Views;

public partial class BulkUpdateChangelogWindow : Window
{
    public sealed record BulkUpdateChangelogItem(string Title, string Changelog);

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
}
