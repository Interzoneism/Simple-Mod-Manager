namespace VintageStoryModManager.Models;

/// <summary>
///     Serializable representation of configuration files that can be embedded
///     inside a PDF export alongside the modlist payload.
/// </summary>
public sealed class SerializableConfigList
{
    public List<SerializableModConfiguration>? Configurations { get; set; }
}

/// <summary>
///     Serializable representation of a single mod configuration file.
/// </summary>
public sealed class SerializableModConfiguration
{
    public string? ModId { get; set; }

    public string? FileName { get; set; }

    public string? Content { get; set; }
}