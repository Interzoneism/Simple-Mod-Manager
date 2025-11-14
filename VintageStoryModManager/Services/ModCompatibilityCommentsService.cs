using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;

namespace VintageStoryModManager.Services;

public sealed class ModCompatibilityCommentsService
{
    private static readonly string ModApiUrlTemplate = DevConfig.ModCompatibilityApiUrlTemplate;
    private static readonly string ModPageUrlTemplate = DevConfig.ModCompatibilityPageUrlTemplate;

    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly Regex VersionRegex = new(
        "\\b1\\.\\d{1,2}(\\.\\d{1,2})?(-rc\\.\\d+|-pre(\\.\\d+)?)?\\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] PositiveCues =
    {
        "works",
        "working",
        "compatible",
        "fine",
        "loads",
        "no issues",
        "ok",
        "plays",
        "runs",
        "up to date",
        "stable",
        "on 1.21",
        "on 1.20"
    };

    private static readonly string[] NegativeCues =
    {
        "not working",
        "doesn't work",
        "doesnt work",
        "broken",
        "crash",
        "crashes",
        "error",
        "exception",
        "incompatible",
        "needs update",
        "no longer",
        "json error",
        "nullref",
        "fails",
        "can't load",
        "cant load",
        "won't",
        "wont"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static ModCompatibilityCommentsService()
    {
        if (!HttpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            HttpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SimpleVSManager", "1.0"));
            HttpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ExperimentalCompReview", "1.0"));
        }
    }

    public async Task<ExperimentalCompReviewResult> GetTop3CommentsAsync(
        string modSlug,
        string? latestVsVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modSlug))
            throw new ArgumentException("Mod slug must be provided.", nameof(modSlug));

        InternetAccessManager.ThrowIfInternetAccessDisabled();

        var normalizedSlug = NormalizeSlug(modSlug);
        var normalizedLatestVersion = string.IsNullOrWhiteSpace(latestVsVersion)
            ? null
            : latestVsVersion!.Trim();

        var metadata = await FetchModMetadataAsync(normalizedSlug, cancellationToken).ConfigureAwait(false);
        var pagePath = metadata.PagePath ?? normalizedSlug;
        var html = await FetchCommentsHtmlAsync(pagePath, cancellationToken).ConfigureAwait(false);

        var cutoffUtc = metadata.LastReleaseUtc ?? DateTime.MinValue;
        var comments = ExtractComments(html, pagePath, cutoffUtc);
        var scoredComments = ScoreComments(comments, normalizedLatestVersion);

        IReadOnlyList<ExperimentalCompReviewComment> top3 = scoredComments
            .Take(3)
            .Select(BuildCommentResult)
            .ToList();

        string? reason = null;
        if (top3.Count == 0)
        {
            if (metadata.LastReleaseUtc is null)
                reason = "The mod has no recorded releases in the Mod DB.";
            else if (comments.Count == 0)
                reason = "No comments were posted after the latest release.";
            else
                reason = "No comments met the compatibility relevance criteria.";
        }

        return new ExperimentalCompReviewResult
        {
            Mod = metadata.CanonicalIdentifier ?? normalizedSlug,
            LatestVsVersion = normalizedLatestVersion,
            ModLastReleaseUtc = metadata.LastReleaseUtc?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            Top3 = top3,
            Reason = reason
        };
    }

