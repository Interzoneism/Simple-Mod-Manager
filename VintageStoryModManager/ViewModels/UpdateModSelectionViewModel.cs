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
            var target = _overrideRelease?.Version
                         ?? Mod.LatestRelease?.Version
                         ?? Mod.Version;
            return string.IsNullOrWhiteSpace(target) ? "—" : target!;
        }
    }

    public string? TargetUpdateVersion => _overrideRelease?.Version ?? Mod.LatestRelease?.Version;

    public bool CanSkip => !string.IsNullOrWhiteSpace(TargetUpdateVersion);

    private string? LatestServerInstallVersion =>
        Mod.LatestRelease?.Version ?? _overrideRelease?.Version ?? Mod.Version;

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

    public IReadOnlyList<ModListItemViewModel.ReleaseChangelog> Changelogs => Mod.NewerReleaseChangelogs;

    public int ChangelogCount => Mod.NewerReleaseChangelogs.Count;

    public bool HasChangelogs => Mod.HasNewerReleaseChangelogs;

    public string ChangelogHeader => ChangelogCount <= 1
        ? "Changelog"
        : $"Changelogs ({ChangelogCount})";

    public bool ShowExcludeVersionButton => !IsSelected && CanSkip;
}