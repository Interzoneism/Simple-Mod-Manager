namespace VintageStoryModManager.ViewModels;

/// <summary>
/// Describes which metric to use when automatically loading mods from the database.
/// </summary>
public enum ModDatabaseAutoLoadMode
{
    TotalDownloads,
    DownloadsLastThirtyDays,
    DownloadsNewModsRecentMonths
}
