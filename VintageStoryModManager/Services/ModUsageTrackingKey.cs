using System.Globalization;

namespace VintageStoryModManager.Services;

public readonly struct ModUsageTrackingKey : IEquatable<ModUsageTrackingKey>
{
    public ModUsageTrackingKey(string modId, string modVersion, string gameVersion)
    {
        ModId = Normalize(modId);
        ModVersion = Normalize(modVersion);
        GameVersion = Normalize(gameVersion);
    }

    public string ModId { get; }

    public string ModVersion { get; }

    public string GameVersion { get; }

    public bool IsValid => !string.IsNullOrEmpty(ModId)
                           && !string.IsNullOrEmpty(ModVersion)
                           && !string.IsNullOrEmpty(GameVersion);

    public bool Equals(ModUsageTrackingKey other)
    {
        return string.Equals(ModId, other.ModId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(ModVersion, other.ModVersion, StringComparison.OrdinalIgnoreCase)
               && string.Equals(GameVersion, other.GameVersion, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj)
    {
        return obj is ModUsageTrackingKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(ModId),
            StringComparer.OrdinalIgnoreCase.GetHashCode(ModVersion),
            StringComparer.OrdinalIgnoreCase.GetHashCode(GameVersion));
    }

    public override string ToString()
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} | {1} | {2}",
            ModId,
            ModVersion,
            GameVersion);
    }

    private static string Normalize(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}