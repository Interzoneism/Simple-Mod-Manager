using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VintageStoryModManager.ViewModels;

public sealed class ModConfigEditorViewModel : ObservableObject
{
    private ModConfigurationViewModel? _selectedConfiguration;

    public ModConfigEditorViewModel(string modDisplayName, IEnumerable<string> filePaths)
    {
        if (string.IsNullOrWhiteSpace(modDisplayName))
            throw new ArgumentException("Mod name is required.", nameof(modDisplayName));

        ModDisplayName = modDisplayName;
        WindowTitle = $"Edit Config - {ModDisplayName}";

        foreach (var path in filePaths ?? Array.Empty<string>()) AddConfiguration(path, false);

        SelectedConfiguration = Configurations.FirstOrDefault();
    }

    public string ModDisplayName { get; }

    public string WindowTitle { get; }

    public ObservableCollection<ModConfigurationViewModel> Configurations { get; } = new();

    public ModConfigurationViewModel? SelectedConfiguration
    {
        get => _selectedConfiguration;
        set
        {
            if (SetProperty(ref _selectedConfiguration, value))
            {
                OnPropertyChanged(nameof(FilePath));
                OnPropertyChanged(nameof(HasConfigurations));
            }
        }
    }

    public bool HasConfigurations => SelectedConfiguration is not null;

    public string FilePath => SelectedConfiguration?.FilePath ?? string.Empty;

    public IReadOnlyList<string> ConfigPaths => Configurations.Select(config => config.FilePath).ToList();

    public ModConfigurationViewModel AddConfiguration(string filePath, bool select = true)
    {
        var configuration = new ModConfigurationViewModel(filePath);
        Configurations.Add(configuration);

        if (select || SelectedConfiguration is null) SelectedConfiguration = configuration;

        return configuration;
    }

    public bool RemoveSelectedConfiguration()
    {
        if (SelectedConfiguration is null) return false;

        var index = Configurations.IndexOf(SelectedConfiguration);
        if (index < 0) return false;

        Configurations.RemoveAt(index);

        if (Configurations.Count == 0)
        {
            SelectedConfiguration = null;
        }
        else
        {
            var nextIndex = Math.Min(index, Configurations.Count - 1);
            SelectedConfiguration = Configurations[nextIndex];
        }

        OnPropertyChanged(nameof(FilePath));
        return true;
    }

    public void ReplaceConfigurationFile(string filePath)
    {
        SelectedConfiguration?.ReplaceConfigurationFile(filePath);
        OnPropertyChanged(nameof(FilePath));
    }

    public void Save()
    {
        foreach (var configuration in Configurations) configuration.Save();
    }

}
