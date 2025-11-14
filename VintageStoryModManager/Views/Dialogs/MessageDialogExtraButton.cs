using System.Windows;

namespace VintageStoryModManager.Views.Dialogs;

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