using System.Windows;

namespace VintageStoryModManager.Views.Dialogs;

public partial class TextInputDialog : Window
{
    public TextInputDialog(string title, string prompt, string defaultValue = "")
    {
        InitializeComponent();

        Title = title;
        PromptTextBlock.Text = prompt;
        InputTextBox.Text = defaultValue;
        InputTextBox.SelectAll();
    }

    public string InputText => InputTextBox.Text;

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(InputTextBox.Text))
        {
            DialogResult = true;
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
