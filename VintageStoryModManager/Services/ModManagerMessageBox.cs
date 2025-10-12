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
        MessageDialogExtraButton? extraButton = null)
    {
        return ShowInternal(null, messageBoxText, caption, button, icon, extraButton);
    }

    public static MessageBoxResult Show(
        Window owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageDialogExtraButton? extraButton = null)
    {
        return ShowInternal(owner, messageBoxText, caption, button, icon, extraButton);
    }

    private static MessageBoxResult ShowInternal(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageDialogExtraButton? extraButton)
    {
        Window? resolvedOwner = owner ?? GetActiveWindow();

        var dialog = new MessageDialogWindow
        {
            Owner = resolvedOwner,
            WindowStartupLocation = resolvedOwner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Topmost = resolvedOwner is null
        };

        dialog.Initialize(messageBoxText, caption, button, icon, extraButton);
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
