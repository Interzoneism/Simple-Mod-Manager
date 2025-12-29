using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace VintageStoryModManager.Views.Dialogs;

public sealed class ModConfigOption : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<string> EmptyPaths = Array.Empty<string>();
    private bool _isSelected;

    public ModConfigOption(string modId, string displayName, IEnumerable<string> configPaths, bool isSelected)
    {
        ModId = modId ?? string.Empty;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? ModId : displayName;
        ConfigPaths = (configPaths ?? EmptyPaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .ToList();
        _isSelected = isSelected;
    }

    public string ModId { get; }

    public string DisplayName { get; }

    public IReadOnlyList<string> ConfigPaths { get; }

    public string ConfigPath => ConfigPaths.FirstOrDefault() ?? string.Empty;

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