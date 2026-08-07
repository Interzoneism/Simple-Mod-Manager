using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.Views.Dialogs;

public partial class CloudSlotSelectionDialog : Window
{
    private readonly List<CloudModlistSlot> _slots;

    public CloudSlotSelectionDialog(
        Window owner,
        IEnumerable<CloudModlistSlot> slots,
        string title,
        string prompt,
        string? defaultSlotKey = null)
    {
        InitializeComponent();

        Owner = owner;
        Title = title;
        PromptTextBlock.Text = prompt;

        _slots = slots?.ToList() ?? new List<CloudModlistSlot>();
        SlotsListBox.ItemsSource = _slots;

        if (!string.IsNullOrWhiteSpace(defaultSlotKey))
        {
            var index = _slots.FindIndex(slot =>
                string.Equals(slot.SlotKey, defaultSlotKey, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) SlotsListBox.SelectedIndex = index;
        }

        if (SlotsListBox.SelectedIndex < 0 && _slots.Count > 0) SlotsListBox.SelectedIndex = 0;

        UpdateSelectButtonState();
    }

    public CloudModlistSlot? SelectedSlot => SlotsListBox.SelectedItem as CloudModlistSlot;

    private void SelectButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSlot is null) return;

        DialogResult = true;
    }

    private void SlotsListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedSlot is null) return;

        DialogResult = true;
    }

    private void SlotsListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectButtonState();
    }

    private void UpdateSelectButtonState()
    {
        SelectButton.IsEnabled = SelectedSlot is not null;
    }
}