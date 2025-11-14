using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace VintageStoryModManager.Views.Dialogs;

public partial class SaveInstalledModsDialog : Window
{
    private bool _isUpdatingConfigSelection;
    private bool _isUpdatingSelectAllCheckBox;

    public SaveInstalledModsDialog(
        string? defaultListName = null,
        IEnumerable<ModConfigOption>? configOptions = null,
        string? defaultCreatedBy = null)
    {
        ConfigOptions = new ObservableCollection<ModConfigOption>(
            (configOptions ?? Array.Empty<ModConfigOption>())
            .Where(option => option is not null)
            .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase));

        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(defaultListName)) NameTextBox.Text = defaultListName.Trim();

        if (!string.IsNullOrWhiteSpace(defaultCreatedBy))
            CreatedByTextBox.Text = defaultCreatedBy.Trim();
        else if (!string.IsNullOrWhiteSpace(Environment.UserName)) CreatedByTextBox.Text = Environment.UserName.Trim();

        foreach (var option in ConfigOptions) option.PropertyChanged += ConfigOption_OnPropertyChanged;

        UpdateConfirmButtonState();
        UpdateSelectAllState();
    }

    public ObservableCollection<ModConfigOption> ConfigOptions { get; }

    public bool HasConfigOptions => ConfigOptions.Count > 0;

    public string ListName => NameTextBox.Text.Trim();

    public string? Description => NormalizeOptionalText(DescriptionTextBox.Text);

    public string? CreatedBy => NormalizeOptionalText(CreatedByTextBox.Text);

    public IReadOnlyList<ModConfigOption> GetSelectedConfigOptions()
    {
        return ConfigOptions.Where(option => option.IsSelected).ToList();
    }

    private static string? NormalizeOptionalText(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateConfirmButtonState();
        NameTextBox.Focus();
        NameTextBox.SelectAll();
        UpdateSelectAllState();
    }

    private void NameTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateConfirmButtonState();
    }

    private void UpdateConfirmButtonState()
    {
        if (ConfirmButton is null) return;

        ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(NameTextBox.Text);
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text)) return;

        DialogResult = true;
        Close();
    }

    private void SelectAllCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSelectAllCheckBox) return;

        var shouldSelect = SelectAllCheckBox.IsChecked == true;

        _isUpdatingConfigSelection = true;
        foreach (var option in ConfigOptions) option.IsSelected = shouldSelect;

        _isUpdatingConfigSelection = false;
        UpdateSelectAllState();
    }

    private void ConfigOption_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(ModConfigOption.IsSelected), StringComparison.Ordinal)) return;

        if (_isUpdatingConfigSelection) return;

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