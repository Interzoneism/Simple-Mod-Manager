using System.Globalization;

namespace VintageStoryModManager.Services;

public static class ServerCommandBuilder
{
    private const string CommandTemplate = "/moddb install {0}@{1}";

    public static string? TryBuildInstallCommand(string? modId, string? version)
    {
        if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(version)) return null;

        var normalizedModId = modId.Trim();
        var normalizedVersion = version.Trim();
        if (normalizedModId.Length == 0 || normalizedVersion.Length == 0) return null;

        return string.Format(CultureInfo.InvariantCulture, CommandTemplate, normalizedModId, normalizedVersion);
    }
}