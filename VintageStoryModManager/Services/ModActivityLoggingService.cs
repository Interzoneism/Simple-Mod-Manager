using System.IO;

namespace VintageStoryModManager.Services;

/// <summary>
///     Logs mod activity (updates, installs, deletions) to a file when enabled.
/// </summary>
public sealed class ModActivityLoggingService
{
    private const string LogFileName = "Simple VS Manager logs.txt";
    private readonly UserConfigurationService _userConfiguration;

    public ModActivityLoggingService(UserConfigurationService userConfiguration)
    {
        _userConfiguration = userConfiguration;
    }

    /// <summary>
    ///     Logs a mod update if logging for updates is enabled.
    /// </summary>
    public void LogModUpdate(string modName, string? oldVersion, string? newVersion)
    {
        if (!_userConfiguration.LogModUpdates) return;

        var oldVersionText = string.IsNullOrWhiteSpace(oldVersion) ? "unknown" : oldVersion;
        var newVersionText = string.IsNullOrWhiteSpace(newVersion) ? "unknown" : newVersion;
        var message = $"{modName} updated from v{oldVersionText} to v{newVersionText}";
        AppendLogEntry(message);
    }

    /// <summary>
    ///     Logs a mod installation if logging for installs is enabled.
    /// </summary>
    public void LogModInstall(string modName, string? version)
    {
        if (!_userConfiguration.LogModInstalls) return;

        var versionText = string.IsNullOrWhiteSpace(version) ? string.Empty : $" v{version}";
        var message = $"{modName} installed{versionText}";
        AppendLogEntry(message);
    }

    /// <summary>
    ///     Logs a mod deletion if logging for deletions is enabled.
    /// </summary>
    public void LogModDeletion(string modName)
    {
        if (!_userConfiguration.LogModDeletions) return;

        var message = $"{modName} deleted";
        AppendLogEntry(message);
    }

    /// <summary>
    ///     Logs an error or exception if logging for errors and exceptions is enabled.
    /// </summary>
    public void LogError(string message, Exception? exception = null)
    {
        if (!_userConfiguration.LogErrorsAndExceptions) return;

        var logMessage = exception != null
            ? $"ERROR: {message} - Exception: {exception.GetType().Name}: {exception.Message}"
            : $"ERROR: {message}";

        AppendLogEntry(logMessage);
    }

    /// <summary>
    ///     Logs a diagnostic message if logging for errors and exceptions is enabled.
    /// </summary>
    public void LogDiagnostic(string message)
    {
        if (!_userConfiguration.LogErrorsAndExceptions) return;

        var logMessage = $"DIAGNOSTIC: {message}";
        AppendLogEntry(logMessage);
    }

    public void LogAppLaunch()
    {
        LogLifecycleEvent("App launched");
    }

    public void LogAppExit()
    {
        LogLifecycleEvent("App exited");
    }

    /// <summary>
    ///     Logs the mod loading performance timing summary.
    /// </summary>
    public void LogModLoadingTimingSummary(string summary)
    {
        if (!_userConfiguration.LogErrorsAndExceptions) return;

        AppendLogEntry(summary);
    }

    private void AppendLogEntry(string message)
    {
        try
        {
            var logDirectory = _userConfiguration.GetConfigurationDirectory();
            var logPath = Path.Combine(logDirectory, LogFileName);

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logLine = $"{timestamp} {message}";

            File.AppendAllText(logPath, logLine + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is best-effort; silently ignore failures.
        }
    }

    private void LogLifecycleEvent(string message)
    {
        if (!_userConfiguration.LogAppLaunchAndExit) return;

        AppendLogEntry(message);
    }
}
