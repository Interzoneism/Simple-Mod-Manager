using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VintageStoryModManager.ViewModels;

public sealed class UpdateModsDialogViewModel : ObservableObject
{
    private bool _isUpdatingSelectAll;
    private bool? _selectAllState;

    public UpdateModsDialogViewModel(IEnumerable<UpdateModSelectionViewModel> mods)
    {
        if (mods is null) throw new ArgumentNullException(nameof(mods));

        Mods = new ObservableCollection<UpdateModSelectionViewModel>(mods);
        foreach (var mod in Mods) mod.PropertyChanged += OnModPropertyChanged;

        UpdateSelectAllState();
    }

    public ObservableCollection<UpdateModSelectionViewModel> Mods { get; }

    public bool HasSelection => Mods.Any(m => m.IsSelected);

    public bool? SelectAllState
    {
        get => _selectAllState;
        set
        {
            if (_isUpdatingSelectAll) return;

            if (SetProperty(ref _selectAllState, value))
            {
                if (!value.HasValue) return;

                try
                {
                    _isUpdatingSelectAll = true;
                    var target = value.Value;
                    foreach (var mod in Mods) mod.IsSelected = target;
                }
                finally
                {
                    _isUpdatingSelectAll = false;
                }

                UpdateSelectAllState();
            }
        }
    }

    public IReadOnlyList<ModListItemViewModel> GetSelectedMods()
    {
        return Mods.Where(m => m.IsSelected).Select(m => m.Mod).ToList();
    }

    public bool RemoveMod(UpdateModSelectionViewModel? mod)
    {
        if (mod is null) return false;

        if (!Mods.Remove(mod)) return false;

        mod.PropertyChanged -= OnModPropertyChanged;
        UpdateSelectAllState();
        return true;
    }

    private void OnModPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(UpdateModSelectionViewModel.IsSelected),
                StringComparison.Ordinal)) return;

        UpdateSelectAllState();
    }

    private void UpdateSelectAllState()
    {
        var allSelected = Mods.Count > 0 && Mods.All(m => m.IsSelected);
        var anySelected = Mods.Any(m => m.IsSelected);

        var newState = allSelected ? true : anySelected ? (bool?)null : false;

        if (_selectAllState != newState)
            try
            {
                _isUpdatingSelectAll = true;
                _selectAllState = newState;
                OnPropertyChanged(nameof(SelectAllState));
            }
            finally
            {
                _isUpdatingSelectAll = false;
            }

        OnPropertyChanged(nameof(HasSelection));
    }
}