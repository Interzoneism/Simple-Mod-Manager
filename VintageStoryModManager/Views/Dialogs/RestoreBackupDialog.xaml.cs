using System.Windows;

namespace VintageStoryModManager.Views.Dialogs;

public partial class RestoreBackupDialog : Window
{
    public RestoreBackupDialog()
    {
        InitializeComponent();
    }

    public bool RestoreConfigurations => RestoreConfigsToggle.IsOn;

    private void RestoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}