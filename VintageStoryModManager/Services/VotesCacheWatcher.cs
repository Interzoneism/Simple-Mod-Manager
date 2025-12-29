using System.IO;

namespace VintageStoryModManager.Services;

/// <summary>
///     Monitors the compat-votes-cache.json file for changes.
/// </summary>
public sealed class VotesCacheWatcher : IDisposable
{
    private readonly string _normalizedCachePath;
    private readonly string _cacheDirectory;
    private readonly string _cacheFileName;
    private readonly object _syncRoot = new();
    private bool _disposed;
    private bool _hasPendingChanges;
    private FileSystemWatcher? _watcher;

    /// <summary>
    ///     Occurs when changes are detected in the votes cache file.
    /// </summary>
    /// <remarks>
    ///     This event provides immediate notification of cache changes, unlike ClientSettingsWatcher
    ///     which uses polling via TryConsumePendingChanges(). The immediate notification is needed
    ///     to ensure the UI updates vote counts promptly after a user submits a vote, providing
    ///     better user experience compared to a polling-based approach.
    /// </remarks>
    public event EventHandler? CacheChanged;

    public VotesCacheWatcher(string cachePath)
    {
        if (string.IsNullOrWhiteSpace(cachePath))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(cachePath));

        _normalizedCachePath = NormalizePath(cachePath);
        _cacheDirectory = Path.GetDirectoryName(_normalizedCachePath)
                         ?? throw new ArgumentException("Cache path does not contain a directory.",
                             nameof(cachePath));
        _cacheFileName = Path.GetFileName(_normalizedCachePath);
        if (string.IsNullOrWhiteSpace(_cacheFileName))
            throw new ArgumentException("Cache path must include a file name.", nameof(cachePath));
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed) return;

            _disposed = true;

            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnChanged;
                _watcher.Created -= OnChanged;
                _watcher.Deleted -= OnChanged;
                _watcher.Renamed -= OnRenamed;
                _watcher.Dispose();
                _watcher = null;
            }

            _hasPendingChanges = false;
        }
    }

    public bool TryConsumePendingChanges()
    {
        lock (_syncRoot)
        {
            if (!_hasPendingChanges) return false;

            _hasPendingChanges = false;
            return true;
        }
    }

    public void EnsureWatcher()
    {
        lock (_syncRoot)
        {
            if (_disposed || _watcher != null) return;

            try
            {
                Directory.CreateDirectory(_cacheDirectory);

                var watcher = new FileSystemWatcher(_cacheDirectory)
                {
                    Filter = _cacheFileName,
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                };

                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.EnableRaisingEvents = true;

                _watcher = watcher;
            }
            catch (Exception)
            {
                // Ignore watcher creation failures and continue.
                // This is expected in scenarios where the directory doesn't exist yet
                // or the application lacks file system permissions.
            }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        HandlePotentialChange(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        HandlePotentialChange(e.FullPath);
        HandlePotentialChange(e.OldFullPath);
    }

    private void HandlePotentialChange(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        string normalized;
        try
        {
            normalized = NormalizePath(path);
        }
        catch (Exception)
        {
            return;
        }

        if (!string.Equals(normalized, _normalizedCachePath, StringComparison.OrdinalIgnoreCase)) return;

        EventHandler? handler;
        lock (_syncRoot)
        {
            if (_disposed) return;

            bool shouldNotify = !_hasPendingChanges;
            _hasPendingChanges = true;

            if (!shouldNotify) return;

            handler = CacheChanged;
        }

        handler?.Invoke(this, EventArgs.Empty);
    }

    private static string NormalizePath(string value)
    {
        return Path.GetFullPath(value);
    }
}
