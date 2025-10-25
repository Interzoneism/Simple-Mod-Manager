using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace VintageStoryModManager.Views.Dialogs;

public partial class CloudModlistDetailsDialog : Window
{
    private bool _isUpdatingConfigOptionSelection;
    private bool _isUpdatingSelectAllCheckBox;

    public CloudModlistDetailsDialog(Window owner, string? suggestedName, IEnumerable<CloudModConfigOption>? configOptions)
    {
        ConfigOptions = new ObservableCollection<CloudModConfigOption>(
            (configOptions ?? Enumerable.Empty<CloudModConfigOption>())
                .Where(option => option is not null)
                .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase));

        InitializeComponent();

        foreach (var option in ConfigOptions)
        {
            option.PropertyChanged += ConfigOption_OnPropertyChanged;
        }

        UpdateSelectAllState();

        Owner = owner;
        NameTextBox.Text = string.IsNullOrWhiteSpace(suggestedName)
            ? string.Empty
            : suggestedName;
        NameTextBox.SelectAll();
        UpdateConfirmButtonState();
    }

    public ObservableCollection<CloudModConfigOption> ConfigOptions { get; }

    public bool HasConfigOptions => ConfigOptions.Count > 0;

    public string ModlistName => NameTextBox.Text.Trim();

    public string? ModlistDescription => string.IsNullOrWhiteSpace(DescriptionTextBox.Text)
        ? null
        : DescriptionTextBox.Text.Trim();

    public string? ModlistVersion => string.IsNullOrWhiteSpace(VersionTextBox.Text)
        ? null
        : VersionTextBox.Text.Trim();

    public IReadOnlyList<CloudModConfigOption> GetSelectedConfigOptions()
    {
        return ConfigOptions.Where(option => option.IsSelected).ToList();
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            return;
        }

        DialogResult = true;
    }

    private void NameTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateConfirmButtonState();
    }

    private void UpdateConfirmButtonState()
    {
        if (ConfirmButton is null)
        {
            return;
        }

        string? modlistName = NameTextBox?.Text;
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
        if (_isUpdatingSelectAllCheckBox)
        {
            return;
        }

        _isUpdatingConfigOptionSelection = true;

        var shouldSelectAll = SelectAllCheckBox.IsChecked == true;

        foreach (var option in ConfigOptions)
        {
            option.IsSelected = shouldSelectAll;
        }

        _isUpdatingConfigOptionSelection = false;

        UpdateSelectAllState();
    }

    private void ConfigOption_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(CloudModConfigOption.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        if (_isUpdatingConfigOptionSelection)
        {
            return;
        }

        UpdateSelectAllState();
    }

    private void UpdateSelectAllState()
    {
        if (SelectAllCheckBox is null)
        {
            return;
        }

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
            _ => null,
        };

        _isUpdatingSelectAllCheckBox = false;
    }

    public sealed class CloudModConfigOption : INotifyPropertyChanged
    {
        private bool _isSelected;

        public CloudModConfigOption(string modId, string displayName, string configPath, bool isSelected)
        {
            ModId = modId ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? ModId : displayName;
            ConfigPath = configPath ?? string.Empty;
            _isSelected = isSelected;
        }

        public string ModId { get; }

        public string DisplayName { get; }

        public string ConfigPath { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
