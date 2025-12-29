using System.Windows;

namespace VintageStoryModManager.Views.Dialogs;

public partial class ChangeManagerFolderDialog : Window
{
    public ChangeManagerFolderDialog(Window owner, string currentFolder)
    {
        InitializeComponent();

        Owner = owner;

        var message = $"Current manager folder:\n{currentFolder}\n\n" +
                      "This will allow you to move the entire \"Simple VS Manager\" folder to a new location.\n\n" +
                      "The manager will:\n" +
                      "• Move all configuration files, cached mods, backups, and presets\n" +
                      "• Update the configuration to use the new location\n" +
                      "• Require a restart to complete the change\n\n" +
                      "Note: The Firebase authentication backup (SVSM Backup folder) will remain in its original location.\n\n" +
                      "Do not proceed unless you really need to move the folder. Do you want to continue?";

        MessageTextBlock.Text = message;
    }

    public ChangeManagerFolderDialogResult Result { get; private set; } = ChangeManagerFolderDialogResult.No;

    private void YesButton_OnClick(object sender, RoutedEventArgs e)
    {
        Result = ChangeManagerFolderDialogResult.Yes;
        DialogResult = true;
    }

    private void NoButton_OnClick(object sender, RoutedEventArgs e)
    {
        Result = ChangeManagerFolderDialogResult.No;
        DialogResult = false;
    }

    private void ResetButton_OnClick(object sender, RoutedEventArgs e)
    {
        Result = ChangeManagerFolderDialogResult.Reset;
        DialogResult = true;
    }
}

public enum ChangeManagerFolderDialogResult
{
    Yes,
    No,
    Reset
}
