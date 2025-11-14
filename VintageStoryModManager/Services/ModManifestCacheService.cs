using System.IO;
using System.Text.Json;

namespace VintageStoryModManager.Services;

/// <summary>
///     Provides disk-backed caching for mod manifests and icons so zip archives do not need to be reopened repeatedly.
/// </summary>
internal static class ModManifestCacheService
{
    private static readonly string MetadataFolderName = DevConfig.MetadataFolderName;
    private static readonly string IndexFileName = DevConfig.MetadataIndexFileName;

    private static readonly object IndexLock = new();
    private static Dictionary<string, CacheEntry>? _index;

    public static bool TryGetManifest(
        string sourcePath,
        DateTime lastWriteTimeUtc,
        long length,
        out string manifestJson,
        out byte[]? iconBytes)
    {
        manifestJson = string.Empty;
        iconBytes = null;

        var root = GetMetadataRoot();
        if (root is null) return false;

        var normalizedPath = NormalizePath(sourcePath);
        var ticks = ToUniversalTicks(lastWriteTimeUtc);

        lock (IndexLock)
        {
            var index = EnsureIndexLocked();
            if (!index.TryGetValue(normalizedPath, out var entry)) return false;

            if (entry.Length != length || entry.LastWriteTimeUtcTicks != ticks)
            {
                index.Remove(normalizedPath);
                SaveIndexLocked(index);
                return false;
            }

            if (!File.Exists(entry.ManifestPath))
            {
                index.Remove(normalizedPath);
                SaveIndexLocked(index);
                return false;
            }

            try
            {
                manifestJson = File.ReadAllText(entry.ManifestPath);
                if (!string.IsNullOrWhiteSpace(entry.IconPath) && File.Exists(entry.IconPath))
                    iconBytes = File.ReadAllBytes(entry.IconPath);
                return true;
            }
            catch (Exception)
            {
                index.Remove(normalizedPath);
                SaveIndexLocked(index);
                return false;
            }
        }
    }

    public static void ClearCache()
    {
        lock (IndexLock)
        {
            _index = null;
        }

        var root = GetMetadataRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

        try
        {
            Directory.Delete(root, true);
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to delete the mod metadata cache at {root}.", ex);
        }
    }

    public static void StoreManifest(
        string sourcePath,
        DateTime lastWriteTimeUtc,
        long length,
        string modId,
        string? version,
        string manifestJson,
        byte[]? iconBytes)
    {
        var root = GetMetadataRoot();
        if (root is null) return;

        var normalizedPath = NormalizePath(sourcePath);
        var ticks = ToUniversalTicks(lastWriteTimeUtc);

        var sanitizedModId = ModCacheLocator.SanitizeFileName(modId, "mod");
        var sanitizedVersion = ModCacheLocator.SanitizeFileName(version, "noversion");

        try
        {
            Directory.CreateDirectory(root);
            var modDirectory = Path.Combine(root, sanitizedModId);
            Directory.CreateDirectory(modDirectory);

            var manifestPath = Path.Combine(modDirectory, sanitizedVersion + ".json");
            File.WriteAllText(manifestPath, manifestJson);

            string? iconPath = null;
            if (iconBytes is { Length: > 0 })
            {
                iconPath = Path.Combine(modDirectory, sanitizedVersion + ".icon");
                File.WriteAllBytes(iconPath, iconBytes);
            }

            lock (IndexLock)
            {
                var index = EnsureIndexLocked();
                if (!index.TryGetValue(normalizedPath, out var entry))
                {
                    entry = new CacheEntry();
                    index[normalizedPath] = entry;
                }

                entry.ModId = modId;
                entry.Version = version;
                entry.ManifestPath = manifestPath;
                entry.Length = length;
                entry.LastWriteTimeUtcTicks = ticks;

                if (iconPath != null)
                    entry.IconPath = iconPath;
                else if (!string.IsNullOrWhiteSpace(entry.IconPath) && !File.Exists(entry.IconPath))
                    entry.IconPath = null;

                SaveIndexLocked(index);
            }
        }
        catch (Exception)
        {
            // Intentionally swallow errors; cache failures should not impact mod loading.
        }
    }

