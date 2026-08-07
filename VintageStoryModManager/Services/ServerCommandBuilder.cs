using System.Globalization;

namespace VintageStoryModManager.Services;

/// <summary>
///     Provides utilities for building Vintage Story server commands.
/// </summary>
public static class ServerCommandBuilder
{
    private const string CommandTemplate = "/moddb install {0}@{1}";

    /// <summary>
    ///     Attempts to build a mod installation command for a Vintage Story server.
    /// </summary>
    /// <param name="modId">The unique identifier of the mod.</param>
    /// <param name="version">The version of the mod to install.</param>
    /// <returns>A server command string if both modId and version are valid; otherwise, null.</returns>
    public static string? TryBuildInstallCommand(string? modId, string? version)
    {
        if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(version)) return null;

        var normalizedModId = modId.Trim();
        var normalizedVersion = version.Trim();
        if (normalizedModId.Length == 0 || normalizedVersion.Length == 0) return null;

        return string.Format(CultureInfo.InvariantCulture, CommandTemplate, normalizedModId, normalizedVersion);
    }
}