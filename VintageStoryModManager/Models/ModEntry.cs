using System;
using System.Collections.Generic;

namespace VintageStoryModManager.Models;

/// <summary>
/// Represents the metadata for an installed mod along with its source location.
/// </summary>
public sealed class ModEntry
{
    public required string ModId { get; init; }

    public required string Name { get; init; }

    public string? Version { get; init; }

    public string? NetworkVersion { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string> Authors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Contributors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ModDependencyInfo> Dependencies { get; init; } = Array.Empty<ModDependencyInfo>();

    public string? Website { get; init; }

    public string SourcePath { get; init; } = string.Empty;

    public ModSourceKind SourceKind { get; init; }

    public byte[]? IconBytes { get; init; }

    public string? IconDescription { get; init; }

    public string? Error { get; init; }

    public bool HasErrors => !string.IsNullOrWhiteSpace(Error);

    public string? LoadError { get; set; }

    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadError);

    public IReadOnlyList<ModDependencyInfo> MissingDependencies { get; set; } = Array.Empty<ModDependencyInfo>();

    public bool DependencyHasErrors { get; set; }

    public bool HasDependencyIssues => DependencyHasErrors || MissingDependencies.Count > 0;

    public string? Side { get; init; }

    public bool? RequiredOnClient { get; init; }

    public bool? RequiredOnServer { get; init; }

    public ModDatabaseInfo? DatabaseInfo { get; set; }

    public double? ModDatabaseSearchScore { get; set; }

    public void UpdateDatabaseInfo(ModDatabaseInfo? info)
    {
        DatabaseInfo = info;
    }

    public override string ToString() => $"{Name} ({ModId})";
}

public enum ModSourceKind
{
    Folder,
    ZipArchive,
    Assembly,
    SourceCode
}
