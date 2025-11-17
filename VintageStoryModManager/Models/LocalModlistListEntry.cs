using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace VintageStoryModManager.Models;

/// <summary>
///     Represents a local modlist entry prepared for display in the UI.
/// </summary>
public sealed class LocalModlistListEntry
{
    public LocalModlistListEntry(
        string filePath,
        string? name,
        string? description,
        string? version,
        string? uploader,
        IReadOnlyList<string> mods,
        DateTimeOffset? lastModified,
        string? gameVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        Uploader = string.IsNullOrWhiteSpace(uploader) ? null : uploader.Trim();
        Mods = mods?.ToList() ?? throw new ArgumentNullException(nameof(mods));
        LastModified = lastModified;
        GameVersion = string.IsNullOrWhiteSpace(gameVersion) ? null : gameVersion.Trim();
    }

    public string FilePath { get; }

    public string FileName { get; }

    public string? Name { get; }

    public string? Description { get; }

    public string? Version { get; }

    public string? Uploader { get; }

    public IReadOnlyList<string> Mods { get; }

    public DateTimeOffset? LastModified { get; }

    public string? GameVersion { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? Path.GetFileNameWithoutExtension(FileName)
        : Name!;

    public string UploaderDisplay => string.IsNullOrWhiteSpace(Uploader) ? "—" : Uploader!;

    public string ModsSummary => Mods.Count == 0
        ? "No mods"
        : Mods.Count == 1
            ? "1 mod"
            : $"{Mods.Count} mods";

    public string LastModifiedDisplay => LastModified?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—";

    public string GameVersionDisplay => string.IsNullOrWhiteSpace(GameVersion) ? "—" : GameVersion!;
}
