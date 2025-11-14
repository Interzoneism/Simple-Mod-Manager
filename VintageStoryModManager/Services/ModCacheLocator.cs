using System.IO;
using System.Text;

namespace VintageStoryModManager.Services;

/// <summary>
///     Provides helpers for locating and naming cached mod archives.
/// </summary>
internal static class ModCacheLocator
{
    public static string? GetManagerDataDirectory()
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

    public static string? GetCachedModsDirectory()
    {
        var managerDirectory = GetManagerDataDirectory();
        return managerDirectory is null
            ? null
            : Path.Combine(managerDirectory, "Cached Mods");
    }

    public static string? GetModCacheDirectory(string modId)
    {
        var cachedModsDirectory = GetCachedModsDirectory();
        if (string.IsNullOrWhiteSpace(cachedModsDirectory)) return null;

        var directoryName = SanitizeFileName(modId, "mod");
        return Path.Combine(cachedModsDirectory, directoryName);
    }

    public static string? GetModDatabaseCacheDirectory()
    {
        var managerDirectory = GetManagerDataDirectory();
        return managerDirectory is null
            ? null
            : Path.Combine(managerDirectory, "Mod Database Cache");
    }

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

    private static string BuildFallbackFileName(string modId, string versionSegment)
    {
        var safeModId = SanitizeFileName(modId, "mod");
        return string.Concat(safeModId, '-', versionSegment, ".zip");
    }

    private static string ReplaceInvalidCacheFileCharacters(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim()) builder.Append(Array.IndexOf(invalidChars, c) >= 0 ? '_' : c);

        var sanitized = builder.ToString().TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? string.Empty : sanitized;
    }

    private static string? GetLegacyModCachePath(string modCacheDirectory, string versionSegment, string? fileName)
    {
        var extension = string.IsNullOrWhiteSpace(fileName) ? ".zip" : Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".zip";

        return Path.Combine(modCacheDirectory, versionSegment + extension);
    }
}