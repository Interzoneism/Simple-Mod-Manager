using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using VintageStoryModManager.Services;

namespace VintageStoryModManager.ViewModels;

public sealed class ModUsageVoteCandidateViewModel : ObservableObject
{
    private bool _isSelected;

    public ModUsageVoteCandidateViewModel(ModListItemViewModel mod, int usageCount, ModUsageTrackingKey trackingKey)
    {
        Mod = mod ?? throw new ArgumentNullException(nameof(mod));
        UsageCount = usageCount < 0 ? 0 : usageCount;
        TrackingKey = trackingKey;
        _isSelected = true;
    }

    public ModListItemViewModel Mod { get; }

    public ModUsageTrackingKey TrackingKey { get; }

    public string ModId => Mod.ModId ?? string.Empty;

    public string DisplayLabel
    {
        get
        {
            var name = Mod.DisplayName;
            var id = Mod.ModId;
            if (string.IsNullOrWhiteSpace(id)) return name ?? string.Empty;

            var trimmedId = id.Trim();
            if (string.IsNullOrWhiteSpace(name)) return trimmedId;

            return string.Format(CultureInfo.CurrentCulture, "{0} ({1})", name.Trim(), trimmedId);
        }
    }

    public int UsageCount { get; }

    public string UsageSummary =>
        UsageCount == 1
            ? "Used in 1 long session"
            : string.Format(CultureInfo.CurrentCulture, "Used in {0} long sessions", UsageCount);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}