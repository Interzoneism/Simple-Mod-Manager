namespace VintageStoryModManager.Models;

/// <summary>
///     Serializable representation of a <see cref="ModPreset" /> used for persisting
///     modlists to JSON or embedding them into PDF exports.
/// </summary>
public sealed class SerializablePreset
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Version { get; set; }

    public string? GameVersion { get; set; }

    public string? Uploader { get; set; }

    public List<string>? DisabledEntries { get; set; }

    public List<SerializablePresetModState>? Mods { get; set; }

    public List<SerializableModConfiguration>? Configurations { get; set; }

    public bool? IncludeModStatus { get; set; }

    public bool? IncludeModVersions { get; set; }

    public bool? Exclusive { get; set; }
}

/// <summary>
///     Serializable representation of <see cref="ModPresetModState" />.
/// </summary>
public sealed class SerializablePresetModState
{
    public string? ModId { get; set; }

    public string? Version { get; set; }

    public bool? IsActive { get; set; }

    public string? ConfigurationFileName { get; set; }

    public string? ConfigurationContent { get; set; }
}

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

    public string? RelativePath { get; set; }

    public string? Content { get; set; }
}
