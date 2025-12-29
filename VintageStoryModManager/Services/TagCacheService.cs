using System.Collections.Concurrent;

namespace VintageStoryModManager.Services;

/// <summary>
///     Provides high-performance caching and querying for mod tags.
///     Uses concurrent collections and efficient lookup structures to minimize
///     allocations and improve tag filtering performance.
/// </summary>
internal sealed class TagCacheService
{
    /// <summary>
    ///     Thread-safe cache of all unique tags observed across all mods.
    ///     Key is the normalized (lowercase) tag name, value is the display name.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _globalTagIndex =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Maps mod IDs to their cached tags. Uses case-insensitive comparison for mod IDs.
    /// </summary>
    private readonly ConcurrentDictionary<string, TagSet> _modTagCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Cache for sorted tag lists to avoid repeated sorting operations.
    /// </summary>
    private volatile IReadOnlyList<string>? _sortedTagListCache;

    /// <summary>
    ///     Version number that increments when the cache is modified.
    ///     Used to invalidate dependent caches.
    /// </summary>
    private volatile int _cacheVersion;

    /// <summary>
    ///     Gets the current cache version for change detection.
    /// </summary>
    public int Version => _cacheVersion;

    /// <summary>
    ///     Gets or creates a TagSet for the specified mod.
    /// </summary>
    public TagSet GetOrCreateModTags(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return TagSet.Empty;

        return _modTagCache.GetOrAdd(modId, _ => new TagSet());
    }

    /// <summary>
    ///     Updates the tags for a specific mod.
    /// </summary>
    public void SetModTags(string modId, IReadOnlyList<string>? tags)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return;

        var tagSet = GetOrCreateModTags(modId);
        tagSet.SetTags(tags);

