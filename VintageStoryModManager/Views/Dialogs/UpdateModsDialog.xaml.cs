using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using VintageStoryModManager.Models;
using VintageStoryModManager.Services;
using VintageStoryModManager.ViewModels;

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

    protected override void OnClosed(EventArgs e)
    {
        foreach (UpdateModSelectionViewModel item in _viewModel.Mods)
        {
            item.PropertyChanged -= OnSelectionPropertyChanged;
        }

        base.OnClosed(e);
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
