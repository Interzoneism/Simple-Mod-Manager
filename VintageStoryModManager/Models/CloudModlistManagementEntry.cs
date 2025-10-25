using System;

namespace VintageStoryModManager.Models;

/// <summary>
/// Represents a cloud modlist owned by the current user for management operations.
/// </summary>
public sealed class CloudModlistManagementEntry
{
    public CloudModlistManagementEntry(
        string slotKey,
        string slotLabel,
        string? name,
        string? version,
        string displayName,
        string? cachedContent)
    {
        SlotKey = slotKey ?? throw new ArgumentNullException(nameof(slotKey));
        SlotLabel = slotLabel ?? throw new ArgumentNullException(nameof(slotLabel));
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        CachedContent = cachedContent;
    }

    public string SlotKey { get; }

    public string SlotLabel { get; }

    public string? Name { get; }

    public string? Version { get; }

    public string DisplayName { get; }

    public string? CachedContent { get; }

    public string EffectiveName => Name ?? "Unnamed Modlist";

    public override string ToString()
    {
        if (!string.IsNullOrWhiteSpace(DisplayName))
        {
            return DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(Name))
        {
            return Name;
        }

        return SlotLabel;
    }
}
