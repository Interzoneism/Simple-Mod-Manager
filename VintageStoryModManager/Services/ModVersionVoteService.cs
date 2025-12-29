using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleVsManager.Cloud;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.Services;

/// <summary>
///     Provides Firebase-backed storage for per-mod user report votes.
/// </summary>
public sealed class ModVersionVoteService : IDisposable
{
    private static readonly string DefaultDbUrl = DevConfig.ModVersionVoteDefaultDbUrl;

    private static readonly string VotesRootPath = DevConfig.ModVersionVoteRootPath;

    private static readonly string VoteCachePath = Path.Combine(DevConfig.FirebaseBackupDirectory, "compat-votes-cache.json");

    /// <summary>
    ///     Gets the full path to the votes cache file.
    /// </summary>
    public static string GetVoteCachePath() => VoteCachePath;

    private static readonly HttpClient HttpClient = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new ModVersionVoteOptionJsonConverter() }
    };

    private readonly FirebaseAnonymousAuthenticator _authenticator;

    private readonly string _dbUrl;

    private readonly ConcurrentDictionary<VoteCacheKey, VoteCacheEntry> _voteCache =
        new(VoteCacheKeyComparer.Instance);

    private readonly SemaphoreSlim _voteIndexLock = new(1, 1);
    private Dictionary<string, HashSet<string>> _voteIndex = new(StringComparer.OrdinalIgnoreCase);
    private bool _voteIndexLoaded;

    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly object _disposeLock = new();
    private readonly bool _ownsAuthenticator;
    private bool _voteCacheLoaded;
    private bool _disposed;

    public ModVersionVoteService()
        : this(DefaultDbUrl, new FirebaseAnonymousAuthenticator())
    {
        _ownsAuthenticator = true;
    }

    public ModVersionVoteService(string databaseUrl, FirebaseAnonymousAuthenticator authenticator)
    {
        if (string.IsNullOrWhiteSpace(databaseUrl))
            throw new ArgumentException("A Firebase database URL must be provided.", nameof(databaseUrl));

        _dbUrl = databaseUrl.TrimEnd('/');
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _ownsAuthenticator = false;
    }


    public async Task<ModVersionVoteSummary> GetVoteSummaryAsync(
        string modId,
        string modVersion,
        string vintageStoryVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifiers(modId, modVersion, vintageStoryVersion);

        var cacheKey = new VoteCacheKey(modId, modVersion, vintageStoryVersion);

        var (found, cachedSummary) = await TryGetCachedSummaryAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (found) return cachedSummary.Summary;

        InternetAccessManager.ThrowIfInternetAccessDisabled();

        var session = await _authenticator
            .GetSessionAsync(cancellationToken)
            .ConfigureAwait(false);

        var (Summary, _) = await GetVoteSummaryWithEtagAsync(
                session,
                cacheKey,
                cancellationToken)
            .ConfigureAwait(false);

        return Summary;
    }

    public async Task<VoteSummaryResult> GetVoteSummaryIfChangedAsync(
        string modId,
        string modVersion,
        string vintageStoryVersion,
        string? knownEtag,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifiers(modId, modVersion, vintageStoryVersion);

        var cacheKey = new VoteCacheKey(modId, modVersion, vintageStoryVersion);

        var (found, cachedSummary) = await TryGetCachedSummaryAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (found)
            return new VoteSummaryResult(cachedSummary.Summary, cachedSummary.ETag, true);

        InternetAccessManager.ThrowIfInternetAccessDisabled();

        var session = await _authenticator
            .GetSessionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await GetVoteSummaryInternalAsync(
                session,
                cacheKey,
                knownEtag,
                false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(ModVersionVoteSummary Summary, string? ETag)> GetVoteSummaryWithEtagAsync(
        string modId,
        string modVersion,
        string vintageStoryVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifiers(modId, modVersion, vintageStoryVersion);

        var cacheKey = new VoteCacheKey(modId, modVersion, vintageStoryVersion);

        var (found, cachedSummary) = await TryGetCachedSummaryAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (found) return cachedSummary.ToSummaryResult();

        InternetAccessManager.ThrowIfInternetAccessDisabled();

        var session = await _authenticator
            .GetSessionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await GetVoteSummaryWithEtagAsync(
                session,
                cacheKey,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(ModVersionVoteSummary Summary, string? ETag)> SubmitVoteAsync(
        string modId,
        string modVersion,
        string vintageStoryVersion,
        ModVersionVoteOption option,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifiers(modId, modVersion, vintageStoryVersion);
        InternetAccessManager.ThrowIfInternetAccessDisabled();

        var session = await _authenticator
            .GetSessionAsync(cancellationToken)
            .ConfigureAwait(false);

        var modKey = SanitizeKey(modId);
        var versionKey = SanitizeKey(modVersion);
        var userKey = session.UserId ??
                      throw new InvalidOperationException("Firebase session did not provide a user ID.");

        var record = new VoteRecord
        {
            Option = option,
            VintageStoryVersion = vintageStoryVersion,
            UpdatedUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Comment = NormalizeComment(comment)
        };

        var url = BuildVotesUrl(session, null, VotesRootPath, modKey, versionKey, "users", userKey);
        var payload = JsonSerializer.Serialize(record, SerializerOptions);

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await HttpClient
            .PutAsync(url, content, cancellationToken)
            .ConfigureAwait(false);

        await EnsureOkAsync(response, "Submit vote").ConfigureAwait(false);

        // Invalidate the vote index so it gets refreshed on next query
        // This ensures that the newly submitted vote is reflected immediately
        await InvalidateVoteIndexAsync(cancellationToken).ConfigureAwait(false);

        return await GetVoteSummaryWithEtagAsync(
                session,
                new VoteCacheKey(modId, modVersion, vintageStoryVersion),
                cancellationToken,
                true)
            .ConfigureAwait(false);
    }

    public async Task<(ModVersionVoteSummary Summary, string? ETag)> RemoveVoteAsync(
        string modId,
        string modVersion,
        string vintageStoryVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifiers(modId, modVersion, vintageStoryVersion);
        InternetAccessManager.ThrowIfInternetAccessDisabled();

        var session = await _authenticator
            .GetSessionAsync(cancellationToken)
            .ConfigureAwait(false);

        var modKey = SanitizeKey(modId);
        var versionKey = SanitizeKey(modVersion);
        var userKey = session.UserId ??
                      throw new InvalidOperationException("Firebase session did not provide a user ID.");

        var url = BuildVotesUrl(session, null, VotesRootPath, modKey, versionKey, "users", userKey);

        using var response = await HttpClient
            .DeleteAsync(url, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureOkAsync(response, "Remove vote").ConfigureAwait(false);

        // Invalidate the vote index so it gets refreshed on next query
        // This ensures that vote removal is reflected immediately
        await InvalidateVoteIndexAsync(cancellationToken).ConfigureAwait(false);

        return await GetVoteSummaryWithEtagAsync(
                session,
                new VoteCacheKey(modId, modVersion, vintageStoryVersion),
                cancellationToken,
                true)
            .ConfigureAwait(false);
    }

    private async Task<(ModVersionVoteSummary Summary, string? ETag)> GetVoteSummaryWithEtagAsync(
        FirebaseAnonymousAuthenticator.FirebaseAuthSession? session,
        VoteCacheKey cacheKey,
        CancellationToken cancellationToken,
        bool bypassCache = false)
    {
        var result = await GetVoteSummaryInternalAsync(
                session,
                cacheKey,
                null,
                bypassCache,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Summary is null) throw new InvalidOperationException("Vote summary was not available.");

        return (result.Summary, result.ETag);
    }

    private async Task<VoteSummaryResult> GetVoteSummaryInternalAsync(
        FirebaseAnonymousAuthenticator.FirebaseAuthSession? session,
        VoteCacheKey cacheKey,
        string? knownEtag,
        bool bypassCache,
        CancellationToken cancellationToken)
    {
        if (!bypassCache)
        {
            var (found, cachedEntry) = await TryGetCachedSummaryAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            if (found)
                return new VoteSummaryResult(cachedEntry.Summary, cachedEntry.ETag, true);
        }

        var modKey = SanitizeKey(cacheKey.ModId);
        var versionKey = SanitizeKey(cacheKey.ModVersion);

        var (indexAvailable, hasVotes) = await TryHasVotesAsync(session, modKey, versionKey, cancellationToken)
            .ConfigureAwait(false);
        if (indexAvailable && !hasVotes)
        {
            var emptySummary = CreateEmptySummary(cacheKey);

            await StoreCachedSummaryAsync(cacheKey, emptySummary, knownEtag, cancellationToken, false)
                .ConfigureAwait(false);

            return new VoteSummaryResult(emptySummary, knownEtag, false);
        }

        var url = BuildVotesUrl(session, null, VotesRootPath, modKey, versionKey, "users");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Firebase-ETag", "true");
        if (!string.IsNullOrWhiteSpace(knownEtag)) request.Headers.TryAddWithoutValidation("If-None-Match", knownEtag);

        using var response = await HttpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var responseEtag = response.Headers.ETag?.Tag;

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            var (foundCached, cachedFromResponse) = await TryGetCachedSummaryAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            if (foundCached)
            {
                await StoreCachedSummaryAsync(cacheKey, cachedFromResponse.Summary, responseEtag ?? knownEtag, cancellationToken).ConfigureAwait(false);
                return new VoteSummaryResult(cachedFromResponse.Summary, responseEtag ?? knownEtag, true);
            }

            return new VoteSummaryResult(null, responseEtag ?? knownEtag, true);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var emptySummary = CreateEmptySummary(cacheKey);

            await StoreCachedSummaryAsync(cacheKey, emptySummary, responseEtag ?? knownEtag, cancellationToken, false)
                .ConfigureAwait(false);

            return new VoteSummaryResult(emptySummary, responseEtag, false);
        }

        await EnsureOkAsync(response, "Fetch votes").ConfigureAwait(false);

        await using var contentStream =
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument
            .ParseAsync(contentStream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Null)
        {
            var emptySummary = CreateEmptySummary(cacheKey);

            await StoreCachedSummaryAsync(cacheKey, emptySummary, responseEtag ?? knownEtag, cancellationToken, false)
                .ConfigureAwait(false);

            return new VoteSummaryResult(emptySummary, responseEtag, false);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            var emptySummary = CreateEmptySummary(cacheKey);

            await StoreCachedSummaryAsync(cacheKey, emptySummary, responseEtag ?? knownEtag, cancellationToken, false)
                .ConfigureAwait(false);

            return new VoteSummaryResult(emptySummary, responseEtag, false);
        }

        var fullyFunctional = 0;
        var noIssuesSoFar = 0;
        var someIssuesButWorks = 0;
        var notFunctional = 0;
        var crashesOrFreezes = 0;
        ModVersionVoteOption? userVote = null;
        string? userComment = null;

        if (session.HasValue && session.Value.UserId is { Length: > 0 } userId)
            if (root.TryGetProperty(userId, out var userElement)
                && TryDeserializeRecord(userElement) is VoteRecord currentUserRecord)
            {
                userVote = currentUserRecord.Option;
                userComment = currentUserRecord.Comment;
            }

        List<string> notFunctionalComments = new();
        List<string> crashesOrFreezesGameComments = new();

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;

            var record = TryDeserializeRecord(property.Value);
            if (record is null) continue;

            switch (record.Value.Option)
            {
                case ModVersionVoteOption.FullyFunctional:
                    fullyFunctional++;
                    break;
                case ModVersionVoteOption.NoIssuesSoFar:
                    noIssuesSoFar++;
                    break;
                case ModVersionVoteOption.SomeIssuesButWorks:
                    someIssuesButWorks++;
                    break;
                case ModVersionVoteOption.NotFunctional:
                    notFunctional++;
                    AddComment(notFunctionalComments, record.Value.Comment);
                    break;
                case ModVersionVoteOption.CrashesOrFreezesGame:
                    crashesOrFreezes++;
                    AddComment(crashesOrFreezesGameComments, record.Value.Comment);
                    break;
            }
        }

        var counts = new ModVersionVoteCounts(
            fullyFunctional,
            noIssuesSoFar,
            someIssuesButWorks,
            notFunctional,
            crashesOrFreezes);
        var comments = new ModVersionVoteComments(
            ToReadOnlyList(notFunctionalComments),
            ToReadOnlyList(crashesOrFreezesGameComments));

        var summary = new ModVersionVoteSummary(
            cacheKey.ModId,
            cacheKey.ModVersion,
            cacheKey.VintageStoryVersion,
            counts,
            comments,
            userVote,
            userComment);

        await StoreCachedSummaryAsync(cacheKey, summary, responseEtag ?? knownEtag, cancellationToken).ConfigureAwait(false);

        return new VoteSummaryResult(summary, responseEtag, false);
    }

    private async Task<(bool IndexAvailable, bool HasVotes)> TryHasVotesAsync(
        FirebaseAnonymousAuthenticator.FirebaseAuthSession? session,
        string modKey,
        string versionKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureVoteIndexLoadedAsync(session, cancellationToken).ConfigureAwait(false);

            var hasVotes = _voteIndex.TryGetValue(modKey, out var versions)
                           && versions.Contains(versionKey);

            return (true, hasVotes);
        }
        catch
        {
            return (false, true);
        }
    }

    private async Task EnsureVoteIndexLoadedAsync(
        FirebaseAnonymousAuthenticator.FirebaseAuthSession? session,
        CancellationToken cancellationToken)
    {
        if (_voteIndexLoaded) return;
        if (_disposed) throw new ObjectDisposedException(nameof(ModVersionVoteService));

        await _voteIndexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_voteIndexLoaded) return;

            _voteIndex = await FetchVoteIndexAsync(session, cancellationToken).ConfigureAwait(false);
            _voteIndexLoaded = true;
        }
        finally
        {
            _voteIndexLock.Release();
        }
    }

    private async Task InvalidateVoteIndexAsync(CancellationToken cancellationToken = default)
    {
        await _voteIndexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _voteIndexLoaded = false;
        }
        finally
        {
            _voteIndexLock.Release();
        }
    }

    private async Task<Dictionary<string, HashSet<string>>> FetchVoteIndexAsync(
        FirebaseAnonymousAuthenticator.FirebaseAuthSession? session,
        CancellationToken cancellationToken)
    {
        var rootUrl = BuildVotesUrl(session, "shallow=true", VotesRootPath);

        using var rootResponse = await HttpClient.GetAsync(rootUrl, cancellationToken).ConfigureAwait(false);

        if (rootResponse.StatusCode == HttpStatusCode.NotFound)
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        await EnsureOkAsync(rootResponse, "Fetch vote index root").ConfigureAwait(false);

        var modList = await rootResponse.Content
            .ReadFromJsonAsync<Dictionary<string, JsonElement>>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        if (modList is null || modList.Count == 0)
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var modKey in modList.Keys)
        {
            var modUrl = BuildVotesUrl(session, "shallow=true", VotesRootPath, modKey);

            using var modResponse = await HttpClient.GetAsync(modUrl, cancellationToken).ConfigureAwait(false);

            if (modResponse.StatusCode == HttpStatusCode.NotFound)
                continue;

            await EnsureOkAsync(modResponse, $"Fetch vote index for {modKey}").ConfigureAwait(false);

            var versions = await modResponse.Content
                .ReadFromJsonAsync<Dictionary<string, JsonElement>>(SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (versions is null || versions.Count == 0) continue;

            foreach (var versionEntry in versions)
            {
                if (versionEntry.Value.ValueKind != JsonValueKind.Object
                    && versionEntry.Value.ValueKind != JsonValueKind.True)
                    continue;

                if (!map.TryGetValue(modKey, out var versionSet))
                {
                    versionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    map[modKey] = versionSet;
                }

                versionSet.Add(versionEntry.Key);
            }
        }

        return map;
    }

    private static VoteRecord? TryDeserializeRecord(JsonElement element)
    {
        try
        {
            return element.Deserialize<VoteRecord>(SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    private static ModVersionVoteSummary CreateEmptySummary(VoteCacheKey cacheKey)
    {
        return new ModVersionVoteSummary(
            cacheKey.ModId,
            cacheKey.ModVersion,
            cacheKey.VintageStoryVersion,
            ModVersionVoteCounts.Empty,
            ModVersionVoteComments.Empty,
            null,
            null);
    }

    private string BuildVotesUrl(
        FirebaseAnonymousAuthenticator.FirebaseAuthSession? session,
        string? query,
        params string[] segments)
    {
        var builder = new StringBuilder();
        builder.Append(_dbUrl);

        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;

            builder.Append('/');
            builder.Append(segment);
        }

        builder.Append(".json");

        var hasQuery = false;
        if (!string.IsNullOrWhiteSpace(query))
        {
            builder.Append('?');
            builder.Append(query);
            hasQuery = true;
        }

        if (session.HasValue)
        {
            builder.Append(hasQuery ? '&' : '?');
            builder.Append("auth=");
            builder.Append(Uri.EscapeDataString(session.Value.IdToken));
        }

        return builder.ToString();
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        throw new InvalidOperationException(
            $"{operation} failed: {(int)response.StatusCode} {response.ReasonPhrase} | {Truncate(body, 200)}");
    }

    private static void ValidateIdentifiers(string modId, string modVersion, string vintageStoryVersion)
    {
        if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("Mod identifier is required.", nameof(modId));

        if (string.IsNullOrWhiteSpace(modVersion))
            throw new ArgumentException("Mod version is required.", nameof(modVersion));

        if (string.IsNullOrWhiteSpace(vintageStoryVersion))
            throw new ArgumentException("Vintage Story version is required.", nameof(vintageStoryVersion));
    }

    private static string SanitizeKey(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return "_";

        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(trimmed));
        return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static void AddComment(List<string> target, string? comment)
    {
        var normalized = NormalizeComment(comment);
        if (string.IsNullOrEmpty(normalized)) return;

        target.Add(normalized);
    }

    private static IReadOnlyList<string> ToReadOnlyList(List<string> source)
    {
        return source.Count == 0
            ? Array.Empty<string>()
            : source.ToArray();
    }

    private static string? NormalizeComment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? NormalizeVersion(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;

        return value[..maxLength];
    }

    public readonly struct VoteSummaryResult
    {
        public VoteSummaryResult(ModVersionVoteSummary? summary, string? eTag, bool isNotModified)
        {
            Summary = summary;
            ETag = eTag;
            IsNotModified = isNotModified;
        }

        public ModVersionVoteSummary? Summary { get; }

        public string? ETag { get; }

        public bool IsNotModified { get; }
    }

    private sealed class ModVersionVoteOptionJsonConverter : JsonConverter<ModVersionVoteOption>
    {
        public override ModVersionVoteOption Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Expected string value for mod version vote option.");

            var value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value)) throw new JsonException("Vote option value was empty.");

            return value switch
            {
                var v when v.Equals("fullyFunctional", StringComparison.OrdinalIgnoreCase) => ModVersionVoteOption
                    .FullyFunctional,
                var v when v.Equals("workingPerfectly", StringComparison.OrdinalIgnoreCase) => ModVersionVoteOption
                    .FullyFunctional,
                var v when v.Equals("noIssuesSoFar", StringComparison.OrdinalIgnoreCase) => ModVersionVoteOption
                    .NoIssuesSoFar,
                var v when v.Equals("someIssuesButWorks", StringComparison.OrdinalIgnoreCase) => ModVersionVoteOption
                    .SomeIssuesButWorks,
                var v when v.Equals("notFunctional", StringComparison.OrdinalIgnoreCase) => ModVersionVoteOption
                    .NotFunctional,
                var v when v.Equals("notWorking", StringComparison.OrdinalIgnoreCase) => ModVersionVoteOption
                    .NotFunctional,
                var v when v.Equals("crashesOrFreezesGame", StringComparison.OrdinalIgnoreCase) => ModVersionVoteOption
                    .CrashesOrFreezesGame,
                _ => throw new JsonException($"Unrecognized vote option '{value}'.")
            };
        }

        public override void Write(Utf8JsonWriter writer, ModVersionVoteOption value, JsonSerializerOptions options)
        {
            var stringValue = value switch
            {
                ModVersionVoteOption.FullyFunctional => "fullyFunctional",
                ModVersionVoteOption.NoIssuesSoFar => "noIssuesSoFar",
                ModVersionVoteOption.SomeIssuesButWorks => "someIssuesButWorks",
                ModVersionVoteOption.NotFunctional => "notFunctional",
                ModVersionVoteOption.CrashesOrFreezesGame => "crashesOrFreezesGame",
                _ => value.ToString() ?? string.Empty
            };

            writer.WriteStringValue(stringValue);
        }
    }

    private struct VoteRecord
    {
        [JsonPropertyName("option")] public ModVersionVoteOption Option { get; set; }

        [JsonPropertyName("vintageStoryVersion")]
        public string? VintageStoryVersion { get; set; }

        [JsonPropertyName("updatedUtc")] public string? UpdatedUtc { get; set; }

        [JsonPropertyName("comment")] public string? Comment { get; set; }
    }

    private sealed record VoteCacheFileEntry(ModVersionVoteSummary? Summary, string? ETag);

    private readonly record struct VoteCacheKey(string ModId, string ModVersion, string VintageStoryVersion);

    private readonly record struct VoteCacheEntry(ModVersionVoteSummary Summary, string? ETag)
    {
        public (ModVersionVoteSummary Summary, string? ETag) ToSummaryResult()
        {
            return (Summary, ETag);
        }
    }

    private sealed class VoteCacheKeyComparer : IEqualityComparer<VoteCacheKey>
    {
        public static readonly VoteCacheKeyComparer Instance = new();

        public bool Equals(VoteCacheKey x, VoteCacheKey y)
        {
            return string.Equals(x.ModId, y.ModId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(x.ModVersion, y.ModVersion, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(x.VintageStoryVersion, y.VintageStoryVersion, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(VoteCacheKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.ModId, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.ModVersion, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.VintageStoryVersion, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }

    private async Task<(bool Found, VoteCacheEntry Entry)> TryGetCachedSummaryAsync(VoteCacheKey key, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ModVersionVoteService));
        await EnsureVoteCacheLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (_voteCache.TryGetValue(key, out var entry))
        {
            return (true, entry);
        }
        return (false, default);
    }

    private async Task StoreCachedSummaryAsync(
        VoteCacheKey key,
        ModVersionVoteSummary summary,
        string? eTag,
        CancellationToken cancellationToken = default,
        bool persist = true)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ModVersionVoteService));
        await EnsureVoteCacheLoadedAsync(cancellationToken).ConfigureAwait(false);

        _voteCache[key] = new VoteCacheEntry(summary, eTag);

        if (!persist) return;

        await PersistVoteCacheAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureVoteCacheLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_voteCacheLoaded) return;
        if (_disposed) throw new ObjectDisposedException(nameof(ModVersionVoteService));

        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_voteCacheLoaded) return;

            TryLoadVoteCacheFromDisk();
            _voteCacheLoaded = true;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private void TryLoadVoteCacheFromDisk()
    {
        try
        {
            if (!File.Exists(VoteCachePath)) return;

            var json = File.ReadAllText(VoteCachePath);
            if (string.IsNullOrWhiteSpace(json)) return;

            var payload = JsonSerializer.Deserialize<Dictionary<string, VoteCacheFileEntry>>(json, SerializerOptions);
            if (payload is null) return;

            foreach (var pair in payload)
            {
                var key = TryParseCacheKey(pair.Key);
                if (key is null) continue;

                var value = pair.Value;
                if (value?.Summary is null) continue;

                _voteCache[key.Value] = new VoteCacheEntry(value.Summary, value.ETag);
            }
        }
        catch
        {
            // Ignore cache load failures; fall back to network.
        }
    }

    private async Task PersistVoteCacheAsync(CancellationToken cancellationToken = default)
    {
        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var directory = Path.GetDirectoryName(VoteCachePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var payload = _voteCache.ToDictionary(
                pair => BuildCacheKey(pair.Key),
                pair => new VoteCacheFileEntry(pair.Value.Summary, pair.Value.ETag),
                StringComparer.Ordinal);

            var json = JsonSerializer.Serialize(payload, SerializerOptions);
            await File.WriteAllTextAsync(VoteCachePath, json, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Ignore cache persistence issues; cache is best-effort.
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private static string BuildCacheKey(VoteCacheKey key)
    {
        return string.Join("||", key.ModId, key.ModVersion, key.VintageStoryVersion);
    }

    private static VoteCacheKey? TryParseCacheKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var parts = key.Split("||", StringSplitOptions.None);
        if (parts.Length != 3) return null;

        return new VoteCacheKey(parts[0], parts[1], parts[2]);
    }

    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // Dispose resources outside the lock to avoid deadlocks.
        // The _disposed flag (set atomically above) prevents new operations from starting.
        _cacheLock.Dispose();
        _voteIndexLock.Dispose();

        if (_ownsAuthenticator)
        {
            _authenticator.Dispose();
        }
    }
}