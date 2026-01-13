namespace VintageStoryModManager.Models;

/// <summary>
///     Represents a category for organizing mods in the mod list.
/// </summary>
public sealed class ModCategory
{
    /// <summary>
    ///     The ID used for the default "Uncategorized" category.
    /// </summary>
    public const string UncategorizedId = "uncategorized";

    /// <summary>
    ///     The display name for the default "Uncategorized" category.
    /// </summary>
    public const string UncategorizedName = "Uncategorized";

    /// <summary>
    ///     Creates a new category with a unique ID.
    /// </summary>
    public ModCategory(string name, int order = 0)
    {
        Id = Guid.NewGuid().ToString("N");
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Order = order;
    }

    /// <summary>
    ///     Creates a category with a specific ID (used for deserialization or default category).
    /// </summary>
    public ModCategory(string id, string name, int order, bool isProfileSpecific = false)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Order = order;
        IsProfileSpecific = isProfileSpecific;
    }

    /// <summary>
    ///     Parameterless constructor for JSON deserialization.
    /// </summary>
    public ModCategory()
    {
        Id = string.Empty;
        Name = string.Empty;
    }

    /// <summary>
    ///     Unique identifier for this category.
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    ///     Display name of the category.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    ///     Sort order for the category. Lower values appear first.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    ///     Whether this is the default "Uncategorized" category that cannot be deleted.
    /// </summary>
    public bool IsDefault => string.Equals(Id, UncategorizedId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Whether this category is specific to the current profile (vs global).
    /// </summary>
    public bool IsProfileSpecific { get; set; }

    /// <summary>
    ///     Creates the default "Uncategorized" category.
    /// </summary>
    public static ModCategory CreateDefault()
    {
        return new ModCategory(UncategorizedId, UncategorizedName, int.MaxValue);
    }

    /// <summary>
    ///     Creates a copy of this category.
    /// </summary>
    public ModCategory Clone()
    {
        return new ModCategory(Id, Name, Order, IsProfileSpecific);
    }

    public override string ToString()
    {
        return $"{Name} ({Id})";
    }

    public override bool Equals(object? obj)
    {
        return obj is ModCategory other && string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Id);
    }
}
