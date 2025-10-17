using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace VintageStoryModManager.Services;

/// <summary>
/// Attempts to determine the installed Vintage Story version for compatibility checks.
/// </summary>
public static class VintageStoryVersionLocator
{
    private static readonly string[] CandidateRelativePaths =
    {
        "Vintagestory.exe",
        "Vintagestory",
        Path.Combine("Vintagestory.app", "Contents", "MacOS", "Vintagestory"),
        "VintagestoryServer.exe",
        "VintagestoryServer",
        "VintagestoryAPI.dll"
    };

    public static string? GetInstalledVersion(string? configuredGameDirectory = null)
    {
        foreach (string candidate in EnumerateCandidates(configuredGameDirectory))
        {
            string? version = TryGetVersionFromFile(candidate);
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidates(string? configuredGameDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in EnumerateRoots(configuredGameDirectory))
        {
            if (File.Exists(root))
            {
                if (seen.Add(root))
                {
                    yield return root;
                }

                continue;
            }

            foreach (string relative in CandidateRelativePaths)
            {
                string candidate = Path.Combine(root, relative);
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateRoots(string? configuredGameDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (TryNormalize(configuredGameDirectory) is { } configured && seen.Add(configured))
        {
            yield return configured;
        }

        string? environmentPath = Environment.GetEnvironmentVariable("VINTAGE_STORY");
        if (TryNormalize(environmentPath) is { } fromEnvironment && seen.Add(fromEnvironment))
        {
            yield return fromEnvironment;
        }

        string baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        if (seen.Add(baseDirectory))
        {
            yield return baseDirectory;
        }

        string vsFolder = Path.Combine(baseDirectory, "VSFOLDER");
        if (TryNormalize(vsFolder) is { } normalizedVsFolder
            && Directory.Exists(normalizedVsFolder)
            && seen.Add(normalizedVsFolder))
        {
            yield return normalizedVsFolder;
        }

        string? currentDirectory = TryNormalize(Directory.GetCurrentDirectory());
        if (currentDirectory is not null && seen.Add(currentDirectory))
        {
            yield return currentDirectory;
        }
    }

    private static string? TryNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryGetVersionFromFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            string? fromFileVersion = VersionStringUtility.Normalize(info.FileVersion);
            if (!string.IsNullOrWhiteSpace(fromFileVersion))
            {
                return fromFileVersion;
            }

            string? fromProductVersion = VersionStringUtility.Normalize(info.ProductVersion);
            if (!string.IsNullOrWhiteSpace(fromProductVersion))
            {
                return fromProductVersion;
            }

            AssemblyName assembly = AssemblyName.GetAssemblyName(path);
            return VersionStringUtility.Normalize(assembly.Version?.ToString());
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
