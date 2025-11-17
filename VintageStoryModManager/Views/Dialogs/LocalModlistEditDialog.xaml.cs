using System.Windows;

namespace VintageStoryModManager.Views.Dialogs;

public partial class LocalModlistEditDialog : Window
{
    public LocalModlistEditDialog(Window owner, string? name, string? description, string? version,
        string? gameVersion)
    {
        InitializeComponent();
        Owner = owner;

        NameTextBox.Text = name ?? string.Empty;
        DescriptionTextBox.Text = description ?? string.Empty;
        VersionTextBox.Text = version ?? string.Empty;
        GameVersionTextBox.Text = gameVersion ?? string.Empty;
    }

    public string? ModlistName => string.IsNullOrWhiteSpace(NameTextBox.Text)
        ? null
        : NameTextBox.Text.Trim();

    public string? ModlistDescription => string.IsNullOrWhiteSpace(DescriptionTextBox.Text)
        ? null
        : DescriptionTextBox.Text.Trim();

    public string? ModlistVersion => string.IsNullOrWhiteSpace(VersionTextBox.Text)
        ? null
        : VersionTextBox.Text.Trim();

    public string? ModlistGameVersion => string.IsNullOrWhiteSpace(GameVersionTextBox.Text)
        ? null
        : GameVersionTextBox.Text.Trim();

    private void NameTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateConfirmButtonState();
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateConfirmButtonState();
        NameTextBox.Focus();
        NameTextBox.SelectAll();
    }

    private void UpdateConfirmButtonState()
    {
        if (ConfirmButton is null) return;

        ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(NameTextBox.Text);
    }
}
