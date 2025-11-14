namespace VintageStoryModManager.Models;

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