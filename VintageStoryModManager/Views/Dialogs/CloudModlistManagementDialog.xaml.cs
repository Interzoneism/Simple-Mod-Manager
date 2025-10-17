using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VintageStoryModManager.Models;
using WpfMessageBox = VintageStoryModManager.Services.ModManagerMessageBox;

namespace VintageStoryModManager.Views.Dialogs;

public partial class CloudModlistManagementDialog : Window
{
    private readonly ObservableCollection<CloudModlistManagementEntry> _entries;
    private readonly Func<Task<IReadOnlyList<CloudModlistManagementEntry>>> _refreshCallback;
    private readonly Func<CloudModlistManagementEntry, string, Task<bool>> _renameCallback;
    private readonly Func<CloudModlistManagementEntry, Task<bool>> _deleteCallback;
    private bool _isBusy;

    public CloudModlistManagementDialog(
        Window owner,
        IEnumerable<CloudModlistManagementEntry> entries,
        Func<Task<IReadOnlyList<CloudModlistManagementEntry>>> refreshCallback,
        Func<CloudModlistManagementEntry, string, Task<bool>> renameCallback,
        Func<CloudModlistManagementEntry, Task<bool>> deleteCallback)
    {
        InitializeComponent();

        Owner = owner;
        _refreshCallback = refreshCallback ?? throw new ArgumentNullException(nameof(refreshCallback));
        _renameCallback = renameCallback ?? throw new ArgumentNullException(nameof(renameCallback));
        _deleteCallback = deleteCallback ?? throw new ArgumentNullException(nameof(deleteCallback));

        _entries = new ObservableCollection<CloudModlistManagementEntry>(
            entries ?? Enumerable.Empty<CloudModlistManagementEntry>());
        ModlistsListView.ItemsSource = _entries;

        if (_entries.Count > 0)
        {
            ModlistsListView.SelectedIndex = 0;
        }

        UpdateButtonStates();
    }

    private CloudModlistManagementEntry? SelectedEntry =>
        ModlistsListView.SelectedItem as CloudModlistManagementEntry;

    private async void RenameButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry is not CloudModlistManagementEntry entry)
        {
            return;
        }

        var renameDialog = new CloudModlistRenameDialog(this, entry.Name ?? entry.EffectiveName);
        bool? dialogResult = renameDialog.ShowDialog();
        if (dialogResult != true)
        {
            return;
        }

        string newName = renameDialog.ModlistName;
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        await RunOperationAsync(() => _renameCallback(entry, newName));
    }

    private async void DeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry is not CloudModlistManagementEntry entry)
        {
            return;
        }

        string displayName = string.IsNullOrWhiteSpace(entry.Name)
            ? entry.SlotLabel
            : $"{entry.SlotLabel} (\"{entry.Name}\")";

        MessageBoxResult confirmation = WpfMessageBox.Show(
            $"Are you sure you want to delete {displayName}? This action cannot be undone.",
            "Simple VS Manager",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await RunOperationAsync(() => _deleteCallback(entry));
    }

    private async Task RunOperationAsync(Func<Task<bool>> operation)
    {
        if (operation is null || _isBusy)
        {
            return;
        }

        SetIsBusy(true);
        try
        {
            bool success = await operation();
            if (success)
            {
                await RefreshEntriesAsync();
            }
        }
        finally
        {
            SetIsBusy(false);
        }
    }

    private async Task RefreshEntriesAsync()
    {
        IReadOnlyList<CloudModlistManagementEntry>? entries;
        try
        {
            entries = await _refreshCallback();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(
                $"Failed to refresh cloud modlists:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        string? selectedSlotKey = SelectedEntry?.SlotKey;

        _entries.Clear();
        if (entries is not null)
        {
            foreach (CloudModlistManagementEntry entry in entries)
            {
                if (entry is not null)
                {
                    _entries.Add(entry);
                }
            }
        }

        if (_entries.Count == 0)
        {
            WpfMessageBox.Show(
                "You do not have any cloud modlists saved.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DialogResult = true;
            return;
        }

        int selectedIndex = -1;
        if (!string.IsNullOrWhiteSpace(selectedSlotKey))
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (string.Equals(_entries[i].SlotKey, selectedSlotKey, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    break;
                }
            }
        }

        if (selectedIndex >= 0)
        {
            ModlistsListView.SelectedIndex = selectedIndex;
        }
        else if (_entries.Count > 0)
        {
            ModlistsListView.SelectedIndex = 0;
        }

        UpdateButtonStates();
    }

    private void SetIsBusy(bool isBusy)
    {
        _isBusy = isBusy;
        ModlistsListView.IsEnabled = !isBusy;
        if (isBusy)
        {
            RenameButton.IsEnabled = false;
            DeleteButton.IsEnabled = false;
        }
        else
        {
            UpdateButtonStates();
        }
    }

    private void UpdateButtonStates()
    {
        if (_isBusy)
        {
            return;
        }

        bool hasSelection = SelectedEntry is not null;
        RenameButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
    }

    private void ModlistsListView_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtonStates();
    }

    private void ModlistsListView_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount < 2)
        {
            return;
        }

        RenameButton_OnClick(sender, e);
    }
}
