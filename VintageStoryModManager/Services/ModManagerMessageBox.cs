using System.Linq;
using System.Windows;
using VintageStoryModManager.Views.Dialogs;

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
        Window? resolvedOwner = owner ?? GetActiveWindow();
        bool hasVisibleOwner = resolvedOwner?.IsVisible == true;

        var dialog = new MessageDialogWindow
        {
            Owner = hasVisibleOwner ? resolvedOwner : null,
            WindowStartupLocation = hasVisibleOwner ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Topmost = resolvedOwner is null || !hasVisibleOwner
        };

        dialog.Initialize(messageBoxText, caption, button, icon, extraButton, buttonContentOverrides);
        _ = dialog.ShowDialog();
        return dialog.Result;
    }

    private static Window? GetActiveWindow()
    {
        if (System.Windows.Application.Current == null)
        {
            return null;
        }

        return System.Windows.Application.Current.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive)
            ?? System.Windows.Application.Current.MainWindow;
    }
}
