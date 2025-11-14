using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;

namespace VintageStoryModManager.Views.Dialogs;

public partial class ModConfigSelectionDialog : Window
{
    private bool _isUpdatingSelectAll;
    private bool _isUpdatingSelection;

    public ModConfigSelectionDialog(IEnumerable<ModConfigOption> configOptions)
    {
        ConfigOptions = new ObservableCollection<ModConfigOption>((configOptions ?? Array.Empty<ModConfigOption>())
            .Where(option => option is not null)
            .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase));

        InitializeComponent();

        foreach (var option in ConfigOptions) option.PropertyChanged += ConfigOption_OnPropertyChanged;

        UpdateSelectAllState();
    }

    public ObservableCollection<ModConfigOption> ConfigOptions { get; }

    public bool HasConfigOptions => ConfigOptions.Count > 0;

    public IReadOnlyList<ModConfigOption> GetSelectedOptions()
    {
        return ConfigOptions.Where(option => option.IsSelected).ToList();
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void SelectAllCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSelectAll) return;

        var shouldSelect = SelectAllCheckBox.IsChecked == true;

        _isUpdatingSelection = true;
        foreach (var option in ConfigOptions) option.IsSelected = shouldSelect;

        _isUpdatingSelection = false;
        UpdateSelectAllState();
    }

    private void ConfigOption_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(ModConfigOption.IsSelected), StringComparison.Ordinal)) return;

        if (_isUpdatingSelection) return;

        UpdateSelectAllState();
    }

    private void UpdateSelectAllState()
    {
        if (SelectAllCheckBox is null) return;

        if (!HasConfigOptions)
        {
            _isUpdatingSelectAll = true;
            SelectAllCheckBox.IsChecked = false;
            _isUpdatingSelectAll = false;
            return;
        }

        var selectedCount = ConfigOptions.Count(option => option.IsSelected);
        var totalCount = ConfigOptions.Count;

        _isUpdatingSelectAll = true;
        SelectAllCheckBox.IsChecked = selectedCount switch
        {
            0 => false,
            _ when selectedCount == totalCount => true,
            _ => null
        };
        _isUpdatingSelectAll = false;
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateSelectAllState();
    }
}