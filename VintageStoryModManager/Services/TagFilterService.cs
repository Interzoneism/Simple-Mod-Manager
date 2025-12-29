using System.Collections.Concurrent;
using VintageStoryModManager.ViewModels;

namespace VintageStoryModManager.Services;

/// <summary>
///     Provides centralized, optimized tag filtering operations for mod lists.
///     This service manages tag filter state and provides efficient filtering algorithms.
/// </summary>
internal sealed class TagFilterService
{
    private readonly TagCacheService _tagCache;
    private readonly object _filterLock = new();

    /// <summary>
    ///     Currently selected tags for installed mods view.
    /// </summary>
    private readonly HashSet<string> _selectedInstalledTags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Cached array of selected installed tags for efficient filtering.
    ///     Updated atomically under _filterLock when selection changes.
    ///     Readers access this without locking (volatile ensures visibility).
    ///     The array is immutable once published, providing thread-safe reads.
    /// </summary>
    private volatile IReadOnlyList<string> _cachedSelectedInstalledTags = Array.Empty<string>();


    /// <summary>
    ///     Cache for all available tags in the installed mods view.
    /// </summary>
    private volatile IReadOnlyList<string> _installedAvailableTags = Array.Empty<string>();


    /// <summary>
    ///     Version number for change detection.
    /// </summary>
    private volatile int _filterVersion;

    public TagFilterService(TagCacheService tagCache)
    {
        _tagCache = tagCache ?? throw new ArgumentNullException(nameof(tagCache));
    }

    /// <summary>
    ///     Gets the current filter version for change detection.
    /// </summary>
    public int FilterVersion => _filterVersion;

    /// <summary>
    ///     Gets whether any tags are currently selected.
    /// </summary>
    public bool HasSelectedTags
    {
        get
        {
            lock (_filterLock)
            {
                return _selectedInstalledTags.Count > 0;
            }
        }
    }

    /// <summary>
    ///     Gets whether any installed mod tags are selected.
    /// </summary>
    public bool HasSelectedInstalledTags
    {
        get
        {
            lock (_filterLock)
            {
                return _selectedInstalledTags.Count > 0;
            }
        }
    }

    /// <summary>
    ///     Gets the currently selected installed mod tags.
    /// </summary>
    public IReadOnlyList<string> GetSelectedInstalledTags()
    {
        lock (_filterLock)
        {
            return _selectedInstalledTags.ToArray();
        }
    }


    /// <summary>
    ///     Updates the selected installed mod tags.
    /// </summary>
    public bool SetSelectedInstalledTags(IEnumerable<string>? tags)
    {
        lock (_filterLock)
        {
            var newTags = NormalizeTags(tags);
            if (TagSetsEqual(_selectedInstalledTags, newTags))
                return false;

            _selectedInstalledTags.Clear();
            foreach (var tag in newTags)
                _selectedInstalledTags.Add(tag);

            // Update cached array to avoid allocation during filtering
            _cachedSelectedInstalledTags = _selectedInstalledTags.Count > 0
                ? _selectedInstalledTags.ToArray()
                : Array.Empty<string>();

            IncrementVersion();
            return true;
        }
    }

    /// <summary>
    ///     Toggles a tag's selection state for installed mods.
    /// </summary>
    public bool ToggleInstalledTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        var trimmed = tag.Trim();
        if (trimmed.Length == 0)
            return false;

