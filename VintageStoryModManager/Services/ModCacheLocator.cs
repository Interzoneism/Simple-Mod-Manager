using System.IO;
using System.Text;

namespace VintageStoryModManager.Services;

/// <summary>
///     Provides helpers for locating and naming cached mod archives.
/// </summary>
internal static class ModCacheLocator
{
    /// <summary>
    ///     Gets the manager's data directory in the user's local or application data folder.
    ///     If a custom folder has been configured, returns that instead.
    /// </summary>
    /// <returns>The full path to the manager's data directory, or null if it cannot be determined.</returns>
    public static string? GetManagerDataDirectory()
    {
        // Check for custom configuration folder first
        var customFolder = CustomConfigFolderManager.GetCustomConfigFolder();
        if (!string.IsNullOrWhiteSpace(customFolder))
            return customFolder;

        return GetDefaultManagerDataDirectory();
    }

    /// <summary>
    ///     Gets the default manager data directory location without considering custom configuration.
    ///     This returns the default location in AppData\Local or fallback locations.
    /// </summary>
    /// <returns>The full path to the default manager's data directory, or null if it cannot be determined.</returns>
    public static string? GetDefaultManagerDataDirectory()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(path))
            path = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrWhiteSpace(path)) path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (string.IsNullOrWhiteSpace(path)) path = Environment.GetFolderPath(Environment.SpecialFolder.Personal);

        return string.IsNullOrWhiteSpace(path)
            ? null
            : Path.Combine(path, "Simple VS Manager");
    }

    /// <summary>
    ///     Gets the directory where all cached mods are stored.
    /// </summary>
    /// <returns>The full path to the cached mods directory, or null if it cannot be determined.</returns>
    public static string? GetCachedModsDirectory()
    {
        var managerDirectory = GetManagerDataDirectory();
        return managerDirectory is null
            ? null
            : Path.Combine(managerDirectory, "Temp Cache", "Cached Mods");
    }

    /// <summary>
    ///     Gets the cache directory for a specific mod.
    /// </summary>
    /// <param name="modId">The unique identifier of the mod.</param>
    /// <returns>The full path to the mod's cache directory, or null if it cannot be determined.</returns>
    public static string? GetModCacheDirectory(string modId)
    {
        var cachedModsDirectory = GetCachedModsDirectory();
        if (string.IsNullOrWhiteSpace(cachedModsDirectory)) return null;

        var directoryName = SanitizeFileName(modId, "mod");
        return Path.Combine(cachedModsDirectory, directoryName);
    }

    /// <summary>
    ///     Gets the directory where the mod database cache is stored.
    /// </summary>
    /// <returns>The full path to the mod database cache directory, or null if it cannot be determined.</returns>
    public static string? GetModDatabaseCacheDirectory()
    {
        var managerDirectory = GetManagerDataDirectory();
        return managerDirectory is null
            ? null
            : Path.Combine(managerDirectory, "Temp Cache", "Mod Database Cache");
    }

    /// <summary>
    ///     Gets the directory where mod database images are cached.
    /// </summary>
    /// <returns>The full path to the mod database image cache directory, or null if it cannot be determined.</returns>
    public static string? GetModDatabaseImageCacheDirectory()
    {
        var databaseCacheDirectory = GetModDatabaseCacheDirectory();
        return databaseCacheDirectory is null
            ? null
            : Path.Combine(databaseCacheDirectory, "Images");
    }

    /// <summary>
    ///     Gets the full cache path for a specific version of a mod.
    /// </summary>
    /// <param name="modId">The unique identifier of the mod.</param>
    /// <param name="version">The version of the mod.</param>
    /// <param name="fileName">The original file name of the mod (optional).</param>
    /// <returns>The full path where the cached mod file should be stored, or null if the path cannot be determined.</returns>
    public static string? GetModCachePath(string modId, string? version, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var modCacheDirectory = GetModCacheDirectory(modId);
        if (string.IsNullOrWhiteSpace(modCacheDirectory)) return null;

        var versionSegment = SanitizeFileName(version!, "version");
        var versionDirectory = Path.Combine(modCacheDirectory, versionSegment);
        var cacheFileName = SanitizeCacheFileName(fileName, modId, versionSegment);

        return Path.Combine(versionDirectory, cacheFileName);
    }

    /// <summary>
    ///     Attempts to locate a cached mod file by searching the mod's cache directory.
    ///     First checks the preferred location, then falls back to any file in the version directory,
    ///     and finally checks for legacy cache files.
    /// </summary>
    /// <param name="modId">The unique identifier of the mod.</param>
    /// <param name="version">The version of the mod.</param>
    /// <param name="fileName">The expected file name.</param>
    /// <param name="cacheFilePath">When this method returns, contains the path to the found cache file, or null if not found.</param>
    /// <returns>True if a cache file was found; otherwise, false.</returns>
    public static bool TryLocateCachedModFile(string modId, string? version, string? fileName,
        out string? cacheFilePath)
    {
        cacheFilePath = null;
        if (string.IsNullOrWhiteSpace(version)) return false;

        var modCacheDirectory = GetModCacheDirectory(modId);
        if (string.IsNullOrWhiteSpace(modCacheDirectory)) return false;

        var versionSegment = SanitizeFileName(version!, "version");
        var versionDirectory = Path.Combine(modCacheDirectory, versionSegment);
        var preferredFileName = SanitizeCacheFileName(fileName, modId, versionSegment);
        var preferredPath = Path.Combine(versionDirectory, preferredFileName);

        if (File.Exists(preferredPath))
        {
            cacheFilePath = preferredPath;
            return true;
        }

        if (Directory.Exists(versionDirectory))
            try
            {
                foreach (var file in Directory.EnumerateFiles(versionDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    cacheFilePath = file;
                    return true;
                }
            }
            catch (Exception)
            {
                // Ignore enumeration failures.
            }

        var legacyPath = GetLegacyModCachePath(modCacheDirectory, versionSegment, fileName);
        if (legacyPath is not null && File.Exists(legacyPath))
        {
            cacheFilePath = legacyPath;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Attempts to move a legacy cache file to the new cache structure.
    /// </summary>
    /// <param name="modId">The unique identifier of the mod.</param>
    /// <param name="version">The version of the mod.</param>
    /// <param name="fileName">The file name.</param>
    /// <param name="targetPath">The target path for the promoted file.</param>
    /// <returns>True if the legacy file was successfully promoted or already exists at the target; otherwise, false.</returns>
    public static bool TryPromoteLegacyCacheFile(string modId, string? version, string? fileName, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;

        var modCacheDirectory = GetModCacheDirectory(modId);
        if (string.IsNullOrWhiteSpace(modCacheDirectory)) return false;

        var versionSegment = SanitizeFileName(version!, "version");
        var legacyPath = GetLegacyModCachePath(modCacheDirectory, versionSegment, fileName);
        if (legacyPath is null || !File.Exists(legacyPath)) return false;

        if (string.Equals(legacyPath, targetPath, StringComparison.OrdinalIgnoreCase)) return true;

        try
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            File.Move(legacyPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Enumerates all cached files for a specific mod across all versions.
    /// </summary>
    /// <param name="modId">The unique identifier of the mod.</param>
    /// <returns>An enumerable collection of file paths for all cached versions of the mod.</returns>
    public static IEnumerable<string> EnumerateCachedFiles(string modId)
    {
        var cacheDirectory = GetModCacheDirectory(modId);
        if (string.IsNullOrWhiteSpace(cacheDirectory)) yield break;

        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> versionDirectories;
        try
        {
            versionDirectories = Directory.EnumerateDirectories(cacheDirectory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception)
        {
            versionDirectories = Array.Empty<string>();
        }

        foreach (var versionDirectory in versionDirectories)
        {
            IEnumerable<string> versionFiles;
            try
            {
                versionFiles = Directory.EnumerateFiles(versionDirectory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var file in versionFiles)
                if (yielded.Add(file))
                    yield return file;
        }

        IEnumerable<string> legacyFiles;
        try
        {
            legacyFiles = Directory.EnumerateFiles(cacheDirectory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var file in legacyFiles)
            if (yielded.Add(file))
                yield return file;
    }

    /// <summary>
    ///     Sanitizes a string to make it safe for use as a file or directory name.
    ///     Replaces invalid characters with underscores.
    /// </summary>
    /// <param name="input">The string to sanitize.</param>
    /// <param name="fallback">The fallback value to use if the input is null, empty, or becomes empty after sanitization.</param>
    /// <returns>A sanitized file name that is safe to use in the file system.</returns>
    public static string SanitizeFileName(string? input, string fallback)
    {
        if (string.IsNullOrWhiteSpace(input)) return fallback;

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(input.Length);
        foreach (var c in input)
            if (Array.IndexOf(invalidChars, c) >= 0)
                builder.Append('_');
            else
                builder.Append(c);

        var sanitized = builder.ToString().Trim();
        return string.IsNullOrEmpty(sanitized) ? fallback : sanitized;
    }

    /// <summary>
    ///     Sanitizes and normalizes a cache file name, adding .zip extension if needed.
    /// </summary>
    private static string SanitizeCacheFileName(string? fileName, string modId, string versionSegment)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var sanitized = ReplaceInvalidCacheFileCharacters(fileName);
            if (!string.IsNullOrWhiteSpace(sanitized))
            {
                if (string.IsNullOrWhiteSpace(Path.GetExtension(sanitized))) sanitized += ".zip";

                return sanitized;
            }
        }

        return BuildFallbackFileName(modId, versionSegment);
    }

    /// <summary>
    ///     Builds a fallback file name from the mod ID and version when the original name is unavailable.
    /// </summary>
    private static string BuildFallbackFileName(string modId, string versionSegment)
    {
        var safeModId = SanitizeFileName(modId, "mod");
        return string.Concat(safeModId, '-', versionSegment, ".zip");
    }

    /// <summary>
    ///     Replaces invalid file name characters with underscores and trims trailing dots.
    /// </summary>
    private static string ReplaceInvalidCacheFileCharacters(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim()) builder.Append(Array.IndexOf(invalidChars, c) >= 0 ? '_' : c);

        var sanitized = builder.ToString().TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? string.Empty : sanitized;
    }

    /// <summary>
    ///     Gets the legacy cache path format used in older versions of the manager.
    /// </summary>
    private static string? GetLegacyModCachePath(string modCacheDirectory, string versionSegment, string? fileName)
    {
        var extension = string.IsNullOrWhiteSpace(fileName) ? ".zip" : Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".zip";

        return Path.Combine(modCacheDirectory, versionSegment + extension);
    }
}