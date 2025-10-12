using System;
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
        string? fromEnvironment = GetEnvironmentOverride();
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return EnsureTrailingDirectory(fromEnvironment!);
        }

        string? appDataPath = GetApplicationDataPath();
        if (!string.IsNullOrWhiteSpace(appDataPath))
        {
            return Path.Combine(appDataPath!, DataFolderName);
        }

        string? portablePath = GetPortableCandidate();
        if (!string.IsNullOrWhiteSpace(portablePath))
        {
            return portablePath!;
        }

        return EnsureTrailingDirectory(Directory.GetCurrentDirectory());
    }

    private static string? GetEnvironmentOverride()
    {
        string? direct = Environment.GetEnvironmentVariable("VINTAGE_STORY_DATA");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        string? server = Environment.GetEnvironmentVariable("VINTAGE_STORY_SERVER_DATA");
        if (!string.IsNullOrWhiteSpace(server))
        {
            return server;
        }

        return null;
    }

    private static string? GetApplicationDataPath()
    {
        try
        {
            string? path = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }
        catch (PlatformNotSupportedException)
        {
            // Ignore and fall back to other options.
        }

        string? appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrWhiteSpace(appData))
        {
            return appData;
        }

        string? home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, ".config");
        }

        return null;
    }

    private static string? GetPortableCandidate()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string candidate = Path.Combine(baseDirectory, DataFolderName);
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        string localData = Path.Combine(baseDirectory, "data");
        if (Directory.Exists(localData))
        {
            return localData;
        }

        return null;
    }

    private static string EnsureTrailingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Directory.GetCurrentDirectory();
        }

        return Path.GetFullPath(path);
    }
}
