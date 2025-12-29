using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using HtmlAgilityPack;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;
using HtmlEntity = HtmlAgilityPack.HtmlEntity;
using HtmlNode = HtmlAgilityPack.HtmlNode;
using HtmlNodeCollection = HtmlAgilityPack.HtmlNodeCollection;
using HtmlNodeType = HtmlAgilityPack.HtmlNodeType;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.Services;

/// <summary>
///     Retrieves additional metadata for installed mods from the Vintage Story mod database.
/// </summary>
public sealed class ModDatabaseService
{
    private static readonly string ApiEndpointFormat = DevConfig.ModDatabaseApiEndpointFormat;
    private static readonly string SearchEndpointFormat = DevConfig.ModDatabaseSearchEndpointFormat;
    private static readonly string MostDownloadedEndpointFormat = DevConfig.ModDatabaseMostDownloadedEndpointFormat;
    private static readonly string RecentlyCreatedEndpointFormat = DevConfig.ModDatabaseRecentlyCreatedEndpointFormat;
    private static readonly string RecentlyUpdatedEndpointFormat = DevConfig.ModDatabaseRecentlyUpdatedEndpointFormat;
    private static readonly string ModPageBaseUrl = DevConfig.ModDatabasePageBaseUrl;
    private static readonly int MaxConcurrentMetadataRequests = DevConfig.ModDatabaseMaxConcurrentMetadataRequests;

    private static readonly int MinimumTotalDownloadsForTrending =
        DevConfig.ModDatabaseMinimumTotalDownloadsForTrending;

    private static readonly int DefaultNewModsMonths = DevConfig.ModDatabaseDefaultNewModsMonths;
    private static readonly int MaxNewModsMonths = DevConfig.ModDatabaseMaxNewModsMonths;

    private static readonly HttpClient HttpClient = new();
    private static readonly ModDatabaseCacheService CacheService = new();

    private static int CalculateRequestLimit(int maxResults)
    {
        var scaledLimit = maxResults * 4L;
        if (scaledLimit < maxResults) scaledLimit = maxResults;

        if (scaledLimit > int.MaxValue) return int.MaxValue;

        return (int)scaledLimit;
    }

