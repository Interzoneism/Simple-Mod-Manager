namespace VintageStoryModManager.Models;

/// <summary>
///     Represents a cloud modlist slot entry exposed to the UI.
/// </summary>
public sealed class CloudModlistSlot
{
    public CloudModlistSlot(string slotKey, bool isOccupied, string displayName, string? name, string? version,
        string? cachedContent)
    {
        SlotKey = slotKey ?? throw new ArgumentNullException(nameof(slotKey));
        IsOccupied = isOccupied;
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        Name = name;
        Version = version;
        CachedContent = cachedContent;
    }

    public string SlotKey { get; }

    public bool IsOccupied { get; }

    public string DisplayName { get; }

    public string? Name { get; }

    public string? Version { get; }

    public string? CachedContent { get; }

    public override string ToString()
    {
        return DisplayName;
    }
}