using System;
using System.IO;
using System.Security;

namespace VintageStoryModManager.Services;

/// <summary>
///     Handles migration of configuration and cache data from the old Documents location
///     to the new AppData/Local location.
/// </summary>
internal static class ConfigurationMigrationService
{
    /// <summary>
    ///     Gets the old (Documents-based) configuration directory path.
    /// </summary>
    public static string? GetOldConfigurationDirectory()
    {
        var documents = GetFolder(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents)) documents = GetFolder(Environment.SpecialFolder.Personal);

        if (string.IsNullOrWhiteSpace(documents)) return null;

        return Path.Combine(documents!, "Simple VS Manager");
    }

    /// <summary>
    ///     Gets the new (AppData/Local-based) configuration directory path.
    /// </summary>
    public static string? GetNewConfigurationDirectory()
    {
        var localAppData = GetFolder(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) return null;

        return Path.Combine(localAppData!, "Simple VS Manager");
    }

    /// <summary>
    ///     Checks if migration from old to new location is needed and possible.
    /// </summary>
    /// <param name="oldConfigVersion">The configuration version from the old location, if any.</param>
    /// <returns>True if migration should be offered to the user.</returns>
    public static bool ShouldOfferMigration(out string? oldConfigVersion)
    {
        oldConfigVersion = null;

        var oldDir = GetOldConfigurationDirectory();
        var newDir = GetNewConfigurationDirectory();

        if (string.IsNullOrWhiteSpace(oldDir) || string.IsNullOrWhiteSpace(newDir)) return false;

        // If old directory doesn't exist, no migration needed
        if (!Directory.Exists(oldDir)) return false;

        // Check if old configuration file exists
        var oldConfigPath = Path.Combine(oldDir!, DevConfig.ConfigurationFileName);
        if (!File.Exists(oldConfigPath)) return false;

        // Try to read the configuration version
        oldConfigVersion = TryReadConfigurationVersion(oldConfigPath);

        // If we can't read the version or it's > 1.4.0, don't migrate
        if (string.IsNullOrWhiteSpace(oldConfigVersion)) return false;

        // Check if version is <= 1.4.0
        if (!IsVersionEligibleForMigration(oldConfigVersion)) return false;

        return true;
    }

    /// <summary>
    ///     Performs the migration of all files from old to new location.
    /// </summary>
    /// <returns>True if migration was successful.</returns>
    public static bool PerformMigration()
    {
        var oldDir = GetOldConfigurationDirectory();
        var newDir = GetNewConfigurationDirectory();

        if (string.IsNullOrWhiteSpace(oldDir) || string.IsNullOrWhiteSpace(newDir)) return false;

        if (!Directory.Exists(oldDir!)) return false;

        try
        {
            // Create the new directory if it doesn't exist
            Directory.CreateDirectory(newDir!);

            // Copy all files and subdirectories
            CopyDirectory(oldDir!, newDir!, true);

            TryRefreshFirebaseBackup(newDir!);

            return true;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or DirectoryNotFoundException
                                       or PathTooLongException)
        {
            return false;
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir, bool recursive)
    {
        var dir = new DirectoryInfo(sourceDir);
        if (!dir.Exists) return;

        // Create destination directory
        Directory.CreateDirectory(destDir);

        // Copy files
        foreach (var file in dir.GetFiles())
        {
            var targetPath = Path.Combine(destDir, file.Name);
            // Don't overwrite if file already exists in destination
            if (!File.Exists(targetPath))
                try
                {
                    file.CopyTo(targetPath, false);
                }
                catch (IOException)
                {
                    // Skip files that fail to copy (might be locked or inaccessible)
                }
        }

        // Copy subdirectories if recursive
        if (recursive)
            foreach (var subDir in dir.GetDirectories())
            {
                var targetPath = Path.Combine(destDir, subDir.Name);
                CopyDirectory(subDir.FullName, targetPath, true);
            }
    }

    private static string? TryReadConfigurationVersion(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return null;

            var json = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(json)) return null;

            // Simple JSON parsing to extract configurationVersion
            // Look for "configurationVersion": "x.x.x"
            var versionIndex = json.IndexOf("\"configurationVersion\"", StringComparison.Ordinal);
            if (versionIndex < 0) return null;

            var colonIndex = json.IndexOf(':', versionIndex);
            if (colonIndex < 0) return null;

            var startQuoteIndex = json.IndexOf('"', colonIndex + 1);
            if (startQuoteIndex < 0) return null;

            var endQuoteIndex = json.IndexOf('"', startQuoteIndex + 1);
            if (endQuoteIndex < 0) return null;

            var version = json.Substring(startQuoteIndex + 1, endQuoteIndex - startQuoteIndex - 1);
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsVersionEligibleForMigration(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;

        try
        {
            // Parse version string (e.g., "1.4.0")
            var parts = version.Split('.');
            if (parts.Length < 2) return false;

            if (!int.TryParse(parts[0], out var major)) return false;

            if (!int.TryParse(parts[1], out var minor)) return false;

            // Check if version <= 1.4.0
            if (major < 1) return true;

            if (major == 1 && minor <= 4) return true;

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? GetFolder(Environment.SpecialFolder folder)
    {
        try
        {
            var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }

        return null;
    }

    private static void TryRefreshFirebaseBackup(string newConfigDirectory)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData)) return;

            var backupDirectory = Path.Combine(localAppData, DevConfig.FirebaseAuthBackupDirectoryName);
            Directory.CreateDirectory(backupDirectory);

            var existingBackupPath = Path.Combine(backupDirectory, DevConfig.FirebaseAuthStateFileName);
            var oldBackupPath = Path.Combine(backupDirectory, $"OLD_BACKUP_{DevConfig.FirebaseAuthStateFileName}");
            var newBackupPath = Path.Combine(backupDirectory, $"BACKUP_{DevConfig.FirebaseAuthStateFileName}");

            if (File.Exists(existingBackupPath)) File.Move(existingBackupPath, oldBackupPath, true);

            var migratedAuthPath = Path.Combine(newConfigDirectory, DevConfig.FirebaseAuthStateFileName);
            if (!File.Exists(migratedAuthPath)) return;

            File.Copy(migratedAuthPath, newBackupPath, true);
        }
        catch (IOException)
        {
            // Backup refresh failures are non-fatal to migration
        }
        catch (UnauthorizedAccessException)
        {
            // Backup refresh failures are non-fatal to migration
        }
        catch (NotSupportedException)
        {
            // Backup refresh failures are non-fatal to migration
        }
        catch (SecurityException)
        {
            // Backup refresh failures are non-fatal to migration
        }
    }
}