namespace VintageStoryModManager.Models;

/// <summary>
///     Represents a dependency declared in a modinfo.json file.
/// </summary>
public sealed record ModDependencyInfo(string ModId, string Version)
{
    public string Display => string.IsNullOrWhiteSpace(Version)
        ? ModId
        : $"{ModId} (≥ {Version})";

    public bool IsGameOrCoreDependency => string.Equals(ModId, "game", StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(ModId, "creative", StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(ModId, "survival", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
///     Represents a downloadable release of a mod from the official mod database.
/// </summary>
public sealed class ModReleaseInfo
{
    public required string Version { get; init; }

    public string? NormalizedVersion { get; init; }

    public required Uri DownloadUri { get; init; }

    public string? FileName { get; init; }

    public IReadOnlyList<string> GameVersionTags { get; init; } = Array.Empty<string>();

    public bool IsCompatibleWithInstalledGame { get; init; }

    public string? Changelog { get; init; }

    public int? Downloads { get; init; }

    public DateTime? CreatedUtc { get; init; }
}

/// <summary>
///     Represents metadata retrieved from the official Vintage Story mod database.
/// </summary>
public sealed class ModDatabaseInfo
{
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public string? CachedTagsVersion { get; init; }

    public string? AssetId { get; init; }

    public string? ModPageUrl { get; init; }

    public string? LatestCompatibleVersion { get; init; }

    public string? LatestVersion { get; init; }

    public IReadOnlyList<string> RequiredGameVersions { get; init; } = Array.Empty<string>();

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

    public ModReleaseInfo? LatestRelease { get; init; }

    public ModReleaseInfo? LatestCompatibleRelease { get; init; }

    public IReadOnlyList<ModReleaseInfo> Releases { get; init; } = Array.Empty<ModReleaseInfo>();

    public bool IsOfflineOnly { get; init; }

    public string? Side { get; init; }
}

/// <summary>
///     Represents a single search hit returned from the Vintage Story mod database.
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

    public string? LogoUrlSource { get; init; }

    public DateTime? LastReleasedUtc { get; init; }

    public DateTime? CreatedUtc { get; init; }

    internal double Score { get; init; }

    public int? LatestReleaseDownloads { get; init; }

    public ModDatabaseInfo? DetailedInfo { get; init; }
}
