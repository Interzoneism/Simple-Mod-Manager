using System;
using System.IO;

namespace VintageStoryModManager.Services;

public sealed class ClientSettingsWatcher : IDisposable
{
    private readonly string _normalizedSettingsPath;
    private readonly string _settingsDirectory;
    private readonly string _settingsFileName;
    private readonly object _syncRoot = new();
    private FileSystemWatcher? _watcher;
    private bool _hasPendingChanges;
    private bool _disposed;

    public ClientSettingsWatcher(string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(settingsPath));
        }

        _normalizedSettingsPath = NormalizePath(settingsPath);
        _settingsDirectory = Path.GetDirectoryName(_normalizedSettingsPath)
            ?? throw new ArgumentException("Settings path does not contain a directory.", nameof(settingsPath));
        _settingsFileName = Path.GetFileName(_normalizedSettingsPath);
        if (string.IsNullOrWhiteSpace(_settingsFileName))
        {
            throw new ArgumentException("Settings path must include a file name.", nameof(settingsPath));
        }
    }

    public bool TryConsumePendingChanges()
    {
        lock (_syncRoot)
        {
            if (!_hasPendingChanges)
            {
                return false;
            }

            _hasPendingChanges = false;
            return true;
        }
    }

    public void SignalPendingChange()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _hasPendingChanges = true;
        }
    }

    public void EnsureWatcher()
    {
        lock (_syncRoot)
        {
            if (_disposed || _watcher != null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_settingsDirectory);

                var watcher = new FileSystemWatcher(_settingsDirectory)
                {
                    Filter = _settingsFileName,
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.CreationTime
                        | NotifyFilters.Size
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
            }
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
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalized;
        try
        {
            normalized = NormalizePath(path);
        }
        catch (Exception)
        {
            return;
        }

        if (!string.Equals(normalized, _normalizedSettingsPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _hasPendingChanges = true;
        }
    }

    private static string NormalizePath(string value)
    {
        return Path.GetFullPath(value);
    }
}
