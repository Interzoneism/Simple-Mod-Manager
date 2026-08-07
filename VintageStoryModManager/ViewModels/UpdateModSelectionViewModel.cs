using CommunityToolkit.Mvvm.ComponentModel;
using VintageStoryModManager.Models;
using VintageStoryModManager.Services;

namespace VintageStoryModManager.ViewModels;

public sealed partial class UpdateModSelectionViewModel : ObservableObject
{
    private readonly bool _isServerOptionsEnabled;
    private readonly ModReleaseInfo? _overrideRelease;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowExcludeVersionButton))]
    private bool _isSelected;

    public UpdateModSelectionViewModel(
        ModListItemViewModel mod,
        bool isSelected,
        ModReleaseInfo? overrideRelease,
        bool isServerOptionsEnabled)
    {
        Mod = mod ?? throw new ArgumentNullException(nameof(mod));
        _overrideRelease = overrideRelease;
        _isSelected = isSelected;
        _isServerOptionsEnabled = isServerOptionsEnabled;
    }

    public ModListItemViewModel Mod { get; }

    public string DisplayName => Mod.DisplayName;

    public string InstalledVersionDisplay => string.IsNullOrWhiteSpace(Mod.Version) ? "—" : Mod.Version!;

    public string TargetVersionDisplay
    {
        get
        {
            var target = TargetRelease?.Version ?? Mod.Version;
            return string.IsNullOrWhiteSpace(target) ? "—" : target!;
        }
    }

    public ModReleaseInfo? TargetRelease => _overrideRelease ?? Mod.LatestCompatibleRelease;

    public string? TargetUpdateVersion => TargetRelease?.Version;

    public bool CanSkip => !string.IsNullOrWhiteSpace(TargetUpdateVersion);

    private string? LatestServerInstallVersion =>
        TargetRelease?.Version ?? Mod.Version;

    public bool ShowServerInstallCommand => _isServerOptionsEnabled
                                            && !string.IsNullOrWhiteSpace(Mod.ModId)
                                            && !string.IsNullOrWhiteSpace(LatestServerInstallVersion);

    public string? LatestInstallCommand => ShowServerInstallCommand
        ? ServerCommandBuilder.TryBuildInstallCommand(Mod.ModId, LatestServerInstallVersion)
        : null;

    public string VersionSummary
    {
        get
        {
            var installed = InstalledVersionDisplay;
            var target = TargetVersionDisplay;
            if (string.Equals(installed, target, StringComparison.OrdinalIgnoreCase)) return $"Installed: {installed}";

            return $"Installed: {installed} → Update to: {target}";
        }
    }

    public IReadOnlyList<ModListItemViewModel.ReleaseChangelog> Changelogs =>
        Mod.GetChangelogEntriesForUpgrade(TargetUpdateVersion);

    public int ChangelogCount => Changelogs.Count;

    public bool HasChangelogs => ChangelogCount > 0;

    public string ChangelogHeader => ChangelogCount <= 1
        ? "Changelog"
        : $"Changelogs ({ChangelogCount})";

    public bool ShowExcludeVersionButton => !IsSelected && CanSkip;
}
