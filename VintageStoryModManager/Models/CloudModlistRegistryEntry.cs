using System;

namespace VintageStoryModManager.Models;

/// <summary>
/// Represents a modlist entry stored in the global cloud registry.
/// </summary>
public sealed class CloudModlistRegistryEntry
{
    public CloudModlistRegistryEntry(string ownerId, string slotKey, string contentJson, DateTimeOffset? dateAdded)
    {
        OwnerId = ownerId ?? throw new ArgumentNullException(nameof(ownerId));
        SlotKey = slotKey ?? throw new ArgumentNullException(nameof(slotKey));
        ContentJson = contentJson ?? throw new ArgumentNullException(nameof(contentJson));
        DateAdded = dateAdded;
    }

    public string OwnerId { get; }

    public string SlotKey { get; }

    public string ContentJson { get; }

    public DateTimeOffset? DateAdded { get; }

    public string RegistryKey => $"{OwnerId}/{SlotKey}";
}
