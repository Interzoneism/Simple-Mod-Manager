using CommunityToolkit.Mvvm.ComponentModel;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.ViewModels;

/// <summary>
///     Represents a selectable mod version in the version picker.
/// </summary>
public sealed class ModVersionOptionViewModel : ObservableObject
{
    private bool _isInstalled;

    private ModVersionOptionViewModel(
        string version,
        string? normalizedVersion,
        ModReleaseInfo? release,
        bool isCompatible,
        bool isInstalled,
        bool isFromDatabase)
    {
        Version = version;
        NormalizedVersion = normalizedVersion;
        Release = release;
        IsCompatible = isCompatible;
        _isInstalled = isInstalled;
        IsFromDatabase = isFromDatabase;
    }

    public string Version { get; }

    public string? NormalizedVersion { get; }

    public ModReleaseInfo? Release { get; }

    public bool IsCompatible { get; }

    public bool IsFromDatabase { get; }

    public bool HasRelease => Release != null;

    public string VersionDisplay => string.IsNullOrWhiteSpace(Version) ? "Unknown" : Version;

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (SetProperty(ref _isInstalled, value)) OnPropertyChanged(nameof(SelectionDisplay));
        }
    }

    public string SelectionDisplay => IsInstalled
        ? $"{VersionDisplay} (Installed)"
        : VersionDisplay;

    public override string ToString()
    {
        return SelectionDisplay;
    }

    public static ModVersionOptionViewModel FromRelease(ModReleaseInfo release, bool isInstalled)
    {
        ArgumentNullException.ThrowIfNull(release);

        return new ModVersionOptionViewModel(
            release.Version,
            release.NormalizedVersion,
            release,
            release.IsCompatibleWithInstalledGame,
            isInstalled,
            true);
    }

    public static ModVersionOptionViewModel FromInstalledVersion(string version, string? normalizedVersion)
    {
        if (string.IsNullOrWhiteSpace(version)) version = "Unknown";

        return new ModVersionOptionViewModel(
            version,
            normalizedVersion,
            null,
            false,
            true,
            false);
    }
}