using CommunityToolkit.Mvvm.ComponentModel;

namespace VintageStoryModManager.ViewModels;

/// <summary>
///     Represents a selectable tag filter option.
/// </summary>
public sealed class TagFilterOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public TagFilterOptionViewModel(string name, bool isSelected = false)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tag name cannot be null or whitespace.", nameof(name));

        Name = name.Trim();
        _isSelected = isSelected;
    }

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}