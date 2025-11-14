using System.Windows;
using VintageStoryModManager.Services;
using Clipboard = System.Windows.Clipboard;

namespace VintageStoryModManager.Views.Dialogs;

public partial class ExperimentalCompReviewDialog : Window
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ModCompatibilityCommentsService _service;
    private bool _isRunning;
    private string? _latestJson;

    public ExperimentalCompReviewDialog(
        Window? owner,
        string? defaultModSlug,
        string? defaultLatestVersion,
        ModCompatibilityCommentsService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        InitializeComponent();

        _service = service;

        if (owner?.IsVisible == true)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            Owner = null;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        ModSlugTextBox.Text = defaultModSlug ?? string.Empty;
        LatestVersionTextBox.Text = defaultLatestVersion ?? string.Empty;

        Loaded += OnLoaded;
        Closed += OnClosed;
        UpdateUiState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _ = Dispatcher.BeginInvoke(new Action(() => ModSlugTextBox.Focus()));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
    }

    private async void RunButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunReviewAsync();
    }

    private async Task RunReviewAsync()
    {
        if (_isRunning) return;

        var modSlug = ModSlugTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(modSlug))
        {
            StatusTextBlock.Text = "Enter a mod slug to continue.";
            ModSlugTextBox.Focus();
            return;
        }

        var latestVersion = string.IsNullOrWhiteSpace(LatestVersionTextBox.Text)
            ? null
            : LatestVersionTextBox.Text.Trim();

        try
        {
            InternetAccessManager.ThrowIfInternetAccessDisabled();
        }
        catch (InternetAccessDisabledException ex)
        {
            StatusTextBlock.Text = ex.Message;
            ModManagerMessageBox.Show(this, ex.Message, "Simple VS Manager", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            _isRunning = true;
            UpdateUiState();
            StatusTextBlock.Text = "Fetching compatibility comments...";
            ResultsTextBox.Text = string.Empty;
            _latestJson = null;

            var result = await _service
                .GetTop3CommentsAsync(modSlug, latestVersion, _cancellationTokenSource.Token)
                .ConfigureAwait(true);

            _latestJson = ModCompatibilityCommentsService.SerializeResult(result);
            ResultsTextBox.Text = _latestJson;
            StatusTextBlock.Text = result.Top3.Count > 0
                ? $"Found {result.Top3.Count} relevant comment{(result.Top3.Count == 1 ? string.Empty : "s")}."
                : result.Reason ?? "No relevant comments were found.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "The review was cancelled.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Review failed.";
            ModManagerMessageBox.Show(this,
                $"The experimental compatibility review failed:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isRunning = false;
            UpdateUiState();
        }
    }

    private void CopyJsonButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_latestJson)) return;

        try
        {
            Clipboard.SetText(_latestJson);
            StatusTextBlock.Text = "JSON copied to clipboard.";
        }
        catch (Exception ex)
        {
            ModManagerMessageBox.Show(this,
                $"Failed to copy the JSON output:\n{ex.Message}",
                "Simple VS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void UpdateUiState()
    {
        var isEnabled = !_isRunning;
        ModSlugTextBox.IsEnabled = isEnabled;
        LatestVersionTextBox.IsEnabled = isEnabled;
        RunButton.IsEnabled = isEnabled;
        CopyJsonButton.IsEnabled = !string.IsNullOrEmpty(_latestJson);
    }
}