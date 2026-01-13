using System.Windows;
using VintageStoryModManager.Models;
using VintageStoryModManager.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace VintageStoryModManager.Views.Dialogs;

public partial class ManageCategoriesDialog : Window
{
    private readonly MainViewModel _viewModel;

    public ManageCategoriesDialog(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        RefreshLists();
    }

    private void RefreshLists()
    {
        GlobalCategoriesListBox.ItemsSource = _viewModel.GetGlobalCategories();
        ProfileCategoriesListBox.ItemsSource = _viewModel.GetProfileCategories();
    }

    private void AddGlobalButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputDialog("New Global Category", "Enter category name:")
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            _viewModel.CreateCategory(dialog.InputText.Trim());
            RefreshLists();
        }
    }

    private void RenameGlobalButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (GlobalCategoriesListBox.SelectedItem is not ModCategory category)
        {
            MessageBox.Show(this, "Please select a category to rename.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (category.IsDefault)
        {
            MessageBox.Show(this, "The default 'Uncategorized' category cannot be renamed.", "Cannot Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new TextInputDialog("Rename Category", "Enter new name:", category.Name)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            _viewModel.RenameCategory(category.Id, dialog.InputText.Trim());
            RefreshLists();
        }
    }

    private void DeleteGlobalButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (GlobalCategoriesListBox.SelectedItem is not ModCategory category)
        {
            MessageBox.Show(this, "Please select a category to delete.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (category.IsDefault)
        {
            MessageBox.Show(this, "The default 'Uncategorized' category cannot be deleted.", "Cannot Delete", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            this,
            $"Are you sure you want to delete the category '{category.Name}'?\n\nAll mods in this category will be moved to 'Uncategorized'.",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _viewModel.DeleteCategory(category.Id);
            RefreshLists();
        }
    }

    private void AddProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputDialog("New Profile Category", "Enter category name:")
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            _viewModel.CreateProfileCategory(dialog.InputText.Trim());
            RefreshLists();
        }
    }

    private void RenameProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ProfileCategoriesListBox.SelectedItem is not ModCategory category)
        {
            MessageBox.Show(this, "Please select a category to rename.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new TextInputDialog("Rename Category", "Enter new name:", category.Name)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            _viewModel.RenameCategory(category.Id, dialog.InputText.Trim());
            RefreshLists();
        }
    }

    private void DeleteProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ProfileCategoriesListBox.SelectedItem is not ModCategory category)
        {
            MessageBox.Show(this, "Please select a category to delete.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            this,
            $"Are you sure you want to delete the category '{category.Name}'?\n\nAll mods in this category will be moved to 'Uncategorized'.",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _viewModel.DeleteCategory(category.Id);
            RefreshLists();
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
