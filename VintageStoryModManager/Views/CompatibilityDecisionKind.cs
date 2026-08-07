namespace VintageStoryModManager.Views;

/// <summary>
///     Defines the user's decision when prompted about mod compatibility during updates.
/// </summary>
internal enum CompatibilityDecisionKind
{
    /// <summary>
    ///     Update to the latest version regardless of compatibility.
    /// </summary>
    Latest,

    /// <summary>
    ///     Update all mods to their latest versions regardless of compatibility.
    /// </summary>
    LatestForAll,

    /// <summary>
    ///     Update to the latest compatible version.
    /// </summary>
    LatestCompatible,

    /// <summary>
    ///     Update all mods to their latest compatible versions.
    /// </summary>
    LatestCompatibleForAll,

    /// <summary>
    ///     Skip updating this mod.
    /// </summary>
    Skip,

    /// <summary>
    ///     Abort the entire update process.
    /// </summary>
    Abort
}