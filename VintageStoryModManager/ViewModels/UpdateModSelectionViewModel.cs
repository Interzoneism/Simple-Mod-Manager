using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.ViewModels;

public sealed partial class UpdateModSelectionViewModel : ObservableObject
{
    private readonly ModReleaseInfo? _overrideRelease;

    public UpdateModSelectionViewModel(
        ModListItemViewModel mod,
        bool isSelected,
        ModReleaseInfo? overrideRelease)
    {
        Mod = mod ?? throw new ArgumentNullException(nameof(mod));
        _overrideRelease = overrideRelease;
        _isSelected = isSelected;
    }

    public ModListItemViewModel Mod { get; }

    public string DisplayName => Mod.DisplayName;

    public string InstalledVersionDisplay => string.IsNullOrWhiteSpace(Mod.Version) ? "—" : Mod.Version!;

    public string TargetVersionDisplay
    {
        get
        {
            string? target = _overrideRelease?.Version
                ?? Mod.LatestRelease?.Version
                ?? Mod.Version;
            return string.IsNullOrWhiteSpace(target) ? "—" : target!;
        }
    }

    public string? TargetUpdateVersion => _overrideRelease?.Version ?? Mod.LatestRelease?.Version;

    public bool CanSkip => !string.IsNullOrWhiteSpace(TargetUpdateVersion);

    public string VersionSummary
    {
        get
        {
            string installed = InstalledVersionDisplay;
            string target = TargetVersionDisplay;
            if (string.Equals(installed, target, StringComparison.OrdinalIgnoreCase))
            {
                return $"Installed: {installed}";
            }

            return $"Installed: {installed} → Update to: {target}";
        }
    }

    public IReadOnlyList<ModListItemViewModel.ReleaseChangelog> Changelogs => Mod.NewerReleaseChangelogs;

    public int ChangelogCount => Mod.NewerReleaseChangelogs.Count;

    public bool HasChangelogs => Mod.HasNewerReleaseChangelogs;

    public string ChangelogHeader => ChangelogCount <= 1
        ? "Changelog"
        : $"Changelogs ({ChangelogCount})";

    [ObservableProperty]
    private bool _isSelected;
}
