using System;
using System.Threading;

namespace VintageStoryModManager.Services;

public static class InternetAccessManager
{
    private static int _isInternetAccessDisabled;

    public static event EventHandler? InternetAccessChanged;

    public static bool IsInternetAccessDisabled => Volatile.Read(ref _isInternetAccessDisabled) != 0;

    public static void SetInternetAccessDisabled(bool disabled)
    {
        int newValue = disabled ? 1 : 0;
        int oldValue = Interlocked.Exchange(ref _isInternetAccessDisabled, newValue);
        if (oldValue == newValue)
        {
            return;
        }

        InternetAccessChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void ThrowIfInternetAccessDisabled()
    {
        if (IsInternetAccessDisabled)
        {
            throw new InternetAccessDisabledException();
        }
    }
}

public sealed class InternetAccessDisabledException : InvalidOperationException
{
    public InternetAccessDisabledException()
        : base("Internet access is disabled. Enable Internet Access in the File menu to continue.")
    {
    }
}
