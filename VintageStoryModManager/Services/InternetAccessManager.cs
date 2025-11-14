namespace VintageStoryModManager.Services;

public static class InternetAccessManager
{
    private static int _isInternetAccessDisabled;

    public static bool IsInternetAccessDisabled => Volatile.Read(ref _isInternetAccessDisabled) != 0;

    public static event EventHandler? InternetAccessChanged;

    public static void SetInternetAccessDisabled(bool disabled)
    {
        var newValue = disabled ? 1 : 0;
        var oldValue = Interlocked.Exchange(ref _isInternetAccessDisabled, newValue);
        if (oldValue == newValue) return;

        InternetAccessChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void ThrowIfInternetAccessDisabled()
    {
        if (IsInternetAccessDisabled) throw new InternetAccessDisabledException();
    }
}

public sealed class InternetAccessDisabledException : InvalidOperationException
{
    private static readonly string DefaultMessage = DevConfig.InternetAccessDisabledMessage;

    public InternetAccessDisabledException()
        : base(DefaultMessage)
    {
    }

    public InternetAccessDisabledException(string? message)
        : base(string.IsNullOrWhiteSpace(message) ? DefaultMessage : message)
    {
    }
}