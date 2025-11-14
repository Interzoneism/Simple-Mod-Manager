namespace VintageStoryModManager.Models;

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