    public static string SerializeResult(ExperimentalCompReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static string NormalizeSlug(string slug)
    {
        var trimmed = slug.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
                trimmed = uri.AbsolutePath;

        trimmed = trimmed.Trim('/');

        var queryIndex = trimmed.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0) trimmed = trimmed[..queryIndex];

        var fragmentIndex = trimmed.IndexOf('#', StringComparison.Ordinal);
        if (fragmentIndex >= 0) trimmed = trimmed[..fragmentIndex];

        const string ShowModPrefix = "show/mod/";
        if (trimmed.StartsWith(ShowModPrefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[ShowModPrefix.Length..];

        return trimmed;
    }

    private static async Task<ModMetadata> FetchModMetadataAsync(string normalizedSlug,
        CancellationToken cancellationToken)
    {
        var requestUrl = string.Format(CultureInfo.InvariantCulture, ModApiUrlTemplate, normalizedSlug);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        using var response = await HttpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var statusCode = TryGetStatusCode(document.RootElement);
        if (!document.RootElement.TryGetProperty("mod", out var modElement))
        {
            if (statusCode == 404)
                throw new InvalidOperationException(string.Format(
                    CultureInfo.CurrentCulture,
                    "Mod '{0}' was not found in the Mod DB.",
                    normalizedSlug));

            if (statusCode is not null)
                throw new InvalidOperationException(string.Format(
                    CultureInfo.CurrentCulture,
                    "Unexpected response from the Mod DB API (status code {0}).",
                    statusCode));

            throw new InvalidOperationException("Unexpected response from the Mod DB API (missing 'mod' element).");
        }

        DateTime? lastReleaseUtc = null;
        var urlAlias = GetStringProperty(modElement, "urlalias");
        var assetId = GetStringProperty(modElement, "assetid");
        var canonicalIdentifier = !string.IsNullOrWhiteSpace(urlAlias)
            ? NormalizeSlug(urlAlias!)
            : !string.IsNullOrWhiteSpace(assetId)
                ? assetId!.Trim()
                : normalizedSlug;

        string? pagePath = null;
        if (!string.IsNullOrWhiteSpace(urlAlias))
        {
            var normalizedAlias = NormalizePageComponent(urlAlias!);
            if (!string.IsNullOrWhiteSpace(normalizedAlias)) pagePath = normalizedAlias;
        }

        if (pagePath is null && !string.IsNullOrWhiteSpace(assetId))
        {
            var trimmedAssetId = assetId!.Trim();
            if (trimmedAssetId.Length > 0) pagePath = "show/mod/" + trimmedAssetId;
        }

        if (modElement.TryGetProperty("releases", out var releasesElement) &&
            releasesElement.ValueKind == JsonValueKind.Array)
            foreach (var release in releasesElement.EnumerateArray())
            {
                if (!release.TryGetProperty("created", out var createdElement)) continue;

                var createdString = createdElement.GetString();
                if (string.IsNullOrWhiteSpace(createdString)) continue;

                if (!DateTime.TryParse(createdString, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal,
                        out var created)) continue;

                var createdUtc = created.ToUniversalTime();
                if (lastReleaseUtc is null || createdUtc > lastReleaseUtc.Value) lastReleaseUtc = createdUtc;
            }

        return new ModMetadata(lastReleaseUtc, pagePath, canonicalIdentifier);
    }

    private static int? TryGetStatusCode(JsonElement rootElement)
    {
        if (!rootElement.TryGetProperty("statuscode", out var statusElement)) return null;

        if (statusElement.ValueKind == JsonValueKind.Number && statusElement.TryGetInt32(out var numeric))
            return numeric;

        if (statusElement.ValueKind == JsonValueKind.String)
        {
            var statusString = statusElement.GetString();
            if (int.TryParse(statusString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return null;
    }

    private static async Task<string> FetchCommentsHtmlAsync(string pagePath, CancellationToken cancellationToken)
    {
        var requestUrl = string.Format(CultureInfo.InvariantCulture, ModPageUrlTemplate, pagePath);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<Comment> ExtractComments(string html, string pagePath, DateTime lastReleaseUtc)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<Comment>();

        var document = new HtmlDocument();
        document.LoadHtml(html);

        var commentNodes = document.DocumentNode.SelectNodes("//div[contains(@class,'comment') and @data-timestamp]");
        if (commentNodes is null || commentNodes.Count == 0) return Array.Empty<Comment>();

        var results = new List<Comment>(commentNodes.Count);
        foreach (var node in commentNodes)
        {
            var timestampUtc = TryGetTimestampUtc(node);
            if (timestampUtc is null) continue;

            if (timestampUtc.Value < lastReleaseUtc) continue;

            var bodyNode = node.SelectSingleNode(".//div[contains(@class,'body')]");
            if (bodyNode is null) continue;

            var text = HtmlEntity.DeEntitize(bodyNode.InnerText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            var titleNode = node.SelectSingleNode(".//div[contains(@class,'title')]");
            var authorNode = titleNode?.SelectSingleNode(".//a[contains(@href,'/show/user')]");
            var author = string.IsNullOrWhiteSpace(authorNode?.InnerText)
                ? "Unknown"
                : HtmlEntity.DeEntitize(authorNode!.InnerText).Trim();

            var isModAuthor = titleNode?.InnerText?.IndexOf("Author", StringComparison.OrdinalIgnoreCase) >= 0;
            var permalink = BuildPermalink(node, pagePath);

            results.Add(new Comment
            {
                Author = author,
                IsModAuthor = isModAuthor,
                TimestampUtc = timestampUtc.Value,
                Text = SquashWhitespace(text),
                Permalink = permalink
            });
        }

        return results;
    }

    private static IReadOnlyList<ScoredComment> ScoreComments(IReadOnlyList<Comment> comments, string? latestVsVersion)
    {
        if (comments.Count == 0) return Array.Empty<ScoredComment>();

        var scored = new List<ScoredComment>(comments.Count);
        foreach (var comment in comments)
        {
            var breakdown = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double score = 0;
            var textLower = comment.Text.ToLowerInvariant();

            var hasExactVersion = false;
            if (!string.IsNullOrEmpty(latestVsVersion))
            {
                if (MentionsExact(latestVsVersion!, textLower))
                {
                    score += 2.5;
                    breakdown["exactVersion"] = 2.5;
                    hasExactVersion = true;
                }
                else if (MentionsSameMinor(latestVsVersion!, textLower))
                {
                    score += 1.5;
                    breakdown["sameMinor"] = 1.5;
                }
            }

            var positiveMatches = CountMatches(textLower, PositiveCues, 3);
            if (positiveMatches > 0)
            {
                score += positiveMatches;
                breakdown["positiveCues"] = positiveMatches;
            }

            var negativeMatches = CountMatches(textLower, NegativeCues, 3);
            if (negativeMatches > 0)
            {
                score -= negativeMatches;
                breakdown["negativeCues"] = -negativeMatches;
            }

            if (HasReproDetails(textLower))
            {
                score += 1;
                breakdown["details"] = 1;
            }

            if (comment.IsModAuthor)
            {
                score += 0.5;
                breakdown["authorBoost"] = 0.5;
            }

            if (textLower.Length < 20)
            {
                score -= 0.5;
                breakdown["lengthPenalty"] = -0.5;
            }

            if (IsOffTopic(textLower))
            {
                score -= 0.5;
                breakdown["offtopicPenalty"] = -0.5;
            }

            if (!HasMeaningfulSignal(score, hasExactVersion, breakdown)) continue;

            var relevance = Math.Abs(score);
            var polarity = score > 0 ? "positive" : score < 0 ? "negative" : "mixed/unclear";

            scored.Add(new ScoredComment(comment, score, relevance, polarity, breakdown, hasExactVersion));
        }

        IReadOnlyList<ScoredComment> deduplicated = scored
            .GroupBy(c => NormalizeForDedup(c.Base.Text))
            .Select(group => group
                .OrderByDescending(item => item.Relevance)
                .ThenByDescending(item => item.Base.TimestampUtc)
                .First())
            .ToList();

        return deduplicated
            .OrderByDescending(item => item.Relevance)
            .ThenByDescending(item => item.Base.TimestampUtc)
            .ThenByDescending(item => item.HasExactVersion)
            .ThenByDescending(item => item.Base.Text.Length)
            .ToList();
    }

    private static bool HasMeaningfulSignal(
        double score,
        bool hasExactVersion,
        IReadOnlyDictionary<string, double> breakdown)
    {
        if (hasExactVersion) return true;

        if (breakdown.ContainsKey("sameMinor") ||
            breakdown.ContainsKey("positiveCues") ||
            breakdown.ContainsKey("negativeCues") ||
            breakdown.ContainsKey("details"))
            return true;

        return Math.Abs(score) >= 1.0;
    }

    private static ExperimentalCompReviewComment BuildCommentResult(ScoredComment comment)
    {
        return new ExperimentalCompReviewComment
        {
            TimestampUtc = comment.Base.TimestampUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            Author = comment.Base.Author,
            IsModAuthor = comment.Base.IsModAuthor,
            Polarity = comment.Polarity,
            Relevance = Math.Round(comment.Relevance, 2),
            ScoreBreakdown = comment.Breakdown,
            Snippet = TrimOneLine(comment.Base.Text, 280),
            Permalink = comment.Base.Permalink,
            WhyPicked = BuildWhyPicked(comment)
        };
    }

    private static DateTime? TryGetTimestampUtc(HtmlNode node)
    {
        var unixTimestamp = node.GetAttributeValue("data-timestamp", null);
        if (!string.IsNullOrWhiteSpace(unixTimestamp) && long.TryParse(unixTimestamp, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var seconds))
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                // Ignore malformed values
            }

        var titleSpan = node.SelectSingleNode(".//div[contains(@class,'title')]//span[@title]");
        var titleValue = titleSpan?.GetAttributeValue("title", null);
        if (!string.IsNullOrWhiteSpace(titleValue) &&
            DateTime.TryParse(titleValue, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed.ToUniversalTime();

        var titleText = HtmlEntity.DeEntitize(titleSpan?.InnerText ?? string.Empty);
        var relative = ParseRelativeTimestamp(titleText);
        if (relative is not null) return relative;

        return null;
    }

    private static DateTime? ParseRelativeTimestamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var match = Regex.Match(text, "\\b(\\d+)\\s+(minute|hour|day|week|month|year)s?\\s+ago\\b",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var quantity)) return null;

        var unit = match.Groups[2].Value.ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        return unit switch
        {
            "minute" => now.AddMinutes(-quantity).UtcDateTime,
            "hour" => now.AddHours(-quantity).UtcDateTime,
            "day" => now.AddDays(-quantity).UtcDateTime,
            "week" => now.AddDays(-7 * quantity).UtcDateTime,
            "month" => now.AddMonths(-quantity).UtcDateTime,
            "year" => now.AddYears(-quantity).UtcDateTime,
            _ => null
        };
    }

    private static string? BuildPermalink(HtmlNode node, string pagePath)
    {
        var id = node.GetAttributeValue("id", null);
        if (!string.IsNullOrWhiteSpace(id))
            return string.Format(CultureInfo.InvariantCulture, "https://mods.vintagestory.at/{0}#{1}", pagePath, id);

        var anchor = node.SelectSingleNode(".//a[starts-with(@href,'#cmt-')]");
        var href = anchor?.GetAttributeValue("href", null);
        if (string.IsNullOrWhiteSpace(href)) return null;

        if (href.StartsWith("#", StringComparison.Ordinal))
            return string.Format(CultureInfo.InvariantCulture, "https://mods.vintagestory.at/{0}{1}", pagePath, href);

        if (href.StartsWith("/", StringComparison.Ordinal)) return "https://mods.vintagestory.at" + href;

        return href;
    }

    private static bool MentionsExact(string latestVsVersion, string textLower)
    {
        return Regex.IsMatch(textLower, Regex.Escape(latestVsVersion.Trim()), RegexOptions.IgnoreCase);
    }

    private static bool MentionsSameMinor(string latestVsVersion, string textLower)
    {
        var match = Regex.Match(latestVsVersion, "^(?<major>\\d+)\\.(?<minor>\\d+)");
        if (!match.Success) return false;

        var pattern = $"\\b{match.Groups["major"].Value}\\.{match.Groups["minor"].Value}\\b";
        return Regex.IsMatch(textLower, pattern, RegexOptions.IgnoreCase);
    }

    private static int CountMatches(string textLower, IEnumerable<string> cues, int cap)
    {
        var count = 0;
        foreach (var cue in cues)
            if (textLower.Contains(cue, StringComparison.OrdinalIgnoreCase))
            {
                count++;
                if (count >= cap) break;
            }

        return count;
    }

    private static bool HasReproDetails(string textLower)
    {
        if (string.IsNullOrEmpty(textLower)) return false;

        if (textLower.Contains("stack trace", StringComparison.OrdinalIgnoreCase) ||
            textLower.Contains("stacktrace", StringComparison.OrdinalIgnoreCase) ||
            textLower.Contains("json error", StringComparison.OrdinalIgnoreCase) ||
            textLower.Contains("nullref", StringComparison.OrdinalIgnoreCase) ||
            textLower.Contains("null reference", StringComparison.OrdinalIgnoreCase))
            return true;

        if (Regex.IsMatch(textLower, "\\bexception\\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(textLower, "\\berror code\\b", RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(textLower, "\\bwhen i\\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(textLower, "\\bwhen opening\\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(textLower, "\\bwhen loading\\b", RegexOptions.IgnoreCase))
            return true;

        if (textLower.Contains("server", StringComparison.OrdinalIgnoreCase) &&
            (textLower.Contains("client", StringComparison.OrdinalIgnoreCase) ||
             textLower.Contains("multiplayer", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private static bool IsOffTopic(string textLower)
    {
        var hasVersion = VersionRegex.IsMatch(textLower);
        var hasCue = PositiveCues.Any(cue => textLower.Contains(cue, StringComparison.OrdinalIgnoreCase)) ||
                     NegativeCues.Any(cue => textLower.Contains(cue, StringComparison.OrdinalIgnoreCase));
        return !hasVersion && !hasCue;
    }

    private static string NormalizeForDedup(string text)
    {
        var lower = text.ToLowerInvariant();
        lower = Regex.Replace(lower, "\\s+", " ").Trim();
        return lower.Length > 300 ? lower[..300] : lower;
    }

    private static string TrimOneLine(string text, int maxLength)
    {
        var normalized = Regex.Replace(text, "\\s+", " ").Trim();
        if (normalized.Length <= maxLength) return normalized;

        return normalized[..(maxLength - 1)] + "…";
    }

    private static string BuildWhyPicked(ScoredComment comment)
    {
        var reasons = new List<string>();
        if (comment.Breakdown.ContainsKey("exactVersion"))
            reasons.Add("Exact version mention");
        else if (comment.Breakdown.ContainsKey("sameMinor")) reasons.Add("Same minor version mention");

        if (comment.Breakdown.TryGetValue("positiveCues", out var positive) && positive > 0)
            reasons.Add("Positive compatibility cues");

        if (comment.Breakdown.TryGetValue("negativeCues", out var negative) && negative < 0)
            reasons.Add("Reported compatibility issues");

        if (comment.Breakdown.ContainsKey("details")) reasons.Add("Diagnostic details");

        if (comment.Breakdown.ContainsKey("authorBoost")) reasons.Add("Mod author insight");

        if (reasons.Count == 0) reasons.Add("High relevance score");

        return string.Join(", ", reasons);
    }

    private static string SquashWhitespace(string text)
    {
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)) return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.TryGetInt64(out var numeric)
                ? numeric.ToString(CultureInfo.InvariantCulture)
                : property.TryGetDouble(out var floating)
                    ? floating.ToString(CultureInfo.InvariantCulture)
                    : property.GetRawText(),
            JsonValueKind.Null => null,
            _ => property.ToString()
        };
    }

    private static string NormalizePageComponent(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return string.Empty;

        return trimmed.Trim('/');
    }

    private sealed record ModMetadata(DateTime? LastReleaseUtc, string? PagePath, string? CanonicalIdentifier);

    private sealed record Comment
    {
        public required string Author { get; init; }

        public required bool IsModAuthor { get; init; }

        public required DateTime TimestampUtc { get; init; }

        public required string Text { get; init; }

        public string? Permalink { get; init; }
    }

    private sealed record ScoredComment(
        Comment Base,
        double Score,
        double Relevance,
        string Polarity,
        IReadOnlyDictionary<string, double> Breakdown,
        bool HasExactVersion);

    public sealed record ExperimentalCompReviewResult
    {
        [JsonPropertyName("mod")] public required string Mod { get; init; }

        [JsonPropertyName("latestVsVersion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LatestVsVersion { get; init; }

        [JsonPropertyName("modLastReleaseUtc")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ModLastReleaseUtc { get; init; }

        [JsonPropertyName("top3")]
        public IReadOnlyList<ExperimentalCompReviewComment> Top3 { get; init; } =
            Array.Empty<ExperimentalCompReviewComment>();

        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; init; }
    }

    public sealed record ExperimentalCompReviewComment
    {
        [JsonPropertyName("timestampUtc")] public required string TimestampUtc { get; init; }

        [JsonPropertyName("author")] public required string Author { get; init; }

        [JsonPropertyName("isModAuthor")] public bool IsModAuthor { get; init; }

        [JsonPropertyName("polarity")] public required string Polarity { get; init; }

        [JsonPropertyName("relevance")] public double Relevance { get; init; }

        [JsonPropertyName("scoreBreakdown")]
        public IReadOnlyDictionary<string, double> ScoreBreakdown { get; init; } = new Dictionary<string, double>();

        [JsonPropertyName("snippet")] public required string Snippet { get; init; }

        [JsonPropertyName("permalink")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Permalink { get; init; }

        [JsonPropertyName("whyPicked")] public required string WhyPicked { get; init; }
    }
}