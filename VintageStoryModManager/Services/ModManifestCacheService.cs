using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VintageStoryModManager.Services;

/// <summary>
///     Provides disk-backed caching for mod manifests and icons so zip archives do not need to be reopened repeatedly.
///     Uses a single unified JSON file for all mod metadata with icons stored separately.
/// </summary>
internal static class ModManifestCacheService
{
    private static readonly string MetadataFolderName = DevConfig.MetadataFolderName;
    private static readonly string UnifiedCacheFileName = "metadata-cache.json";
    private static readonly string IconCacheFolderName = DevConfig.IconCacheFolderName;

    private static readonly object CacheLock = new();
    private static UnifiedMetadataCache? _cache;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool TryGetManifest(
        string sourcePath,
        DateTime lastWriteTimeUtc,
        long length,
        out string manifestJson,
        out byte[]? iconBytes)
    {
        manifestJson = string.Empty;
        iconBytes = null;

        var normalizedPath = NormalizePath(sourcePath);
        var ticks = ToUniversalTicks(lastWriteTimeUtc);

        lock (CacheLock)
        {
            var cache = EnsureCacheLocked();
            if (!cache.Entries.TryGetValue(normalizedPath, out var entry)) return false;

            if (entry.Length != length || entry.LastWriteTimeUtcTicks != ticks)
            {
                cache.Entries.Remove(normalizedPath);
                SaveCacheLocked(cache);
                return false;
            }

            try
            {
                manifestJson = entry.ManifestJson ?? string.Empty;

                // Try to load icon from cache folder
                var iconPath = GetIconPath(entry.ModId, entry.Version);
                if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
                    iconBytes = File.ReadAllBytes(iconPath);

                return true;
            }
            catch (Exception)
            {
                cache.Entries.Remove(normalizedPath);
                SaveCacheLocked(cache);
                return false;
            }
        }
    }

