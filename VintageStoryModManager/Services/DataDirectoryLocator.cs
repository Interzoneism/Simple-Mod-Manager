using System;
using System.Collections.Generic;
using System.IO;

namespace VintageStoryModManager.Services;

/// <summary>
/// Resolves the Vintage Story data directory in the same way as the game.
/// </summary>
public static class DataDirectoryLocator
{
    private const string DataFolderName = "VintagestoryData";

    /// <summary>
    /// Returns the absolute path to the Vintage Story data directory.
    /// </summary>
    public static string Resolve()
    {
        foreach (string candidate in EnumerateCandidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        string? fromAppData = TryCombine(GetFolder(Environment.SpecialFolder.ApplicationData), DataFolderName);
        if (!string.IsNullOrWhiteSpace(fromAppData))
        {
            yield return fromAppData!;
        }

        string? fromLocalAppData = TryCombine(GetFolder(Environment.SpecialFolder.LocalApplicationData), DataFolderName);
        if (!string.IsNullOrWhiteSpace(fromLocalAppData))
        {
            yield return fromLocalAppData!;
        }

        string? portable = GetPortableCandidate();
        if (!string.IsNullOrWhiteSpace(portable))
        {
            yield return portable!;
        }

        foreach (string root in EnumerateAdditionalWindowsRoots())
        {
            string? candidate = TryCombine(root, DataFolderName);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                yield return candidate!;
            }
        }

        string? currentDirectory = TryNormalize(Directory.GetCurrentDirectory());
        if (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            yield return currentDirectory!;
        }
    }

    private static string? GetFolder(Environment.SpecialFolder folder)
    {
        try
        {
            string? path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }

        return null;
    }

    private static string? GetPortableCandidate()
    {
        string baseDirectory = AppContext.BaseDirectory;

        foreach (string folder in new[] { DataFolderName, "data" })
        {
            string? candidate = TryCombine(baseDirectory, folder);
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateAdditionalWindowsRoots()
    {
        foreach (string root in new[]
                 {
                     @"C:\\Games",
                     @"D:\\Games",
                     @"C:\\Program Files",
                     @"C:\\Program Files (x86)"
                 })
        {
            string? normalized = TryNormalize(root);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                yield return normalized!;
            }
        }
    }

    private static string? TryCombine(string? basePath, string relative)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(basePath, relative));
        }
        catch (Exception)
        {
            return null;
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
}
