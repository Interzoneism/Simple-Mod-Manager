namespace VintageStoryModManager.Models;

/// <summary>
///     Represents a captured configuration file for a mod, including its file name and content.
/// </summary>
/// <param name="FileName">The name of the configuration file.</param>
/// <param name="Content">The contents of the configuration file.</param>
/// <param name="RelativePath">The relative path where the configuration file should be placed (optional).</param>
public sealed record ModConfigurationSnapshot(string FileName, string Content, string? RelativePath = null);

/// <summary>
///     Represents the saved state for a single mod inside an advanced preset.
/// </summary>
/// <param name="ModId">The unique identifier of the mod.</param>
/// <param name="Version">The saved version string for the mod, if available.</param>
/// <param name="IsActive">
///     Whether the mod was active when the preset was saved. A null value indicates the preset did not
///     record the state.
/// </param>
/// <param name="ConfigurationFileName">The original file name of the configuration file that was captured, if any.</param>
/// <param name="ConfigurationContent">The contents of the configuration file that was captured, if any.</param>
/// <param name="Configurations">Additional configuration files captured for the mod, if any.</param>
public sealed record ModPresetModState(
    string ModId,
    string? Version,
    bool? IsActive,
    string? ConfigurationFileName,
    string? ConfigurationContent,
    IReadOnlyList<ModConfigurationSnapshot>? Configurations = null);

/// <summary>
///     Represents a saved set of mod activation states.
/// </summary>
/// <param name="Name">Display name for the preset.</param>
/// <param name="DisabledEntries">Collection of disabled mod identifiers stored in clientsettings.json.</param>
/// <param name="ModStates">Optional snapshot of per-mod state captured when the preset was saved.</param>
/// <param name="IncludesModStatus">Indicates whether the preset recorded activation state for mods.</param>
/// <param name="IncludesModVersions">Indicates whether the preset recorded specific mod versions.</param>
/// <param name="IsExclusive">Indicates whether loading the preset should remove mods that were not saved.</param>
public sealed record ModPreset(
    string Name,
    IReadOnlyList<string> DisabledEntries,
    IReadOnlyList<ModPresetModState> ModStates,
    bool IncludesModStatus,
    bool IncludesModVersions,
    bool IsExclusive)
{
    /// <summary>
    ///     Gets a value indicating whether the preset contains any recorded mod states.
    /// </summary>
    public bool HasModStates => ModStates.Count > 0;
}
