using System.Globalization;
using System.IO;
using System.Text.Json;

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

/// <summary>
///     Represents a modlist entry stored in the global cloud registry.
/// </summary>
public sealed class CloudModlistRegistryEntry
{
    public CloudModlistRegistryEntry(
        string ownerId,
        string slotKey,
        string contentJson,
        DateTimeOffset? dateAdded,
        bool isContentComplete = true)
    {
        OwnerId = ownerId ?? throw new ArgumentNullException(nameof(ownerId));
        SlotKey = slotKey ?? throw new ArgumentNullException(nameof(slotKey));
        ContentJson = contentJson ?? throw new ArgumentNullException(nameof(contentJson));
        DateAdded = dateAdded;
        IsContentComplete = isContentComplete;
    }

    public string OwnerId { get; }

    public string SlotKey { get; }

    public string ContentJson { get; }

    public DateTimeOffset? DateAdded { get; }

    public string RegistryKey => $"{OwnerId}/{SlotKey}";

    /// <summary>
    ///     Indicates whether <see cref="ContentJson" /> contains the full modlist payload
    ///     or a minimal summary that should be refreshed before installation.
    /// </summary>
    public bool IsContentComplete { get; }
}

/// <summary>
///     Represents a cloud modlist owned by the current user for management operations.
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
        if (!string.IsNullOrWhiteSpace(DisplayName)) return DisplayName;

        if (!string.IsNullOrWhiteSpace(Name)) return Name;

        return SlotLabel;
    }
}

/// <summary>
///     Represents a cloud modlist entry prepared for display in the UI.
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
        DateTimeOffset? dateAdded,
        string? gameVersion,
        bool isContentComplete)
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
        GameVersion = string.IsNullOrWhiteSpace(gameVersion) ? null : gameVersion.Trim();
        IsContentComplete = isContentComplete;
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

    public string? GameVersion { get; }

    public bool IsContentComplete { get; }

    public string DisplayName => Name ?? "Unnamed Modlist";

    public string ModsSummary => Mods.Count == 0
        ? "No mods"
        : Mods.Count == 1
            ? "1 mod"
            : $"{Mods.Count} mods";

    public string DateAddedDisplay => DateAdded?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—";

    public string GameVersionDisplay => string.IsNullOrWhiteSpace(GameVersion) ? "—" : GameVersion!;
}

/// <summary>
///     Minimal metadata required to display a cloud modlist in the UI.
/// </summary>
public sealed class CloudModlistSummary
{
    public static readonly CloudModlistSummary Empty = new(null, null, null, null, Array.Empty<ModReference>(), null);

    public CloudModlistSummary(
        string? name,
        string? description,
        string? version,
        string? uploader,
        IReadOnlyList<ModReference> mods,
        string? gameVersion)
    {
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        Uploader = string.IsNullOrWhiteSpace(uploader) ? null : uploader.Trim();
        Mods = mods ?? Array.Empty<ModReference>();
        GameVersion = string.IsNullOrWhiteSpace(gameVersion) ? null : gameVersion.Trim();
    }

    public string? Name { get; }

    public string? Description { get; }

    public string? Version { get; }

    public string? Uploader { get; }

    public IReadOnlyList<ModReference> Mods { get; }

    public string? GameVersion { get; }

    public static CloudModlistSummary FromJsonElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return Empty;

        var name = TryGetTrimmedProperty(element, "name");
        var description = TryGetTrimmedProperty(element, "description");
        var version = TryGetTrimmedProperty(element, "version");
        var uploader = TryGetTrimmedProperty(element, "uploader")
                       ?? TryGetTrimmedProperty(element, "uploaderName");
        var gameVersion = TryGetTrimmedProperty(element, "gameVersion")
                          ?? TryGetTrimmedProperty(element, "vsVersion");

        var mods = new List<ModReference>();
        if (element.TryGetProperty("mods", out var modsElement) && modsElement.ValueKind == JsonValueKind.Array)
            foreach (var mod in modsElement.EnumerateArray())
            {
                if (mod.ValueKind != JsonValueKind.Object) continue;

                var modId = TryGetTrimmedProperty(mod, "modId");
                if (string.IsNullOrWhiteSpace(modId)) continue;

                var modVersion = TryGetTrimmedProperty(mod, "version");
                mods.Add(new ModReference(modId, modVersion));
            }

        return new CloudModlistSummary(name, description, version, uploader, mods, gameVersion);
    }

    public object ToFirebasePayload(string dateAddedIso)
    {
        return new
        {
            content = new
            {
                name = Name,
                description = Description,
                version = Version,
                uploader = Uploader,
                uploaderName = Uploader,
                gameVersion = GameVersion,
                vsVersion = GameVersion,
                mods = Mods.Select(m => new { modId = m.ModId, version = m.Version })
            },
            dateAdded = dateAddedIso
        };
    }

    private static string? TryGetTrimmedProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property))
            if (property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }

        return null;
    }

    public readonly struct ModReference
    {
        public ModReference(string modId, string? version)
        {
            ModId = modId ?? throw new ArgumentNullException(nameof(modId));
            Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }

        public string ModId { get; }

        public string? Version { get; }
    }
}

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
