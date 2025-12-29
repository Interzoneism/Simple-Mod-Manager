using System.Text.Json.Serialization;

namespace VintageStoryModManager.Models;

/// <summary>
/// Represents full mod details from the Vintage Story mod database.
/// </summary>
public class DownloadableMod
{
    [JsonPropertyName("modid")]
    public int ModId { get; set; }

    [JsonPropertyName("modidstr")]
    public string? ModIdStr { get; set; }

    [JsonPropertyName("assetid")]
    public int AssetId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("urlalias")]
    public string? UrlAlias { get; set; }

    [JsonPropertyName("logofiledb")]
    public string? LogoFileDatabase { get; set; }

    [JsonPropertyName("homepageurl")]
    public string? HomepageUrl { get; set; }

    [JsonPropertyName("sourcecodeurl")]
    public string? SourceCodeUrl { get; set; }

    [JsonPropertyName("trailervideourl")]
    public string? TrailerVideoUrl { get; set; }

    [JsonPropertyName("issuetrackerurl")]
    public string? IssueTrackerUrl { get; set; }

    [JsonPropertyName("wikiurl")]
    public string? WikiUrl { get; set; }

    [JsonPropertyName("downloads")]
    public int Downloads { get; set; }

    [JsonPropertyName("follows")]
    public int Follows { get; set; }

    [JsonPropertyName("trendingpoints")]
    public int TrendingPoints { get; set; }

    [JsonPropertyName("comments")]
    public int Comments { get; set; }

    [JsonPropertyName("side")]
    public string Side { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("createdat")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("lastmodified")]
    public string LastModified { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("releases")]
    public List<DownloadableModRelease> Releases { get; set; } = [];

    [JsonPropertyName("screenshots")]
    public List<DownloadableModScreenshot> Screenshots { get; set; } = [];
}

/// <summary>
/// Represents a mod release/version.
/// </summary>
public class DownloadableModRelease
{
    [JsonPropertyName("releaseid")]
    public int ReleaseId { get; set; }

    [JsonPropertyName("mainfile")]
    public string MainFile { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("fileid")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int FileId { get; set; }

    [JsonPropertyName("downloads")]
    public int Downloads { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("modidstr")]
    public string ModIdStr { get; set; } = string.Empty;

    [JsonPropertyName("modversion")]
    public string ModVersion { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public string Created { get; set; } = string.Empty;

    [JsonPropertyName("changelog")]
    public string Changelog { get; set; } = string.Empty;

    /// <summary>
    /// Gets a formatted date string for display.
    /// </summary>
    public string FormattedDate =>
        DateTime.TryParse(Created, out var date)
            ? date.ToShortDateString()
            : Created;

    /// <summary>
    /// Gets the compatible game versions as a comma-separated string.
    /// </summary>
    public string GameVersionsDisplay => string.Join(", ", Tags);
}

/// <summary>
/// Represents a mod screenshot.
/// </summary>
public class DownloadableModScreenshot
{
    [JsonPropertyName("fileid")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int FileId { get; set; }

    [JsonPropertyName("mainfile")]
    public string MainFile { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("thumbnailfile")]
    public string ThumbnailFile { get; set; } = string.Empty;

    [JsonPropertyName("createdat")]
    public string CreatedAt { get; set; } = string.Empty;
}
