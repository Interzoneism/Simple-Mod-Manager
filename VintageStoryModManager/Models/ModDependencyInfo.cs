using System;

namespace VintageStoryModManager.Models;

/// <summary>
/// Represents a dependency declared in a modinfo.json file.
/// </summary>
public sealed record ModDependencyInfo(string ModId, string Version)
{
    public string Display => string.IsNullOrWhiteSpace(Version)
        ? ModId
        : $"{ModId} (≥ {Version})";

    public bool IsGameOrCoreDependency => string.Equals(ModId, "game", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ModId, "creative", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ModId, "survival", StringComparison.OrdinalIgnoreCase);
}
