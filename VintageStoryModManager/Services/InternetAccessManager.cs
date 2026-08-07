namespace VintageStoryModManager.Services;

/// <summary>
///     Manages internet access state for the application and provides methods to check and control access.
/// </summary>
public static class InternetAccessManager
{
    private static int _isInternetAccessDisabled;

    /// <summary>
    ///     Gets a value indicating whether internet access is currently disabled.
    /// </summary>
    public static bool IsInternetAccessDisabled => Volatile.Read(ref _isInternetAccessDisabled) != 0;

    /// <summary>
    ///     Occurs when the internet access state changes.
    /// </summary>
    public static event EventHandler? InternetAccessChanged;

    /// <summary>
    ///     Sets whether internet access should be disabled for the application.
    /// </summary>
    /// <param name="disabled">True to disable internet access; false to enable it.</param>
    public static void SetInternetAccessDisabled(bool disabled)
    {
        var newValue = disabled ? 1 : 0;
        var oldValue = Interlocked.Exchange(ref _isInternetAccessDisabled, newValue);
        if (oldValue == newValue) return;

        InternetAccessChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    ///     Throws an <see cref="InternetAccessDisabledException"/> if internet access is currently disabled.
    /// </summary>
    /// <exception cref="InternetAccessDisabledException">Thrown when internet access is disabled.</exception>
    public static void ThrowIfInternetAccessDisabled()
    {
        if (IsInternetAccessDisabled) throw new InternetAccessDisabledException();
    }
}

/// <summary>
///     Exception thrown when an operation requiring internet access is attempted while internet access is disabled.
/// </summary>
public sealed class InternetAccessDisabledException : InvalidOperationException
{
    private static readonly string DefaultMessage = DevConfig.InternetAccessDisabledMessage;

    /// <summary>
    ///     Initializes a new instance of the <see cref="InternetAccessDisabledException"/> class.
    /// </summary>
    public InternetAccessDisabledException()
        : base(DefaultMessage)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="InternetAccessDisabledException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public InternetAccessDisabledException(string? message)
        : base(string.IsNullOrWhiteSpace(message) ? DefaultMessage : message)
    {
    }
}