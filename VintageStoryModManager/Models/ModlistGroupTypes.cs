namespace VintageStoryModManager.Models;

/// <summary>
/// Persisted metadata for a local modlist group. Members are references to the
/// original modlist files; mod contents are intentionally never copied here.
/// </summary>
public sealed class ModlistGroupDefinition
{
    public int SchemaVersion { get; set; } = 1;

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Version { get; set; }

    public string? GameVersion { get; set; }

    public List<string> Members { get; set; } = new();

    public Dictionary<string, string> ConflictResolutions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ModlistGroupSource(
    string Reference,
    string FilePath,
    string DisplayName,
    SerializablePreset Preset);

public sealed record ModlistGroupMemberListEntry(
    string GroupFilePath,
    string GroupName,
    string MemberReference,
    string? SourceFilePath,
    string DisplayName,
    bool IsMissing)
{
    public bool IsAvailable => !IsMissing && !string.IsNullOrWhiteSpace(SourceFilePath);

    public string DisplayNameWithStatus => IsMissing ? $"{DisplayName} (Missing)" : DisplayName;
}

public sealed record ModlistGroupConflictChoice(
    string MemberReference,
    string FilePath,
    string ModlistName,
    string? Version,
    bool HasCustomConfiguration)
{
    public string VersionDisplay => string.IsNullOrWhiteSpace(Version) ? "Version not specified" : $"Version {Version}";

    public string ConfigurationDisplay => HasCustomConfiguration ? "Custom config included" : "No custom config";

    public string DisplayText => $"{ModlistName} — {VersionDisplay} — {ConfigurationDisplay}";
}

public sealed record ModlistGroupConflict(
    string ModId,
    IReadOnlyList<ModlistGroupConflictChoice> Choices,
    string? SelectedMemberReference)
{
    public bool IsResolved => !string.IsNullOrWhiteSpace(SelectedMemberReference)
                              && Choices.Any(choice => string.Equals(
                                  choice.MemberReference,
                                  SelectedMemberReference,
                                  StringComparison.OrdinalIgnoreCase));
}

public sealed record ModlistGroupBuildResult(
    SerializablePreset MergedPreset,
    IReadOnlyList<ModlistGroupConflict> Conflicts,
    IReadOnlyList<string> MissingMembers);