        lock (_filterLock)
        {
            bool changed;
            if (_selectedInstalledTags.Contains(trimmed))
            {
                changed = _selectedInstalledTags.Remove(trimmed);
            }
            else
            {
                // Add returns true if the item was not already present
                changed = _selectedInstalledTags.Add(trimmed);
            }

            if (changed)
            {
                // Update cached array to avoid allocation during filtering
                _cachedSelectedInstalledTags = _selectedInstalledTags.Count > 0
                    ? _selectedInstalledTags.ToArray()
                    : Array.Empty<string>();
                IncrementVersion();
            }

            return changed;
        }
    }


    /// <summary>
    ///     Clears all selected tags for both views.
    /// </summary>
    public void ClearAllSelections()
    {
        lock (_filterLock)
        {
            if (_selectedInstalledTags.Count == 0)
                return;

            _selectedInstalledTags.Clear();

            // Update cached arrays
            _cachedSelectedInstalledTags = Array.Empty<string>();

            IncrementVersion();
        }
    }

    /// <summary>
    ///     Clears selected installed mod tags.
    /// </summary>
    public void ClearInstalledTagSelection()
    {
        lock (_filterLock)
        {
            if (_selectedInstalledTags.Count == 0)
                return;

            _selectedInstalledTags.Clear();
            _cachedSelectedInstalledTags = Array.Empty<string>();
            IncrementVersion();
        }
    }

    /// <summary>
    ///     Filters a mod based on the currently selected installed mod tags.
    ///     Uses cached tag array to avoid allocation per call during filtering.
    /// </summary>
    public bool PassesInstalledTagFilter(IReadOnlyList<string>? modTags)
    {
        // Read the cached array without locking for better performance during filtering.
        // The cached array is updated atomically via volatile read/write.
        var requiredTags = _cachedSelectedInstalledTags;

        if (requiredTags.Count == 0)
            return true;

        return ContainsAllTags(modTags, requiredTags);
    }

    /// <summary>
    ///     Updates the available tags for installed mods.
    /// </summary>
    public void SetInstalledAvailableTags(IEnumerable<string> tags)
    {
        var sorted = SortAndDeduplicate(tags);
        _installedAvailableTags = sorted;
    }

    /// <summary>
    ///     Gets the available tags for installed mods.
    /// </summary>
    public IReadOnlyList<string> GetInstalledAvailableTags()
    {
        // Include selected tags in the result to ensure they're always visible
        IReadOnlyList<string> selected;
        lock (_filterLock)
        {
            selected = _selectedInstalledTags.ToArray();
        }

        if (selected.Count == 0)
            return _installedAvailableTags;

        return MergeTags(_installedAvailableTags, selected);
    }

    /// <summary>
    ///     Collects and updates available tags from a collection of mods.
    /// </summary>
    public void UpdateInstalledAvailableTagsFromMods(IEnumerable<ModListItemViewModel> mods)
    {
        var allTags = EnumerateModTags(mods);
        SetInstalledAvailableTags(allTags);
    }

    /// <summary>
    ///     Checks if a tag is currently selected for installed mods.
    /// </summary>
    public bool IsInstalledTagSelected(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        lock (_filterLock)
        {
            return _selectedInstalledTags.Contains(tag.Trim());
        }
    }


    private void IncrementVersion()
    {
        Interlocked.Increment(ref _filterVersion);
    }

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags)
    {
        if (tags is null)
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var trimmed = tag.Trim();
            if (trimmed.Length == 0) continue;
            result.Add(trimmed);
        }

        return result;
    }

    private static bool TagSetsEqual(HashSet<string> set, IReadOnlyList<string> list)
    {
        if (set.Count != list.Count)
            return false;

        foreach (var tag in list)
        {
            if (!set.Contains(tag))
                return false;
        }

        return true;
    }

    private static bool ContainsAllTags(IReadOnlyList<string>? modTags, IReadOnlyList<string> requiredTags)
    {
        if (requiredTags.Count == 0)
            return true;

        if (modTags is null || modTags.Count == 0)
            return false;

        // Optimization: For 1-3 required tags, use linear search
        if (requiredTags.Count <= 3)
        {
            Span<bool> found = stackalloc bool[requiredTags.Count];
            var foundCount = 0;

            foreach (var tag in modTags)
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;
                var trimmed = tag.Trim();
                if (trimmed.Length == 0) continue;

                for (var i = 0; i < requiredTags.Count; i++)
                {
                    if (found[i]) continue;

                    if (string.Equals(trimmed, requiredTags[i], StringComparison.OrdinalIgnoreCase))
                    {
                        found[i] = true;
                        foundCount++;
                        if (foundCount == requiredTags.Count)
                            return true;
                        break;
                    }
                }
            }

            return foundCount == requiredTags.Count;
        }

        // For more required tags, build a HashSet
        var lookup = new HashSet<string>(modTags.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var tag in modTags)
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var trimmed = tag.Trim();
            if (trimmed.Length > 0)
                lookup.Add(trimmed);
        }

        foreach (var required in requiredTags)
        {
            if (!lookup.Contains(required))
                return false;
        }

        return true;
    }

    private static IEnumerable<string> EnumerateModTags(IEnumerable<ModListItemViewModel> mods)
    {
        foreach (var mod in mods)
        {
            if (mod.DatabaseTags is not { Count: > 0 } tags) continue;

            foreach (var tag in tags)
                yield return tag;
        }
    }

    private static IReadOnlyList<string> SortAndDeduplicate(IEnumerable<string> tags)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var trimmed = tag.Trim();
            if (trimmed.Length > 0)
                unique.Add(trimmed);
        }

        if (unique.Count == 0)
            return Array.Empty<string>();

        var list = unique.ToList();
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list.AsReadOnly();
    }

    private static IReadOnlyList<string> MergeTags(IReadOnlyList<string> existing, IReadOnlyList<string> additional)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in existing)
            unique.Add(tag);

        foreach (var tag in additional)
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var trimmed = tag.Trim();
            if (trimmed.Length > 0)
                unique.Add(trimmed);
        }

        var list = unique.ToList();
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list.AsReadOnly();
    }
}
