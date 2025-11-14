using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    private static readonly Regex HtmlBreakRegex = new(
        @"<\s*br\s*/?\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HtmlParagraphOpenRegex = new(
        @"<\s*p[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HtmlParagraphCloseRegex = new(
        @"</\s*p\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HtmlListItemOpenRegex = new(
        @"<\s*li[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HtmlListItemCloseRegex = new(
        @"</\s*li\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HtmlBlockCloseRegex = new(
        @"</\s*(div|section|article|h[1-6])\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HtmlTagRegex = new(
        @"<[^>]+>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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

            var cached = await CacheService
                .TryLoadAsync(
                    modEntry.ModId,
                    normalizedGameVersion,
                    installedModVersion,
                    !internetDisabled,
                    requireExactVersionMatch,
                    cancellationToken)
                .ConfigureAwait(false);

            if (cached != null) modEntry.DatabaseInfo = cached;

            if (internetDisabled) return;

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
                if (info != null) modEntry.DatabaseInfo = info;
            }
            finally
            {
                semaphore.Release();
            }
        }
    }

    public Task<ModDatabaseInfo?> TryLoadDatabaseInfoAsync(string modId, string? modVersion,
        string? installedGameVersion, bool requireExactVersionMatch = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modId)) return Task.FromResult<ModDatabaseInfo?>(null);

        var normalizedGameVersion = VersionStringUtility.Normalize(installedGameVersion);
        return TryLoadDatabaseInfoAsyncCore(modId, modVersion, normalizedGameVersion, requireExactVersionMatch,
            cancellationToken);
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
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<ModDatabaseInfo?> TryLoadDatabaseInfoAsyncCore(
        string modId,
        string? modVersion,
        string? normalizedGameVersion,
        bool requireExactVersionMatch,
        CancellationToken cancellationToken)
    {
        var internetDisabled = InternetAccessManager.IsInternetAccessDisabled;

        var cached = await CacheService
            .TryLoadAsync(
                modId,
                normalizedGameVersion,
                modVersion,
                !internetDisabled,
                requireExactVersionMatch,
                cancellationToken)
            .ConfigureAwait(false);

        if (internetDisabled) return cached;

        var info = await TryLoadDatabaseInfoInternalAsync(modId, modVersion, normalizedGameVersion,
                requireExactVersionMatch, cancellationToken)
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

        InternetAccessManager.ThrowIfInternetAccessDisabled();

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

        InternetAccessManager.ThrowIfInternetAccessDisabled();

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

        InternetAccessManager.ThrowIfInternetAccessDisabled();

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

        InternetAccessManager.ThrowIfInternetAccessDisabled();

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

        InternetAccessManager.ThrowIfInternetAccessDisabled();

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

        InternetAccessManager.ThrowIfInternetAccessDisabled();

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

        InternetAccessManager.ThrowIfInternetAccessDisabled();

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

        InternetAccessManager.ThrowIfInternetAccessDisabled();

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
        InternetAccessManager.ThrowIfInternetAccessDisabled();

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

            return orderResults(candidates)
                .Take(maxResults)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
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

        InternetAccessManager.ThrowIfInternetAccessDisabled();

        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var info = await TryLoadDatabaseInfoInternalAsync(candidate.ModId, null, null, false, cancellationToken)
                .ConfigureAwait(false);
            var latestDownloads = ExtractLatestReleaseDownloads(info);
            return CloneResultWithDetails(candidate, info, latestDownloads);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return CloneResultWithDetails(candidate, null, null);
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
        string? normalizedGameVersion, bool requireExactVersionMatch, CancellationToken cancellationToken)
    {
        if (InternetAccessManager.IsInternetAccessDisabled) return null;

        try
        {
            var normalizedModVersion = VersionStringUtility.Normalize(modVersion);

            var requestUri =
                string.Format(CultureInfo.InvariantCulture, ApiEndpointFormat, Uri.EscapeDataString(modId));
            using HttpRequestMessage request = new(HttpMethod.Get, requestUri);
            using var response = await HttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var contentStream =
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("mod", out var modElement) ||
                modElement.ValueKind != JsonValueKind.Object) return null;

            var tags = GetStringList(modElement, "tags");
            var assetId = TryGetAssetId(modElement);
            var modPageUrl = assetId == null ? null : ModPageBaseUrl + assetId;
            var downloads = GetNullableInt(modElement, "downloads");
            var comments = GetNullableInt(modElement, "comments");
            var follows = GetNullableInt(modElement, "follows");
            var trendingPoints = GetNullableInt(modElement, "trendingpoints");
            var side = GetString(modElement, "side");
            var logoUrl = GetString(modElement, "logofile");
            if (string.IsNullOrWhiteSpace(logoUrl)) logoUrl = GetString(modElement, "logo");
            var lastReleasedUtc = TryParseDateTime(GetString(modElement, "lastreleased"));
            var createdUtc = TryParseDateTime(GetString(modElement, "created"));
            var releases = BuildReleaseInfos(modElement, normalizedGameVersion, requireExactVersionMatch);
            var latestRelease = releases.Count > 0 ? releases[0] : null;
            var latestCompatibleRelease = releases.FirstOrDefault(release => release.IsCompatibleWithInstalledGame);
            var latestVersion = latestRelease?.Version;
            var latestCompatibleVersion = latestCompatibleRelease?.Version;
            var requiredVersions = FindRequiredGameVersions(modElement, modVersion);
            var recentDownloads = CalculateDownloadsLastThirtyDays(releases);
            var tenDayDownloads = CalculateDownloadsLastTenDays(releases);

            var info = new ModDatabaseInfo
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
                DownloadsLastThirtyDays = recentDownloads,
                DownloadsLastTenDays = tenDayDownloads,
                LastReleasedUtc = lastReleasedUtc,
                CreatedUtc = createdUtc,
                LatestRelease = latestRelease,
                LatestCompatibleRelease = latestCompatibleRelease,
                Releases = releases,
                Side = side
            };

            await CacheService.StoreAsync(modId, normalizedGameVersion, info, modVersion, cancellationToken)
                .ConfigureAwait(false);

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

    private static string? ConvertChangelogToPlainText(string? changelog)
    {
        if (string.IsNullOrWhiteSpace(changelog)) return null;

        var text = changelog.Trim();
        if (text.Length == 0) return null;

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        text = text.Replace('\r', '\n');
        text = HtmlBreakRegex.Replace(text, "\n");
        text = HtmlParagraphCloseRegex.Replace(text, "\n\n");
        text = HtmlParagraphOpenRegex.Replace(text, string.Empty);
        text = HtmlListItemCloseRegex.Replace(text, "\n");
        text = HtmlListItemOpenRegex.Replace(text, "\u2022 ");
        text = HtmlBlockCloseRegex.Replace(text, "\n\n");
        text = HtmlTagRegex.Replace(text, string.Empty);

        text = WebUtility.HtmlDecode(text);

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

            var trimmedStart = trimmedEnd.TrimStart();
            if (trimmedStart.StartsWith("\u2022 ", StringComparison.Ordinal))
                trimmedStart = "\u2022 " + trimmedStart[2..].Trim();
            else
                trimmedStart = trimmedStart.Trim();

            normalizedLines.Add(trimmedStart);
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
        var logo = GetString(element, "logo");
        if (string.IsNullOrWhiteSpace(logo)) logo = GetString(element, "logofile");

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