using System.Diagnostics;

namespace VintageStoryModManager.Services;

/// <summary>
///     Captures System.Diagnostics trace messages and routes them to the ModActivityLoggingService.
/// </summary>
public sealed class ModManagerTraceListener : TraceListener
{
    private readonly ModActivityLoggingService _loggingService;

    public ModManagerTraceListener(ModActivityLoggingService loggingService)
    {
        _loggingService = loggingService;
    }

    public override void Write(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            _loggingService.LogDiagnostic(message.Trim());
        }
    }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            _loggingService.LogDiagnostic(message.Trim());
        }
    }
}
