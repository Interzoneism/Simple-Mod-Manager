using System.Windows;
using System.Windows.Controls;

namespace VintageStoryModManager.Views.Dialogs;

public partial class ModVoteReasonDialog : Window
{
    private readonly IReadOnlyList<string> _reasons;

    public ModVoteReasonDialog(string title, string description, IReadOnlyList<string> reasons, string? selectedReason)
    {
        InitializeComponent();

        ArgumentNullException.ThrowIfNull(reasons);

        TitleTextBlock.Text = title;
        DescriptionTextBlock.Text = description;

        _reasons = reasons;
        ReasonComboBox.ItemsSource = _reasons;

        if (!string.IsNullOrWhiteSpace(selectedReason))
        {
            var index = _reasons.IndexOf(selectedReason);
            if (index >= 0) ReasonComboBox.SelectedIndex = index;
        }

        Loaded += (_, _) => ReasonComboBox.Focus();
    }

    public string? SelectedReason => ReasonComboBox.SelectedItem as string;

    private void SubmitButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedReason is null) return;

        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ReasonComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SubmitButton.IsEnabled = SelectedReason is not null;
    }
}

internal static class ReasonListExtensions
{
    public static int IndexOf(this IReadOnlyList<string> source, string value)
    {
        for (var i = 0; i < source.Count; i++)
            if (string.Equals(source[i], value, StringComparison.Ordinal))
                return i;

        return -1;
    }
}