        // Update global index with any new tags
        if (tags is { Count: > 0 })
        {
            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;
                var trimmed = tag.Trim();
                if (trimmed.Length == 0) continue;

                // Store with lowercase key for case-insensitive lookup,
                // but preserve original casing in value
                _globalTagIndex.TryAdd(trimmed.ToLowerInvariant(), trimmed);
            }
        }

        InvalidateSortedCache();
    }

    /// <summary>
    ///     Gets the tags for a specific mod.
    /// </summary>
    public IReadOnlyList<string> GetModTags(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return Array.Empty<string>();

        return _modTagCache.TryGetValue(modId, out var tagSet)
            ? tagSet.GetTags()
            : Array.Empty<string>();
    }

    /// <summary>
    ///     Checks if a mod has all the specified required tags.
    ///     Optimized for common cases (0-3 required tags).
    /// </summary>
    public bool ModHasAllTags(string modId, IReadOnlyList<string> requiredTags)
    {
        if (requiredTags.Count == 0)
            return true;

        if (string.IsNullOrWhiteSpace(modId))
            return false;

        if (!_modTagCache.TryGetValue(modId, out var tagSet))
            return false;

        return tagSet.ContainsAll(requiredTags);
    }

    /// <summary>
    ///     Gets all unique tags as a sorted list.
    ///     The result is cached and only recomputed when the cache changes.
    /// </summary>
    public IReadOnlyList<string> GetAllTagsSorted()
    {
        var cached = _sortedTagListCache;
        if (cached is not null)
            return cached;

        var tags = _globalTagIndex.Values.ToList();
        tags.Sort(StringComparer.OrdinalIgnoreCase);

        // Store and return the sorted list
        _sortedTagListCache = tags.AsReadOnly();
        return _sortedTagListCache;
    }

    /// <summary>
    ///     Clears the entire tag cache.
    /// </summary>
    public void Clear()
    {
        _modTagCache.Clear();
        _globalTagIndex.Clear();
        InvalidateSortedCache();
    }

    /// <summary>
    ///     Removes tags for a specific mod from the cache.
    /// </summary>
    public void RemoveModTags(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return;

        _modTagCache.TryRemove(modId, out _);
        InvalidateSortedCache();
    }

    /// <summary>
    ///     Rebuilds the global tag index from all cached mod tags.
    ///     Call this after bulk updates to ensure consistency.
    /// </summary>
    public void RebuildGlobalIndex()
    {
        _globalTagIndex.Clear();

        foreach (var modTagSet in _modTagCache.Values)
        {
            foreach (var tag in modTagSet.GetTags())
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;
                var trimmed = tag.Trim();
                if (trimmed.Length == 0) continue;

                _globalTagIndex.TryAdd(trimmed.ToLowerInvariant(), trimmed);
            }
        }

        InvalidateSortedCache();
    }

    private void InvalidateSortedCache()
    {
        _sortedTagListCache = null;
        Interlocked.Increment(ref _cacheVersion);
    }

    /// <summary>
    ///     Represents an optimized, thread-safe collection of tags for a single mod.
    /// </summary>
    public sealed class TagSet
    {
        /// <summary>
        ///     A static empty TagSet for use when no tags are available.
        /// </summary>
        public static readonly TagSet Empty = new();

        private readonly object _lock = new();
        private volatile IReadOnlyList<string> _tags = Array.Empty<string>();
        private volatile HashSet<string>? _tagLookup;

        /// <summary>
        ///     Gets the current tags as a list.
        /// </summary>
        public IReadOnlyList<string> GetTags() => _tags;

        /// <summary>
        ///     Sets the tags for this mod.
        /// </summary>
        public void SetTags(IReadOnlyList<string>? tags)
        {
            lock (_lock)
            {
                if (tags is null || tags.Count == 0)
                {
                    _tags = Array.Empty<string>();
                    _tagLookup = null;
                    return;
                }

                // Normalize and deduplicate tags
                var normalized = new List<string>(tags.Count);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var tag in tags)
                {
                    if (string.IsNullOrWhiteSpace(tag)) continue;
                    var trimmed = tag.Trim();
                    if (trimmed.Length == 0) continue;

                    if (seen.Add(trimmed))
                        normalized.Add(trimmed);
                }

                if (normalized.Count == 0)
                {
                    _tags = Array.Empty<string>();
                    _tagLookup = null;
                    return;
                }

                _tags = normalized.ToArray();
                _tagLookup = null; // Lazily rebuild lookup when needed
            }
        }

        /// <summary>
        ///     Checks if this tag set contains all the specified required tags.
        ///     Uses optimized search strategies based on the number of required tags.
        /// </summary>
        public bool ContainsAll(IReadOnlyList<string> requiredTags)
        {
            if (requiredTags.Count == 0)
                return true;

            var tags = _tags;
            if (tags.Count == 0)
                return false;

            // Optimization: For 1-3 required tags, use linear search to avoid HashSet overhead
            if (requiredTags.Count <= 3)
                return ContainsAllLinear(tags, requiredTags);

            // For more required tags, use HashSet for O(1) lookups
            return ContainsAllWithSet(tags, requiredTags);
        }

        /// <summary>
        ///     Checks if this tag set contains the specified tag.
        /// </summary>
        public bool Contains(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return false;

            var trimmed = tag.Trim();
            if (trimmed.Length == 0)
                return false;

            var tags = _tags;
            if (tags.Count == 0)
                return false;

            // For small tag sets, linear search is faster
            if (tags.Count <= 10)
            {
                foreach (var t in tags)
                {
                    if (string.Equals(t, trimmed, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            // For larger tag sets, use the lookup HashSet
            return EnsureLookup().Contains(trimmed);
        }

        private static bool ContainsAllLinear(IReadOnlyList<string> tags, IReadOnlyList<string> requiredTags)
        {
            // Use stack allocation for small arrays to avoid heap allocation
            Span<bool> found = stackalloc bool[requiredTags.Count];
            var foundCount = 0;

            foreach (var tag in tags)
            {
                if (foundCount == requiredTags.Count)
                    return true;

                for (var i = 0; i < requiredTags.Count; i++)
                {
                    if (found[i]) continue;

                    if (string.Equals(tag, requiredTags[i], StringComparison.OrdinalIgnoreCase))
                    {
                        found[i] = true;
                        foundCount++;
                        break;
                    }
                }
            }

            return foundCount == requiredTags.Count;
        }

        private bool ContainsAllWithSet(IReadOnlyList<string> tags, IReadOnlyList<string> requiredTags)
        {
            var lookup = EnsureLookup();

            foreach (var required in requiredTags)
            {
                if (!lookup.Contains(required))
                    return false;
            }

            return true;
        }

        private HashSet<string> EnsureLookup()
        {
            var lookup = _tagLookup;
            if (lookup is not null)
                return lookup;

            lock (_lock)
            {
                lookup = _tagLookup;
                if (lookup is not null)
                    return lookup;

                var tags = _tags;
                lookup = new HashSet<string>(tags.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var tag in tags)
                    lookup.Add(tag);

                _tagLookup = lookup;
                return lookup;
            }
        }
    }
}
