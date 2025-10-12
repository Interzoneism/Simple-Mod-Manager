using System;
using System.Collections.Generic;

namespace VintageStoryModManager.Models;

/// <summary>
/// Represents a single search hit returned from the Vintage Story mod database.
/// </summary>
public sealed class ModDatabaseSearchResult
{
    public required string ModId { get; init; }

    public required string Name { get; init; }

    public IReadOnlyList<string> AlternateIds { get; init; } = Array.Empty<string>();

    public string? Summary { get; init; }

    public string? Author { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public int Downloads { get; init; }

    public int Follows { get; init; }

    public int TrendingPoints { get; init; }

    public int Comments { get; init; }

    public string? AssetId { get; init; }

    public string? UrlAlias { get; init; }

    public string? Side { get; init; }

    public string? LogoUrl { get; init; }

    public DateTime? LastReleasedUtc { get; init; }

    public DateTime? CreatedUtc { get; init; }

    internal double Score { get; init; }

    public int? LatestReleaseDownloads { get; init; }

    public ModDatabaseInfo? DetailedInfo { get; init; }
}
