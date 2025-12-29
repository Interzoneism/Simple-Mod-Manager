using System.Text.Json.Serialization;

namespace VintageStoryModManager.Models;

/// <summary>
/// Represents a tag from the Vintage Story mod database.
/// </summary>
public class ModTag
{
    [JsonPropertyName("tagid")]
    public int TagId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("color")]
    public string Color { get; set; } = string.Empty;

    public override string ToString() => Name;
}
