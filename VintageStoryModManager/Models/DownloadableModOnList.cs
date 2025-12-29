using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace VintageStoryModManager.Models;

/// <summary>
/// Represents a mod listing from the Vintage Story mod database.
/// </summary>
public class DownloadableModOnList : INotifyPropertyChanged
{
    [JsonPropertyName("modid")]
    public int ModId { get; set; }

    [JsonPropertyName("assetid")]
    public int AssetId { get; set; }

    [JsonPropertyName("downloads")]
    public int Downloads { get; set; }

    [JsonPropertyName("follows")]
    public int Follows { get; set; }

    [JsonPropertyName("trendingpoints")]
    public int TrendingPoints { get; set; }

    [JsonPropertyName("comments")]
    public int Comments { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("modidstrs")]
    public List<string> ModIdStrings { get; set; } = [];

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("urlalias")]
    public string? UrlAlias { get; set; }

    [JsonPropertyName("side")]
    public string Side { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    private string? _logo;

    [JsonPropertyName("logo")]
    public string? Logo
    {
        get => _logo;
        set
        {
            if (value == _logo) return;

            var previousLogoUrl = LogoUrl;
            _logo = value;
            OnPropertyChanged();

            // Only notify LogoUrl if it actually changed (cache new value to avoid redundant computation)
            var newLogoUrl = LogoUrl;
            if (newLogoUrl != previousLogoUrl)
            {
                OnPropertyChanged(nameof(LogoUrl));
            }
        }
    }

    private string? _logoFileDatabase;

    [JsonPropertyName("logofiledb")]
    public string? LogoFileDatabase
    {
        get => _logoFileDatabase;
        set
        {
            if (value == _logoFileDatabase) return;

            var previousLogoUrl = LogoUrl;
            _logoFileDatabase = value;
            OnPropertyChanged();

            // Only notify LogoUrl if it actually changed (cache new value to avoid redundant computation)
            var newLogoUrl = LogoUrl;
            if (newLogoUrl != previousLogoUrl)
            {
                OnPropertyChanged(nameof(LogoUrl));
            }
        }
    }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("lastreleased")]
    public string LastReleased { get; set; } = string.Empty;

    private string _userReportDisplay = string.Empty;

    private string _userReportTooltip = "User reports require a known Vintage Story version.";

    private bool _showUserReportBadge;

    private bool _isInstalled;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets or sets the user report summary display text.
    /// </summary>
    public string UserReportDisplay
    {
        get => _userReportDisplay;
        set
        {
            if (value == _userReportDisplay) return;

            _userReportDisplay = value;
            OnPropertyChanged();
        }
    }

    public bool ShowUserReportBadge
    {
        get => _showUserReportBadge;
        set
        {
            if (value == _showUserReportBadge) return;

            _showUserReportBadge = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets the tooltip describing all user report vote options.
    /// </summary>
    public string UserReportTooltip
    {
        get => _userReportTooltip;
        set
        {
            if (value == _userReportTooltip) return;

            _userReportTooltip = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets whether the mod is installed locally.
    /// </summary>
    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (value == _isInstalled) return;

            _isInstalled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets a formatted download count (e.g., "10K" for 10000+).
    /// </summary>
    public string FormattedDownloads =>
        Downloads > 10000 ? $"{Downloads / 1000}K" : Downloads.ToString();

    /// <summary>
    /// Gets a formatted follows count (e.g., "10K" for 10000+).
    /// </summary>
    public string FormattedFollows =>
        Follows > 10000 ? $"{Follows / 1000}K" : Follows.ToString();

    /// <summary>
    /// Gets a formatted comments count (e.g., "10K" for 10000+).
    /// </summary>
    public string FormattedComments =>
        Comments > 10000 ? $"{Comments / 1000}K" : Comments.ToString();

    /// <summary>
    /// Gets the logo URL for display.
    /// Prioritizes LogoFileDatabase (high-quality thumbnail from individual API call) if available,
    /// otherwise falls back to Logo (lower-quality thumbnail from initial batch API response).
    /// </summary>
    public string LogoUrl => LogoFileDatabase ?? Logo ?? string.Empty;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
