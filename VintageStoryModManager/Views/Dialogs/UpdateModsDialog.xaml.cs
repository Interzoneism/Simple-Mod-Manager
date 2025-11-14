using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using VintageStoryModManager.Models;
using VintageStoryModManager.Services;
using VintageStoryModManager.ViewModels;
using Button = System.Windows.Controls.Button;
using WpfMessageBox = VintageStoryModManager.Services.ModManagerMessageBox;
using WinFormsClipboard = System.Windows.Forms.Clipboard;

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

        var items = mods
            .Select(mod =>
            {
                var isSelected = !_configuration.IsModExcludedFromBulkUpdates(mod.ModId);
                ModReleaseInfo? overrideRelease = null;
                if (releaseOverrides != null) releaseOverrides.TryGetValue(mod, out overrideRelease);

                return new UpdateModSelectionViewModel(mod, isSelected, overrideRelease,
                    _configuration.EnableServerOptions);
            })
            .ToList();

        _viewModel = new UpdateModsDialogViewModel(items);
        DataContext = _viewModel;

        foreach (var item in _viewModel.Mods) item.PropertyChanged += OnSelectionPropertyChanged;
    }

    public IReadOnlyList<ModListItemViewModel> SelectedMods => _viewModel.GetSelectedMods();

    private void UpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void SkipVersionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: UpdateModSelectionViewModel selection }) return;

        var versionToSkip = selection.TargetUpdateVersion;
        if (string.IsNullOrWhiteSpace(versionToSkip)) return;

        var message =
            $"This will permanently skip version {versionToSkip} and prevent any further update prompts for it.";
        var confirmation = WpfMessageBox.Show(
            this,
            message,
            "Simple VS Manager",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes) return;

        _configuration.SkipModVersion(selection.Mod.ModId, versionToSkip);
        _configuration.SetModExcludedFromBulkUpdates(selection.Mod.ModId, false);

        selection.Mod.RefreshSkippedUpdateState();
        RemoveMod(selection);
    }

    private void CopyServerCommandButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: UpdateModSelectionViewModel selection }) return;

        var command = selection.LatestInstallCommand;
        if (string.IsNullOrWhiteSpace(command)) return;

        try
        {
            WinFormsClipboard.SetDataObject(command, true, 10, 100);
            var trimmedCommand = command.Trim();
            var statusMessage = $"Copied {trimmedCommand}";
            if (Owner is MainWindow mainWindow)
                mainWindow.ReportStatus(statusMessage);
            else
                StatusLogService.AppendStatus(statusMessage, false);
        }
        catch (ExternalException)
        {
            WpfMessageBox.Show(
                this,
                "Failed to copy the server install command. Please try again.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var item in _viewModel.Mods) item.PropertyChanged -= OnSelectionPropertyChanged;

        base.OnClosed(e);
    }

    private void RemoveMod(UpdateModSelectionViewModel selection)
    {
        selection.PropertyChanged -= OnSelectionPropertyChanged;
        _viewModel.RemoveMod(selection);
    }

    private void OnSelectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(UpdateModSelectionViewModel.IsSelected),
                StringComparison.Ordinal)) return;

        if (sender is not UpdateModSelectionViewModel selection) return;

        var isExcluded = !selection.IsSelected;
        _configuration.SetModExcludedFromBulkUpdates(selection.Mod.ModId, isExcluded);
    }
}