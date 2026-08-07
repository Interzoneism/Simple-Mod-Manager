using System;
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
        string? defaultCreatedBy = null,
        string? defaultVersion = null,
        string? defaultGameVersion = null,
        SaveInstalledModsDialogResult defaultAction = SaveInstalledModsDialogResult.SaveJson)
    {
        ConfigOptions = new ObservableCollection<ModConfigOption>(
            (configOptions ?? Array.Empty<ModConfigOption>())
            .Where(option => option is not null)
            .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase));

        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(defaultListName)) NameTextBox.Text = defaultListName.Trim();
        if (!string.IsNullOrWhiteSpace(defaultVersion)) VersionTextBox.Text = defaultVersion.Trim();
        if (!string.IsNullOrWhiteSpace(defaultGameVersion)) GameVersionTextBox.Text = defaultGameVersion.Trim();

        if (!string.IsNullOrWhiteSpace(defaultCreatedBy))
            CreatedByTextBox.Text = defaultCreatedBy.Trim();
        else if (!string.IsNullOrWhiteSpace(Environment.UserName)) CreatedByTextBox.Text = Environment.UserName.Trim();

        foreach (var option in ConfigOptions) option.PropertyChanged += ConfigOption_OnPropertyChanged;

        SetDefaultAction(defaultAction);

        UpdateActionButtonsState();
        UpdateSelectAllState();
    }

    public ObservableCollection<ModConfigOption> ConfigOptions { get; }

    public bool HasConfigOptions => ConfigOptions.Count > 0;

    public string ListName => NameTextBox.Text.Trim();

    public string? Version => NormalizeOptionalText(VersionTextBox.Text);

    public string? VintageStoryVersion => NormalizeOptionalText(GameVersionTextBox.Text);

    public string? Description => NormalizeOptionalText(DescriptionTextBox.Text);

    public string? CreatedBy => NormalizeOptionalText(CreatedByTextBox.Text);

    public SaveInstalledModsDialogResult SelectedAction { get; private set; } = SaveInstalledModsDialogResult.SaveJson;

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
        UpdateActionButtonsState();
        NameTextBox.Focus();
        NameTextBox.SelectAll();
        UpdateSelectAllState();
    }

    private void NameTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateActionButtonsState();
    }

    private void UpdateActionButtonsState()
    {
        var hasName = !string.IsNullOrWhiteSpace(NameTextBox?.Text);

        if (ConfirmButton is not null) ConfirmButton.IsEnabled = hasName;
        if (SaveAsPdfButton is not null) SaveAsPdfButton.IsEnabled = hasName;
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text)) return;

        SelectedAction = SaveInstalledModsDialogResult.SaveJson;
        DialogResult = true;
        Close();
    }

    private void SaveAsPdfButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text)) return;

        SelectedAction = SaveInstalledModsDialogResult.SavePdf;
        DialogResult = true;
        Close();
    }

    private void SetDefaultAction(SaveInstalledModsDialogResult action)
    {
        if (ConfirmButton is null || SaveAsPdfButton is null) return;

        ConfirmButton.IsDefault = action == SaveInstalledModsDialogResult.SaveJson;
        SaveAsPdfButton.IsDefault = action == SaveInstalledModsDialogResult.SavePdf;
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

public enum SaveInstalledModsDialogResult
{
    SaveJson,
    SavePdf
}
