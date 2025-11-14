using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace VintageStoryModManager.Services;

/// <summary>
///     Attempts to determine the installed Vintage Story version for compatibility checks.
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
        foreach (var candidate in EnumerateCandidates(configuredGameDirectory))
        {
            var version = TryGetVersionFromFile(candidate);
            if (!string.IsNullOrWhiteSpace(version)) return version;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidates(string? configuredGameDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in EnumerateRoots(configuredGameDirectory))
        {
            if (File.Exists(root))
            {
                if (seen.Add(root)) yield return root;

                continue;
            }

            foreach (var relative in CandidateRelativePaths)
            {
                var candidate = Path.Combine(root, relative);
                if (seen.Add(candidate)) yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateRoots(string? configuredGameDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (TryNormalize(configuredGameDirectory) is { } configured && seen.Add(configured)) yield return configured;

        var environmentPath = Environment.GetEnvironmentVariable("VINTAGE_STORY");
        if (TryNormalize(environmentPath) is { } fromEnvironment && seen.Add(fromEnvironment))
            yield return fromEnvironment;

        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        if (seen.Add(baseDirectory)) yield return baseDirectory;

        var vsFolder = Path.Combine(baseDirectory, "VSFOLDER");
        if (TryNormalize(vsFolder) is { } normalizedVsFolder
            && Directory.Exists(normalizedVsFolder)
            && seen.Add(normalizedVsFolder))
            yield return normalizedVsFolder;

        var currentDirectory = TryNormalize(Directory.GetCurrentDirectory());
        if (currentDirectory is not null && seen.Add(currentDirectory)) yield return currentDirectory;
    }

    private static string? TryNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

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
            if (!File.Exists(path)) return null;

            var info = FileVersionInfo.GetVersionInfo(path);
            var fromFileVersion = VersionStringUtility.Normalize(info.FileVersion);
            if (!string.IsNullOrWhiteSpace(fromFileVersion)) return fromFileVersion;

            var fromProductVersion = VersionStringUtility.Normalize(info.ProductVersion);
            if (!string.IsNullOrWhiteSpace(fromProductVersion)) return fromProductVersion;

            var assembly = AssemblyName.GetAssemblyName(path);
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