using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VintageStoryModManager.ViewModels;
using YamlDotNet.Core;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using WpfMessageBox = VintageStoryModManager.Services.ModManagerMessageBox;

namespace VintageStoryModManager.Views;

public partial class ModConfigEditorWindow : Window
{
    public ModConfigEditorWindow(ModConfigEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(EnsureFilePathDoesNotOverlapButtons));
    }

    private void EnsureFilePathDoesNotOverlapButtons()
    {
        if (FilePathTextBlock is null || ActionButtonsPanel is null) return;

        UpdateLayout();

        const int maxIterations = 3;
        for (var i = 0; i < maxIterations; i++)
        {
            var filePathBounds = GetElementBounds(FilePathTextBlock);
            var buttonsBounds = GetElementBounds(ActionButtonsPanel);

            if (filePathBounds.Right <= buttonsBounds.Left) break;

            var overlap = filePathBounds.Right - buttonsBounds.Left;
            if (overlap <= 0) break;

            var additionalWidth = overlap + 24; // Add some spacing to separate the elements.
            Width = Math.Max(Width + additionalWidth, MinWidth);

            UpdateLayout();
        }
    }

    private Rect GetElementBounds(FrameworkElement element)
    {
        if (!element.IsLoaded) element.UpdateLayout();

        var transform = element.TransformToAncestor(this);
        return transform.TransformBounds(new Rect(element.RenderSize));
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ModConfigEditorViewModel viewModel) return;

        try
        {
            viewModel.Save();
            DialogResult = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                       or JsonException or YamlException)
        {
            WpfMessageBox.Show(this,
                $"Failed to save the configuration:\n{ex.Message}",
                "Edit Config",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ModConfigEditorViewModel viewModel) return;

        if (!viewModel.HasConfigurations)
        {
            AddButton_OnClick(sender, e);
            return;
        }

        var initialDirectory = GetInitialDirectory(viewModel.FilePath);

        var dialog = new OpenFileDialog
        {
            Title = "Select configuration file",
            Filter = "Config files (*.json;*.yaml;*.yml)|*.json;*.yaml;*.yml|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        try
        {
            var currentFileName = Path.GetFileName(viewModel.FilePath);
            if (!string.IsNullOrWhiteSpace(currentFileName)) dialog.FileName = currentFileName;
        }
        catch (Exception)
        {
            // Ignore invalid paths and fall back to the default behaviour of the dialog.
        }

        var result = dialog.ShowDialog(this);
        if (result != true) return;

        try
        {
            viewModel.ReplaceConfigurationFile(dialog.FileName);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(EnsureFilePathDoesNotOverlapButtons));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or YamlException
                                       or ArgumentException or PathTooLongException or NotSupportedException)
        {
            WpfMessageBox.Show(this,
                $"Failed to open the configuration file:\n{ex.Message}",
                "Edit Config",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void AddButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ModConfigEditorViewModel viewModel) return;

        var initialDirectory = GetInitialDirectory(viewModel.FilePath);

        var dialog = new OpenFileDialog
        {
            Title = "Add configuration file",
            Filter = "Config files (*.json;*.yaml;*.yml)|*.json;*.yaml;*.yml|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        var result = dialog.ShowDialog(this);
        if (result != true) return;

        try
        {
            viewModel.AddConfiguration(dialog.FileName);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(EnsureFilePathDoesNotOverlapButtons));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or YamlException
                                       or ArgumentException or PathTooLongException or NotSupportedException)
        {
            WpfMessageBox.Show(this,
                $"Failed to add the configuration file:\n{ex.Message}",
                "Edit Config",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ClearButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ModConfigEditorViewModel viewModel) return;

        var removed = viewModel.RemoveSelectedConfiguration();
        if (removed)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(EnsureFilePathDoesNotOverlapButtons));
    }

    private void TreeView_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;

        if (sender is not DependencyObject dependencyObject) return;

        var scrollViewer = FindAncestorScrollViewer(dependencyObject);
        if (scrollViewer is null) return;

        if (e.Delta > 0)
            scrollViewer.LineUp();
        else if (e.Delta < 0) scrollViewer.LineDown();

        e.Handled = true;
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is ScrollViewer scrollViewer) return scrollViewer;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void TreeViewItem_OnExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item) return;

        if (item.DataContext is not ModConfigArrayNodeViewModel) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ExpandArrayChildren(item)));
    }

    private void TreeViewItem_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item) return;

        if (item.DataContext is not ModConfigArrayNodeViewModel) return;

        if (e.OriginalSource is DependencyObject source)
        {
            if (FindAncestor<ToggleButton>(source) is not null) return;

            if (FindAncestor<TextBoxBase>(source) is not null) return;
        }

        item.IsSelected = true;
        item.IsExpanded = !item.IsExpanded;
        e.Handled = true;
    }

    private void ExpandArrayChildren(TreeViewItem arrayItem)
    {
        if (arrayItem.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
        {
            arrayItem.UpdateLayout();

            if (arrayItem.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
            {
                arrayItem.ItemContainerGenerator.StatusChanged += OnStatusChanged;
                return;
            }
        }

        foreach (var child in arrayItem.Items)
            if (arrayItem.ItemContainerGenerator.ContainerFromItem(child) is TreeViewItem childItem
                && childItem.DataContext is ModConfigContainerNodeViewModel)
            {
                childItem.IsExpanded = true;
                ExpandArrayChildren(childItem);
            }

        void OnStatusChanged(object? sender, EventArgs e)
        {
            if (arrayItem.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
            {
                arrayItem.ItemContainerGenerator.StatusChanged -= OnStatusChanged;
                ExpandArrayChildren(arrayItem);
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;

            var parent = VisualTreeHelper.GetParent(current);
            if (parent is null) break;

            current = parent;
        }

        return null;
    }

    private static string? GetInitialDirectory(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(directory)) return null;

            var candidate = directory;
            while (!string.IsNullOrWhiteSpace(candidate))
            {
                var trimmed = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var folderName = Path.GetFileName(trimmed);
                if (string.Equals(folderName, "ModConfig", StringComparison.OrdinalIgnoreCase)) return candidate;

                var parent = Path.GetDirectoryName(candidate);
                if (string.IsNullOrWhiteSpace(parent) ||
                    string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase)) break;

                candidate = parent;
            }

            return directory;
        }
        catch (Exception)
        {
            return null;
        }
    }
}