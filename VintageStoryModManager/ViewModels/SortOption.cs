using System.ComponentModel;

namespace VintageStoryModManager.ViewModels;

/// <summary>
///     Represents a reusable sort configuration for the mod list.
/// </summary>
public sealed class SortOption
{
    private readonly (string Property, ListSortDirection Direction)[] _sorts;

    public SortOption(string displayName, params (string Property, ListSortDirection Direction)[] sorts)
    {
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        _sorts = sorts ?? Array.Empty<(string, ListSortDirection)>();
    }

    public string DisplayName { get; }

    public IReadOnlyList<(string Property, ListSortDirection Direction)> SortDescriptions => _sorts;

    public void Apply(ICollectionView view)
    {
        if (view == null) return;

        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            foreach (var sort in _sorts) view.SortDescriptions.Add(new SortDescription(sort.Property, sort.Direction));
        }
    }

    public override string ToString()
    {
        return DisplayName;
    }
}