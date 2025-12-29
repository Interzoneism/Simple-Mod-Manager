using System.IO;

namespace VintageStoryModManager.Services;

/// <summary>
///     Monitors mod directories for file system changes and tracks which mods need to be rescanned.
/// </summary>
public sealed class ModDirectoryWatcher : IDisposable
{
    private static readonly char[] DirectorySeparators =
    {
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
    };

    private readonly ModDiscoveryService _discoveryService;
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private bool _isWatching;

    private bool _requiresFullRescan;

    /// <summary>
    ///     Occurs when file system changes are detected in the watched directories.
    /// </summary>
    public event EventHandler? ChangesDetected;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ModDirectoryWatcher"/> class.
    /// </summary>
    /// <param name="discoveryService">The mod discovery service that provides search paths.</param>
    public ModDirectoryWatcher(ModDiscoveryService discoveryService)
    {
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
    }

    /// <summary>
    ///     Gets a value indicating whether there are pending file system changes that haven't been consumed.
    /// </summary>
    public bool HasPendingChanges
    {
        get
        {
            lock (_syncRoot)
            {
                return _requiresFullRescan || _pendingPaths.Count > 0;
            }
        }
    }

    /// <summary>
    ///     Gets a value indicating whether the watcher is actively monitoring directories.
    /// </summary>
    public bool IsWatching
    {
        get
        {
            lock (_syncRoot)
            {
                return _isWatching;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed) return;

            foreach (var watcher in _watchers.Values)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnChanged;
                watcher.Changed -= OnChanged;
                watcher.Deleted -= OnChanged;
                watcher.Renamed -= OnRenamed;
                watcher.Dispose();
            }

            _watchers.Clear();
            _pendingPaths.Clear();
            _disposed = true;
            _isWatching = false;
        }
    }

    /// <summary>
    ///     Ensures that file system watchers are created for all current mod search paths.
    ///     Removes watchers for paths that are no longer being monitored.
    /// </summary>
    public void EnsureWatchers()
    {
        var searchPaths = _discoveryService.GetSearchPaths();

        var shouldNotifyChanges = false;

        lock (_syncRoot)
        {
            if (_disposed) return;

            var normalizedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Create watchers for new paths
            foreach (var path in searchPaths)
            {
                var normalized = NormalizePath(path);
                if (normalized == null) continue;

                normalizedTargets.Add(normalized);

                if (_watchers.ContainsKey(normalized) || !Directory.Exists(normalized)) continue;

                try
                {
                    var watcher = new FileSystemWatcher(normalized)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.DirectoryName
                                       | NotifyFilters.FileName
                                       | NotifyFilters.LastWrite
                                       | NotifyFilters.Size
                    };

                    watcher.Created += OnChanged;
                    watcher.Changed += OnChanged;
                    watcher.Deleted += OnChanged;
                    watcher.Renamed += OnRenamed;
                    watcher.EnableRaisingEvents = true;

                    _watchers[normalized] = watcher;
                    _requiresFullRescan = true;
                    shouldNotifyChanges = true;
                }
                catch (Exception)
                {
                    // Ignore watcher creation failures and continue with other paths.
                }
            }

            // Remove watchers for obsolete paths
            var toRemove = new List<string>();
            foreach (var existing in _watchers.Keys)
                if (!normalizedTargets.Contains(existing))
                    toRemove.Add(existing);

            if (toRemove.Count > 0)
            {
                foreach (var obsolete in toRemove)
                {
                    if (_watchers.TryGetValue(obsolete, out var watcher))
                    {
                        watcher.EnableRaisingEvents = false;
                        watcher.Created -= OnChanged;
                        watcher.Changed -= OnChanged;
                        watcher.Deleted -= OnChanged;
                        watcher.Renamed -= OnRenamed;
                        watcher.Dispose();
                    }

                    _watchers.Remove(obsolete);
                }

                _requiresFullRescan = true;
                shouldNotifyChanges = true;
            }

            _isWatching = _watchers.Count > 0;
        }

        if (shouldNotifyChanges) NotifyChangeDetected();
    }

    /// <summary>
    ///     Retrieves and clears all pending file system changes.
    /// </summary>
    /// <returns>A <see cref="ModDirectoryChangeSet"/> containing all pending changes.</returns>
    public ModDirectoryChangeSet ConsumeChanges()
    {
        lock (_syncRoot)
        {
            if (_pendingPaths.Count == 0 && !_requiresFullRescan) return ModDirectoryChangeSet.Empty;

            var paths = _pendingPaths.Count == 0
                ? Array.Empty<string>()
                : new List<string>(_pendingPaths).ToArray();
            var changeSet = new ModDirectoryChangeSet(_requiresFullRescan, paths);
            _pendingPaths.Clear();
            _requiresFullRescan = false;
            return changeSet;
        }
    }

    /// <summary>
    ///     Handles file system change events (create, modify, delete).
    /// </summary>
    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (sender is not FileSystemWatcher watcher || string.IsNullOrWhiteSpace(e.FullPath)) return;

        var root = TryResolveRootPath(watcher.Path, e.FullPath);
        if (root == null) return;

        var shouldNotify = false;
        lock (_syncRoot)
        {
            if (_pendingPaths.Add(root)) shouldNotify = true;
        }

        if (shouldNotify) NotifyChangeDetected();
    }

    /// <summary>
    ///     Handles file system rename events. Tracks both the old and new paths.
    /// </summary>
    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        OnChanged(sender, e);

        if (string.IsNullOrWhiteSpace(e.OldFullPath) || sender is not FileSystemWatcher watcher) return;

        var root = TryResolveRootPath(watcher.Path, e.OldFullPath);
        if (root == null) return;

        var shouldNotify = false;
        lock (_syncRoot)
        {
            if (_pendingPaths.Add(root)) shouldNotify = true;
        }

        if (shouldNotify) NotifyChangeDetected();
    }

    /// <summary>
    ///     Resolves a full path to its top-level directory relative to the search path.
    ///     This ensures we track changes at the mod root level, not individual files.
    /// </summary>
    /// <param name="searchPath">The base search path being monitored.</param>
    /// <param name="fullPath">The full path of the changed item.</param>
    /// <returns>The top-level directory path, or null if it cannot be determined.</returns>
    private static string? TryResolveRootPath(string searchPath, string fullPath)
    {
        var normalizedSearch = NormalizePath(searchPath);
        var normalizedFull = NormalizePath(fullPath);

        if (normalizedSearch == null || normalizedFull == null) return normalizedFull;

        if (!normalizedFull.StartsWith(normalizedSearch, StringComparison.OrdinalIgnoreCase)) return normalizedFull;

        var relative = normalizedFull.Length == normalizedSearch.Length
            ? string.Empty
            : normalizedFull.Substring(normalizedSearch.Length).TrimStart(DirectorySeparators);

        if (relative.Length == 0) return null;

        // Extract just the top-level directory name
        var separatorIndex = relative.IndexOfAny(DirectorySeparators);
        var topLevel = separatorIndex >= 0 ? relative[..separatorIndex] : relative;
        if (string.IsNullOrWhiteSpace(topLevel)) return null;

        return Path.Combine(normalizedSearch, topLevel);
    }

    /// <summary>
    ///     Normalizes a path by converting it to a full path and removing trailing separators.
    /// </summary>
    /// <param name="path">The path to normalize.</param>
    /// <returns>The normalized path, or null if the path is invalid.</returns>
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var normalized = Path.GetFullPath(path);
            return normalized.TrimEnd(DirectorySeparators);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void NotifyChangeDetected()
    {
        ChangesDetected?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
///     Represents a set of file system changes detected in mod directories.
/// </summary>
public readonly struct ModDirectoryChangeSet
{
    /// <summary>
    ///     An empty change set with no changes.
    /// </summary>
    public static readonly ModDirectoryChangeSet Empty = new(false, Array.Empty<string>());

    /// <summary>
    ///     Initializes a new instance of the <see cref="ModDirectoryChangeSet"/> struct.
    /// </summary>
    /// <param name="requiresFullRescan">Whether a full rescan of all mods is required.</param>
    /// <param name="paths">The collection of specific paths that have changes.</param>
    public ModDirectoryChangeSet(bool requiresFullRescan, IReadOnlyCollection<string> paths)
    {
        RequiresFullRescan = requiresFullRescan;
        Paths = paths ?? Array.Empty<string>();
    }

    /// <summary>
    ///     Gets a value indicating whether a full rescan of all mods is required
    ///     (e.g., when watchers are added or removed).
    /// </summary>
    public bool RequiresFullRescan { get; }

    /// <summary>
    ///     Gets the collection of specific paths that have detected changes.
    /// </summary>
    public IReadOnlyCollection<string> Paths { get; }
}