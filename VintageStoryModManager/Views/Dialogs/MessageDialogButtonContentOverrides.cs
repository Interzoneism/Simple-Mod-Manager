using System.Windows;

namespace VintageStoryModManager.Views.Dialogs;

public sealed class MessageDialogButtonContentOverrides
{
    public string? Ok { get; init; }

    public string? Cancel { get; init; }

    public string? Yes { get; init; }

    public string? No { get; init; }

    public string? GetContent(MessageBoxResult result)
    {
        return result switch
        {
            MessageBoxResult.OK => Ok,
            MessageBoxResult.Cancel => Cancel,
            MessageBoxResult.Yes => Yes,
            MessageBoxResult.No => No,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Unsupported message box result.")
        };
    }
}