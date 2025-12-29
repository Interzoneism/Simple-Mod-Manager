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

    /// <summary>
    ///     Applies this sort configuration to the specified collection view.
    ///     Skips application if the sort is already correctly applied to avoid
    ///     unnecessary refresh operations that can be expensive with large collections.
    /// </summary>
    public void Apply(ICollectionView view)
    {
        if (view == null) return;

        // Check if sort is already correctly applied to avoid expensive refresh
        if (IsSortAlreadyApplied(view)) return;

        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            foreach (var sort in _sorts) view.SortDescriptions.Add(new SortDescription(sort.Property, sort.Direction));
        }
    }

    /// <summary>
    ///     Checks if this sort configuration is already applied to the view.
    /// </summary>
    private bool IsSortAlreadyApplied(ICollectionView view)
    {
        var viewSorts = view.SortDescriptions;
        if (viewSorts.Count != _sorts.Length) return false;

        for (var i = 0; i < _sorts.Length; i++)
        {
            var viewSort = viewSorts[i];
            var targetSort = _sorts[i];
            var propertyMismatch = !string.Equals(viewSort.PropertyName, targetSort.Property, StringComparison.Ordinal);
            if (propertyMismatch || viewSort.Direction != targetSort.Direction)
                return false;
        }

        return true;
    }

    public override string ToString()
    {
        return DisplayName;
    }
}