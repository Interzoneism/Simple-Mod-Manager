namespace VintageStoryModManager.ViewModels;

/// <summary>
///     Represents the outcome of attempting to change a mod's activation state.
/// </summary>
public readonly record struct ActivationResult(bool Success, string? ErrorMessage);

/// <summary>
///     Describes which metric to use when automatically loading mods from the database.
/// </summary>
public enum ModDatabaseAutoLoadMode
{
    TotalDownloads,
    DownloadsLastThirtyDays,
    DownloadsLastTenDays,
    DownloadsNewModsRecentMonths,
    RecentlyUpdated,
    RecentlyAdded,
    MostTrending,
    AddedLast30Days,
    Random
}
