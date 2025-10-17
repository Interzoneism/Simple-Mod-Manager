using System;
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
        string path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
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
        string extension = string.IsNullOrWhiteSpace(fileName) ? ".zip" : Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".zip";
        }

        return Path.Combine(modCacheDirectory, versionSegment + extension);
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
}
