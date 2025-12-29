using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace VintageStoryModManager.Views
{

    /// <summary>
    /// A multi-select dropdown control for WPF.
    /// </summary>
    public partial class MultiSelectDropdown : System.Windows.Controls.UserControl
    {
        /// <summary>
        /// Event raised when the selection changes (items are selected or deselected).
        /// </summary>
        public event EventHandler? SelectionChanged;
        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(
                nameof(ItemsSource),
                typeof(IEnumerable),
                typeof(MultiSelectDropdown),
                new PropertyMetadata(null, OnItemsSourceChanged));

        public static readonly DependencyProperty SelectedItemsProperty =
            DependencyProperty.Register(
                nameof(SelectedItems),
                typeof(IList),
                typeof(MultiSelectDropdown),
                new FrameworkPropertyMetadata(
                    null,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnSelectedItemsChanged));

        public static readonly DependencyProperty PlaceholderTextProperty =
            DependencyProperty.Register(
                nameof(PlaceholderText),
                typeof(string),
                typeof(MultiSelectDropdown),
                new PropertyMetadata("Select items...", OnPlaceholderTextChanged));

        public static readonly DependencyProperty DisplayMemberPathProperty =
            DependencyProperty.Register(
                nameof(DisplayMemberPath),
                typeof(string),
                typeof(MultiSelectDropdown),
                new PropertyMetadata("Name"));

        public static readonly DependencyProperty DisplayTextProperty =
            DependencyProperty.Register(
                nameof(DisplayText),
                typeof(string),
                typeof(MultiSelectDropdown),
                new PropertyMetadata("Select items..."));

        public static readonly DependencyProperty DisplayTextBrushProperty =
            DependencyProperty.Register(
                nameof(DisplayTextBrush),
                typeof(Brush),
                typeof(MultiSelectDropdown),
                new PropertyMetadata(new SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170)))); // TextSecondaryBrush

        public IEnumerable ItemsSource
        {
            get => (IEnumerable)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public IList SelectedItems
        {
            get => (IList)GetValue(SelectedItemsProperty);
            set => SetValue(SelectedItemsProperty, value);
        }

        public string PlaceholderText
        {
            get => (string)GetValue(PlaceholderTextProperty);
            set => SetValue(PlaceholderTextProperty, value);
        }

        public string DisplayMemberPath
        {
            get => (string)GetValue(DisplayMemberPathProperty);
            set => SetValue(DisplayMemberPathProperty, value);
        }

        public string DisplayText
        {
            get => (string)GetValue(DisplayTextProperty);
            private set => SetValue(DisplayTextProperty, value);
        }

        public Brush DisplayTextBrush
        {
            get => (Brush)GetValue(DisplayTextBrushProperty);
            private set => SetValue(DisplayTextBrushProperty, value);
        }

        private ObservableCollection<SelectableItem> _selectableItems = [];
        private Brush? _textPrimaryBrush;
        private Brush? _textSecondaryBrush;
        private bool _isEventSubscribed = false;
        private bool _suppressNextToggleClick = false;

        // Fallback brushes if resources aren't available
        private static readonly Brush FallbackTextPrimaryBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 250, 250));
        private static readonly Brush FallbackTextSecondaryBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170));

        private Brush TextPrimaryBrush => _textPrimaryBrush ??= TryFindResource("TextPrimaryBrush") as Brush ?? FallbackTextPrimaryBrush;
        private Brush TextSecondaryBrush => _textSecondaryBrush ??= TryFindResource("TextSecondaryBrush") as Brush ?? FallbackTextSecondaryBrush;

        public MultiSelectDropdown()
        {
            InitializeComponent();
            ItemsList.ItemsSource = _selectableItems;
            Loaded += (_, _) => UpdateDisplayText(); // Update after resources are available

            // Subscribe to PreviewMouseDown on the root visual to detect clicks outside
            Loaded += (_, _) =>
            {
                if (!_isEventSubscribed)
                {
                    var window = Window.GetWindow(this);
                    if (window != null)
                    {
                        window.PreviewMouseDown += Window_PreviewMouseDown;
                        _isEventSubscribed = true;
                    }
                }
            };

            Unloaded += (_, _) =>
            {
                if (_isEventSubscribed)
                {
                    var window = Window.GetWindow(this);
                    if (window != null)
                    {
                        window.PreviewMouseDown -= Window_PreviewMouseDown;
                        _isEventSubscribed = false;
                    }
                }
            };
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!DropdownPopup.IsOpen)
                return;

            // Check if the click is outside the dropdown
            var popupChild = DropdownPopup.Child;
            var toggleButton = DropdownToggle;

            if (popupChild != null && toggleButton != null)
            {
                var clickedElement = e.OriginalSource as DependencyObject;

                // Check if click is inside popup content
                bool isClickInPopup = IsDescendantOf(clickedElement, popupChild);
                bool isClickOnToggle = IsDescendantOf(clickedElement, toggleButton);

                if (isClickOnToggle)
                {
                    _suppressNextToggleClick = true;
                    DropdownToggle.IsChecked = false;
                    e.Handled = true;
                }
                // If click is outside the popup content and toggle button, close it
                else if (!isClickInPopup)
                {
                    DropdownToggle.IsChecked = false;
                }
            }
        }

        private bool IsDescendantOf(DependencyObject? child, DependencyObject parent)
        {
            if (child == null || parent == null)
                return false;

            DependencyObject current = child;
            while (current != null)
            {
                if (current == parent)
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MultiSelectDropdown dropdown)
            {
                // Unsubscribe from old collection
                if (e.OldValue is INotifyCollectionChanged oldCollection)
                {
                    oldCollection.CollectionChanged -= dropdown.OnItemsSourceCollectionChanged;
                }

                // Subscribe to new collection
                if (e.NewValue is INotifyCollectionChanged newCollection)
                {
                    newCollection.CollectionChanged += dropdown.OnItemsSourceCollectionChanged;
                }

                dropdown.UpdateSelectableItems();
            }
        }

        private void OnItemsSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Handle collection changes incrementally to avoid O(n²) performance issues
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    if (e.NewItems != null)
                    {
                        var insertIndex = e.NewStartingIndex >= 0 ? e.NewStartingIndex : _selectableItems.Count;
                        foreach (var item in e.NewItems)
                        {
                            var displayText = GetDisplayText(item);
                            var isSelected = SelectedItems?.Contains(item) == true;
                            var selectableItem = new SelectableItem
                            {
                                Item = item,
                                DisplayText = displayText,
                                IsSelected = isSelected
                            };

                            if (insertIndex <= _selectableItems.Count)
                            {
                                _selectableItems.Insert(insertIndex, selectableItem);
                                insertIndex++;
                            }
                            else
                            {
                                _selectableItems.Add(selectableItem);
                            }
                        }
                    }
                    break;

                case NotifyCollectionChangedAction.Remove:
                    if (e.OldItems != null && e.OldStartingIndex >= 0)
                    {
                        for (int i = 0; i < e.OldItems.Count; i++)
                        {
                            if (e.OldStartingIndex < _selectableItems.Count)
                            {
                                _selectableItems.RemoveAt(e.OldStartingIndex);
                            }
                        }
                    }
                    break;

                case NotifyCollectionChangedAction.Replace:
                    if (e.NewItems != null && e.NewStartingIndex >= 0 && e.NewStartingIndex < _selectableItems.Count)
                    {
                        for (int i = 0; i < e.NewItems.Count; i++)
                        {
                            var index = e.NewStartingIndex + i;
                            if (index < _selectableItems.Count)
                            {
                                var item = e.NewItems[i]!;
                                var displayText = GetDisplayText(item);
                                var isSelected = SelectedItems?.Contains(item) == true;
                                _selectableItems[index] = new SelectableItem
                                {
                                    Item = item,
                                    DisplayText = displayText,
                                    IsSelected = isSelected
                                };
                            }
                        }
                    }
                    break;

                case NotifyCollectionChangedAction.Move:
                    if (e.OldStartingIndex >= 0 && e.NewStartingIndex >= 0 &&
                        e.OldStartingIndex < _selectableItems.Count)
                    {
                        var item = _selectableItems[e.OldStartingIndex];
                        _selectableItems.RemoveAt(e.OldStartingIndex);
                        if (e.NewStartingIndex <= _selectableItems.Count)
                        {
                            _selectableItems.Insert(e.NewStartingIndex, item);
                        }
                        else
                        {
                            _selectableItems.Add(item);
                        }
                    }
                    break;

                case NotifyCollectionChangedAction.Reset:
                    // For Reset, we need to rebuild the entire list
                    UpdateSelectableItems();
                    break;
            }
        }

        private static void OnSelectedItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MultiSelectDropdown dropdown)
            {
                // Unsubscribe from old collection
                if (e.OldValue is INotifyCollectionChanged oldCollection)
                {
                    oldCollection.CollectionChanged -= dropdown.OnSelectedItemsCollectionChanged;
                }

                // Subscribe to new collection
                if (e.NewValue is INotifyCollectionChanged newCollection)
                {
                    newCollection.CollectionChanged += dropdown.OnSelectedItemsCollectionChanged;
                }

                dropdown.UpdateDisplayText();
                dropdown.UpdateSelectableItems();
            }
        }

        private void OnSelectedItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateDisplayText();

            // Update selection state incrementally instead of rebuilding entire list
            // Note: Using FirstOrDefault results in O(n) lookup per item, but this is acceptable because:
            // 1. SelectedItems changes are infrequent and typically involve only a few items
            // 2. Users rarely select/deselect many items at once (typical case: 1-10 selections)
            // 3. For 222 items with 5 selections: 5 * 222 = ~1,100 operations (acceptable)
            // 4. This avoids the complexity of maintaining a separate dictionary lookup structure
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    if (e.NewItems != null)
                    {
                        foreach (var item in e.NewItems)
                        {
                            var selectableItem = _selectableItems.FirstOrDefault(si => si.Item == item);
                            if (selectableItem != null)
                            {
                                selectableItem.IsSelected = true;
                            }
                        }
                    }
                    break;

                case NotifyCollectionChangedAction.Remove:
                    if (e.OldItems != null)
                    {
                        foreach (var item in e.OldItems)
                        {
                            var selectableItem = _selectableItems.FirstOrDefault(si => si.Item == item);
                            if (selectableItem != null)
                            {
                                selectableItem.IsSelected = false;
                            }
                        }
                    }
                    break;

                case NotifyCollectionChangedAction.Reset:
                    // Reset all selection states
                    foreach (var selectableItem in _selectableItems)
                    {
                        selectableItem.IsSelected = SelectedItems?.Contains(selectableItem.Item) == true;
                    }
                    break;

                case NotifyCollectionChangedAction.Replace:
                    // Handle replace by removing old and adding new
                    if (e.OldItems != null)
                    {
                        foreach (var item in e.OldItems)
                        {
                            var selectableItem = _selectableItems.FirstOrDefault(si => si.Item == item);
                            if (selectableItem != null)
                            {
                                selectableItem.IsSelected = false;
                            }
                        }
                    }
                    if (e.NewItems != null)
                    {
                        foreach (var item in e.NewItems)
                        {
                            var selectableItem = _selectableItems.FirstOrDefault(si => si.Item == item);
                            if (selectableItem != null)
                            {
                                selectableItem.IsSelected = true;
                            }
                        }
                    }
                    break;
            }
        }

        private static void OnPlaceholderTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MultiSelectDropdown dropdown)
            {
                dropdown.UpdateDisplayText();
            }
        }

        private void UpdateDisplayText()
        {
            if (SelectedItems == null || SelectedItems.Count == 0)
            {
                DisplayText = PlaceholderText;
                DisplayTextBrush = TextSecondaryBrush;
            }
            else if (SelectedItems.Count == 1)
            {
                DisplayText = GetDisplayText(SelectedItems[0]!);
                DisplayTextBrush = TextPrimaryBrush;
            }
            else
            {
                DisplayText = $"{SelectedItems.Count} selected";
                DisplayTextBrush = TextPrimaryBrush;
            }
        }

        private void UpdateSelectableItems()
        {
            _selectableItems.Clear();

            if (ItemsSource == null)
            {
                return;
            }

            foreach (var item in ItemsSource)
            {
                var displayText = GetDisplayText(item);
                var isSelected = SelectedItems?.Contains(item) == true;

                _selectableItems.Add(new SelectableItem
                {
                    Item = item,
                    DisplayText = displayText,
                    IsSelected = isSelected
                });
            }
        }

        private string GetDisplayText(object item)
        {
            if (string.IsNullOrEmpty(DisplayMemberPath))
                return item.ToString() ?? string.Empty;

            var property = item.GetType().GetProperty(DisplayMemberPath);
            return property?.GetValue(item)?.ToString() ?? item.ToString() ?? string.Empty;
        }

        private void DropdownToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressNextToggleClick)
            {
                _suppressNextToggleClick = false;
                e.Handled = true;
                return;
            }

            // Refresh selection state when opening
            if (DropdownToggle.IsChecked == true)
            {
                foreach (var selectableItem in _selectableItems)
                {
                    selectableItem.IsSelected = SelectedItems?.Contains(selectableItem.Item) == true;
                }
            }
        }

        private void Item_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is SelectableItem selectableItem)
            {
                selectableItem.IsSelected = !selectableItem.IsSelected;

                if (SelectedItems != null)
                {
                    if (selectableItem.IsSelected)
                    {
                        if (!SelectedItems.Contains(selectableItem.Item))
                        {
                            SelectedItems.Add(selectableItem.Item);
                        }
                    }
                    else
                    {
                        SelectedItems.Remove(selectableItem.Item);
                    }
                }

                UpdateDisplayText();

                // Raise SelectionChanged event to notify listeners
                SelectionChanged?.Invoke(this, EventArgs.Empty);

                // Mark event as handled to prevent popup from closing when clicking items
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// Wrapper for items in the multi-select dropdown.
    /// </summary>
    public class SelectableItem : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected;

        public object Item { get; set; } = null!;
        public string DisplayText { get; set; } = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}