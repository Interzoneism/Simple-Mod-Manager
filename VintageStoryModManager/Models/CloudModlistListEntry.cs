using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VintageStoryModManager.Models;

/// <summary>
/// Represents a cloud modlist entry prepared for display in the UI.
/// </summary>
public sealed class CloudModlistListEntry
{
    public CloudModlistListEntry(
        string ownerId,
        string slotKey,
        string slotLabel,
        string? name,
        string? description,
        string? version,
        string? uploader,
        IReadOnlyList<string> mods,
        string contentJson,
        DateTimeOffset? dateAdded)
    {
        OwnerId = ownerId ?? throw new ArgumentNullException(nameof(ownerId));
        SlotKey = slotKey ?? throw new ArgumentNullException(nameof(slotKey));
        SlotLabel = slotLabel ?? throw new ArgumentNullException(nameof(slotLabel));
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        Uploader = string.IsNullOrWhiteSpace(uploader) ? OwnerId : uploader.Trim();
        Mods = mods?.ToList() ?? throw new ArgumentNullException(nameof(mods));
        ContentJson = contentJson ?? throw new ArgumentNullException(nameof(contentJson));
        DateAdded = dateAdded;
    }

    public string OwnerId { get; }

    public string SlotKey { get; }

    public string SlotLabel { get; }

    public string? Name { get; }

    public string? Description { get; }

    public string? Version { get; }

    public string Uploader { get; }

    public IReadOnlyList<string> Mods { get; }

    public string ContentJson { get; }

    public DateTimeOffset? DateAdded { get; }

    public string DisplayName => Name ?? "Unnamed Modlist";

    public string ModsSummary => Mods.Count == 0
        ? "No mods"
        : Mods.Count == 1
            ? "1 mod"
            : $"{Mods.Count} mods";

    public string DateAddedDisplay => DateAdded?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—";
}
