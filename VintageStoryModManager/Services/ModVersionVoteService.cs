using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleVsManager.Cloud;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.Services;

/// <summary>
///     Provides Firebase-backed storage for per-mod user report votes.
/// </summary>
public sealed class ModVersionVoteService
{
    private static readonly string DefaultDbUrl = DevConfig.ModVersionVoteDefaultDbUrl;

    private static readonly string VotesRootPath = DevConfig.ModVersionVoteRootPath;

    private static readonly HttpClient HttpClient = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new ModVersionVoteOptionJsonConverter() }
    };

    private readonly FirebaseAnonymousAuthenticator _authenticator;

    private readonly string _dbUrl;

    public ModVersionVoteService()
        : this(DefaultDbUrl, new FirebaseAnonymousAuthenticator())
    {
    }

    public ModVersionVoteService(string databaseUrl, FirebaseAnonymousAuthenticator authenticator)
    {
        if (string.IsNullOrWhiteSpace(databaseUrl))
            throw new ArgumentException("A Firebase database URL must be provided.", nameof(databaseUrl));

        _dbUrl = databaseUrl.TrimEnd('/');
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
    }


    public async Task<ModVersionVoteSummary> GetVoteSummaryAsync(
        string modId,
        string modVersion,
        string vintageStoryVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifiers(modId, modVersion, vintageStoryVersion);
        InternetAccessManager.ThrowIfInternetAccessDisabled();

        var session = await _authenticator
            .TryGetExistingSessionAsync(cancellationToken)
            .ConfigureAwait(false);

        var (Summary, _) = await GetVoteSummaryWithEtagAsync(
                session,
                modId,
                modVersion,
                vintageStoryVersion,
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
        InternetAccessManager.ThrowIfInternetAccessDisabled();

        var session = await _authenticator
            .TryGetExistingSessionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await GetVoteSummaryInternalAsync(
                session,
                modId,
                modVersion,
                vintageStoryVersion,
                knownEtag,
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
        InternetAccessManager.ThrowIfInternetAccessDisabled();

        var session = await _authenticator
            .TryGetExistingSessionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await GetVoteSummaryWithEtagAsync(
                session,
                modId,
                modVersion,
                vintageStoryVersion,
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

        var url = BuildVotesUrl(session, VotesRootPath, modKey, versionKey, "users", userKey);
        var payload = JsonSerializer.Serialize(record, SerializerOptions);

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await HttpClient
            .PutAsync(url, content, cancellationToken)
            .ConfigureAwait(false);

        await EnsureOkAsync(response, "Submit vote").ConfigureAwait(false);

        return await GetVoteSummaryWithEtagAsync(
                session,
                modId,
                modVersion,
                vintageStoryVersion,
                cancellationToken)
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

        var url = BuildVotesUrl(session, VotesRootPath, modKey, versionKey, "users", userKey);

        using var response = await HttpClient
            .DeleteAsync(url, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureOkAsync(response, "Remove vote").ConfigureAwait(false);

        return await GetVoteSummaryWithEtagAsync(
                session,
                modId,
                modVersion,
                vintageStoryVersion,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(ModVersionVoteSummary Summary, string? ETag)> GetVoteSummaryWithEtagAsync(
        FirebaseAnonymousAuthenticator.FirebaseAuthSession? session,
        string modId,
        string modVersion,
        string vintageStoryVersion,
        CancellationToken cancellationToken)
    {
        var result = await GetVoteSummaryInternalAsync(
                session,
                modId,
                modVersion,
                vintageStoryVersion,
                null,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Summary is null) throw new InvalidOperationException("Vote summary was not available.");

        return (result.Summary, result.ETag);
    }

    private async Task<VoteSummaryResult> GetVoteSummaryInternalAsync(
        FirebaseAnonymousAuthenticator.FirebaseAuthSession? session,
        string modId,
        string modVersion,
        string vintageStoryVersion,
        string? knownEtag,
        CancellationToken cancellationToken)
    {
        var modKey = SanitizeKey(modId);
        var versionKey = SanitizeKey(modVersion);
        var url = BuildVotesUrl(session, VotesRootPath, modKey, versionKey, "users");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Firebase-ETag", "true");
        if (!string.IsNullOrWhiteSpace(knownEtag)) request.Headers.TryAddWithoutValidation("If-None-Match", knownEtag);

        using var response = await HttpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var responseEtag = response.Headers.ETag?.Tag;

        if (response.StatusCode == HttpStatusCode.NotModified)
            return new VoteSummaryResult(null, responseEtag ?? knownEtag, true);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var emptySummary = new ModVersionVoteSummary(
                modId,
                modVersion,
                vintageStoryVersion,
                ModVersionVoteCounts.Empty,
                ModVersionVoteComments.Empty,
                null,
                null);

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
            var emptySummary = new ModVersionVoteSummary(
                modId,
                modVersion,
                vintageStoryVersion,
                ModVersionVoteCounts.Empty,
                ModVersionVoteComments.Empty,
                null,
                null);

            return new VoteSummaryResult(emptySummary, responseEtag, false);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            var emptySummary = new ModVersionVoteSummary(
                modId,
                modVersion,
                vintageStoryVersion,
                ModVersionVoteCounts.Empty,
                ModVersionVoteComments.Empty,
                null,
                null);

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
            modId,
            modVersion,
            vintageStoryVersion,
            counts,
            comments,
            userVote,
            userComment);

        return new VoteSummaryResult(summary, responseEtag, false);
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

    private string BuildVotesUrl(
        FirebaseAnonymousAuthenticator.FirebaseAuthSession? session,
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

        if (session.HasValue)
        {
            builder.Append("?auth=");
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
}