using System.Globalization;

namespace VintageStoryModManager.Services;

/// <summary>
///     Represents a unique identifier for tracking mod usage across game versions.
///     Used as a dictionary key for mod usage statistics and compatibility tracking.
/// </summary>
public readonly struct ModUsageTrackingKey : IEquatable<ModUsageTrackingKey>
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ModUsageTrackingKey"/> struct.
    /// </summary>
    /// <param name="modId">The unique identifier of the mod.</param>
    /// <param name="modVersion">The version of the mod.</param>
    /// <param name="gameVersion">The game version.</param>
    public ModUsageTrackingKey(string modId, string modVersion, string gameVersion)
    {
        ModId = NormalizeValue(modId);
        ModVersion = NormalizeValue(modVersion);
        GameVersion = NormalizeValue(gameVersion);
    }

    /// <summary>
    ///     Gets the unique identifier of the mod.
    /// </summary>
    public string ModId { get; }

    /// <summary>
    ///     Gets the version of the mod.
    /// </summary>
    public string ModVersion { get; }

    /// <summary>
    ///     Gets the game version.
    /// </summary>
    public string GameVersion { get; }

    /// <summary>
    ///     Gets a value indicating whether this key has all required fields populated.
    /// </summary>
    public bool IsValid => !string.IsNullOrEmpty(ModId)
                           && !string.IsNullOrEmpty(ModVersion)
                           && !string.IsNullOrEmpty(GameVersion);

    /// <inheritdoc/>
    public bool Equals(ModUsageTrackingKey other)
    {
        return string.Equals(ModId, other.ModId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(ModVersion, other.ModVersion, StringComparison.OrdinalIgnoreCase)
               && string.Equals(GameVersion, other.GameVersion, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is ModUsageTrackingKey other && Equals(other);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(ModId),
            StringComparer.OrdinalIgnoreCase.GetHashCode(ModVersion),
            StringComparer.OrdinalIgnoreCase.GetHashCode(GameVersion));
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} | {1} | {2}",
            ModId,
            ModVersion,
            GameVersion);
    }

    /// <summary>
    ///     Normalizes a string value by trimming whitespace.
    /// </summary>
    /// <param name="value">The value to normalize.</param>
    /// <returns>The normalized string, or empty string if the input is null or whitespace.</returns>
    private static string NormalizeValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}

/// <summary>
///     Represents a mod usage tracking entry for collecting user feedback on mod compatibility.
/// </summary>
public readonly struct ModUsageTrackingEntry
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ModUsageTrackingEntry"/> struct.
    /// </summary>
    /// <param name="modId">The unique identifier of the mod.</param>
    /// <param name="modVersion">The version of the mod.</param>
    /// <param name="gameVersion">The game version the mod was used with.</param>
    /// <param name="canSubmitVote">Whether the user can submit a compatibility vote for this mod.</param>
    /// <param name="hasUserVote">Whether the user has already voted on this mod's compatibility.</param>
    public ModUsageTrackingEntry(
        string? modId,
        string? modVersion,
        string? gameVersion,
        bool canSubmitVote,
        bool hasUserVote)
    {
        ModId = string.IsNullOrWhiteSpace(modId) ? string.Empty : modId.Trim();
        ModVersion = string.IsNullOrWhiteSpace(modVersion) ? string.Empty : modVersion.Trim();
        GameVersion = string.IsNullOrWhiteSpace(gameVersion) ? string.Empty : gameVersion.Trim();
        CanSubmitVote = canSubmitVote;
        HasUserVote = hasUserVote;
    }

    /// <summary>
    ///     Gets the unique identifier of the mod.
    /// </summary>
    public string ModId { get; }

    /// <summary>
    ///     Gets the version of the mod.
    /// </summary>
    public string ModVersion { get; }

    /// <summary>
    ///     Gets the game version the mod was used with.
    /// </summary>
    public string GameVersion { get; }

    /// <summary>
    ///     Gets a value indicating whether the user can submit a compatibility vote for this mod.
    /// </summary>
    public bool CanSubmitVote { get; }

    /// <summary>
    ///     Gets a value indicating whether the user has already voted on this mod's compatibility.
    /// </summary>
    public bool HasUserVote { get; }
}
