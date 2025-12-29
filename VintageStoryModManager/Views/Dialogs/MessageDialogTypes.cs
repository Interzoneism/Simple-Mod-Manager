using System.Windows;

namespace VintageStoryModManager.Views.Dialogs;

/// <summary>
///     Allows customization of button content in message dialogs.
/// </summary>
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

/// <summary>
///     Represents an additional button in a message dialog.
/// </summary>
public sealed class MessageDialogExtraButton
{
    public MessageDialogExtraButton(
        string content,
        MessageBoxResult result,
        Action? onClick = null,
        bool isDefault = false,
        bool isCancel = false)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Result = result;
        OnClick = onClick;
        IsDefault = isDefault;
        IsCancel = isCancel;
    }

    public string Content { get; }

    public MessageBoxResult Result { get; }

    public bool IsDefault { get; }

    public bool IsCancel { get; }

    public Action? OnClick { get; }
}
