namespace VintageStoryModManager.Models;

/// <summary>
/// Represents the saved state for a single mod inside an advanced preset.
/// </summary>
/// <param name="ModId">The unique identifier of the mod.</param>
/// <param name="Version">The saved version string for the mod, if available.</param>
/// <param name="IsActive">Whether the mod was active when the preset was saved. A null value indicates the preset did not record the state.</param>
public sealed record ModPresetModState(string ModId, string? Version, bool? IsActive);
