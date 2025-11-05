using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VintageStoryModManager.Services;

/// <summary>
/// Retrieves information about Vintage Story game releases.
/// </summary>
public static class VintageStoryGameVersionService
{
    private static readonly string GameVersionsEndpoint = DevConfig.GameVersionsEndpoint;

    private static readonly HttpClient HttpClient = new();

    /// <summary>
    /// Gets the latest Vintage Story release version that is publicly available.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The latest release version string, or <c>null</c> if it could not be determined.</returns>
    public static async Task<string?> GetLatestReleaseVersionAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GameVersionsEndpoint);
        using HttpResponseMessage response = await HttpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        JsonElement root = document.RootElement;
        if (root.TryGetProperty("statuscode", out JsonElement statusCodeElement))
        {
            if (!IsSuccessStatus(statusCodeElement))
            {
                return null;
            }
        }

        if (!root.TryGetProperty("gameversions", out JsonElement versionsElement)
            || versionsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? bestVersion = null;
        string? bestNormalized = null;
        bool bestIsPrerelease = true;

        foreach (JsonElement versionElement in versionsElement.EnumerateArray())
        {
            if (versionElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!versionElement.TryGetProperty("name", out JsonElement nameElement)
                || nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? rawName = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(rawName))
            {
                continue;
            }

            string candidate = rawName.Trim();
            string? normalized = VersionStringUtility.Normalize(candidate);
            if (normalized is null)
            {
                continue;
            }

            bool isPrerelease = candidate.Contains('-', StringComparison.Ordinal);

            if (bestNormalized is null)
            {
                bestVersion = candidate;
                bestNormalized = normalized;
                bestIsPrerelease = isPrerelease;
                continue;
            }

            if (VersionStringUtility.IsCandidateVersionNewer(normalized, bestNormalized))
            {
                bestVersion = candidate;
                bestNormalized = normalized;
                bestIsPrerelease = isPrerelease;
                continue;
            }

            if (!isPrerelease && bestIsPrerelease
                && string.Equals(normalized, bestNormalized, StringComparison.OrdinalIgnoreCase))
            {
                bestVersion = candidate;
                bestIsPrerelease = false;
            }
        }

        return bestVersion;
    }

    /// <summary>
    /// Gets the most recent Vintage Story release versions that are publicly available.
    /// </summary>
    /// <param name="count">The maximum number of versions to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list containing up to <paramref name="count"/> versions, ordered from newest to oldest.</returns>
    public static async Task<IReadOnlyList<string>> GetRecentReleaseVersionsAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        if (count <= 0)
        {
            return Array.Empty<string>();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, GameVersionsEndpoint);
        using HttpResponseMessage response = await HttpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        JsonElement root = document.RootElement;
        if (root.TryGetProperty("statuscode", out JsonElement statusCodeElement))
        {
            if (!IsSuccessStatus(statusCodeElement))
            {
                return Array.Empty<string>();
            }
        }

        if (!root.TryGetProperty("gameversions", out JsonElement versionsElement)
            || versionsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var entries = new List<VersionEntry>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement versionElement in versionsElement.EnumerateArray())
        {
            if (versionElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!versionElement.TryGetProperty("name", out JsonElement nameElement)
                || nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? rawName = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(rawName))
            {
                continue;
            }

            string candidate = rawName.Trim();
            if (!seenNames.Add(candidate))
            {
                continue;
            }

            string? normalized = VersionStringUtility.Normalize(candidate);
            Version? parsedVersion = null;
            if (normalized is not null)
            {
                if (Version.TryParse(normalized, out Version? version))
                {
                    parsedVersion = version;
                }
            }

            bool isPrerelease = candidate.Contains('-', StringComparison.Ordinal);
            entries.Add(new VersionEntry(candidate, parsedVersion, isPrerelease));
        }

        if (entries.Count == 0)
        {
            return Array.Empty<string>();
        }

        entries.Sort(VersionEntryComparer.Instance);

        int resultCount = Math.Min(count, entries.Count);
        var result = new List<string>(resultCount);
        for (int i = 0; i < resultCount; i++)
        {
            result.Add(entries[i].Name);
        }

        return result;
    }

    private static bool IsSuccessStatus(JsonElement statusCodeElement)
    {
        return statusCodeElement.ValueKind switch
        {
            JsonValueKind.Number => statusCodeElement.TryGetInt32(out int value) && value >= 200 && value < 300,
            JsonValueKind.String => int.TryParse(statusCodeElement.GetString(), out int value)
                && value >= 200
                && value < 300,
            _ => false
        };
    }

    private sealed record VersionEntry(string Name, Version? ParsedVersion, bool IsPrerelease);

    private sealed class VersionEntryComparer : IComparer<VersionEntry>
    {
        public static VersionEntryComparer Instance { get; } = new();

        public int Compare(VersionEntry? x, VersionEntry? y)
        {
            if (x is null && y is null)
            {
                return 0;
            }

            if (x is null)
            {
                return 1;
            }

            if (y is null)
            {
                return -1;
            }

            int versionComparison = CompareParsedVersions(x.ParsedVersion, y.ParsedVersion);
            if (versionComparison != 0)
            {
                return versionComparison;
            }

            if (x.IsPrerelease != y.IsPrerelease)
            {
                return x.IsPrerelease ? 1 : -1;
            }

            return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareParsedVersions(Version? x, Version? y)
        {
            if (x is null && y is null)
            {
                return 0;
            }

            if (x is null)
            {
                return 1;
            }

            if (y is null)
            {
                return -1;
            }

            return y.CompareTo(x);
        }
    }
}
