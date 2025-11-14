using System.Windows;
using System.Windows.Controls;

namespace VintageStoryModManager.Views.Dialogs;

public partial class GameProfileDialog : Window
{
    public GameProfileDialog(Window owner)
    {
        InitializeComponent();

        Owner = owner;
        UpdateConfirmButtonState();
    }

    public string ProfileName => NameTextBox.Text.Trim();

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProfileName)) return;

        DialogResult = true;
    }

    private void NameTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateConfirmButtonState();
    }

    private void UpdateConfirmButtonState()
    {
        if (ConfirmButton is null) return;

        ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(NameTextBox.Text);
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        NameTextBox.Focus();
        NameTextBox.SelectAll();
    }
}