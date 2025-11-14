using System.Windows;
using VintageStoryModManager.Views.Dialogs;
using Application = System.Windows.Application;

namespace VintageStoryModManager.Services;

public static class ModManagerMessageBox
{
    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageDialogExtraButton? extraButton = null,
        MessageDialogButtonContentOverrides? buttonContentOverrides = null)
    {
        return ShowInternal(null, messageBoxText, caption, button, icon, extraButton, buttonContentOverrides);
    }

    public static MessageBoxResult Show(
        Window owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageDialogExtraButton? extraButton = null,
        MessageDialogButtonContentOverrides? buttonContentOverrides = null)
    {
        return ShowInternal(owner, messageBoxText, caption, button, icon, extraButton, buttonContentOverrides);
    }

    private static MessageBoxResult ShowInternal(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageDialogExtraButton? extraButton,
        MessageDialogButtonContentOverrides? buttonContentOverrides)
    {
        var resolvedOwner = owner ?? GetActiveWindow();
        var hasVisibleOwner = resolvedOwner?.IsVisible == true;

        var dialog = new MessageDialogWindow
        {
            Owner = hasVisibleOwner ? resolvedOwner : null,
            WindowStartupLocation =
                hasVisibleOwner ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Topmost = resolvedOwner is null || !hasVisibleOwner
        };

        dialog.Initialize(messageBoxText, caption, button, icon, extraButton, buttonContentOverrides);
        _ = dialog.ShowDialog();
        return dialog.Result;
    }

    private static Window? GetActiveWindow()
    {
        if (Application.Current == null) return null;

        return Application.Current.Windows
                   .OfType<Window>()
                   .FirstOrDefault(window => window.IsActive)
               ?? Application.Current.MainWindow;
    }
}