using System.IO;

namespace VintageStoryModManager.Services;

/// <summary>
///     Manages the custom configuration folder path for the mod manager.
///     This allows users to relocate the entire "Simple VS Manager" folder to a custom location.
/// </summary>
public static class CustomConfigFolderManager
{
    private const string CustomFolderPathFileName = "CustomConfigFolder.txt";

    /// <summary>
    ///     Gets the custom configuration folder path file location.
    ///     This file is stored in the application's base directory (alongside the executable).
    /// </summary>
    private static string GetCustomFolderPathFile()
    {
        var baseDirectory = AppContext.BaseDirectory;
        return Path.Combine(baseDirectory, CustomFolderPathFileName);
    }

    /// <summary>
    ///     Gets the custom configuration folder path if it has been set by the user.
    /// </summary>
    /// <returns>The custom folder path, or null if not set or if the folder doesn't exist.</returns>
    public static string? GetCustomConfigFolder()
    {
        var pathFile = GetCustomFolderPathFile();

        if (!File.Exists(pathFile))
            return null;

        try
        {
            var customPath = File.ReadAllText(pathFile).Trim();

            if (string.IsNullOrWhiteSpace(customPath))
                return null;

            // Verify the folder exists
            if (!Directory.Exists(customPath))
                return null;

            return customPath;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     Sets the custom configuration folder path.
    /// </summary>
    /// <param name="folderPath">The new folder path to use for configuration.</param>
    public static void SetCustomConfigFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("Folder path cannot be null or empty.", nameof(folderPath));

        var pathFile = GetCustomFolderPathFile();
        File.WriteAllText(pathFile, folderPath);
    }

    /// <summary>
    ///     Clears the custom configuration folder path, reverting to the default location.
    /// </summary>
    public static void ClearCustomConfigFolder()
    {
        var pathFile = GetCustomFolderPathFile();

        if (File.Exists(pathFile))
        {
            try
            {
                File.Delete(pathFile);
            }
            catch (Exception)
            {
                // Ignore deletion failures
            }
        }
    }

    /// <summary>
    ///     Checks if a custom configuration folder has been set.
    /// </summary>
    public static bool HasCustomConfigFolder()
    {
        return GetCustomConfigFolder() != null;
    }
}
