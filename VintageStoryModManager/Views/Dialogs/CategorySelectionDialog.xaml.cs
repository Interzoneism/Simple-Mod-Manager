using System.Windows;
using System.Windows.Input;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.Views.Dialogs;

public partial class CategorySelectionDialog : Window
{
    private readonly Action<string> _onCreateCategory;

    public CategorySelectionDialog(
        IReadOnlyList<ModCategory> categories,
        string currentCategoryId,
        Action<string> onCreateCategory)
    {
        InitializeComponent();

        _onCreateCategory = onCreateCategory;

        // Create view models for the categories
        var items = categories.Select(c => new CategoryItemViewModel
        {
            Id = c.Id,
            Name = c.Name,
            IsCurrentCategory = string.Equals(c.Id, currentCategoryId, StringComparison.OrdinalIgnoreCase),
            IsProfileSpecific = c.IsProfileSpecific
        }).ToList();

        CategoryListBox.ItemsSource = items;

        // Select the current category
        var currentItem = items.FirstOrDefault(i => i.IsCurrentCategory);
        if (currentItem != null)
        {
            CategoryListBox.SelectedItem = currentItem;
        }
    }

    public string? SelectedCategoryId { get; private set; }

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (CategoryListBox.SelectedItem is CategoryItemViewModel selected)
        {
            SelectedCategoryId = selected.Id;
            DialogResult = true;
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void CategoryListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CategoryListBox.SelectedItem is CategoryItemViewModel selected)
        {
            SelectedCategoryId = selected.Id;
            DialogResult = true;
        }
    }

    private void NewCategoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        var inputDialog = new TextInputDialog("New Category", "Enter category name:")
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        if (inputDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(inputDialog.InputText))
        {
            _onCreateCategory(inputDialog.InputText.Trim());

            // Close this dialog so it can be reopened with the new category
            DialogResult = false;
        }
    }

    private class CategoryItemViewModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsCurrentCategory { get; set; }
        public bool IsProfileSpecific { get; set; }

        public string DisplayName => IsCurrentCategory ? $"✓  {Name}" : $"    {Name}";
    }
}
