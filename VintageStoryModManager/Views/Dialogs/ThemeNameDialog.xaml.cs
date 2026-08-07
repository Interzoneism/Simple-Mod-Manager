using System.Windows;
using VintageStoryModManager.Services;

namespace VintageStoryModManager.Views.Dialogs;

public partial class ThemeNameDialog : Window
{
    private readonly UserConfigurationService _configuration;

    public ThemeNameDialog(string? initialName, UserConfigurationService configuration)
    {
        InitializeComponent();
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        ThemeName = initialName ?? string.Empty;
        DataContext = this;
        
        // Update button text if the initial name matches an existing theme
        UpdateSaveButtonText();
        
        // Update button text as user types
        ThemeNameTextBox.TextChanged += (s, e) => UpdateSaveButtonText();
    }

    public string ThemeName { get; set; }

    private void UpdateSaveButtonText()
    {
        var currentText = ThemeNameTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(currentText))
        {
            SaveButton.Content = "Save";
            return;
        }

        var allThemes = _configuration.GetAllThemeNames();
        var themeExists = allThemes.Any(name => 
            string.Equals(name, currentText, StringComparison.OrdinalIgnoreCase));
        
        SaveButton.Content = themeExists ? "Replace" : "Save";
    }

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        var enteredName = ThemeNameTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(enteredName))
        {
            ModManagerMessageBox.Show(
                this,
                "Please enter a theme name.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // Check if the entered name is a built-in theme name
        if (ThemePaletteEditorDialog.ThemeOption.IsBuiltInThemeName(enteredName))
        {
            ModManagerMessageBox.Show(
                this,
                "Cannot save theme with a built-in theme name. Please choose a different name.",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ThemeName = enteredName;
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
