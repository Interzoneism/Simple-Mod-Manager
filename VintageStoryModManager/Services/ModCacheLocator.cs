using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VintageStoryModManager.Services;

/// <summary>
/// Provides helpers for locating and naming cached mod archives.
/// </summary>
internal static class ModCacheLocator
{
    public static string? GetManagerDataDirectory()
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        }

        return string.IsNullOrWhiteSpace(path)
            ? null
            : Path.Combine(path, "Simple VS Manager");
    }

    public static string? GetCachedModsDirectory()
    {
        string? managerDirectory = GetManagerDataDirectory();
        return managerDirectory is null
            ? null
            : Path.Combine(managerDirectory, "Cached Mods");
    }

    public static string? GetModCacheDirectory(string modId)
    {
        string? cachedModsDirectory = GetCachedModsDirectory();
        if (string.IsNullOrWhiteSpace(cachedModsDirectory))
        {
            return null;
        }

        string directoryName = SanitizeFileName(modId, "mod");
        return Path.Combine(cachedModsDirectory, directoryName);
    }

    public static string? GetModDatabaseCacheDirectory()
    {
        string? managerDirectory = GetManagerDataDirectory();
        return managerDirectory is null
            ? null
            : Path.Combine(managerDirectory, "Mod Database Cache");
    }

    public static string? GetModCachePath(string modId, string? version, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        string? modCacheDirectory = GetModCacheDirectory(modId);
        if (string.IsNullOrWhiteSpace(modCacheDirectory))
        {
            return null;
        }

        string versionSegment = SanitizeFileName(version!, "version");
        string versionDirectory = Path.Combine(modCacheDirectory, versionSegment);
        string cacheFileName = SanitizeCacheFileName(fileName, modId, versionSegment);

        return Path.Combine(versionDirectory, cacheFileName);
    }

    public static bool TryLocateCachedModFile(string modId, string? version, string? fileName, out string? cacheFilePath)
    {
        cacheFilePath = null;
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        string? modCacheDirectory = GetModCacheDirectory(modId);
        if (string.IsNullOrWhiteSpace(modCacheDirectory))
        {
            return false;
        }

        string versionSegment = SanitizeFileName(version!, "version");
        string versionDirectory = Path.Combine(modCacheDirectory, versionSegment);
        string preferredFileName = SanitizeCacheFileName(fileName, modId, versionSegment);
        string preferredPath = Path.Combine(versionDirectory, preferredFileName);

        if (File.Exists(preferredPath))
        {
            cacheFilePath = preferredPath;
            return true;
        }

        if (Directory.Exists(versionDirectory))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(versionDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    cacheFilePath = file;
                    return true;
                }
            }
            catch (Exception)
            {
                // Ignore enumeration failures.
            }
        }

        string? legacyPath = GetLegacyModCachePath(modCacheDirectory, versionSegment, fileName);
        if (legacyPath is not null && File.Exists(legacyPath))
        {
            cacheFilePath = legacyPath;
            return true;
        }

        return false;
    }

    public static bool TryPromoteLegacyCacheFile(string modId, string? version, string? fileName, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        string? modCacheDirectory = GetModCacheDirectory(modId);
        if (string.IsNullOrWhiteSpace(modCacheDirectory))
        {
            return false;
        }

        string versionSegment = SanitizeFileName(version!, "version");
        string? legacyPath = GetLegacyModCachePath(modCacheDirectory, versionSegment, fileName);
        if (legacyPath is null || !File.Exists(legacyPath))
        {
            return false;
        }

        if (string.Equals(legacyPath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            string? directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

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
        string? cacheDirectory = GetModCacheDirectory(modId);
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            yield break;
        }

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

        foreach (string versionDirectory in versionDirectories)
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

            foreach (string file in versionFiles)
            {
                if (yielded.Add(file))
                {
                    yield return file;
                }
            }
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

        foreach (string file in legacyFiles)
        {
            if (yielded.Add(file))
            {
                yield return file;
            }
        }
    }

    public static string SanitizeFileName(string? input, string fallback)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return fallback;
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            if (Array.IndexOf(invalidChars, c) >= 0)
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(c);
            }
        }

        string sanitized = builder.ToString().Trim();
        return string.IsNullOrEmpty(sanitized) ? fallback : sanitized;
    }

    private static string SanitizeCacheFileName(string? fileName, string modId, string versionSegment)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            string sanitized = ReplaceInvalidCacheFileCharacters(fileName);
            if (!string.IsNullOrWhiteSpace(sanitized))
            {
                if (string.IsNullOrWhiteSpace(Path.GetExtension(sanitized)))
                {
                    sanitized += ".zip";
                }

                return sanitized;
            }
        }

        return BuildFallbackFileName(modId, versionSegment);
    }

    private static string BuildFallbackFileName(string modId, string versionSegment)
    {
        string safeModId = SanitizeFileName(modId, "mod");
        return string.Concat(safeModId, '-', versionSegment, ".zip");
    }

    private static string ReplaceInvalidCacheFileCharacters(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (char c in value.Trim())
        {
            builder.Append(Array.IndexOf(invalidChars, c) >= 0 ? '_' : c);
        }

        string sanitized = builder.ToString().TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? string.Empty : sanitized;
    }

    private static string? GetLegacyModCachePath(string modCacheDirectory, string versionSegment, string? fileName)
    {
        string extension = string.IsNullOrWhiteSpace(fileName) ? ".zip" : Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".zip";
        }

        return Path.Combine(modCacheDirectory, versionSegment + extension);
    }
}
