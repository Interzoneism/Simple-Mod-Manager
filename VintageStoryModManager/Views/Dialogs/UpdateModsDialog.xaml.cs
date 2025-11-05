using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using VintageStoryModManager.Models;
using VintageStoryModManager.Services;
using VintageStoryModManager.ViewModels;
using WpfMessageBox = VintageStoryModManager.Services.ModManagerMessageBox;

namespace VintageStoryModManager.Views.Dialogs;

public partial class UpdateModsDialog : Window
{
    private readonly UserConfigurationService _configuration;
    private readonly UpdateModsDialogViewModel _viewModel;

    public UpdateModsDialog(
        UserConfigurationService configuration,
        IEnumerable<ModListItemViewModel> mods,
        IReadOnlyDictionary<ModListItemViewModel, ModReleaseInfo>? releaseOverrides)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(mods);

        InitializeComponent();

        _configuration = configuration;

        List<UpdateModSelectionViewModel> items = mods
            .Select(mod =>
            {
                bool isSelected = !_configuration.IsModExcludedFromBulkUpdates(mod.ModId);
                ModReleaseInfo? overrideRelease = null;
                if (releaseOverrides != null)
                {
                    releaseOverrides.TryGetValue(mod, out overrideRelease);
                }

                return new UpdateModSelectionViewModel(mod, isSelected, overrideRelease);
            })
            .ToList();

        _viewModel = new UpdateModsDialogViewModel(items);
        DataContext = _viewModel;

        foreach (UpdateModSelectionViewModel item in _viewModel.Mods)
        {
            item.PropertyChanged += OnSelectionPropertyChanged;
        }
    }

    public IReadOnlyList<ModListItemViewModel> SelectedMods => _viewModel.GetSelectedMods();

    private void UpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void SkipVersionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: UpdateModSelectionViewModel selection })
        {
            return;
        }

        string? versionToSkip = selection.TargetUpdateVersion;
        if (string.IsNullOrWhiteSpace(versionToSkip))
        {
            return;
        }

        string message = $"This will permanently skip version {versionToSkip} and prevent any further update prompts for it.";
        MessageBoxResult confirmation = WpfMessageBox.Show(
            this,
            message,
            "Simple VS Manager",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _configuration.SkipModVersion(selection.Mod.ModId, versionToSkip);
        _configuration.SetModExcludedFromBulkUpdates(selection.Mod.ModId, isExcluded: false);

        selection.Mod.RefreshSkippedUpdateState();
        RemoveMod(selection);
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (UpdateModSelectionViewModel item in _viewModel.Mods)
        {
            item.PropertyChanged -= OnSelectionPropertyChanged;
        }

        base.OnClosed(e);
    }

    private void RemoveMod(UpdateModSelectionViewModel selection)
    {
        selection.PropertyChanged -= OnSelectionPropertyChanged;
        _viewModel.RemoveMod(selection);
    }

    private void OnSelectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(UpdateModSelectionViewModel.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        if (sender is not UpdateModSelectionViewModel selection)
        {
            return;
        }

        bool isExcluded = !selection.IsSelected;
        _configuration.SetModExcludedFromBulkUpdates(selection.Mod.ModId, isExcluded);
    }
}