    public async Task PopulateModDatabaseInfoAsync(IEnumerable<ModEntry> mods, string? installedGameVersion,
        bool requireExactVersionMatch = false, CancellationToken cancellationToken = default)
    {
        if (mods is null) throw new ArgumentNullException(nameof(mods));

        var normalizedGameVersion = VersionStringUtility.Normalize(installedGameVersion);

        var internetDisabled = InternetAccessManager.IsInternetAccessDisabled;

        using var semaphore = new SemaphoreSlim(MaxConcurrentMetadataRequests);
        var tasks = new List<Task>();

        foreach (var mod in mods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (mod is null || string.IsNullOrWhiteSpace(mod.ModId)) continue;

            tasks.Add(ProcessModAsync(mod));
        }

        if (tasks.Count == 0) return;

        await Task.WhenAll(tasks).ConfigureAwait(false);

        async Task ProcessModAsync(ModEntry modEntry)
        {
            var installedModVersion = modEntry.Version;

            // Always load from cache first
            var (cached, cachedLastModified, cachedAt) = await CacheService
                .TryLoadWithLastModifiedAsync(
                    modEntry.ModId,
                    normalizedGameVersion,
                    installedModVersion,
                    requireExactVersionMatch,
                    cancellationToken)
                .ConfigureAwait(false);

            if (cached != null)
            {
                modEntry.DatabaseInfo = cached;
                // Update metadata cache with tags from database cache
                if (cached.Tags is { Count: > 0 })
                    ModManifestCacheService.UpdateTags(modEntry.ModId, installedModVersion, cached.Tags);
            }

            // Skip network request if internet is disabled
            if (internetDisabled) return;

            // Check if we should fetch from network:
            // 1. No cache exists
            // 2. No cached lastmodified value (old cache format)
            // 3. Cache has hard-expired (> 2 hours)
            var isHardExpired = cachedAt.HasValue && DateTimeOffset.Now - cachedAt.Value > ModCacheHardExpiry;
            var needsFetch = cached == null ||
                             string.IsNullOrWhiteSpace(cachedLastModified) ||
                             isHardExpired;

            if (!needsFetch) return;

            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var info = await TryLoadDatabaseInfoInternalAsync(
                        modEntry.ModId,
                        installedModVersion,
                        normalizedGameVersion,
                        requireExactVersionMatch,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (info != null)
                {
                    modEntry.DatabaseInfo = info;
                    // Update metadata cache with tags from database info
                    if (info.Tags is { Count: > 0 })
                        ModManifestCacheService.UpdateTags(modEntry.ModId, installedModVersion, info.Tags);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }
    }

    /// <summary>
    ///     Soft expiry time for per-mod cache entries. Cache entries older than this will trigger
    ///     a refresh from the network.
    /// </summary>
    private static readonly TimeSpan ModCacheSoftExpiry = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Hard expiry time for per-mod cache entries. Cache entries older than this will always
    ///     be refreshed from the network, regardless of the lastmodified value.
    ///     This ensures data eventually gets refreshed even if the API's lastmodified field is not changing.
    /// </summary>
    private static readonly TimeSpan ModCacheHardExpiry = TimeSpan.FromHours(2);

    /// <summary>
    ///     Checks if a cache entry is soft-expired based on its timestamp.
    ///     Returns true if the cache doesn't exist or is older than soft expiry.
    /// </summary>
    private static bool IsCacheSoftExpired(DateTimeOffset? cachedAt)
    {
        return !cachedAt.HasValue || DateTimeOffset.Now - cachedAt.Value > ModCacheSoftExpiry;
    }

    /// <summary>
    ///     Checks if a cache entry is hard-expired based on its timestamp.
    ///     Returns true if the cache doesn't exist or is older than hard expiry.
    /// </summary>
    private static bool IsCacheHardExpired(DateTimeOffset? cachedAt)
    {
        return !cachedAt.HasValue || DateTimeOffset.Now - cachedAt.Value > ModCacheHardExpiry;
    }

    /// <summary>
    ///     Checks if a refresh is needed based on the cached lastmodified value and cache age.
    ///     Returns true if cache is missing, soft-expired, or hard-expired.
    ///     Returns false if cache exists with a lastmodified value and hasn't soft-expired.
    /// </summary>
    private async Task<bool> CheckIfRefreshNeededAsync(
        string modId,
        string? normalizedGameVersion,
        CancellationToken cancellationToken)
    {
        if (InternetAccessManager.IsInternetAccessDisabled) return false;

        try
        {
            // Get cached lastmodified value and timestamp
            var (cachedLastModified, cachedAt) = await CacheService
                .GetCachedLastModifiedAsync(modId, normalizedGameVersion, cancellationToken)
                .ConfigureAwait(false);

            // If cache is older than hard expiry or doesn't exist, force a refresh
            if (IsCacheHardExpired(cachedAt))
            {
                return true;
            }

            // If no cached lastmodified value exists, we need to fetch data
            if (string.IsNullOrWhiteSpace(cachedLastModified))
            {
                return true;
            }

            // If cache is older than soft expiry, trigger a refresh
            // The API's lastmodified field will be checked after fetching fresh data
            if (IsCacheSoftExpired(cachedAt))
            {
                return true;
            }

            // Cache exists with lastmodified and hasn't soft-expired or hard-expired - no refresh needed
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] JSON parse error checking refresh for mod '{modId}': {ex.Message}");
            // On error, assume cache is valid to avoid excessive requests
            return false;
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] HTTP request error checking refresh for mod '{modId}': {ex.Message}");
            // On error, assume cache is valid to avoid excessive requests
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] Unexpected error checking refresh for mod '{modId}': {ex.Message}");
            // On error, assume cache is valid to avoid excessive requests
            return false;
        }
    }

    public Task<ModDatabaseInfo?> TryLoadDatabaseInfoAsync(string modId, string? modVersion,
        string? installedGameVersion, bool requireExactVersionMatch = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modId)) return Task.FromResult<ModDatabaseInfo?>(null);

        var normalizedGameVersion = VersionStringUtility.Normalize(installedGameVersion);
        return TryLoadDatabaseInfoAsyncCore(modId, modVersion, normalizedGameVersion, requireExactVersionMatch,
            null, cancellationToken);
    }

    /// <summary>
    ///     Loads database info for a mod, optionally using pre-loaded cached info to avoid double disk reads.
    ///     When preloaded cache info is provided, callers should first check cache freshness using
    ///     <see cref="TryLoadCachedDatabaseInfoWithFreshnessAsync"/> and only call this method when
    ///     a network refresh is desired (i.e., when the cache is stale).
    /// </summary>
    /// <param name="modId">The mod ID to look up.</param>
    /// <param name="modVersion">The installed mod version.</param>
    /// <param name="installedGameVersion">The installed game version.</param>
    /// <param name="requireExactVersionMatch">Whether to require exact version matching.</param>
    /// <param name="preloadedCachedInfo">Previously loaded cached info to avoid re-reading from disk. When provided,
    /// this method will attempt a network refresh. Pass null to have freshness checked automatically.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="timingService">Optional timing service for detailed performance measurement.</param>
    /// <returns>The database info, or null if not found.</returns>
    public Task<ModDatabaseInfo?> TryLoadDatabaseInfoAsync(
        string modId,
        string? modVersion,
        string? installedGameVersion,
        bool requireExactVersionMatch,
        ModDatabaseInfo? preloadedCachedInfo,
        CancellationToken cancellationToken = default,
        ModLoadingTimingService? timingService = null)
    {
        if (string.IsNullOrWhiteSpace(modId)) return Task.FromResult<ModDatabaseInfo?>(null);

        var normalizedGameVersion = VersionStringUtility.Normalize(installedGameVersion);
        return TryLoadDatabaseInfoAsyncCore(modId, modVersion, normalizedGameVersion, requireExactVersionMatch,
            preloadedCachedInfo, cancellationToken, timingService);
    }

    public Task<ModDatabaseInfo?> TryLoadCachedDatabaseInfoAsync(
        string modId,
        string? modVersion,
        string? installedGameVersion,
        bool requireExactVersionMatch = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modId)) return Task.FromResult<ModDatabaseInfo?>(null);

        var normalizedGameVersion = VersionStringUtility.Normalize(installedGameVersion);
        return CacheService.TryLoadAsync(
            modId,
            normalizedGameVersion,
            modVersion,
            false,
            requireExactVersionMatch,
            cancellationToken);
    }

    /// <summary>
    ///     Attempts to load cached database info and checks if refresh is needed by version comparison.
    /// </summary>
    /// <param name="modId">The mod ID to look up.</param>
    /// <param name="modVersion">The installed mod version.</param>
    /// <param name="installedGameVersion">The installed game version.</param>
    /// <param name="requireExactVersionMatch">Whether to require exact version matching.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple containing the cached info (or null) and whether a refresh is needed.</returns>
    public async Task<(ModDatabaseInfo? Info, bool NeedsRefresh)> TryLoadCachedDatabaseInfoWithRefreshCheckAsync(
        string modId,
        string? modVersion,
        string? installedGameVersion,
        bool requireExactVersionMatch = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modId)) return (null, true);

        var normalizedGameVersion = VersionStringUtility.Normalize(installedGameVersion);

        // Always load from cache first
        var cached = await CacheService.TryLoadWithoutExpiryAsync(
            modId,
            normalizedGameVersion,
            modVersion,
            requireExactVersionMatch,
            cancellationToken).ConfigureAwait(false);

        // If no cache or internet is disabled, return what we have
        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            return (cached, false);
        }

        // Check if refresh is needed using HTTP conditional request
        var needsRefresh = cached == null || await CheckIfRefreshNeededAsync(
            modId, normalizedGameVersion, cancellationToken).ConfigureAwait(false);

        return (cached, needsRefresh);
    }

    public async Task<string?> TryFetchLatestReleaseVersionAsync(string modId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modId) || InternetAccessManager.IsInternetAccessDisabled) return null;

        try
        {
            var requestUri =
                string.Format(CultureInfo.InvariantCulture, ApiEndpointFormat, Uri.EscapeDataString(modId));
            using HttpRequestMessage request = new(HttpMethod.Get, requestUri);
            using var response = await HttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return null;

            await using var contentStream =
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument
                .ParseAsync(contentStream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("mod", out var modElement)
                || modElement.ValueKind != JsonValueKind.Object)
                return null;

            if (TryGetLatestReleaseVersion(modElement, out var version) && !string.IsNullOrWhiteSpace(version))
                return version;

            return GetString(modElement, "latestversion");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] JSON parse error for mod '{modId}': {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] JSON exception details: {ex}");
            return null;
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] HTTP request error for mod '{modId}': {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] Unexpected error fetching version for mod '{modId}': {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] Exception details: {ex}");
            return null;
        }
    }

    private async Task<ModDatabaseInfo?> TryLoadDatabaseInfoAsyncCore(
        string modId,
        string? modVersion,
        string? normalizedGameVersion,
        bool requireExactVersionMatch,
        ModDatabaseInfo? preloadedCachedInfo,
        CancellationToken cancellationToken,
        ModLoadingTimingService? timingService = null)
    {
        var internetDisabled = InternetAccessManager.IsInternetAccessDisabled;

        ModDatabaseInfo? cached;
        bool needsRefresh;

        if (preloadedCachedInfo != null)
        {
            // When preloaded cache info is provided, the caller is expected to have already
            // checked if refresh is needed and only call this method when a network refresh is desired.
            // This avoids duplicate cache reads and version checks.
            cached = preloadedCachedInfo;
            needsRefresh = true;
        }
        else
        {
            // Load from disk and check if refresh is needed via HTTP conditional request
            cached = await CacheService
                .TryLoadWithoutExpiryAsync(
                    modId,
                    normalizedGameVersion,
                    modVersion,
                    requireExactVersionMatch,
                    cancellationToken)
                .ConfigureAwait(false);

            if (internetDisabled) return cached;

            // Check if data has changed on the server using HTTP conditional request
            needsRefresh = cached == null || await CheckIfRefreshNeededAsync(
                modId, normalizedGameVersion, cancellationToken).ConfigureAwait(false);
        }

        // Skip network request if internet is disabled or no refresh needed
        if (internetDisabled || !needsRefresh) return cached;

        var info = await TryLoadDatabaseInfoInternalAsync(modId, modVersion, normalizedGameVersion,
                requireExactVersionMatch, cancellationToken, timingService)
            .ConfigureAwait(false);

        return info ?? cached;
    }

    public async Task<IReadOnlyList<ModDatabaseSearchResult>> SearchModsAsync(string query, int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0) return Array.Empty<ModDatabaseSearchResult>();

        var trimmed = query.Trim();
        var tokens = CreateSearchTokens(trimmed);
        if (tokens.Count == 0) return Array.Empty<ModDatabaseSearchResult>();

        InternetAccessManager.ThrowIfInternetAccessDisabled();

        var requestLimit = CalculateRequestLimit(maxResults);
        var requestUri = string.Format(
            CultureInfo.InvariantCulture,
            SearchEndpointFormat,
            Uri.EscapeDataString(trimmed),
            requestLimit.ToString(CultureInfo.InvariantCulture));

        return await QueryModsAsync(
                requestUri,
                maxResults,
                tokens,
                true,
                candidates => candidates
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenByDescending(candidate => candidate.Downloads)
                    .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ModDatabaseSearchResult>> GetMostDownloadedModsAsync(int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (maxResults <= 0) return Array.Empty<ModDatabaseSearchResult>();

        var requestLimit = CalculateRequestLimit(maxResults);
        var requestUri = string.Format(
            CultureInfo.InvariantCulture,
            MostDownloadedEndpointFormat,
            requestLimit.ToString(CultureInfo.InvariantCulture));

        return await QueryModsAsync(
                requestUri,
                maxResults,
                Array.Empty<string>(),
                false,
                candidates => candidates
                    .OrderByDescending(candidate => candidate.Downloads)
                    .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ModDatabaseSearchResult>> GetMostDownloadedModsLastThirtyDaysAsync(
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (maxResults <= 0) return Array.Empty<ModDatabaseSearchResult>();

        var requestLimit = CalculateRequestLimit(maxResults);
        var requestUri = string.Format(
            CultureInfo.InvariantCulture,
            MostDownloadedEndpointFormat,
            requestLimit.ToString(CultureInfo.InvariantCulture));

        var candidates = await QueryModsAsync(
                requestUri,
                requestLimit,
                Array.Empty<string>(),
                false,
                results => results
                    .OrderByDescending(candidate => candidate.Downloads)
                    .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase),
                cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0) return Array.Empty<ModDatabaseSearchResult>();

        IReadOnlyList<ModDatabaseSearchResult> filtered = candidates
            .Where(candidate => candidate.Downloads >= MinimumTotalDownloadsForTrending)
            .ToArray();

        if (filtered.Count == 0) return Array.Empty<ModDatabaseSearchResult>();

        var enriched = await EnrichWithLatestReleaseDownloadsAsync(filtered, cancellationToken)
            .ConfigureAwait(false);

        if (enriched.Count == 0) return Array.Empty<ModDatabaseSearchResult>();

        return enriched
            .OrderByDescending(candidate => candidate.DetailedInfo?.DownloadsLastThirtyDays ?? 0)
            .ThenByDescending(candidate => candidate.Downloads)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray();
    }

    public async Task<IReadOnlyList<ModDatabaseSearchResult>> GetMostDownloadedModsLastTenDaysAsync(
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (maxResults <= 0) return Array.Empty<ModDatabaseSearchResult>();

        var requestLimit = CalculateRequestLimit(maxResults);
        var requestUri = string.Format(
            CultureInfo.InvariantCulture,
            MostDownloadedEndpointFormat,
            requestLimit.ToString(CultureInfo.InvariantCulture));

        var candidates = await QueryModsAsync(
                requestUri,
                requestLimit,
                Array.Empty<string>(),
                false,
                results => results
                    .OrderByDescending(candidate => candidate.Downloads)
                    .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase),
                cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0) return Array.Empty<ModDatabaseSearchResult>();

        IReadOnlyList<ModDatabaseSearchResult> filtered = candidates
            .Where(candidate => candidate.Downloads >= MinimumTotalDownloadsForTrending)
            .ToArray();

        if (filtered.Count == 0) return Array.Empty<ModDatabaseSearchResult>();

        var enriched = await EnrichWithLatestReleaseDownloadsAsync(filtered, cancellationToken)
            .ConfigureAwait(false);

        if (enriched.Count == 0) return Array.Empty<ModDatabaseSearchResult>();

        return enriched
            .OrderByDescending(candidate => candidate.DetailedInfo?.DownloadsLastTenDays ?? 0)
            .ThenByDescending(candidate => candidate.Downloads)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray();
    }

    public async Task<IReadOnlyList<ModDatabaseSearchResult>> GetMostDownloadedNewModsAsync(
        int months,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (maxResults <= 0) return Array.Empty<ModDatabaseSearchResult>();

        var normalizedMonths = months <= 0 ? DefaultNewModsMonths : Math.Clamp(months, 1, MaxNewModsMonths);

        var requestLimit = Math.Clamp(maxResults * 6, Math.Max(maxResults, 60), 150);
        var requestUri = string.Format(
            CultureInfo.InvariantCulture,
            RecentlyCreatedEndpointFormat,
            requestLimit.ToString(CultureInfo.InvariantCulture));

        var candidates = await QueryModsAsync(
                requestUri,
                requestLimit,
                Array.Empty<string>(),
                false,
                results => results,
                cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0) return Array.Empty<ModDatabaseSearchResult>();

        var enriched = await EnrichWithLatestReleaseDownloadsAsync(candidates, cancellationToken)
            .ConfigureAwait(false);

        if (enriched.Count == 0) return Array.Empty<ModDatabaseSearchResult>();

        var threshold = DateTime.UtcNow.AddMonths(-normalizedMonths);

        var filtered = enriched
            .Where(candidate => WasCreatedOnOrAfter(candidate, threshold))
            .OrderByDescending(candidate => candidate.Downloads)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray();

        return filtered.Length == 0 ? Array.Empty<ModDatabaseSearchResult>() : filtered;
    }

    public async Task<IReadOnlyList<ModDatabaseSearchResult>> GetRecentlyUpdatedModsAsync(
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (maxResults <= 0) return Array.Empty<ModDatabaseSearchResult>();

        var requestLimit = CalculateRequestLimit(maxResults);
        var requestUri = string.Format(
            CultureInfo.InvariantCulture,
            RecentlyUpdatedEndpointFormat,
            requestLimit.ToString(CultureInfo.InvariantCulture));

        return await QueryModsAsync(
                requestUri,
                maxResults,
                Array.Empty<string>(),
                false,
                candidates => candidates
                    .Where(candidate => candidate.LastReleasedUtc.HasValue)
                    .OrderByDescending(candidate => candidate.LastReleasedUtc!.Value)
                    .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ModDatabaseSearchResult>> GetRecentlyAddedModsAsync(
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (maxResults <= 0) return Array.Empty<ModDatabaseSearchResult>();

        var requestLimit = CalculateRequestLimit(maxResults);
        var requestUri = string.Format(
            CultureInfo.InvariantCulture,
            RecentlyCreatedEndpointFormat,
            requestLimit.ToString(CultureInfo.InvariantCulture));

        var candidates = await QueryModsAsync(
                requestUri,
                requestLimit,
                Array.Empty<string>(),
                false,
                results => results,
                cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0) return Array.Empty<ModDatabaseSearchResult>();

        var enriched = await EnrichWithLatestReleaseDownloadsAsync(
                candidates,
                cancellationToken)
            .ConfigureAwait(false);

        if (enriched.Count == 0) return Array.Empty<ModDatabaseSearchResult>();

        return enriched
            .Select((candidate, index) => new
            {
                Candidate = candidate,
                Index = index,
                SortKey = candidate.CreatedUtc ?? candidate.LastReleasedUtc
            })
            .OrderByDescending(item => item.SortKey ?? DateTime.MinValue)
            .ThenBy(item => item.Candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Index)
            .Select(item => item.Candidate)
            .Take(maxResults)
            .ToArray();
    }

    public async Task<IReadOnlyList<ModDatabaseSearchResult>> GetMostTrendingModsAsync(
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (maxResults <= 0) return Array.Empty<ModDatabaseSearchResult>();

        var requestLimit = CalculateRequestLimit(maxResults);
        var requestUri = string.Format(
            CultureInfo.InvariantCulture,
            MostDownloadedEndpointFormat,
            requestLimit.ToString(CultureInfo.InvariantCulture));

        return await QueryModsAsync(
                requestUri,
                maxResults,
                Array.Empty<string>(),
                false,
                candidates => candidates
                    .OrderByDescending(candidate => candidate.TrendingPoints)
                    .ThenByDescending(candidate => candidate.Downloads)
                    .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ModDatabaseSearchResult>> GetRandomModsAsync(
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (maxResults <= 0) return Array.Empty<ModDatabaseSearchResult>();

        // Fetch a larger pool to randomize from
        var requestLimit = CalculateRequestLimit(maxResults * 10);
        var requestUri = string.Format(
            CultureInfo.InvariantCulture,
            MostDownloadedEndpointFormat,
            requestLimit.ToString(CultureInfo.InvariantCulture));

        var candidates = await QueryModsAsync(
                requestUri,
                requestLimit,
                Array.Empty<string>(),
                false,
                results => results,
                cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0) return Array.Empty<ModDatabaseSearchResult>();

        // Shuffle the results using Fisher-Yates algorithm
        var random = new Random(Guid.NewGuid().GetHashCode());
        var shuffled = candidates.ToArray();
        for (var i = shuffled.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        return shuffled.Take(maxResults).ToArray();
    }

    private static bool WasCreatedOnOrAfter(ModDatabaseSearchResult candidate, DateTime thresholdUtc)
    {
        var createdUtc = candidate.CreatedUtc ?? candidate.DetailedInfo?.CreatedUtc;
        if (createdUtc.HasValue) return createdUtc.Value >= thresholdUtc;

        var info = candidate.DetailedInfo;
        if (info?.Releases is { Count: > 0 } releases)
        {
            DateTime? earliest = null;
            foreach (var release in releases)
            {
                if (release?.CreatedUtc is not { } releaseCreatedUtc) continue;

                if (earliest is null || releaseCreatedUtc < earliest.Value) earliest = releaseCreatedUtc;
            }

            if (earliest.HasValue) return earliest.Value >= thresholdUtc;
        }

        if (candidate.LastReleasedUtc is { } lastReleasedUtc) return lastReleasedUtc >= thresholdUtc;

        return false;
    }

    private async Task<IReadOnlyList<ModDatabaseSearchResult>> QueryModsAsync(
        string requestUri,
        int maxResults,
        IReadOnlyList<string> tokens,
        bool requireTokenMatch,
        Func<IEnumerable<ModDatabaseSearchResult>, IEnumerable<ModDatabaseSearchResult>> orderResults,
        CancellationToken cancellationToken)
    {
        // If internet is disabled, return empty results
        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            return Array.Empty<ModDatabaseSearchResult>();
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, requestUri);
            using var response = await HttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return Array.Empty<ModDatabaseSearchResult>();

            await using var contentStream =
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("mods", out var modsElement)
                || modsElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<ModDatabaseSearchResult>();

            var candidates = new List<ModDatabaseSearchResult>();

            foreach (var modElement in modsElement.EnumerateArray())
            {
                if (modElement.ValueKind != JsonValueKind.Object) continue;

                var result = TryCreateSearchResult(modElement, tokens, requireTokenMatch);
                if (result != null) candidates.Add(result);
            }

            if (candidates.Count == 0) return Array.Empty<ModDatabaseSearchResult>();

            var orderedResults = orderResults(candidates)
                .Take(maxResults)
                .ToArray();

            return orderedResults;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] JSON parse error in search: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] Request URI: {requestUri}");
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] JSON exception details: {ex}");
            return Array.Empty<ModDatabaseSearchResult>();
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] HTTP request error in search: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] Request URI: {requestUri}");
            return Array.Empty<ModDatabaseSearchResult>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] Unexpected error in search: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] Request URI: {requestUri}");
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] Exception details: {ex}");
            return Array.Empty<ModDatabaseSearchResult>();
        }
    }

    private async Task<IReadOnlyList<ModDatabaseSearchResult>> EnrichWithLatestReleaseDownloadsAsync(
        IReadOnlyList<ModDatabaseSearchResult> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return Array.Empty<ModDatabaseSearchResult>();

        InternetAccessManager.ThrowIfInternetAccessDisabled();

        const int MaxConcurrentRequests = 6;
        using var semaphore = new SemaphoreSlim(MaxConcurrentRequests);

        var tasks = new Task<ModDatabaseSearchResult>[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            tasks[i] = EnrichCandidateWithLatestReleaseDownloadsAsync(candidate, semaphore, cancellationToken);
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<ModDatabaseSearchResult> EnrichCandidateWithLatestReleaseDownloadsAsync(
        ModDatabaseSearchResult candidate,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.ModId)) return CloneResultWithDetails(candidate, null, null);

        // Try to satisfy the request from cache first to avoid re-downloading identical payloads
        var cachedInfo = await CacheService
            .TryLoadWithoutExpiryAsync(candidate.ModId, null, null, false, cancellationToken)
            .ConfigureAwait(false);
        var cachedDownloads = ExtractLatestReleaseDownloads(cachedInfo);

        // If internet is disabled, return cached data (if any) without throwing
        if (InternetAccessManager.IsInternetAccessDisabled)
            return CloneResultWithDetails(candidate, cachedInfo, cachedDownloads);

        var needsRefresh = cachedInfo == null || await CheckIfRefreshNeededAsync(candidate.ModId, null, cancellationToken)
            .ConfigureAwait(false);
        if (!needsRefresh)
            return CloneResultWithDetails(candidate, cachedInfo, cachedDownloads);

        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var info = await TryLoadDatabaseInfoInternalAsync(candidate.ModId, null, null, false, cancellationToken)
                .ConfigureAwait(false);
            var latestDownloads = ExtractLatestReleaseDownloads(info ?? cachedInfo);
            return CloneResultWithDetails(candidate, info ?? cachedInfo, latestDownloads);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] JSON parse error enriching search result: {ex.Message}");
            return CloneResultWithDetails(candidate, cachedInfo, cachedDownloads);
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] HTTP request error enriching search result: {ex.Message}");
            return CloneResultWithDetails(candidate, cachedInfo, cachedDownloads);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] Unexpected error enriching search result: {ex.Message}");
            return CloneResultWithDetails(candidate, cachedInfo, cachedDownloads);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static ModDatabaseSearchResult CloneResultWithDetails(
        ModDatabaseSearchResult source,
        ModDatabaseInfo? info,
        int? latestDownloads)
    {
        return new ModDatabaseSearchResult
        {
            ModId = source.ModId,
            Name = source.Name,
            AlternateIds = source.AlternateIds,
            Summary = source.Summary,
            Author = source.Author,
            Tags = source.Tags,
            Downloads = source.Downloads,
            Follows = source.Follows,
            TrendingPoints = source.TrendingPoints,
            Comments = source.Comments,
            AssetId = source.AssetId,
            UrlAlias = source.UrlAlias,
            Side = source.Side,
            LogoUrl = source.LogoUrl,
            LogoUrlSource = source.LogoUrlSource,
            LastReleasedUtc = source.LastReleasedUtc,
            CreatedUtc = info?.CreatedUtc ?? source.CreatedUtc,
            Score = source.Score,
            LatestReleaseDownloads = latestDownloads,
            DetailedInfo = info
        };
    }

    private static int? ExtractLatestReleaseDownloads(ModDatabaseInfo? info)
    {
        if (info is null) return null;

        if (info.LatestRelease?.Downloads is int downloads) return downloads;

        if (info.Releases.Count > 0)
        {
            var latest = info.Releases[0];
            if (latest?.Downloads is int releaseDownloads) return releaseDownloads;
        }

        return null;
    }

    private static async Task<ModDatabaseInfo?> TryLoadDatabaseInfoInternalAsync(string modId, string? modVersion,
        string? normalizedGameVersion, bool requireExactVersionMatch, CancellationToken cancellationToken, ModLoadingTimingService? timingService = null)
    {
        if (InternetAccessManager.IsInternetAccessDisabled) return null;

        try
        {
            var normalizedModVersion = VersionStringUtility.Normalize(modVersion);

            var requestUri =
                string.Format(CultureInfo.InvariantCulture, ApiEndpointFormat, Uri.EscapeDataString(modId));
            using HttpRequestMessage request = new(HttpMethod.Get, requestUri);

            // Measure HTTP request/response time
            HttpResponseMessage response;
            using (timingService?.MeasureDbNetworkHttp())
            {
                response = await HttpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
            }

            using (response)
            {
                // Capture HTTP headers for conditional requests
                // Last-Modified can be in either response headers or content headers depending on the server
                string? lastModified = null;
                string? etag = null;
                if (response.Content.Headers.TryGetValues("Last-Modified", out var contentLastModifiedValues))
                {
                    lastModified = contentLastModifiedValues.FirstOrDefault();
                }
                else if (response.Headers.TryGetValues("Last-Modified", out var responseLastModifiedValues))
                {
                    lastModified = responseLastModifiedValues.FirstOrDefault();
                }
                // ETag can also be in either location
                if (response.Headers.TryGetValues("ETag", out var responseEtagValues))
                {
                    etag = responseEtagValues.FirstOrDefault();
                }
                else if (response.Content.Headers.TryGetValues("ETag", out var contentEtagValues))
                {
                    etag = contentEtagValues.FirstOrDefault();
                }

                // Measure JSON parsing time
                JsonDocument document;
                using (timingService?.MeasureDbNetworkParse())
                {
                    await using var contentStream =
                        await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }

                using (document)
                {
                    if (!document.RootElement.TryGetProperty("mod", out var modElement) ||
                        modElement.ValueKind != JsonValueKind.Object) return null;

                    // Measure data extraction time
                    ModDatabaseInfo info;
                    string? lastModifiedApiValue;
                    using (timingService?.MeasureDbNetworkExtract())
                    {
                        var tags = GetStringList(modElement, "tags");
                        var assetId = TryGetAssetId(modElement);
                        var modPageUrl = assetId == null ? null : ModPageBaseUrl + assetId;
                        var downloads = GetNullableInt(modElement, "downloads");
                        var comments = GetNullableInt(modElement, "comments");
                        var follows = GetNullableInt(modElement, "follows");
                        var trendingPoints = GetNullableInt(modElement, "trendingpoints");
                        var side = GetString(modElement, "side");
                        var logoUrl = GetString(modElement, "logofiledb");
                        var logoUrlSource = string.IsNullOrWhiteSpace(logoUrl) ? null : "logofiledb";
                        var lastReleasedUtc = TryParseDateTime(GetString(modElement, "lastreleased"));
                        var createdUtc = TryParseDateTime(GetString(modElement, "created"));
                        lastModifiedApiValue = GetString(modElement, "lastmodified");
                        var releases = BuildReleaseInfos(modElement, normalizedGameVersion, requireExactVersionMatch);
                        var latestRelease = releases.Count > 0 ? releases[0] : null;
                        var latestCompatibleRelease = releases.FirstOrDefault(release => release.IsCompatibleWithInstalledGame);
                        var latestVersion = latestRelease?.Version;
                        var latestCompatibleVersion = latestCompatibleRelease?.Version;
                        var requiredVersions = FindRequiredGameVersions(modElement, modVersion);
                        var recentDownloads = CalculateDownloadsLastThirtyDays(releases);
                        var tenDayDownloads = CalculateDownloadsLastTenDays(releases);

                        info = new ModDatabaseInfo
                        {
                            Tags = tags,
                            CachedTagsVersion = normalizedModVersion,
                            AssetId = assetId,
                            ModPageUrl = modPageUrl,
                            LatestCompatibleVersion = latestCompatibleVersion,
                            LatestVersion = latestVersion,
                            RequiredGameVersions = requiredVersions,
                            Downloads = downloads,
                            Comments = comments,
                            Follows = follows,
                            TrendingPoints = trendingPoints,
                            LogoUrl = logoUrl,
                            LogoUrlSource = logoUrlSource,
                            DownloadsLastThirtyDays = recentDownloads,
                            DownloadsLastTenDays = tenDayDownloads,
                            LastReleasedUtc = lastReleasedUtc,
                            CreatedUtc = createdUtc,
                            LatestRelease = latestRelease,
                            LatestCompatibleRelease = latestCompatibleRelease,
                            Releases = releases,
                            Side = side
                        };
                    }

                    // Measure cache storage time
                    using (timingService?.MeasureDbNetworkStore())
                    {
                        // Store with API lastmodified value for cache invalidation
                        await CacheService.StoreAsync(modId, normalizedGameVersion, info, modVersion, lastModified, etag, lastModifiedApiValue, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return info;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] JSON parse error loading database info for mod '{modId}': {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] JSON exception details: {ex}");
            return null;
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] HTTP request error loading database info for mod '{modId}': {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] Unexpected error loading database info for mod '{modId}': {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModDatabaseService] Exception details: {ex}");
            return null;
        }
    }

    private static IReadOnlyList<string> GetStringList(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text)) list.Add(text);
            }

        return list.Count == 0 ? Array.Empty<string>() : list.ToArray();
    }

    private static string? TryGetAssetId(JsonElement element)
    {
        if (!element.TryGetProperty("assetid", out var assetIdElement)) return null;

        return assetIdElement.ValueKind switch
        {
            JsonValueKind.Number when assetIdElement.TryGetInt64(out var number) => number.ToString(CultureInfo
                .InvariantCulture),
            JsonValueKind.Number when assetIdElement.TryGetDecimal(out var decimalValue) => decimalValue.ToString(
                CultureInfo.InvariantCulture),
            JsonValueKind.String => string.IsNullOrWhiteSpace(assetIdElement.GetString())
                ? null
                : assetIdElement.GetString(),
            _ => null
        };
    }

    private static IReadOnlyList<ModReleaseInfo> BuildReleaseInfos(JsonElement modElement,
        string? normalizedGameVersion, bool requireExactVersionMatch)
    {
        if (!modElement.TryGetProperty("releases", out var releasesElement) ||
            releasesElement.ValueKind != JsonValueKind.Array) return Array.Empty<ModReleaseInfo>();

        var releases = new List<ModReleaseInfo>();

        foreach (var releaseElement in releasesElement.EnumerateArray())
        {
            if (releaseElement.ValueKind != JsonValueKind.Object) continue;

            if (TryCreateReleaseInfo(releaseElement, normalizedGameVersion, requireExactVersionMatch, out var release))
                releases.Add(release);
        }

        return releases.Count == 0 ? Array.Empty<ModReleaseInfo>() : releases;
    }

    private static int? CalculateDownloadsLastThirtyDays(IReadOnlyList<ModReleaseInfo> releases)
    {
        return CalculateDownloadsForPeriod(releases, 30);
    }

    private static int? CalculateDownloadsLastTenDays(IReadOnlyList<ModReleaseInfo> releases)
    {
        return CalculateDownloadsForPeriod(releases, 10);
    }

    private static int? CalculateDownloadsForPeriod(IReadOnlyList<ModReleaseInfo> releases, int days)
    {
        if (releases.Count == 0) return null;

        var now = DateTime.UtcNow;
        var windowStart = now.AddDays(-days);

        var relevantReleases = releases
            .Where(release => release?.CreatedUtc.HasValue == true && release.Downloads.HasValue)
            .OrderByDescending(release => release!.CreatedUtc!.Value)
            .ToArray();

        if (relevantReleases.Length == 0) return null;

        var minimumIntervalDays = DevConfig.ModDatabaseMinimumIntervalDays; // Default: one hour.

        double estimatedDownloads = 0;
        var intervalEnd = now;

        foreach (var release in relevantReleases)
        {
            if (intervalEnd <= windowStart) break;

            var releaseDate = release.CreatedUtc!.Value;
            if (releaseDate > intervalEnd) releaseDate = intervalEnd;

            var intervalLengthDays = (intervalEnd - releaseDate).TotalDays;
            if (intervalLengthDays <= 0)
            {
                intervalEnd = releaseDate;
                continue;
            }

            var dailyDownloads = Math.Max(release.Downloads!.Value, 0) /
                                 Math.Max(intervalLengthDays, minimumIntervalDays);

            var effectiveStart = releaseDate < windowStart ? windowStart : releaseDate;
            var effectiveIntervalDays = (intervalEnd - effectiveStart).TotalDays;
            if (effectiveIntervalDays > 0) estimatedDownloads += dailyDownloads * effectiveIntervalDays;

            intervalEnd = releaseDate;

            if (releaseDate <= windowStart) break;
        }

        if (estimatedDownloads <= 0) return 0;

        return (int)Math.Round(estimatedDownloads, MidpointRounding.AwayFromZero);
    }

    private static bool TryCreateReleaseInfo(JsonElement releaseElement, string? normalizedGameVersion,
        bool requireExactVersionMatch, out ModReleaseInfo release)
    {
        release = default!;

        var downloadUrl = GetString(releaseElement, "mainfile");
        if (string.IsNullOrWhiteSpace(downloadUrl) ||
            !Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri)) return false;

        var version = ExtractReleaseVersion(releaseElement);
        if (string.IsNullOrWhiteSpace(version)) return false;

        var normalizedVersion = VersionStringUtility.Normalize(version);
        var releaseTags = GetStringList(releaseElement, "tags");
        var isCompatible = false;

        if (normalizedGameVersion != null && releaseTags.Count > 0)
            foreach (var tag in releaseTags)
                if (VersionStringUtility.SupportsVersion(tag, normalizedGameVersion, requireExactVersionMatch))
                {
                    isCompatible = true;
                    break;
                }

        var fileName = GetString(releaseElement, "filename");
        var changelog = ConvertChangelogToPlainText(GetString(releaseElement, "changelog"));
        var downloads = GetNullableInt(releaseElement, "downloads");
        var createdUtc = TryParseDateTime(GetString(releaseElement, "created"));

        release = new ModReleaseInfo
        {
            Version = version!,
            NormalizedVersion = normalizedVersion,
            DownloadUri = downloadUri,
            FileName = fileName,
            GameVersionTags = releaseTags,
            IsCompatibleWithInstalledGame = isCompatible,
            Changelog = changelog,
            Downloads = downloads,
            CreatedUtc = createdUtc
        };

        return true;
    }

    private static bool TryGetLatestReleaseVersion(JsonElement modElement, out string? version)
    {
        version = null;

        if (!modElement.TryGetProperty("releases", out var releasesElement)
            || releasesElement.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var releaseElement in releasesElement.EnumerateArray())
        {
            if (releaseElement.ValueKind != JsonValueKind.Object) continue;

            var releaseVersion = ExtractReleaseVersion(releaseElement);
            if (!string.IsNullOrWhiteSpace(releaseVersion))
            {
                version = releaseVersion;
                return true;
            }
        }

        return false;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    // Bullet symbols for different nesting levels in changelogs
    private static readonly string[] BulletSymbols = new[]
    {
        "\u2022 ", // • Level 1: Filled dot (bullet)
        "\u25E6 ", // ◦ Level 2: White bullet (hollow circle)
        "\u25AA ", // ▪ Level 3: Black small square
        "\u25AB "  // ▫ Level 4+: White small square
    };

    private static string? ConvertChangelogToPlainText(string? changelog)
    {
        if (string.IsNullOrWhiteSpace(changelog)) return null;

        var text = changelog.Trim();
        if (text.Length == 0) return null;

        var document = new HtmlDocument();
        document.OptionFixNestedTags = true;
        document.LoadHtml(text);

        var builder = new System.Text.StringBuilder(text.Length);

        void AppendNodes(HtmlNodeCollection nodes, System.Text.StringBuilder output, int listDepth)
        {
            foreach (var node in nodes)
            {
                AppendNode(node, output, listDepth);
            }
        }

        void AppendNode(HtmlNode node, System.Text.StringBuilder output, int listDepth)
        {
            switch (node.NodeType)
            {
                case HtmlNodeType.Text:
                    AppendText(output, HtmlEntity.DeEntitize(node.InnerText));
                    break;

                case HtmlNodeType.Element:
                    var name = node.Name.ToLowerInvariant();

                    switch (name)
                    {
                        case "br":
                            EnsureEndsWithNewlines(output, 1);
                            break;

                        case "p":
                            AppendNodes(node.ChildNodes, output, listDepth);
                            EnsureEndsWithNewlines(output, 2);
                            break;

                        case "div":
                        case "section":
                        case "article":
                        case "h1":
                        case "h2":
                        case "h3":
                        case "h4":
                        case "h5":
                        case "h6":
                            AppendNodes(node.ChildNodes, output, listDepth);
                            EnsureEndsWithNewlines(output, 2);
                            break;

                        case "ul":
                        case "ol":
                            EnsureEndsWithNewlines(output, 1);
                            AppendNodes(node.ChildNodes, output, listDepth + 1);
                            EnsureEndsWithNewlines(output, 1);
                            break;

                        case "li":
                            EnsureEndsWithNewlines(output, 1);
                            var indent = new string(' ', Math.Max(0, listDepth - 1) * 2);
                            output.Append(indent);
                            // Use different bullet symbols based on nesting level (1-indexed)
                            // Clamp to valid range: [0, BulletSymbols.Length - 1]
                            var bulletIndex = Math.Clamp(listDepth - 1, 0, BulletSymbols.Length - 1);
                            var bulletSymbol = BulletSymbols[bulletIndex];
                            output.Append(bulletSymbol);
                            AppendNodes(node.ChildNodes, output, listDepth + 1);
                            break;

                        default:
                            AppendNodes(node.ChildNodes, output, listDepth);
                            break;
                    }

                    break;
            }
        }

        void AppendText(System.Text.StringBuilder output, string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            if (string.IsNullOrWhiteSpace(value))
            {
                if (output.Length > 0 && output[^1] != ' ' && output[^1] != '\n')
                    output.Append(' ');

                return;
            }

            output.Append(value);
        }

        void EnsureEndsWithNewlines(System.Text.StringBuilder output, int required)
        {
            var current = 0;

            for (var index = output.Length - 1; index >= 0; index--)
            {
                if (output[index] != '\n') break;

                current++;
            }

            var toAdd = required - current;
            if (toAdd <= 0) return;

            for (var i = 0; i < toAdd; i++) output.Append('\n');
        }

        AppendNodes(document.DocumentNode.ChildNodes, builder, 0);

        text = builder.ToString();
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        text = text.Replace('\r', '\n');

        var lines = text.Split('\n');
        var normalizedLines = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            var trimmedEnd = line.TrimEnd();
            if (trimmedEnd.Length == 0)
            {
                if (normalizedLines.Count == 0 || normalizedLines[^1].Length == 0) continue;

                normalizedLines.Add(string.Empty);
                continue;
            }

            // Don't trim leading spaces from lines with bullets to preserve indentation
            var trimmedStart = trimmedEnd.TrimStart();
            var foundBullet = false;

            foreach (var bulletSymbol in BulletSymbols)
            {
                if (trimmedStart.StartsWith(bulletSymbol, StringComparison.Ordinal))
                {
                    // Keep leading spaces, but clean up the content after the bullet
                    // Calculate leading whitespace length by comparing original and trimmed strings
                    var prefixLength = trimmedEnd.Length - trimmedStart.Length;
                    var prefix = trimmedEnd[..prefixLength];
                    var content = trimmedStart[bulletSymbol.Length..].Trim();
                    normalizedLines.Add(prefix + bulletSymbol + content);
                    foundBullet = true;
                    break;
                }
            }

            if (!foundBullet)
            {
                normalizedLines.Add(trimmedEnd.Trim());
            }
        }

        while (normalizedLines.Count > 0 && normalizedLines[^1].Length == 0)
            normalizedLines.RemoveAt(normalizedLines.Count - 1);

        if (normalizedLines.Count == 0) return null;

        return string.Join(Environment.NewLine, normalizedLines);
    }

    private static ModDatabaseSearchResult? TryCreateSearchResult(JsonElement element, IReadOnlyList<string> tokens,
        bool requireTokenMatch)
    {
        var name = GetString(element, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var modIds = GetStringList(element, "modidstrs");
        var primaryId = modIds.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
                        ?? GetString(element, "urlalias")
                        ?? name;

        if (string.IsNullOrWhiteSpace(primaryId)) return null;

        primaryId = primaryId.Trim();

        var summary = GetString(element, "summary");
        var author = GetString(element, "author");
        var assetId = TryGetAssetId(element);
        var urlAlias = GetString(element, "urlalias");
        var side = GetString(element, "side");
        var logo = GetString(element, "logofiledb");
        var logoSource = string.IsNullOrWhiteSpace(logo) ? null : "logofiledb";

        var tags = GetStringList(element, "tags");
        var downloads = GetInt(element, "downloads");
        var follows = GetInt(element, "follows");
        var trendingPoints = GetInt(element, "trendingpoints");
        var comments = GetInt(element, "comments");
        var lastReleased = TryParseDateTime(GetString(element, "lastreleased"));
        var createdUtc = TryParseDateTime(GetString(element, "created"));

        var alternateIds = modIds.Count == 0 ? new[] { primaryId } : modIds;

        double score;
        if (requireTokenMatch)
        {
            if (!TryCalculateSearchScore(
                    name,
                    primaryId,
                    author,
                    summary,
                    alternateIds,
                    tags,
                    tokens,
                    downloads,
                    follows,
                    trendingPoints,
                    comments,
                    lastReleased,
                    out score))
                return null;
        }
        else
        {
            score = downloads;
        }

        return new ModDatabaseSearchResult
        {
            Name = name,
            ModId = primaryId,
            AlternateIds = alternateIds,
            Summary = summary,
            Author = author,
            Tags = tags,
            Downloads = downloads,
            Follows = follows,
            TrendingPoints = trendingPoints,
            Comments = comments,
            AssetId = assetId,
            UrlAlias = urlAlias,
            Side = side,
            LogoUrl = logo,
            LogoUrlSource = logoSource,
            LastReleasedUtc = lastReleased,
            CreatedUtc = createdUtc,
            Score = score
        };
    }

    private static bool TryCalculateSearchScore(
        string name,
        string primaryId,
        string? author,
        string? summary,
        IReadOnlyList<string> alternateIds,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> tokens,
        int downloads,
        int follows,
        int trendingPoints,
        int comments,
        DateTime? lastReleased,
        out double score)
    {
        score = 0;

        if (tokens.Count == 0) return false;

        var matchedTokenCount = 0;
        var summaryText = summary ?? string.Empty;
        var authorTokens = string.IsNullOrWhiteSpace(author)
            ? Array.Empty<string>()
            : CreateSearchTokens(author);

        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token)) continue;

            var currentToken = token.Trim();
            if (currentToken.Length == 0) continue;

            var tokenMatched = false;

            var nameExactMatch = string.Equals(name, currentToken, StringComparison.OrdinalIgnoreCase);
            if (nameExactMatch)
            {
                score += 12;
                tokenMatched = true;
            }
            else if (name.Contains(currentToken, StringComparison.OrdinalIgnoreCase))
            {
                score += 6;
                tokenMatched = true;
            }

            var primaryExactMatch = string.Equals(primaryId, currentToken, StringComparison.OrdinalIgnoreCase);
            if (primaryExactMatch)
            {
                score += 10;
                tokenMatched = true;
            }
            else if (primaryId.Contains(currentToken, StringComparison.OrdinalIgnoreCase))
            {
                score += 5;
                tokenMatched = true;
            }
            else
            {
                var alternateExact =
                    alternateIds.Any(id => string.Equals(id, currentToken, StringComparison.OrdinalIgnoreCase));
                if (alternateExact)
                {
                    score += 9;
                    tokenMatched = true;
                }
                else if (alternateIds.Any(id => id.Contains(currentToken, StringComparison.OrdinalIgnoreCase)))
                {
                    score += 4;
                    tokenMatched = true;
                }
            }

            if (authorTokens.Count > 0)
            {
                var authorExactMatch = authorTokens.Any(authorToken =>
                    string.Equals(authorToken, currentToken, StringComparison.OrdinalIgnoreCase));
                if (authorExactMatch)
                {
                    score += 4;
                    tokenMatched = true;
                }
                else if (authorTokens.Any(authorToken =>
                             authorToken.Contains(currentToken, StringComparison.OrdinalIgnoreCase)))
                {
                    score += 2.5;
                    tokenMatched = true;
                }
            }

            var tagExactMatch = tags.Any(tag => string.Equals(tag, currentToken, StringComparison.OrdinalIgnoreCase));
            if (tagExactMatch)
            {
                score += 3;
                tokenMatched = true;
            }
            else if (tags.Any(tag => tag.Contains(currentToken, StringComparison.OrdinalIgnoreCase)))
            {
                score += 2;
                tokenMatched = true;
            }

            if (!string.IsNullOrEmpty(summaryText)
                && summaryText.Contains(currentToken, StringComparison.OrdinalIgnoreCase))
            {
                score += 1.5;
                tokenMatched = true;
            }

            if (tokenMatched) matchedTokenCount++;
        }

        if (matchedTokenCount == 0)
        {
            score = 0;
            return false;
        }

        score += matchedTokenCount * 1.5;
        score += Math.Log10(downloads + 1) * 1.2;
        score += Math.Log10(follows + 1) * 1.5;
        score += Math.Log10(trendingPoints + 1);
        score += Math.Log10(comments + 1) * 0.5;

        if (lastReleased.HasValue)
        {
            var days = (DateTime.UtcNow - lastReleased.Value).TotalDays;
            if (!double.IsNaN(days)) score += Math.Max(0, 4 - days / 45.0);
        }

        return true;
    }

    private static IReadOnlyList<string> CreateSearchTokens(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();

        var trimmed = value.Trim();
        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return;

            token = token.Trim();
            if (seen.Add(token)) tokens.Add(token);
        }

        AddToken(trimmed);

        foreach (var token in trimmed.Split(
                     [' ', '\t', '\r', '\n', '-', '_', '.', '/', '\\'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            AddToken(token);

        return tokens.Count == 0 ? Array.Empty<string>() : tokens;
    }

    private static int? GetNullableInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return null;

        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetInt64(out var longValue):
                return (int)Math.Clamp(longValue, int.MinValue, int.MaxValue);
            case JsonValueKind.Number when value.TryGetDouble(out var doubleValue):
                if (double.IsNaN(doubleValue) || double.IsInfinity(doubleValue)) return null;

                var truncated = Math.Truncate(doubleValue);
                if (truncated < int.MinValue) return int.MinValue;

                if (truncated > int.MaxValue) return int.MaxValue;

                return (int)truncated;
            case JsonValueKind.String when long.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed):
                return (int)Math.Clamp(parsed, int.MinValue, int.MaxValue);
            default:
                return null;
        }
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        return GetNullableInt(element, propertyName) ?? 0;
    }

    private static DateTime? TryParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var result))
            return result;

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            return DateTime.SpecifyKind(result, DateTimeKind.Utc);

        return null;
    }

    private static IReadOnlyList<string> FindRequiredGameVersions(JsonElement modElement, string? modVersion)
    {
        if (string.IsNullOrWhiteSpace(modVersion)) return Array.Empty<string>();

        if (!modElement.TryGetProperty("releases", out var releasesElement) ||
            releasesElement.ValueKind != JsonValueKind.Array) return Array.Empty<string>();

        var normalizedModVersion = VersionStringUtility.Normalize(modVersion);

        foreach (var release in releasesElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object) continue;

            if (!release.TryGetProperty("modversion", out var releaseModVersionElement) ||
                releaseModVersionElement.ValueKind != JsonValueKind.String) continue;

            var releaseModVersion = releaseModVersionElement.GetString();
            if (string.IsNullOrWhiteSpace(releaseModVersion)) continue;

            if (!ReleaseMatchesModVersion(releaseModVersion, modVersion, normalizedModVersion)) continue;

            var tags = GetStringList(release, "tags");
            return tags.Count == 0 ? Array.Empty<string>() : tags;
        }

        return Array.Empty<string>();
    }

    private static bool ReleaseMatchesModVersion(string releaseModVersion, string? modVersion,
        string? normalizedModVersion)
    {
        if (modVersion != null &&
            string.Equals(releaseModVersion, modVersion, StringComparison.OrdinalIgnoreCase)) return true;

        var normalizedReleaseVersion = VersionStringUtility.Normalize(releaseModVersion);
        if (normalizedReleaseVersion == null || normalizedModVersion == null) return false;

        return string.Equals(normalizedReleaseVersion, normalizedModVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractReleaseVersion(JsonElement releaseElement)
    {
        if (releaseElement.TryGetProperty("modversion", out var modVersion) &&
            modVersion.ValueKind == JsonValueKind.String)
        {
            var value = modVersion.GetString();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        if (releaseElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
        {
            var value = version.GetString();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }
}