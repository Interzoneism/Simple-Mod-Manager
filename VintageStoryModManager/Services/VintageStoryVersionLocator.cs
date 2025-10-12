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

    public static string? GetInstalledVersion()
    {
        string? fromEnvironment = VersionStringUtility.Normalize(Environment.GetEnvironmentVariable("VINTAGE_STORY_VERSION"));
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        foreach (string candidate in EnumerateCandidates())
        {
            string? version = TryGetVersionFromFile(candidate);
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        foreach (string root in EnumerateRoots())
        {
            foreach (string relative in CandidateRelativePaths)
            {
                yield return Path.Combine(root, relative);
            }
        }
    }

    private static IEnumerable<string> EnumerateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? explicitFolder = Environment.GetEnvironmentVariable("VINTAGE_STORY");
        if (!string.IsNullOrWhiteSpace(explicitFolder))
        {
            string fullPath = Path.GetFullPath(explicitFolder);
            if (seen.Add(fullPath))
            {
                yield return fullPath;
            }
        }

        string baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        if (seen.Add(baseDirectory))
        {
            yield return baseDirectory;
        }

        string vsFolder = Path.Combine(baseDirectory, "VSFOLDER");
        if (Directory.Exists(vsFolder) && seen.Add(vsFolder))
        {
            yield return vsFolder;
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
