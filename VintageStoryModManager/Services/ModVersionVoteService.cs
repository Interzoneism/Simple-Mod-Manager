using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SimpleVsManager.Cloud;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.Services;

/// <summary>
/// Provides Firebase-backed storage for per-mod user report votes.
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

    private readonly string _dbUrl;
    private readonly FirebaseAnonymousAuthenticator _authenticator;

    public ModVersionVoteService()
        : this(DefaultDbUrl, new FirebaseAnonymousAuthenticator())
    {
    }

    public ModVersionVoteService(string databaseUrl, FirebaseAnonymousAuthenticator authenticator)
    {
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            throw new ArgumentException("A Firebase database URL must be provided.", nameof(databaseUrl));
        }

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

        FirebaseAnonymousAuthenticator.FirebaseAuthSession? session = await _authenticator
            .TryGetExistingSessionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await GetVoteSummaryInternalAsync(
                session,
                modId,
                modVersion,
                vintageStoryVersion,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ModVersionVoteSummary> SubmitVoteAsync(
        string modId,
        string modVersion,
        string vintageStoryVersion,
        ModVersionVoteOption option,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifiers(modId, modVersion, vintageStoryVersion);
        InternetAccessManager.ThrowIfInternetAccessDisabled();

        FirebaseAnonymousAuthenticator.FirebaseAuthSession session = await _authenticator
            .GetSessionAsync(cancellationToken)
            .ConfigureAwait(false);

        string modKey = SanitizeKey(modId);
        string versionKey = SanitizeKey(modVersion);
        string userKey = session.UserId ?? throw new InvalidOperationException("Firebase session did not provide a user ID.");

        var record = new VoteRecord
        {
            Option = option,
            VintageStoryVersion = vintageStoryVersion,
            UpdatedUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Comment = NormalizeComment(comment)
        };

        string url = BuildVotesUrl(session, VotesRootPath, modKey, versionKey, "users", userKey);
        string payload = JsonSerializer.Serialize(record, SerializerOptions);

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await HttpClient
            .PutAsync(url, content, cancellationToken)
            .ConfigureAwait(false);

        await EnsureOkAsync(response, "Submit vote").ConfigureAwait(false);

        return await GetVoteSummaryInternalAsync(
                session,
                modId,
                modVersion,
                vintageStoryVersion,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ModVersionVoteSummary> RemoveVoteAsync(
        string modId,
        string modVersion,
        string vintageStoryVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifiers(modId, modVersion, vintageStoryVersion);
        InternetAccessManager.ThrowIfInternetAccessDisabled();

        FirebaseAnonymousAuthenticator.FirebaseAuthSession session = await _authenticator
            .GetSessionAsync(cancellationToken)
            .ConfigureAwait(false);

        string modKey = SanitizeKey(modId);
        string versionKey = SanitizeKey(modVersion);
        string userKey = session.UserId ?? throw new InvalidOperationException("Firebase session did not provide a user ID.");

        string url = BuildVotesUrl(session, VotesRootPath, modKey, versionKey, "users", userKey);

        using HttpResponseMessage response = await HttpClient
            .DeleteAsync(url, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureOkAsync(response, "Remove vote").ConfigureAwait(false);
        }

        return await GetVoteSummaryInternalAsync(
                session,
                modId,
                modVersion,
                vintageStoryVersion,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ModVersionVoteSummary> GetVoteSummaryInternalAsync(
        FirebaseAnonymousAuthenticator.FirebaseAuthSession? session,
        string modId,
        string modVersion,
        string vintageStoryVersion,
        CancellationToken cancellationToken)
    {
        string modKey = SanitizeKey(modId);
        string versionKey = SanitizeKey(modVersion);
        string url = BuildVotesUrl(session, VotesRootPath, modKey, versionKey, "users");

        using HttpResponseMessage response = await HttpClient
            .GetAsync(url, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ModVersionVoteSummary(
                modId,
                modVersion,
                vintageStoryVersion,
                ModVersionVoteCounts.Empty,
                ModVersionVoteComments.Empty,
                null,
                null);
        }

        await EnsureOkAsync(response, "Fetch votes").ConfigureAwait(false);

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json.Trim(), "null", StringComparison.OrdinalIgnoreCase))
        {
            return new ModVersionVoteSummary(
                modId,
                modVersion,
                vintageStoryVersion,
                ModVersionVoteCounts.Empty,
                ModVersionVoteComments.Empty,
                null,
                null);
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ModVersionVoteSummary(
                modId,
                modVersion,
                vintageStoryVersion,
                ModVersionVoteCounts.Empty,
                ModVersionVoteComments.Empty,
                null,
                null);
        }

        int fullyFunctional = 0;
        int noIssuesSoFar = 0;
        int someIssuesButWorks = 0;
        int notFunctional = 0;
        int crashesOrFreezes = 0;
        ModVersionVoteOption? userVote = null;
        string? userComment = null;
        string? installedVersionNormalized = NormalizeVersion(vintageStoryVersion);
        string? currentUserId = session?.UserId;
        var notFunctionalComments = new List<string>();
        var crashesOrFreezesComments = new List<string>();

        foreach (JsonProperty property in root.EnumerateObject())
        {
            VoteRecord? record = TryDeserializeRecord(property.Value);
            if (record is null)
            {
                continue;
            }

            ModVersionVoteOption option = record.Value.Option;
            string? normalizedComment = NormalizeComment(record.Value.Comment);

            if (!string.IsNullOrWhiteSpace(currentUserId)
                && string.Equals(property.Name, currentUserId, StringComparison.Ordinal))
            {
                userVote = option;
                userComment = normalizedComment;
            }

            if (!string.Equals(
                    NormalizeVersion(record.Value.VintageStoryVersion),
                    installedVersionNormalized,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            switch (option)
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
                    AddComment(notFunctionalComments, normalizedComment);
                    break;
                case ModVersionVoteOption.CrashesOrFreezesGame:
                    crashesOrFreezes++;
                    AddComment(crashesOrFreezesComments, normalizedComment);
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
            ToReadOnlyList(crashesOrFreezesComments));
        return new ModVersionVoteSummary(
            modId,
            modVersion,
            vintageStoryVersion,
            counts,
            comments,
            userVote,
            userComment);
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

        foreach (string segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

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
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        throw new InvalidOperationException(
            $"{operation} failed: {(int)response.StatusCode} {response.ReasonPhrase} | {Truncate(body, 200)}");
    }

    private static void ValidateIdentifiers(string modId, string modVersion, string vintageStoryVersion)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            throw new ArgumentException("Mod identifier is required.", nameof(modId));
        }

        if (string.IsNullOrWhiteSpace(modVersion))
        {
            throw new ArgumentException("Mod version is required.", nameof(modVersion));
        }

        if (string.IsNullOrWhiteSpace(vintageStoryVersion))
        {
            throw new ArgumentException("Vintage Story version is required.", nameof(vintageStoryVersion));
        }
    }

    private static string SanitizeKey(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return "_";
        }

        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(trimmed));
        return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static void AddComment(List<string> target, string? comment)
    {
        string? normalized = NormalizeComment(comment);
        if (string.IsNullOrEmpty(normalized))
        {
            return;
        }

        target.Add(normalized);
    }

    private static IReadOnlyList<string> ToReadOnlyList(List<string> source) => source.Count == 0
        ? Array.Empty<string>()
        : source.ToArray();

    private static string? NormalizeComment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? NormalizeVersion(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim();

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private sealed class ModVersionVoteOptionJsonConverter : JsonConverter<ModVersionVoteOption>
    {
        public override ModVersionVoteOption Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Expected string value for mod version vote option.");
            }

            string? value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new JsonException("Vote option value was empty.");
            }

            return value switch
            {
                var v when v.Equals("fullyFunctional", StringComparison.OrdinalIgnoreCase) => ModVersionVoteOption.FullyFunctional,
                var v when v.Equals("workingPerfectly", StringComparison.OrdinalIgnoreCase) => ModVersionVoteOption.FullyFunctional,
                var v when v.Equals("noIssuesSoFar", StringComparison.OrdinalIgnoreCase) => ModVersionVoteOption.NoIssuesSoFar,
                var v when v.Equals("someIssuesButWorks", StringComparison.OrdinalIgnoreCase) => ModVersionVoteOption.SomeIssuesButWorks,
                var v when v.Equals("notFunctional", StringComparison.OrdinalIgnoreCase) => ModVersionVoteOption.NotFunctional,
                var v when v.Equals("notWorking", StringComparison.OrdinalIgnoreCase) => ModVersionVoteOption.NotFunctional,
                var v when v.Equals("crashesOrFreezesGame", StringComparison.OrdinalIgnoreCase) => ModVersionVoteOption.CrashesOrFreezesGame,
                _ => throw new JsonException($"Unrecognized vote option '{value}'.")
            };
        }

        public override void Write(Utf8JsonWriter writer, ModVersionVoteOption value, JsonSerializerOptions options)
        {
            string stringValue = value switch
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
        [JsonPropertyName("option")]
        public ModVersionVoteOption Option { get; set; }

        [JsonPropertyName("vintageStoryVersion")]
        public string? VintageStoryVersion { get; set; }

        [JsonPropertyName("updatedUtc")]
        public string? UpdatedUtc { get; set; }

        [JsonPropertyName("comment")]
        public string? Comment { get; set; }
    }
}
