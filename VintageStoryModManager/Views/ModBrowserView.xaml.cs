using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VintageStoryModManager.Models;
using VintageStoryModManager.Services;
using VintageStoryModManager.ViewModels;

namespace VintageStoryModManager.Views;

/// <summary>
/// Interaction logic for ModBrowserView.xaml
/// </summary>
public partial class ModBrowserView : System.Windows.Controls.UserControl
{
    private static readonly HttpClient ScreenshotHttpClient = CreateScreenshotHttpClient();
    private bool _isInitialized;
    private int _screenshotLoadVersion;
    private CancellationTokenSource? _screenshotLoadCancellation;
    private ModBrowserViewModel? _subscribedViewModel;

    public bool IsModBrowserInitialized => _isInitialized;

    public ModBrowserView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnControlUnloaded;
    }

    private ModBrowserViewModel? ViewModel => DataContext as ModBrowserViewModel;

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToViewModel(ViewModel);
        _ = UpdateScreenshotImageAsync(clearExistingImage: true);

        // Don't initialize ViewModel here - defer until tab is selected
        // This prevents unnecessary network requests on application startup
    }

    /// <summary>
    /// Initializes the ModBrowserViewModel. This should be called only when the Database tab is selected for the first time.
    /// </summary>
    public async Task InitializeAsync()
    {
        // Initialize the ViewModel only once
        if (_isInitialized || ViewModel == null)
            return;

        // Only initialize if the view is actually loaded
        // The tab visibility is managed by MainWindow which only calls this when the tab is selected
        if (!IsLoaded)
        {
            System.Diagnostics.Debug.WriteLine("[ModBrowserView] Skipping initialization - view not loaded");
            return;
        }

        // Set flag before awaiting to prevent race conditions
        _isInitialized = true;

        try
        {
            await ViewModel.InitializeCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // Log the error but don't crash the application
            System.Diagnostics.Debug.WriteLine($"[ModBrowserView] Error during initialization: {ex.Message}");
            // Reset flag to allow retry if initialization failed
            _isInitialized = false;
        }
    }

    private void OnControlUnloaded(object sender, RoutedEventArgs e)
    {
        CancelScreenshotLoad();
        CurrentScreenshotImage.Source = null;
        SubscribeToViewModel(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            SubscribeToViewModel(e.NewValue as ModBrowserViewModel);
            _ = UpdateScreenshotImageAsync(clearExistingImage: true);
        }
    }

    private void SubscribeToViewModel(ModBrowserViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribedViewModel, viewModel))
            return;

        if (_subscribedViewModel != null)
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _subscribedViewModel = viewModel;

        if (_subscribedViewModel != null)
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModBrowserViewModel.CurrentScreenshotUrl))
        {
            // Never leave the previous mod's screenshot visible while its replacement loads.
            _ = UpdateScreenshotImageAsync(clearExistingImage: true);
        }
        else if (e.PropertyName == nameof(ModBrowserViewModel.IsModDetailsOpen) &&
                 ViewModel?.IsModDetailsOpen != true)
        {
            CancelScreenshotLoad();
            CurrentScreenshotImage.Source = null;
        }
    }

    private async Task UpdateScreenshotImageAsync(bool clearExistingImage)
    {
        var screenshotUrl = ViewModel?.IsModDetailsOpen == true
            ? ViewModel.CurrentScreenshotUrl
            : null;
        var loadVersion = ++_screenshotLoadVersion;
        _screenshotLoadCancellation?.Cancel();
        _screenshotLoadCancellation?.Dispose();
        _screenshotLoadCancellation = new CancellationTokenSource();
        var cancellationToken = _screenshotLoadCancellation.Token;

        if (clearExistingImage || string.IsNullOrWhiteSpace(screenshotUrl))
            CurrentScreenshotImage.Source = null;

        if (string.IsNullOrWhiteSpace(screenshotUrl))
            return;

        try
        {
            var imageBytes = await LoadScreenshotBytesAsync(screenshotUrl, cancellationToken);
            if (imageBytes is null || imageBytes.Length == 0)
                return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (loadVersion != _screenshotLoadVersion ||
                    cancellationToken.IsCancellationRequested ||
                    !string.Equals(screenshotUrl, ViewModel?.CurrentScreenshotUrl, StringComparison.Ordinal))
                {
                    return;
                }

                // BitmapImage is a WPF Freezable, so it must be created on the UI
                // dispatcher that will own and display it. Only raw bytes cross
                // the background-loading boundary.
                CurrentScreenshotImage.Source = CreateBitmapImage(imageBytes);
            });
        }
        catch (OperationCanceledException)
        {
            // A newer gallery selection or closing the overlay superseded this load.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ModBrowserView] Failed to load screenshot '{screenshotUrl}': {ex.Message}");
        }
    }

    private static async Task<byte[]?> LoadScreenshotBytesAsync(
        string screenshotUrl,
        CancellationToken cancellationToken)
    {
        var cachedBytes = await ModImageCacheService
            .TryGetCachedImageAsync(screenshotUrl, cancellationToken)
            .ConfigureAwait(false);
        if (cachedBytes is { Length: > 0 })
            return cachedBytes;

        if (InternetAccessManager.IsInternetAccessDisabled ||
            !Uri.TryCreate(screenshotUrl, UriKind.Absolute, out var screenshotUri) ||
            (screenshotUri.Scheme != Uri.UriSchemeHttp && screenshotUri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        using var response = await ScreenshotHttpClient
            .GetAsync(screenshotUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var imageBytes = await response.Content
            .ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        await ModImageCacheService
            .StoreImageAsync(screenshotUrl, imageBytes, cancellationToken)
            .ConfigureAwait(false);

        return imageBytes;
    }

    private static BitmapImage CreateBitmapImage(byte[] imageBytes)
    {
        using var stream = new MemoryStream(imageBytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void CancelScreenshotLoad()
    {
        _screenshotLoadVersion++;
        _screenshotLoadCancellation?.Cancel();
        _screenshotLoadCancellation?.Dispose();
        _screenshotLoadCancellation = null;
    }

    private static HttpClient CreateScreenshotHttpClient()
    {
        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SimpleVSManager/1.0");
        return httpClient;
    }

    private void ScrollToTop_Click(object sender, RoutedEventArgs e)
    {
        ModsScrollViewer.ScrollToTop();
    }

    private void ModsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Load more mods when scrolling near the bottom
        // Trigger at 1.5 viewports from bottom to preload content smoothly
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - (e.ViewportHeight * 1.5))
        {
            ViewModel?.LoadMoreCommand.Execute(null);
        }
    }

    private void ModsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        const double ScrollSpeed = 65.0; // Increase for faster scrolling, decrease for slower
        if (sender is ScrollViewer scrollViewer)
        {
            double offset = scrollViewer.VerticalOffset - (e.Delta * ScrollSpeed / Mouse.MouseWheelDeltaForOneLine);
            scrollViewer.ScrollToVerticalOffset(offset);
            e.Handled = true;
        }
    }

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // Prevent card click
        if (sender is FrameworkElement element && element.Tag is int modId)
        {
            ViewModel?.ToggleFavoriteCommand.Execute(modId);
        }
    }

    private void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // Prevent card click

        if (ViewModel == null)
            return;

        if (sender is FrameworkElement element && element.Tag is int modId)
        {
            if (ViewModel.InstallModCommand == null)
                return;

            ViewModel.InstallModCommand.Execute(modId);
        }
    }

    private void OpenInBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // Prevent card click
        if (sender is FrameworkElement element && element.Tag is int assetId)
        {
            ViewModel?.OpenModInBrowserCommand.Execute(assetId);
        }
    }

    private void ModCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Models.DownloadableModOnList mod })
        {
            ViewModel?.OpenModDetailsCommand.Execute(mod);
        }
    }

    private void ModDetailOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        // Clicking the dimmed backdrop closes the detail overlay.
        ViewModel?.CloseModDetailsCommand.Execute(null);
    }

    private void ModDetailCard_Click(object sender, MouseButtonEventArgs e)
    {
        // Prevent clicks on the card itself from bubbling up and closing the overlay.
        e.Handled = true;
    }

    private void ModDetailOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UIElement { IsVisible: true } overlay)
            return;

        // Opening the overlay does not inherently move keyboard focus away from the
        // clicked card. Defer until the current mouse event has finished so Escape
        // and the other routed keyboard input work immediately.
        overlay.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (overlay.IsVisible)
                    Keyboard.Focus(overlay);
            }));
    }

    private void ModBrowserView_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel?.IsModDetailsOpen == true)
        {
            ViewModel.CloseModDetailsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void VersionsDropdown_SelectionChanged(object? sender, EventArgs e)
    {
        await TriggerSearchOnSelectionChanged();
    }

    private async void TagsDropdown_SelectionChanged(object? sender, EventArgs e)
    {
        await TriggerSearchOnSelectionChanged();
    }

    private async Task TriggerSearchOnSelectionChanged()
    {
        // Trigger search when filter selection changes
        if (ViewModel != null)
        {
            await ViewModel.RefreshSearchAsync();
        }
    }
}
