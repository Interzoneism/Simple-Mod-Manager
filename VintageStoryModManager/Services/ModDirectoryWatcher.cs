using System.IO;

namespace VintageStoryModManager.Services;

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

    public ModDirectoryWatcher(ModDiscoveryService discoveryService)
    {
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
    }

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

    public void EnsureWatchers()
    {
        var searchPaths = _discoveryService.GetSearchPaths();

        lock (_syncRoot)
        {
            if (_disposed) return;

            var normalizedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                }
                catch (Exception)
                {
                    // Ignore watcher creation failures and continue with other paths.
                }
            }

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
            }

            _isWatching = _watchers.Count > 0;
        }
    }

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

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (sender is not FileSystemWatcher watcher || string.IsNullOrWhiteSpace(e.FullPath)) return;

        var root = TryResolveRootPath(watcher.Path, e.FullPath);
        if (root == null) return;

        lock (_syncRoot)
        {
            _pendingPaths.Add(root);
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        OnChanged(sender, e);

        if (string.IsNullOrWhiteSpace(e.OldFullPath) || sender is not FileSystemWatcher watcher) return;

        var root = TryResolveRootPath(watcher.Path, e.OldFullPath);
        if (root == null) return;

        lock (_syncRoot)
        {
            _pendingPaths.Add(root);
        }
    }

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

        var separatorIndex = relative.IndexOfAny(DirectorySeparators);
        var topLevel = separatorIndex >= 0 ? relative[..separatorIndex] : relative;
        if (string.IsNullOrWhiteSpace(topLevel)) return null;

        return Path.Combine(normalizedSearch, topLevel);
    }

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
}

public readonly struct ModDirectoryChangeSet
{
    public static readonly ModDirectoryChangeSet Empty = new(false, Array.Empty<string>());

    public ModDirectoryChangeSet(bool requiresFullRescan, IReadOnlyCollection<string> paths)
    {
        RequiresFullRescan = requiresFullRescan;
        Paths = paths ?? Array.Empty<string>();
    }

    public bool RequiresFullRescan { get; }

    public IReadOnlyCollection<string> Paths { get; }
}