using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace VintageStoryModManager.Views.Dialogs;

public partial class CloudModlistDetailsDialog : Window
{
    private bool _isUpdatingConfigOptionSelection;
    private bool _isUpdatingSelectAllCheckBox;

    public CloudModlistDetailsDialog(Window owner, string? suggestedName, IEnumerable<ModConfigOption>? configOptions)
    {
        ConfigOptions = new ObservableCollection<ModConfigOption>(
            (configOptions ?? Enumerable.Empty<ModConfigOption>())
            .Where(option => option is not null)
            .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase));

        InitializeComponent();

        foreach (var option in ConfigOptions) option.PropertyChanged += ConfigOption_OnPropertyChanged;

        UpdateSelectAllState();

        Owner = owner;
        NameTextBox.Text = string.IsNullOrWhiteSpace(suggestedName)
            ? string.Empty
            : suggestedName;
        NameTextBox.SelectAll();
        UpdateConfirmButtonState();
    }

    public ObservableCollection<ModConfigOption> ConfigOptions { get; }

    public bool HasConfigOptions => ConfigOptions.Count > 0;

    public string ModlistName => NameTextBox.Text.Trim();

    public string? ModlistDescription => string.IsNullOrWhiteSpace(DescriptionTextBox.Text)
        ? null
        : DescriptionTextBox.Text.Trim();

    public string? ModlistVersion => string.IsNullOrWhiteSpace(VersionTextBox.Text)
        ? null
        : VersionTextBox.Text.Trim();

    public IReadOnlyList<ModConfigOption> GetSelectedConfigOptions()
    {
        return ConfigOptions.Where(option => option.IsSelected).ToList();
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text)) return;

        DialogResult = true;
    }

    private void NameTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateConfirmButtonState();
    }

    private void UpdateConfirmButtonState()
    {
        if (ConfirmButton is null) return;

        var modlistName = NameTextBox?.Text;
        ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(modlistName);
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        NameTextBox.Focus();
        NameTextBox.SelectAll();
        UpdateSelectAllState();
    }

    private void SelectAllCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSelectAllCheckBox) return;

        _isUpdatingConfigOptionSelection = true;

        var shouldSelectAll = SelectAllCheckBox.IsChecked == true;

        foreach (var option in ConfigOptions) option.IsSelected = shouldSelectAll;

        _isUpdatingConfigOptionSelection = false;

        UpdateSelectAllState();
    }

    private void ConfigOption_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(ModConfigOption.IsSelected), StringComparison.Ordinal)) return;

        if (_isUpdatingConfigOptionSelection) return;

        UpdateSelectAllState();
    }

    private void UpdateSelectAllState()
    {
        if (SelectAllCheckBox is null) return;

        if (!HasConfigOptions)
        {
            _isUpdatingSelectAllCheckBox = true;
            SelectAllCheckBox.IsChecked = false;
            _isUpdatingSelectAllCheckBox = false;
            return;
        }

        var selectedCount = ConfigOptions.Count(option => option.IsSelected);
        var totalCount = ConfigOptions.Count;

        _isUpdatingSelectAllCheckBox = true;

        SelectAllCheckBox.IsChecked = selectedCount switch
        {
            0 => false,
            _ when selectedCount == totalCount => true,
            _ => null
        };

        _isUpdatingSelectAllCheckBox = false;
    }
}