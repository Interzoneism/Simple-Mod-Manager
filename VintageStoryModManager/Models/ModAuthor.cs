using System.Text.Json.Serialization;

namespace VintageStoryModManager.Models;

/// <summary>
/// Represents an author from the Vintage Story mod database.
/// </summary>
public class ModAuthor
{
    [JsonPropertyName("userid")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    public override string ToString() => Name;
}
