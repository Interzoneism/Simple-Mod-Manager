using VintageStoryModManager.Models;

namespace VintageStoryModManager.Services;

public static class TargetGameVersionReleaseSelector
{
    public static ModReleaseInfo? SelectLatestCompatibleRelease(
        IEnumerable<ModReleaseInfo?>? releases,
        string? targetGameVersion,
        bool requireExactVersionMatch)
    {
        if (releases is null) return null;

        var normalizedTarget = VersionStringUtility.Normalize(targetGameVersion);
        foreach (var release in releases)
        {
            if (release is null) continue;

            if (string.IsNullOrWhiteSpace(normalizedTarget))
                return release;

            if (ReleaseSupportsTarget(release, normalizedTarget, requireExactVersionMatch))
                return release;
        }

        return null;
    }

    public static bool ReleaseSupportsTarget(
        ModReleaseInfo release,
        string? targetGameVersion,
        bool requireExactVersionMatch)
    {
        ArgumentNullException.ThrowIfNull(release);

        var normalizedTarget = VersionStringUtility.Normalize(targetGameVersion);
        if (string.IsNullOrWhiteSpace(normalizedTarget))
            return true;

        return TagsSupportTarget(release.GameVersionTags, normalizedTarget, requireExactVersionMatch);
    }

    public static bool TagsSupportTarget(
        IReadOnlyList<string>? gameVersionTags,
        string? targetGameVersion,
        bool requireExactVersionMatch)
    {
        var normalizedTarget = VersionStringUtility.Normalize(targetGameVersion);
        if (string.IsNullOrWhiteSpace(normalizedTarget))
            return true;

        if (gameVersionTags is not { Count: > 0 })
            return false;

        foreach (var tag in gameVersionTags)
            if (VersionStringUtility.SupportsVersion(tag, normalizedTarget, requireExactVersionMatch))
                return true;

        return false;
    }
}
