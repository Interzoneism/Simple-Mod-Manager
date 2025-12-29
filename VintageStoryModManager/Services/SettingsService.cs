using System.IO;
using System.Text.Json;

namespace VintageStoryModManager.Services;

/// <summary>
/// Service for persisting application settings.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Gets or sets the list of favorite mod IDs.
    /// </summary>
    List<int> FavoriteMods { get; set; }

    /// <summary>
    /// Gets or sets the last used installation path.
    /// </summary>
    string? LastInstallationPath { get; set; }

    /// <summary>
    /// Gets or sets the preferred order by field.
    /// </summary>
    string OrderBy { get; set; }

    /// <summary>
    /// Gets or sets the preferred order direction.
    /// </summary>
    string OrderByDirection { get; set; }

    /// <summary>
    /// Saves settings to disk.
    /// </summary>
    Task SaveAsync();

    /// <summary>
    /// Loads settings from disk.
    /// </summary>
    Task LoadAsync();
}

/// <summary>
/// Implementation of settings service using JSON file storage.
/// </summary>
public class SettingsService : ISettingsService
{
    private readonly string _settingsPath;
    private AppSettings _settings = new();

    public SettingsService()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appDataPath, "NOT SET YET");
        Directory.CreateDirectory(appFolder);
        _settingsPath = Path.Combine(appFolder, "NOT SET YET");
    }

    public List<int> FavoriteMods
    {
        get => _settings.FavoriteMods;
        set => _settings.FavoriteMods = value;
    }

    public string? LastInstallationPath
    {
        get => _settings.LastInstallationPath;
        set => _settings.LastInstallationPath = value;
    }

    public string OrderBy
    {
        get => _settings.OrderBy;
        set => _settings.OrderBy = value;
    }

    public string OrderByDirection
    {
        get => _settings.OrderByDirection;
        set => _settings.OrderByDirection = value;
    }

    public async Task SaveAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(_settingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex.Message}");
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = await File.ReadAllTextAsync(_settingsPath);
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading settings: {ex.Message}");
            _settings = new AppSettings();
        }
    }

    private class AppSettings
    {
        public List<int> FavoriteMods { get; set; } = [];
        public string? LastInstallationPath { get; set; }
        public string OrderBy { get; set; } = "follows";
        public string OrderByDirection { get; set; } = "desc";
    }
}
