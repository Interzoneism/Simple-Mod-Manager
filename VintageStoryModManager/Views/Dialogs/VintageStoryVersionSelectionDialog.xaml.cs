using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VintageStoryModManager.Services;

namespace VintageStoryModManager.Views.Dialogs;

public partial class VintageStoryVersionSelectionDialog : Window
{
    private sealed class VersionListItem
    {
        public VersionListItem(string version, bool isCurrent)
        {
            Version = version;
            Display = isCurrent ? $"{version} (current)" : version;
        }

        public string Version { get; }

        public string Display { get; }
    }

    private readonly List<VersionListItem> _versions;

    public VintageStoryVersionSelectionDialog(Window owner, IEnumerable<string> versions, string? currentVersion = null)
    {
        InitializeComponent();

        Owner = owner;
        string? normalizedCurrent = VersionStringUtility.Normalize(currentVersion);
        _versions = versions?.Where(version => !string.IsNullOrWhiteSpace(version))
            .Select(version => version.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(version =>
            {
                string? normalized = VersionStringUtility.Normalize(version);
                bool isCurrent = normalizedCurrent is not null
                    && normalized is not null
                    && string.Equals(normalized, normalizedCurrent, StringComparison.OrdinalIgnoreCase);
                return new VersionListItem(version, isCurrent);
            })
            .ToList() ?? new List<VersionListItem>();

        VersionsListBox.ItemsSource = _versions;

        if (_versions.Count > 0)
        {
            VersionsListBox.SelectedIndex = 0;
        }

        UpdateSelectButtonState();
    }

    public string? SelectedVersion => (VersionsListBox.SelectedItem as VersionListItem)?.Version;

    private void SelectButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion is null)
        {
            return;
        }

        DialogResult = true;
    }

    private void VersionsListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedVersion is null)
        {
            return;
        }

        DialogResult = true;
    }

    private void VersionsListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectButtonState();
    }

    private void UpdateSelectButtonState()
    {
        SelectButton.IsEnabled = SelectedVersion is not null;
    }
}
