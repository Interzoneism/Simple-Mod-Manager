using System;
using System.IO;

namespace VintageStoryModManager;

/// <summary>
/// Central location for developer-tunable configuration values.
/// </summary>
public static class DevConfig
{
    // Application startup and general behaviour.
    public static string SingleInstanceMutexName { get; } = "VintageStoryModManager.SingleInstance";

    // View model tuning.
    public static int MaxConcurrentDatabaseRefreshes { get; } = 4;
    public static int MaxNewModsRecentMonths { get; } = 24;
    public static int InstalledModsIncrementalBatchSize { get; } = 32;
    public static int MaxModDatabaseResultLimit { get; } = int.MaxValue;

    // Main window behaviour and layout.
    public static double ModListScrollMultiplier { get; } = 0.5;
    public static double ModDbDesignScrollMultiplier { get; } = 20.0;
    public static double LoadMoreScrollThreshold { get; } = 0.98;
    public static double HoverOverlayOpacity { get; } = 0.1;
    public static double SelectionOverlayOpacity { get; } = 0.25;
    public static double ModInfoPanelHorizontalOverhang { get; } = 0;
    public static double DefaultModInfoPanelLeft { get; } = 1060.0;
    public static double DefaultModInfoPanelTop { get; } = 350.0;
    public static double DefaultModInfoPanelRightMargin { get; } = 40.0;
    public static string ManagerModDatabaseUrl { get; } = "https://mods.vintagestory.at/simplevsmanager";
    public static string ManagerModDatabaseModId { get; } = "5545";
    public static string ModDatabaseUnavailableMessage { get; } =
        "Could not reach the online Mod Database, please check your internet connection";
    public static string PresetDirectoryName { get; } = "Presets";
    public static string ModListDirectoryName { get; } = "Modlists";
    public static string CloudModListCacheDirectoryName { get; } = "Modlists (Cloud Cache)";
    public static string BackupDirectoryName { get; } = "Backups";
    public static int AutomaticConfigMaxWordDistance { get; } = 2;

    // Mod metadata cache.
    public static string MetadataFolderName { get; } = "Mod Metadata";
    public static string MetadataIndexFileName { get; } = "metadata-index.json";

    // Firebase mod list storage.
    public static string FirebaseModlistDefaultDbUrl { get; } =
        "https://simple-vs-manager-default-rtdb.europe-west1.firebasedatabase.app";

    // Cloud/Firebase backups location (AppData/Local/SVSM Backup/)
    public static string FirebaseBackupDirectory
    {
        get
        {
            string? local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
            {
                local = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }

            if (string.IsNullOrWhiteSpace(local))
            {
                return Path.Combine(AppContext.BaseDirectory, "SVSM Backup");
            }

            return Path.Combine(local, "SVSM Backup");
        }
    }

    // Status log display.
    public static string StatusTimestampFormat { get; } = "yyyy-MM-dd HH:mm:ss.fff";
    public static int StatusLogMaxModNameLength { get; } = 10;
    public static int StatusLogMaxLines { get; } = 5000;

    // Mod compatibility comments service.
    public static string ModCompatibilityApiUrlTemplate { get; } = "https://mods.vintagestory.at/api/mod/{0}";
    public static string ModCompatibilityPageUrlTemplate { get; } = "https://mods.vintagestory.at/{0}";

    // Firebase anonymous authentication.
    public static string FirebaseSignInEndpoint { get; } = "https://identitytoolkit.googleapis.com/v1/accounts:signUp";
    public static string FirebaseRefreshEndpoint { get; } = "https://securetoken.googleapis.com/v1/token";
    public static string FirebaseDeleteEndpoint { get; } = "https://identitytoolkit.googleapis.com/v1/accounts:delete";
    public static string FirebaseAuthStateFileName { get; } = "firebase-auth.json";
    public static string FirebaseDefaultApiKey { get; } = "AIzaSyCmDJ9yC1ccUEUf41fC-SI8fuXFJzWWlHY";
    public static string FirebaseAuthBackupDirectoryName { get; } = "SVSM Backup";

    // User configuration defaults.
    public static string ConfigurationFileName { get; } = "SimpleVSManagerConfiguration.json";
    public static string ModConfigPathsFileName { get; } = "SimpleVSManagerModConfigPaths.json";
    public static int DefaultModDatabaseSearchResultLimit { get; } = 30;
    public static int DefaultModDatabaseNewModsRecentMonths { get; } = 3;
    public static int MaxModDatabaseNewModsRecentMonths { get; } = 24;
    public static int GameSessionVoteThreshold { get; } = 5;

    // Game session monitoring.
    public static TimeSpan MinimumSessionDuration { get; } = TimeSpan.FromMinutes(30);

    // Vintage Story game version metadata.
    public static string GameVersionsEndpoint { get; } = "https://mods.vintagestory.at/api/gameversions";

    // Mod database caching.
    public static int ModDatabaseCacheSchemaVersion { get; } = 2;
    public static int ModDatabaseMinimumSupportedCacheSchemaVersion { get; } = 1;
    public static string ModDatabaseAnyGameVersionToken { get; } = "any";

    // Mod discovery.
    public static int ModDiscoveryBatchSize { get; } = 16;
    public static string ModDiscoveryGeneralLoadErrorMessage { get; } = "Unable to load mod. Check log files.";
    public static string ModDiscoveryDependencyErrorMessage { get; } =
        "Unable to load mod. A dependency has an error. Make sure they all load correctly.";

    // Data directory resolution.
    public static string DataFolderName { get; } = "VintagestoryData";

    // Compatibility vote storage.
    public static string ModVersionVoteDefaultDbUrl { get; } =
        "https://simple-vs-manager-default-rtdb.europe-west1.firebasedatabase.app";
    public static string ModVersionVoteRootPath { get; } = "compatVotes";

    // Internet access restrictions messaging.
    public static string InternetAccessDisabledMessage { get; } =
        "Internet access is disabled for the mod database and compatibility voting.";

    // Mod database service endpoints and limits.
    public static string ModDatabaseApiEndpointFormat { get; } = "https://mods.vintagestory.at/api/mod/{0}";
    public static string ModDatabaseSearchEndpointFormat { get; } = "https://mods.vintagestory.at/api/mods?search={0}&limit={1}";
    public static string ModDatabaseMostDownloadedEndpointFormat { get; } =
        "https://mods.vintagestory.at/api/mods?sort=downloadsdesc&limit={0}";
    public static string ModDatabaseRecentlyCreatedEndpointFormat { get; } =
        "https://mods.vintagestory.at/api/mods?sortby=created&sortdir=d&limit={0}";
    public static string ModDatabaseRecentlyUpdatedEndpointFormat { get; } =
        "https://mods.vintagestory.at/api/mods?sortby=updated&sortdir=d&limit={0}";
    public static string ModDatabasePageBaseUrl { get; } = "https://mods.vintagestory.at/show/mod/";
    public static int ModDatabaseMaxConcurrentMetadataRequests { get; } = 4;
    public static int ModDatabaseMinimumTotalDownloadsForTrending { get; } = 500;
    public static int ModDatabaseDefaultNewModsMonths { get; } = 3;
    public static int ModDatabaseMaxNewModsMonths { get; } = 24;
    public static double ModDatabaseMinimumIntervalDays { get; } = 1d / 24d;
}
