using System;
using System.Windows;
using System.Windows.Controls;

namespace VintageStoryModManager.Views.Dialogs;

public partial class CloudModlistRenameDialog : Window
{
    public CloudModlistRenameDialog(Window owner, string? currentName)
    {
        InitializeComponent();

        Owner = owner;
        NameTextBox.Text = string.IsNullOrWhiteSpace(currentName)
            ? string.Empty
            : currentName;
        NameTextBox.SelectAll();
        UpdateConfirmButtonState();
    }

    public string ModlistName => NameTextBox.Text.Trim();

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            return;
        }

        DialogResult = true;
    }

    private void NameTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateConfirmButtonState();
    }

    private void UpdateConfirmButtonState()
    {
        if (ConfirmButton is null)
        {
            return;
        }

        string? modlistName = NameTextBox?.Text;
        ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(modlistName);
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        NameTextBox.Focus();
        NameTextBox.SelectAll();
    }
}
