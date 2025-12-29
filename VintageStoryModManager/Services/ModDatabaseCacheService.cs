using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.Services;

/// <summary>
///     Provides persistence for metadata retrieved from the Vintage Story mod database so that
///     subsequent requests can be served without repeatedly downloading large payloads.
/// </summary>
internal sealed class ModDatabaseCacheService
{
    private static readonly int CacheSchemaVersion = DevConfig.ModDatabaseCacheSchemaVersion;

    private static readonly int MinimumSupportedCacheSchemaVersion =
        DevConfig.ModDatabaseMinimumSupportedCacheSchemaVersion;

    private static readonly string AnyGameVersionToken = DevConfig.ModDatabaseAnyGameVersionToken;
    // Note: Time-based cache expiry has been removed. Cache invalidation is now based on
    // version comparison - we always load from cache first, then check if the latest version
    // has changed before fetching full data from the network.

    /// <summary>
    ///     Maximum number of entries to keep in the in-memory cache.
    ///     This is tuned to balance memory usage with cache hit rate.
    /// </summary>
    private const int MaxInMemoryCacheSize = 500;

    /// <summary>
    ///     How long an in-memory cache entry is valid before requiring disk re-read.
    ///     This is shorter than disk cache expiry to catch file updates.
    /// </summary>
    private static readonly TimeSpan InMemoryCacheMaxAge = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     In-memory cache to avoid repeated disk reads for the same mod.
    ///     Key is the cache file path, value contains the cached data and metadata.
    /// </summary>
    private readonly ConcurrentDictionary<string, InMemoryCacheEntry> _inMemoryCache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static void ClearCacheDirectory()
    {
        var baseDirectory = ModCacheLocator.GetModDatabaseCacheDirectory();
        if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory)) return;

        try
        {
            Directory.Delete(baseDirectory, true);
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to delete the mod database cache at {baseDirectory}.", ex);
        }
    }

    /// <summary>
    ///     Clears the in-memory cache. Call this when the disk cache is cleared or when
    ///     memory pressure is high.
    /// </summary>
    internal void ClearInMemoryCache()
    {
        _inMemoryCache.Clear();
    }

    public async Task<ModDatabaseInfo?> TryLoadAsync(
        string modId,
        string? normalizedGameVersion,
        string? installedModVersion,
        bool allowExpiredEntryRefresh,
        bool requireExactVersionMatch,
        CancellationToken cancellationToken)
    {
        // The allowExpiredEntryRefresh parameter is now unused since we no longer use time-based expiry.
        // Cache invalidation is based on HTTP conditional requests (ETag/Last-Modified) by the caller.
        return await TryLoadWithoutExpiryAsync(
            modId,
            normalizedGameVersion,
            installedModVersion,
            requireExactVersionMatch,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Attempts to load cached mod database info from disk.
    ///     Cache entries are never expired by time - invalidation is based on HTTP conditional
    ///     requests performed by the caller using <see cref="GetCachedHttpHeadersAsync"/>.
    /// </summary>
    /// <param name="modId">The mod identifier.</param>
    /// <param name="normalizedGameVersion">The normalized game version.</param>
    /// <param name="installedModVersion">The installed mod version.</param>
    /// <param name="requireExactVersionMatch">Whether to require exact version matching.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The cached mod database info, or null if not found.</returns>
    public async Task<ModDatabaseInfo?> TryLoadWithoutExpiryAsync(
        string modId,
        string? normalizedGameVersion,
        string? installedModVersion,
        bool requireExactVersionMatch,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCacheFilePath(modId, normalizedGameVersion);
        if (string.IsNullOrWhiteSpace(cachePath)) return null;

        // Build a cache key that includes version-specific parameters for lookup
        var cacheKey = BuildInMemoryCacheKey(cachePath, installedModVersion, requireExactVersionMatch);

        // Try to get from in-memory cache first
        if (_inMemoryCache.TryGetValue(cacheKey, out var memoryEntry))
        {
            if (!IsInMemoryCacheEntryExpired(memoryEntry))
            {
                // Return cached result (which may be null if no disk cache existed)
                return memoryEntry.Result;
            }

            // In-memory entry expired, remove it and re-read from disk
            _inMemoryCache.TryRemove(cacheKey, out _);
        }

        // Check if file exists before acquiring lock
        if (!File.Exists(cachePath))
        {
            // Cache the null result to avoid repeated file existence checks
            TryAddToInMemoryCache(cacheKey, null);
            return null;
        }

        var fileLock = await AcquireLockAsync(cachePath, cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check file exists after acquiring lock
            if (!File.Exists(cachePath))
            {
                TryAddToInMemoryCache(cacheKey, null);
                return null;
            }

            await using FileStream stream = new(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var cached = await JsonSerializer
                .DeserializeAsync<CachedModDatabaseInfo>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (cached is null
                || !IsSupportedSchemaVersion(cached.SchemaVersion)
                || !IsGameVersionMatch(cached.GameVersion, normalizedGameVersion))
            {
                TryAddToInMemoryCache(cacheKey, null);
                return null;
            }

            // No time-based expiry check - cache is valid until data changes on the server
            var info = ConvertToDatabaseInfo(cached, normalizedGameVersion, installedModVersion, requireExactVersionMatch);
            TryAddToInMemoryCache(cacheKey, info);
            return info;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    ///     Attempts to load cached mod database info along with the lastmodified API value and cache timestamp.
    /// </summary>
    /// <param name="modId">The mod identifier.</param>
    /// <param name="normalizedGameVersion">The normalized game version.</param>
    /// <param name="installedModVersion">The installed mod version.</param>
    /// <param name="requireExactVersionMatch">Whether to require exact version matching.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A tuple containing the cached info, lastmodified API value, and cache timestamp.</returns>
    public async Task<(ModDatabaseInfo? Info, string? LastModifiedApiValue, DateTimeOffset? CachedAt)> TryLoadWithLastModifiedAsync(
        string modId,
        string? normalizedGameVersion,
        string? installedModVersion,
        bool requireExactVersionMatch,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCacheFilePath(modId, normalizedGameVersion);
        if (string.IsNullOrWhiteSpace(cachePath)) return (null, null, null);

        if (!File.Exists(cachePath)) return (null, null, null);

        var fileLock = await AcquireLockAsync(cachePath, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(cachePath)) return (null, null, null);

            await using FileStream stream = new(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var cached = await JsonSerializer
                .DeserializeAsync<CachedModDatabaseInfo>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (cached is null
                || !IsSupportedSchemaVersion(cached.SchemaVersion)
                || !IsGameVersionMatch(cached.GameVersion, normalizedGameVersion))
            {
                return (null, null, null);
            }

            var info = ConvertToDatabaseInfo(cached, normalizedGameVersion, installedModVersion, requireExactVersionMatch);
            return (info, cached.LastModifiedApiValue, cached.CachedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, null, null);
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    ///     Gets the latest version stored in the cache for a mod, used for staleness checking.
    /// </summary>
    /// <param name="modId">The mod identifier.</param>
    /// <param name="normalizedGameVersion">The normalized game version.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The cached latest version, or null if not cached.</returns>
    public async Task<string?> GetCachedLatestVersionAsync(
        string modId,
        string? normalizedGameVersion,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCacheFilePath(modId, normalizedGameVersion);
        if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath)) return null;

        var fileLock = await AcquireLockAsync(cachePath, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(cachePath)) return null;

            await using FileStream stream = new(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var cached = await JsonSerializer
                .DeserializeAsync<CachedModDatabaseInfo>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (cached is null || !IsSupportedSchemaVersion(cached.SchemaVersion)) return null;

            // Return the latest version from the first release (they are ordered by version descending)
            return cached.Releases is { Length: > 0 } ? cached.Releases[0].Version : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    ///     Gets the cached lastmodified value from the API and cache timestamp for cache invalidation.
    /// </summary>
    /// <param name="modId">The mod identifier.</param>
    /// <param name="normalizedGameVersion">The normalized game version.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A tuple containing the lastmodified API value and cached timestamp, or nulls if not cached.</returns>
    public async Task<(string? LastModifiedApiValue, DateTimeOffset? CachedAt)> GetCachedLastModifiedAsync(
        string modId,
        string? normalizedGameVersion,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCacheFilePath(modId, normalizedGameVersion);
        if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath)) return (null, null);

        var fileLock = await AcquireLockAsync(cachePath, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(cachePath)) return (null, null);

            await using FileStream stream = new(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var cached = await JsonSerializer
                .DeserializeAsync<CachedModDatabaseInfo>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (cached is null || !IsSupportedSchemaVersion(cached.SchemaVersion)) return (null, null);

            return (cached.LastModifiedApiValue, cached.CachedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, null);
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    ///     Gets the cached HTTP headers and cache timestamp for conditional request validation.
    ///     This method is kept for backward compatibility but the lastmodified API value
    ///     approach via <see cref="GetCachedLastModifiedAsync"/> is preferred.
    /// </summary>
    /// <param name="modId">The mod identifier.</param>
    /// <param name="normalizedGameVersion">The normalized game version.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A tuple containing the Last-Modified header, ETag, and cached timestamp, or nulls if not cached.</returns>
    public async Task<(string? LastModified, string? ETag, DateTimeOffset? CachedAt)> GetCachedHttpHeadersAsync(
        string modId,
        string? normalizedGameVersion,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCacheFilePath(modId, normalizedGameVersion);
        if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath)) return (null, null, null);

        var fileLock = await AcquireLockAsync(cachePath, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(cachePath)) return (null, null, null);

            await using FileStream stream = new(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var cached = await JsonSerializer
                .DeserializeAsync<CachedModDatabaseInfo>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (cached is null || !IsSupportedSchemaVersion(cached.SchemaVersion)) return (null, null, null);

            return (cached.LastModifiedHeader, cached.ETag, cached.CachedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, null, null);
        }
        finally
        {
            fileLock.Release();
        }
    }

    public Task StoreAsync(
        string modId,
        string? normalizedGameVersion,
        ModDatabaseInfo info,
        string? installedModVersion,
        CancellationToken cancellationToken)
    {
        return StoreAsync(modId, normalizedGameVersion, info, installedModVersion, null, null, null, cancellationToken);
    }

    public async Task StoreAsync(
        string modId,
        string? normalizedGameVersion,
        ModDatabaseInfo info,
        string? installedModVersion,
        string? lastModifiedHeader,
        string? etag,
        CancellationToken cancellationToken)
    {
        await StoreAsync(modId, normalizedGameVersion, info, installedModVersion, lastModifiedHeader, etag, null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task StoreAsync(
        string modId,
        string? normalizedGameVersion,
        ModDatabaseInfo info,
        string? installedModVersion,
        string? lastModifiedHeader,
        string? etag,
        string? lastModifiedApiValue,
        CancellationToken cancellationToken)
    {
        if (info is null) return;

        var cachePath = GetCacheFilePath(modId, normalizedGameVersion);
        if (string.IsNullOrWhiteSpace(cachePath)) return;

        var directory = Path.GetDirectoryName(cachePath);
        if (string.IsNullOrWhiteSpace(directory)) return;

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            return;
        }

        var fileLock = await AcquireLockAsync(cachePath, cancellationToken).ConfigureAwait(false);
        try
        {
            var tagsByModVersion = await LoadExistingTagsByVersionAsync(cachePath, cancellationToken)
                .ConfigureAwait(false);

            var tagsVersionKey = NormalizeModVersion(info.CachedTagsVersion);
            if (string.IsNullOrWhiteSpace(tagsVersionKey)) tagsVersionKey = NormalizeModVersion(installedModVersion);

            if (!string.IsNullOrWhiteSpace(tagsVersionKey))
                tagsByModVersion[tagsVersionKey] = info.Tags?.ToArray() ?? Array.Empty<string>();

            var tempPath = cachePath + ".tmp";

            var cacheModel = CreateCacheModel(modId, normalizedGameVersion, info, tagsByModVersion, lastModifiedHeader, etag, lastModifiedApiValue);

            await using (FileStream tempStream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(tempStream, cacheModel, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                File.Move(tempPath, cachePath, true);
            }
            catch (IOException)
            {
                try
                {
                    // Retry with replace semantics when running on platforms that require it.
                    File.Replace(tempPath, cachePath, null);
                }
                finally
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
            }

            // Invalidate in-memory cache for this mod since we just updated the disk cache
            InvalidateInMemoryCacheForMod(cachePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Ignore serialization or file system failures to keep cache best-effort.
        }
        finally
        {
            fileLock.Release();
        }
    }

    private async Task<Dictionary<string, string[]>> LoadExistingTagsByVersionAsync(
        string cachePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(cachePath)) return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await using FileStream stream = new(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var cached = await JsonSerializer
                .DeserializeAsync<CachedModDatabaseInfo>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (cached?.TagsByModVersion is { Count: > 0 })
                return new Dictionary<string, string[]>(cached.TagsByModVersion, StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Ignore failures when reading the existing cache entry.
        }

        return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    }

    private static string? NormalizeModVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        return VersionStringUtility.Normalize(version);
    }

    private static CachedModDatabaseInfo CreateCacheModel(
        string modId,
        string? normalizedGameVersion,
        ModDatabaseInfo info,
        IReadOnlyDictionary<string, string[]> tagsByModVersion,
        string? lastModifiedHeader = null,
        string? etag = null,
        string? lastModifiedApiValue = null)
    {
        var releases = info.Releases ?? Array.Empty<ModReleaseInfo>();
        var releaseModels = new List<CachedModRelease>(releases.Count);

        foreach (var release in releases)
        {
            if (release?.DownloadUri is not Uri downloadUri) continue;

            releaseModels.Add(new CachedModRelease
            {
                Version = release.Version,
                NormalizedVersion = release.NormalizedVersion,
                DownloadUrl = downloadUri.ToString(),
                FileName = release.FileName,
                GameVersionTags = release.GameVersionTags?.ToArray() ?? Array.Empty<string>(),
                Changelog = release.Changelog,
                Downloads = release.Downloads,
                CreatedUtc = release.CreatedUtc
            });
        }

        return new CachedModDatabaseInfo
        {
            SchemaVersion = CacheSchemaVersion,
            CachedAt = DateTimeOffset.Now,
            ModId = modId,
            GameVersion = string.IsNullOrWhiteSpace(normalizedGameVersion)
                ? AnyGameVersionToken
                : normalizedGameVersion!,
            LastModifiedHeader = lastModifiedHeader,
            ETag = etag,
            LastModifiedApiValue = lastModifiedApiValue,
            Tags = info.Tags?.ToArray() ?? Array.Empty<string>(),
            TagsByModVersion = tagsByModVersion.Count == 0
                ? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string[]>(tagsByModVersion, StringComparer.OrdinalIgnoreCase),
            AssetId = info.AssetId,
            ModPageUrl = info.ModPageUrl,
            Downloads = info.Downloads,
            Comments = info.Comments,
            Follows = info.Follows,
            TrendingPoints = info.TrendingPoints,
            LogoUrl = info.LogoUrl,
            LogoUrlSource = info.LogoUrlSource,
            DownloadsLastThirtyDays = info.DownloadsLastThirtyDays,
            DownloadsLastTenDays = info.DownloadsLastTenDays,
            LastReleasedUtc = info.LastReleasedUtc,
            CreatedUtc = info.CreatedUtc,
            RequiredGameVersions = info.RequiredGameVersions?.ToArray() ?? Array.Empty<string>(),
            Releases = releaseModels.ToArray(),
            Side = info.Side
        };
    }

    private static ModDatabaseInfo? ConvertToDatabaseInfo(
        CachedModDatabaseInfo cached,
        string? normalizedGameVersion,
        string? installedModVersion,
        bool requireExactVersionMatch)
    {
        var normalizedInstalledVersion = NormalizeModVersion(installedModVersion);

        var tags = GetTagsForInstalledVersion(
            cached,
            normalizedInstalledVersion,
            out var cachedTagsVersion);

        var releases = BuildReleases(cached.Releases, normalizedGameVersion, requireExactVersionMatch);

        var latestRelease = releases.Count > 0 ? releases[0] : null;
        var latestCompatibleRelease = releases.FirstOrDefault(r => r.IsCompatibleWithInstalledGame);

        var requiredVersions = DetermineRequiredGameVersions(
            cached.Releases,
            installedModVersion);

        return new ModDatabaseInfo
        {
            Tags = tags,
            CachedTagsVersion = cachedTagsVersion,
            AssetId = cached.AssetId,
            ModPageUrl = cached.ModPageUrl,
            LatestCompatibleVersion = latestCompatibleRelease?.Version,
            LatestVersion = latestRelease?.Version,
            RequiredGameVersions = requiredVersions,
            Downloads = cached.Downloads,
            Comments = cached.Comments,
            Follows = cached.Follows,
            TrendingPoints = cached.TrendingPoints,
            LogoUrl = cached.LogoUrl,
            LogoUrlSource = cached.LogoUrlSource,
            DownloadsLastThirtyDays = cached.DownloadsLastThirtyDays,
            DownloadsLastTenDays = cached.DownloadsLastTenDays,
            LastReleasedUtc = cached.LastReleasedUtc,
            CreatedUtc = cached.CreatedUtc,
            LatestRelease = latestRelease,
            LatestCompatibleRelease = latestCompatibleRelease,
            Releases = releases,
            Side = cached.Side
        };
    }

    private static IReadOnlyList<string> GetTagsForInstalledVersion(
        CachedModDatabaseInfo cached,
        string? normalizedInstalledVersion,
        out string? cachedTagsVersion)
    {
        cachedTagsVersion = null;

        if (!string.IsNullOrWhiteSpace(normalizedInstalledVersion)
            && cached.TagsByModVersion is { Count: > 0 }
            && cached.TagsByModVersion.TryGetValue(normalizedInstalledVersion, out var versionTags))
        {
            cachedTagsVersion = normalizedInstalledVersion;
            return versionTags is { Length: > 0 } ? versionTags : Array.Empty<string>();
        }

        if (cached.Tags is { Length: > 0 }) return cached.Tags;

        if (cached.TagsByModVersion is { Count: > 0 })
            foreach (var entry in cached.TagsByModVersion)
                if (entry.Value is { Length: > 0 })
                    return entry.Value;

        return Array.Empty<string>();
    }

    private static IReadOnlyList<ModReleaseInfo> BuildReleases(
        IReadOnlyList<CachedModRelease>? cachedReleases,
        string? normalizedGameVersion,
        bool requireExactVersionMatch)
    {
        if (cachedReleases is null || cachedReleases.Count == 0) return Array.Empty<ModReleaseInfo>();

        var releases = new List<ModReleaseInfo>(cachedReleases.Count);

        foreach (var release in cachedReleases)
        {
            if (string.IsNullOrWhiteSpace(release.Version)
                || string.IsNullOrWhiteSpace(release.DownloadUrl)
                || !Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var downloadUri))
                continue;

            var isCompatible = false;
            if (normalizedGameVersion != null && release.GameVersionTags is { Length: > 0 })
                foreach (var tag in release.GameVersionTags)
                    if (VersionStringUtility.SupportsVersion(tag, normalizedGameVersion, requireExactVersionMatch))
                    {
                        isCompatible = true;
                        break;
                    }

            releases.Add(new ModReleaseInfo
            {
                Version = release.Version,
                NormalizedVersion = release.NormalizedVersion,
                DownloadUri = downloadUri,
                FileName = release.FileName,
                GameVersionTags = release.GameVersionTags ?? Array.Empty<string>(),
                IsCompatibleWithInstalledGame = isCompatible,
                Changelog = release.Changelog,
                Downloads = release.Downloads,
                CreatedUtc = release.CreatedUtc
            });
        }

        return releases.Count == 0 ? Array.Empty<ModReleaseInfo>() : releases;
    }

    private static IReadOnlyList<string> DetermineRequiredGameVersions(
        IReadOnlyList<CachedModRelease>? cachedReleases,
        string? installedModVersion)
    {
        if (string.IsNullOrWhiteSpace(installedModVersion)
            || cachedReleases is null
            || cachedReleases.Count == 0)
            return Array.Empty<string>();

        var normalizedInstalledVersion = VersionStringUtility.Normalize(installedModVersion);

        foreach (var release in cachedReleases)
        {
            if (string.IsNullOrWhiteSpace(release.Version)) continue;

            if (ReleaseMatchesInstalledVersion(
                    release.Version,
                    release.NormalizedVersion,
                    installedModVersion,
                    normalizedInstalledVersion))
            {
                var tags = release.GameVersionTags ?? Array.Empty<string>();
                return tags.Length == 0 ? Array.Empty<string>() : tags;
            }
        }

        return Array.Empty<string>();
    }

    private static bool ReleaseMatchesInstalledVersion(
        string releaseVersion,
        string? normalizedReleaseVersion,
        string installedVersion,
        string? normalizedInstalledVersion)
    {
        if (string.Equals(releaseVersion, installedVersion, StringComparison.OrdinalIgnoreCase)) return true;

        if (string.IsNullOrWhiteSpace(normalizedReleaseVersion) ||
            string.IsNullOrWhiteSpace(normalizedInstalledVersion)) return false;

        return string.Equals(normalizedReleaseVersion, normalizedInstalledVersion, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<SemaphoreSlim> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        var gate = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return gate;
    }

    private static bool IsSupportedSchemaVersion(int schemaVersion)
    {
        return schemaVersion >= MinimumSupportedCacheSchemaVersion
               && schemaVersion <= CacheSchemaVersion;
    }

    private static string? GetCacheFilePath(string modId, string? normalizedGameVersion)
    {
        if (string.IsNullOrWhiteSpace(modId)) return null;

        var baseDirectory = ModCacheLocator.GetModDatabaseCacheDirectory();
        if (string.IsNullOrWhiteSpace(baseDirectory)) return null;

        var safeModId = ModCacheLocator.SanitizeFileName(modId, "mod");
        var safeGameVersion = string.IsNullOrWhiteSpace(normalizedGameVersion)
            ? AnyGameVersionToken
            : ModCacheLocator.SanitizeFileName(normalizedGameVersion!, "game");

        var fileName = $"{safeModId}__{safeGameVersion}.json";

        return Path.Combine(baseDirectory, fileName);
    }

    private static bool IsGameVersionMatch(string? cachedGameVersion, string? normalizedGameVersion)
    {
        var cachedValue = string.IsNullOrWhiteSpace(cachedGameVersion) ? AnyGameVersionToken : cachedGameVersion;
        var currentValue = string.IsNullOrWhiteSpace(normalizedGameVersion)
            ? AnyGameVersionToken
            : normalizedGameVersion;
        return string.Equals(cachedValue, currentValue, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CachedModDatabaseInfo
    {
        public int SchemaVersion { get; init; }

        public string ModId { get; init; } = string.Empty;

        public string GameVersion { get; init; } = AnyGameVersionToken;

        public DateTimeOffset CachedAt { get; init; }

        /// <summary>
        ///     The Last-Modified header value from the HTTP response, used for conditional requests.
        /// </summary>
        public string? LastModifiedHeader { get; init; }

        /// <summary>
        ///     The ETag header value from the HTTP response, used for conditional requests.
        /// </summary>
        public string? ETag { get; init; }

        /// <summary>
        ///     The lastmodified value from the API response JSON, used for cache invalidation.
        /// </summary>
        public string? LastModifiedApiValue { get; init; }

        public string[] Tags { get; init; } = Array.Empty<string>();

        public Dictionary<string, string[]> TagsByModVersion { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public string? AssetId { get; init; }

        public string? ModPageUrl { get; init; }

        public int? Downloads { get; init; }

        public int? Comments { get; init; }

        public int? Follows { get; init; }

        public int? TrendingPoints { get; init; }

        public string? LogoUrl { get; init; }

        public string? LogoUrlSource { get; init; }

        public int? DownloadsLastThirtyDays { get; init; }

        public int? DownloadsLastTenDays { get; init; }

        public DateTime? LastReleasedUtc { get; init; }

        public DateTime? CreatedUtc { get; init; }

        public string[] RequiredGameVersions { get; init; } = Array.Empty<string>();

        public CachedModRelease[] Releases { get; init; } = Array.Empty<CachedModRelease>();

        public string? Side { get; init; }
    }

    private sealed class CachedModRelease
    {
        public string Version { get; init; } = string.Empty;

        public string? NormalizedVersion { get; init; }

        public string DownloadUrl { get; init; } = string.Empty;

        public string? FileName { get; init; }

        public string[] GameVersionTags { get; init; } = Array.Empty<string>();

        public string? Changelog { get; init; }

        public int? Downloads { get; init; }

        public DateTime? CreatedUtc { get; init; }
    }

    /// <summary>
    ///     Represents an entry in the in-memory cache.
    /// </summary>
    private sealed class InMemoryCacheEntry
    {
        public required ModDatabaseInfo? Result { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
    }

    private static string BuildInMemoryCacheKey(string cachePath, string? installedModVersion, bool requireExactVersionMatch)
    {
        // Include version and match mode in the key since they affect the result
        var normalizedVersion = string.IsNullOrWhiteSpace(installedModVersion) ? "_" : installedModVersion;
        var matchMode = requireExactVersionMatch ? "e" : "p";
        return $"{cachePath}|{normalizedVersion}|{matchMode}";
    }

    private static bool IsInMemoryCacheEntryExpired(InMemoryCacheEntry entry)
    {
        return DateTimeOffset.Now - entry.CreatedAt > InMemoryCacheMaxAge;
    }

    private void TryAddToInMemoryCache(string key, ModDatabaseInfo? result)
    {
        // Simple size limit enforcement: if at capacity, don't add new entries
        // This is a basic approach; a more sophisticated LRU could be implemented if needed
        if (_inMemoryCache.Count >= MaxInMemoryCacheSize)
        {
            // Evict oldest entries when at capacity
            EvictOldestCacheEntries();
        }

        var entry = new InMemoryCacheEntry
        {
            Result = result,
            CreatedAt = DateTimeOffset.Now
        };

        _inMemoryCache.TryAdd(key, entry);
    }

    private void EvictOldestCacheEntries()
    {
        // Remove expired entries first using a simple scan
        // This is more efficient than sorting for the common case
        var now = DateTimeOffset.Now;
        DateTimeOffset? oldestTimestamp = null;
        string? oldestKey = null;

        // First pass: collect keys to remove (to avoid modifying while iterating)
        var expiredKeys = new List<string>();

        foreach (var kvp in _inMemoryCache)
        {
            if (now - kvp.Value.CreatedAt > InMemoryCacheMaxAge)
            {
                expiredKeys.Add(kvp.Key);
            }
            else if (oldestTimestamp is null || kvp.Value.CreatedAt < oldestTimestamp)
            {
                oldestTimestamp = kvp.Value.CreatedAt;
                oldestKey = kvp.Key;
            }
        }

        // Second pass: remove expired entries
        foreach (var key in expiredKeys)
        {
            _inMemoryCache.TryRemove(key, out _);
        }

        // If still over capacity after removing expired entries, remove oldest entry
        // This is a simple approach that removes one entry at a time
        // The cache will naturally stay under limit with repeated calls
        if (_inMemoryCache.Count >= MaxInMemoryCacheSize && oldestKey != null)
        {
            _inMemoryCache.TryRemove(oldestKey, out _);
        }
    }

    /// <summary>
    ///     Invalidates the in-memory cache entry for a specific mod when the disk cache is updated.
    /// </summary>
    private void InvalidateInMemoryCacheForMod(string cachePath)
    {
        // Build the prefix once for comparison
        var prefix = cachePath + "|";

        // Collect keys to remove first (to avoid modifying while iterating)
        var keysToRemove = new List<string>();
        foreach (var key in _inMemoryCache.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                keysToRemove.Add(key);
            }
        }

        // Remove the collected keys
        foreach (var key in keysToRemove)
        {
            _inMemoryCache.TryRemove(key, out _);
        }
    }
}