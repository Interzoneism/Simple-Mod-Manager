namespace VintageStoryModManager.Services;

public static class TargetGameVersionPreference
{
    public const string Latest = "__target_latest__";

    public static bool IsLatest(string? value) =>
        string.Equals(value, Latest, StringComparison.Ordinal);

    public static bool HasConcreteOverride(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !IsLatest(value);

    public static string? ResolveEffectiveGameVersion(string? detectedVersion, string? overrideVersion) =>
        IsLatest(overrideVersion)
            ? null
            : string.IsNullOrWhiteSpace(overrideVersion)
                ? detectedVersion
                : overrideVersion;
}