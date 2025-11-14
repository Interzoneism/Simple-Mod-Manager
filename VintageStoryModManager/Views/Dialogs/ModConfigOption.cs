using System.ComponentModel;

namespace VintageStoryModManager.Views.Dialogs;

public sealed class ModConfigOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public ModConfigOption(string modId, string displayName, string configPath, bool isSelected)
    {
        ModId = modId ?? string.Empty;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? ModId : displayName;
        ConfigPath = configPath ?? string.Empty;
        _isSelected = isSelected;
    }

    public string ModId { get; }

    public string DisplayName { get; }

    public string ConfigPath { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}