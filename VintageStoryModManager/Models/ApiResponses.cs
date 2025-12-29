using System.Text.Json.Serialization;

namespace VintageStoryModManager.Models;

/// <summary>
/// API response wrapper for mod list query.
/// </summary>
public class ModListResponse
{
    [JsonPropertyName("mods")]
    public List<DownloadableModOnList> Mods { get; set; } = [];
}

/// <summary>
/// API response wrapper for single mod query.
/// </summary>
public class ModResponse
{
    [JsonPropertyName("statuscode")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int StatusCode { get; set; }

    [JsonPropertyName("mod")]
    public DownloadableMod? Mod { get; set; }
}

/// <summary>
/// API response wrapper for authors query.
/// </summary>
public class AuthorsResponse
{
    [JsonPropertyName("authors")]
    public List<ModAuthor> Authors { get; set; } = [];
}

/// <summary>
/// API response wrapper for game versions query.
/// </summary>
public class GameVersionsResponse
{
    [JsonPropertyName("gameversions")]
    public List<GameVersion> GameVersions { get; set; } = [];
}

/// <summary>
/// API response wrapper for tags query.
/// </summary>
public class TagsResponse
{
    [JsonPropertyName("tags")]
    public List<ModTag> Tags { get; set; } = [];
}