    public static void Invalidate(string sourcePath)
    {
        var normalizedPath = NormalizePath(sourcePath);
        lock (IndexLock)
        {
            var index = EnsureIndexLocked();
            if (index.Remove(normalizedPath)) SaveIndexLocked(index);
        }
    }

    private static Dictionary<string, CacheEntry> EnsureIndexLocked()
    {
        return _index ??= LoadIndex();
    }

    private static Dictionary<string, CacheEntry> LoadIndex()
    {
        var indexPath = GetIndexPath();
        if (indexPath == null) return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!File.Exists(indexPath)) return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(indexPath);
            var model = JsonSerializer.Deserialize<CacheIndex>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (model?.Entries == null) return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

            var dictionary = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in model.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.SourcePath) ||
                    string.IsNullOrWhiteSpace(entry.ManifestPath)) continue;

                dictionary[entry.SourcePath] = new CacheEntry
                {
                    ModId = entry.ModId ?? string.Empty,
                    Version = entry.Version,
                    ManifestPath = entry.ManifestPath,
                    IconPath = entry.IconPath,
                    Length = entry.Length,
                    LastWriteTimeUtcTicks = entry.LastWriteTimeUtcTicks
                };
            }

            return dictionary;
        }
        catch (Exception)
        {
            return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveIndexLocked(Dictionary<string, CacheEntry> index)
    {
        var indexPath = GetIndexPath();
        if (indexPath == null) return;

        try
        {
            var directory = Path.GetDirectoryName(indexPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var model = new CacheIndex
            {
                Entries = new List<CacheIndexEntry>(index.Count)
            };

            foreach (var pair in index)
                model.Entries.Add(new CacheIndexEntry
                {
                    SourcePath = pair.Key,
                    ModId = pair.Value.ModId,
                    Version = pair.Value.Version,
                    ManifestPath = pair.Value.ManifestPath,
                    IconPath = pair.Value.IconPath,
                    Length = pair.Value.Length,
                    LastWriteTimeUtcTicks = pair.Value.LastWriteTimeUtcTicks
                });

            var json = JsonSerializer.Serialize(model);
            File.WriteAllText(indexPath, json);
        }
        catch (Exception)
        {
            // Ignore cache persistence failures.
        }
    }

    private static string? GetMetadataRoot()
    {
        var baseDirectory = ModCacheLocator.GetManagerDataDirectory();
        return baseDirectory is null ? null : Path.Combine(baseDirectory, MetadataFolderName);
    }

    private static string? GetIndexPath()
    {
        var root = GetMetadataRoot();
        return root is null ? null : Path.Combine(root, IndexFileName);
    }

    private static string NormalizePath(string sourcePath)
    {
        try
        {
            return Path.GetFullPath(sourcePath);
        }
        catch (Exception)
        {
            return sourcePath;
        }
    }

    private static long ToUniversalTicks(DateTime value)
    {
        if (value.Kind == DateTimeKind.Unspecified) value = DateTime.SpecifyKind(value, DateTimeKind.Local);

        return value.ToUniversalTime().Ticks;
    }

    private sealed class CacheEntry
    {
        public string ModId { get; set; } = string.Empty;
        public string? Version { get; set; }
        public string ManifestPath { get; set; } = string.Empty;
        public string? IconPath { get; set; }
        public long Length { get; set; }
        public long LastWriteTimeUtcTicks { get; set; }
    }

    private sealed class CacheIndex
    {
        public List<CacheIndexEntry> Entries { get; set; } = new();
    }

    private sealed class CacheIndexEntry
    {
        public string SourcePath { get; set; } = string.Empty;
        public string ModId { get; set; } = string.Empty;
        public string? Version { get; set; }
        public string ManifestPath { get; set; } = string.Empty;
        public string? IconPath { get; set; }
        public long Length { get; set; }
        public long LastWriteTimeUtcTicks { get; set; }
    }
}