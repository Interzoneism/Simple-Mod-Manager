using System;
using System.Collections.Generic;
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
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);

    private bool _requiresFullRescan;
    private bool _isWatching;
    private bool _disposed;

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

    public void EnsureWatchers()
    {
        IReadOnlyList<string> searchPaths = _discoveryService.GetSearchPaths();

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            var normalizedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in searchPaths)
            {
                string? normalized = NormalizePath(path);
                if (normalized == null)
                {
                    continue;
                }

                normalizedTargets.Add(normalized);

                if (_watchers.ContainsKey(normalized) || !Directory.Exists(normalized))
                {
                    continue;
                }

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
            foreach (string existing in _watchers.Keys)
            {
                if (!normalizedTargets.Contains(existing))
                {
                    toRemove.Add(existing);
                }
            }

            if (toRemove.Count > 0)
            {
                foreach (string obsolete in toRemove)
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
            if (_pendingPaths.Count == 0 && !_requiresFullRescan)
            {
                return ModDirectoryChangeSet.Empty;
            }

            var paths = _pendingPaths.Count == 0
                ? Array.Empty<string>()
                : new List<string>(_pendingPaths).ToArray();
            var changeSet = new ModDirectoryChangeSet(_requiresFullRescan, paths);
            _pendingPaths.Clear();
            _requiresFullRescan = false;
            return changeSet;
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

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

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (sender is not FileSystemWatcher watcher || string.IsNullOrWhiteSpace(e.FullPath))
        {
            return;
        }

        string? root = TryResolveRootPath(watcher.Path, e.FullPath);
        if (root == null)
        {
            return;
        }

        lock (_syncRoot)
        {
            _pendingPaths.Add(root);
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        OnChanged(sender, e);

        if (string.IsNullOrWhiteSpace(e.OldFullPath) || sender is not FileSystemWatcher watcher)
        {
            return;
        }

        string? root = TryResolveRootPath(watcher.Path, e.OldFullPath);
        if (root == null)
        {
            return;
        }

        lock (_syncRoot)
        {
            _pendingPaths.Add(root);
        }
    }

    private static string? TryResolveRootPath(string searchPath, string fullPath)
    {
        string? normalizedSearch = NormalizePath(searchPath);
        string? normalizedFull = NormalizePath(fullPath);

        if (normalizedSearch == null || normalizedFull == null)
        {
            return normalizedFull;
        }

        if (!normalizedFull.StartsWith(normalizedSearch, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedFull;
        }

        string relative = normalizedFull.Length == normalizedSearch.Length
            ? string.Empty
            : normalizedFull.Substring(normalizedSearch.Length).TrimStart(DirectorySeparators);

        if (relative.Length == 0)
        {
            return null;
        }

        int separatorIndex = relative.IndexOfAny(DirectorySeparators);
        string topLevel = separatorIndex >= 0 ? relative[..separatorIndex] : relative;
        if (string.IsNullOrWhiteSpace(topLevel))
        {
            return null;
        }

        return Path.Combine(normalizedSearch, topLevel);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string normalized = Path.GetFullPath(path);
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
