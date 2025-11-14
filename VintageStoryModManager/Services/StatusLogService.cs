namespace VintageStoryModManager.Services;

/// <summary>
///     Placeholder service that previously persisted status updates to disk.
///     Debug logging has been removed, so calls are now ignored.
/// </summary>
public static class StatusLogService
{
    /// <summary>
    ///     Appends a status entry. No-op because status logging has been disabled.
    /// </summary>
    public static void AppendStatus(string message, bool isError)
    {
        // Intentionally left blank.
    }

    /// <summary>
    ///     Begins a debug scope. Always returns <c>null</c> because debug logging has been removed.
    /// </summary>
    public static DebugLogScope? BeginDebugScope(string? modName, string? modId, string process)
    {
        return null;
    }
}

public sealed class DebugLogScope : IDisposable
{
    public void Dispose()
    {
        // Intentionally left blank.
    }

    public void SetCacheStatus(bool cacheHit)
    {
        // Intentionally left blank.
    }

    public void SetDetail(string key, string? value)
    {
        // Intentionally left blank.
    }

    public void SetDetail(string key, int value)
    {
        // Intentionally left blank.
    }

    public void SetDetail(string key, long value)
    {
        // Intentionally left blank.
    }
}