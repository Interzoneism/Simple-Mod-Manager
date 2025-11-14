using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;
using VintageStoryModManager.ViewModels;

namespace VintageStoryModManager.Design;

/// <summary>
///     Provides design-time data for the <see cref="VintageStoryModManager.Views.MainWindow" />.
/// </summary>
public sealed class MainWindowDesignData
{
    private readonly ObservableCollection<ModListItemViewModel> _mods;
    private readonly ObservableCollection<SortOption> _sortOptions;
    private SortOption? _selectedSortOption;

    public MainWindowDesignData()
    {
        DataDirectory = @"C:\\Users\\Player\\AppData\\Roaming\\VintagestoryData";

        _mods = new ObservableCollection<ModListItemViewModel>(DesignDataFactory.CreateSampleMods());
        ModsView = CollectionViewSource.GetDefaultView(_mods);

        _sortOptions = new ObservableCollection<SortOption>
        {
            new("Active (Active → Inactive)",
                (nameof(ModListItemViewModel.ActiveSortOrder), ListSortDirection.Ascending),
                (nameof(ModListItemViewModel.NameSortKey), ListSortDirection.Ascending)),
            new("Name", (nameof(ModListItemViewModel.NameSortKey), ListSortDirection.Ascending)),
            new("Version", (nameof(ModListItemViewModel.VersionDisplay), ListSortDirection.Ascending))
        };
        SortOptions = new ReadOnlyObservableCollection<SortOption>(_sortOptions);

        RefreshCommand = new AsyncRelayCommand(() => Task.CompletedTask);

        SelectedSortOption = SortOptions.FirstOrDefault();

        TotalMods = _mods.Count;
        ActiveMods = _mods.Count(mod => mod.IsActive);
        UpdatableModsCount = _mods.Count(mod => mod.CanUpdate);

        StatusMessage = "Loaded sample mods.";
    }

    public string DataDirectory { get; }

    public ICollectionView ModsView { get; }

    public ReadOnlyObservableCollection<SortOption> SortOptions { get; }

    public SortOption? SelectedSortOption
    {
        get => _selectedSortOption;
        set
        {
            _selectedSortOption = value;
            _selectedSortOption?.Apply(ModsView);
        }
    }

    public IAsyncRelayCommand RefreshCommand { get; }

    public bool IsBusy => false;

    public string StatusMessage { get; }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool IsErrorStatus => false;

    public int TotalMods { get; }

    public int ActiveMods { get; }

    public string SummaryText => TotalMods == 0
        ? "No mods found."
        : $"{ActiveMods} active of {TotalMods} mods";

    public int UpdatableModsCount { get; }

    public string UpdateAllButtonLabel => UpdatableModsCount == 0
        ? "Update All"
        : $"Update All ({UpdatableModsCount})";

    public string UpdateAllModsMenuHeader => UpdatableModsCount == 0
        ? "_Update All Mods"
        : $"_Update All Mods ({UpdatableModsCount})";

    public string NoModsFoundMessage =>
        $"No mods found. If this is unexpected, verify that your VintageStoryData folder is correctly set: {DataDirectory}. You can change it in the File Menu.";
}