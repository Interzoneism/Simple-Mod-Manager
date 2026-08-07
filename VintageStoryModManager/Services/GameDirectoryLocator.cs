using System.IO;

namespace VintageStoryModManager.Services;

/// <summary>
///     Attempts to locate the Vintage Story installation directory and executable.
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
    ///     Attempts to resolve the installation directory of Vintage Story.
    /// </summary>
    /// <returns>The absolute path to the game directory or <see cref="string.Empty" /> if it cannot be located.</returns>
    public static string Resolve()
    {
        foreach (var candidate in EnumerateCandidates())
            if (IsValidGameDirectory(candidate))
                return candidate;

        return string.Empty;
    }

    /// <summary>
    ///     Attempts to locate the Vintage Story executable inside the provided directory.
    /// </summary>
    /// <param name="gameDirectory">The candidate game directory.</param>
    /// <returns>The absolute path to the executable or <c>null</c> when none of the known candidates are present.</returns>
    public static string? FindExecutable(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return null;

        var fullDirectory = NormalizePath(gameDirectory);
        if (string.IsNullOrWhiteSpace(fullDirectory) || !Directory.Exists(fullDirectory)) return null;

        foreach (var candidate in ExecutableCandidates)
        {
            var path = Path.Combine(fullDirectory, candidate);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var baseDirectory = TryNormalize(AppContext.BaseDirectory);
        if (baseDirectory != null && seen.Add(baseDirectory)) yield return baseDirectory;

        var vsFolder = Path.Combine(AppContext.BaseDirectory, "VSFOLDER");
        var normalizedVsFolder = TryNormalize(vsFolder);
        if (normalizedVsFolder != null && Directory.Exists(normalizedVsFolder) && seen.Add(normalizedVsFolder))
            yield return normalizedVsFolder;

        var currentDirectory = TryNormalize(Directory.GetCurrentDirectory());
        if (currentDirectory != null && seen.Add(currentDirectory)) yield return currentDirectory;

        foreach (var defaultPath in EnumerateDefaultInstallPaths())
        {
            var normalized = TryNormalize(defaultPath);
            if (normalized != null && seen.Add(normalized)) yield return normalized;
        }
    }

    private static IEnumerable<string> EnumerateDefaultInstallPaths()
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.LocalApplicationData
                 })
        {
            var root = TryGetFolder(folder);
            if (!string.IsNullOrWhiteSpace(root)) yield return Path.Combine(root!, "Vintagestory");
        }

        foreach (var root in EnumerateAdditionalWindowsRoots()) yield return Path.Combine(root, "Vintagestory");
    }

    private static bool IsValidGameDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var fullPath = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(fullPath) || !Directory.Exists(fullPath)) return false;

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

    private static string? TryNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        return NormalizePath(path);
    }

    private static string? TryGetFolder(Environment.SpecialFolder folder)
    {
        try
        {
            var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateAdditionalWindowsRoots()
    {
        foreach (var root in new[]
                 {
                     @"C:\\Games",
                     @"D:\\Games",
                     @"C:\\Program Files",
                     @"C:\\Program Files (x86)"
                 })
        {
            var normalized = TryNormalize(root);
            if (!string.IsNullOrWhiteSpace(normalized)) yield return normalized!;
        }
    }
}