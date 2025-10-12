using System;
using System.Collections.Generic;
using System.IO;

namespace VintageStoryModManager.Services;

/// <summary>
/// Attempts to locate the Vintage Story installation directory and executable.
/// </summary>
public static class GameDirectoryLocator
{
    private static readonly string[] ExecutableCandidates =
    {
        "Vintagestory.exe",
        "Vintagestory",
        Path.Combine("Vintagestory.app", "Contents", "MacOS", "Vintagestory")
    };

    /// <summary>
    /// Attempts to resolve the installation directory of Vintage Story.
    /// </summary>
    /// <returns>The absolute path to the game directory or <c>null</c> if it cannot be located.</returns>
    public static string? Resolve()
    {
        foreach (string candidate in EnumerateCandidates())
        {
            if (IsValidGameDirectory(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts to locate the Vintage Story executable inside the provided directory.
    /// </summary>
    /// <param name="gameDirectory">The candidate game directory.</param>
    /// <returns>The absolute path to the executable or <c>null</c> when none of the known candidates are present.</returns>
    public static string? FindExecutable(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return null;
        }

        string? fullDirectory = NormalizePath(gameDirectory);
        if (string.IsNullOrWhiteSpace(fullDirectory) || !Directory.Exists(fullDirectory))
        {
            return null;
        }

        foreach (string candidate in ExecutableCandidates)
        {
            string path = Path.Combine(fullDirectory, candidate);
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string? TryNormalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception) when (path != null)
            {
                return null;
            }
        }

        string? fromEnvironment = TryNormalize(Environment.GetEnvironmentVariable("VINTAGE_STORY"));
        if (fromEnvironment != null)
        {
            if (File.Exists(fromEnvironment))
            {
                fromEnvironment = Path.GetDirectoryName(fromEnvironment);
            }

            if (fromEnvironment != null && seen.Add(fromEnvironment))
            {
                yield return fromEnvironment;
            }
        }

        string? baseDirectory = TryNormalize(AppContext.BaseDirectory);
        if (baseDirectory != null && seen.Add(baseDirectory))
        {
            yield return baseDirectory;
        }

        string vsFolder = Path.Combine(AppContext.BaseDirectory, "VSFOLDER");
        string? normalizedVsFolder = TryNormalize(vsFolder);
        if (normalizedVsFolder != null && Directory.Exists(normalizedVsFolder) && seen.Add(normalizedVsFolder))
        {
            yield return normalizedVsFolder;
        }

        foreach (string defaultPath in EnumerateDefaultInstallPaths())
        {
            string? normalized = TryNormalize(defaultPath);
            if (normalized != null && seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static IEnumerable<string> EnumerateDefaultInstallPaths()
    {
        string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "Vintagestory");
        }

        string? programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "Vintagestory");
        }

        string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "VintageStory");
        }
    }

    private static bool IsValidGameDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string? fullPath = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(fullPath) || !Directory.Exists(fullPath))
        {
            return false;
        }

        return FindExecutable(fullPath) != null;
    }

    private static string? NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