    public static void ClearCache()
    {
        lock (CacheLock)
        {
            _cache = null;
        }

        var root = GetMetadataRoot();
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception ex)
            {
                throw new IOException($"Failed to delete the mod metadata cache at {root}.", ex);
            }
        }

        var iconRoot = GetIconCacheRoot();
        if (!string.IsNullOrWhiteSpace(iconRoot) && Directory.Exists(iconRoot))
        {
            try
            {
                Directory.Delete(iconRoot, true);
            }
            catch (Exception ex)
            {
                throw new IOException($"Failed to delete the mod icon cache at {iconRoot}.", ex);
            }
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
        var normalizedPath = NormalizePath(sourcePath);
        var ticks = ToUniversalTicks(lastWriteTimeUtc);

        try
        {
            // Store icon separately if provided
            if (iconBytes is { Length: > 0 })
            {
                var iconPath = GetIconPath(modId, version);
                if (!string.IsNullOrWhiteSpace(iconPath))
                {
                    var iconDirectory = Path.GetDirectoryName(iconPath);
                    if (!string.IsNullOrWhiteSpace(iconDirectory))
                    {
                        Directory.CreateDirectory(iconDirectory);
                        File.WriteAllBytes(iconPath, iconBytes);
                    }
                }
            }

            lock (CacheLock)
            {
                var cache = EnsureCacheLocked();

                // Update or create cache entry
                var entry = new CachedMetadataEntry
                {
                    ModId = modId,
                    Version = version,
                    ManifestJson = manifestJson,
                    Length = length,
                    LastWriteTimeUtcTicks = ticks,
                    Tags = Array.Empty<string>() // Tags updated via UpdateTags when database info is loaded
                };

                cache.Entries[normalizedPath] = entry;
                cache.InvalidateIndex(); // Rebuild index on next access
                SaveCacheLocked(cache);
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
        lock (CacheLock)
        {
            var cache = EnsureCacheLocked();
            if (cache.Entries.Remove(normalizedPath))
            {
                cache.InvalidateIndex(); // Rebuild index on next access
                SaveCacheLocked(cache);
            }
        }
    }

    public static void UpdateTags(string modId, string? version, IReadOnlyList<string> tags)
    {
        if (string.IsNullOrWhiteSpace(modId) || tags is null || tags.Count == 0)
            return;

        lock (CacheLock)
        {
            var cache = EnsureCacheLocked();
            var modified = false;

            // Use index for efficient lookup
            var key = GetModVersionKey(modId, version);
            if (cache.ModVersionIndex.TryGetValue(key, out var sourcePaths))
            {
                foreach (var sourcePath in sourcePaths)
                {
                    if (cache.Entries.TryGetValue(sourcePath, out var entry))
                    {
                        entry.Tags = tags.ToArray();
                        modified = true;
                    }
                }
            }

            if (modified)
            {
                SaveCacheLocked(cache);
            }
        }
    }

    public static IReadOnlyList<string> GetTags(string modId, string? version)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return Array.Empty<string>();

        lock (CacheLock)
        {
            var cache = EnsureCacheLocked();

            // Use index for efficient lookup
            var key = GetModVersionKey(modId, version);
            if (cache.ModVersionIndex.TryGetValue(key, out var sourcePaths) && sourcePaths.Count > 0)
            {
                // Return tags from first matching entry
                if (cache.Entries.TryGetValue(sourcePaths[0], out var entry))
                {
                    return entry.Tags ?? Array.Empty<string>();
                }
            }

            return Array.Empty<string>();
        }
    }

    private static string GetModVersionKey(string modId, string? version)
    {
        return string.IsNullOrWhiteSpace(version)
            ? modId.ToLowerInvariant()
            : $"{modId.ToLowerInvariant()}:{version.ToLowerInvariant()}";
    }

    private static UnifiedMetadataCache EnsureCacheLocked()
    {
        return _cache ??= LoadCache();
    }

    private static UnifiedMetadataCache LoadCache()
    {
        var cachePath = GetUnifiedCachePath();
        if (cachePath == null)
            return new UnifiedMetadataCache();

        try
        {
            if (!File.Exists(cachePath))
                return new UnifiedMetadataCache();

            var json = File.ReadAllText(cachePath);
            var cache = JsonSerializer.Deserialize<UnifiedMetadataCache>(json, SerializerOptions);

            return cache ?? new UnifiedMetadataCache();
        }
        catch (Exception)
        {
            return new UnifiedMetadataCache();
        }
    }

    private static void SaveCacheLocked(UnifiedMetadataCache cache)
    {
        var cachePath = GetUnifiedCachePath();
        if (cachePath == null) return;

        try
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(cache, SerializerOptions);
            File.WriteAllText(cachePath, json);
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

    private static string? GetIconCacheRoot()
    {
        var baseDirectory = ModCacheLocator.GetManagerDataDirectory();
        return baseDirectory is null ? null : Path.Combine(baseDirectory, IconCacheFolderName);
    }

    private static string? GetUnifiedCachePath()
    {
        var root = GetMetadataRoot();
        return root is null ? null : Path.Combine(root, UnifiedCacheFileName);
    }

    private static string? GetIconPath(string modId, string? version)
    {
        var root = GetIconCacheRoot();
        if (root is null) return null;

        var sanitizedModId = ModCacheLocator.SanitizeFileName(modId, "mod");
        var sanitizedVersion = ModCacheLocator.SanitizeFileName(version, "noversion");
        var fileName = $"{sanitizedModId}_{sanitizedVersion}.icon";

        return Path.Combine(root, fileName);
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
        if (value.Kind == DateTimeKind.Unspecified)
            value = DateTime.SpecifyKind(value, DateTimeKind.Local);

        return value.ToUniversalTime().Ticks;
    }

    private sealed class UnifiedMetadataCache
    {
        public Dictionary<string, CachedMetadataEntry> Entries { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        // Secondary index for efficient lookups by ModId+Version
        [JsonIgnore]
        private Dictionary<string, List<string>>? _modVersionIndex;

        [JsonIgnore]
        public Dictionary<string, List<string>> ModVersionIndex
        {
            get
            {
                if (_modVersionIndex == null)
                {
                    _modVersionIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in Entries)
                    {
                        var key = ModManifestCacheService.GetModVersionKey(kvp.Value.ModId, kvp.Value.Version);
                        if (!_modVersionIndex.TryGetValue(key, out var paths))
                        {
                            paths = new List<string>();
                            _modVersionIndex[key] = paths;
                        }
                        paths.Add(kvp.Key);
                    }
                }
                return _modVersionIndex;
            }
        }

        public void InvalidateIndex()
        {
            _modVersionIndex = null;
        }
    }

    private sealed class CachedMetadataEntry
    {
        public string ModId { get; set; } = string.Empty;
        public string? Version { get; set; }
        public string? ManifestJson { get; set; }
        public long Length { get; set; }
        public long LastWriteTimeUtcTicks { get; set; }
        public string[] Tags { get; set; } = Array.Empty<string>();
    }
}