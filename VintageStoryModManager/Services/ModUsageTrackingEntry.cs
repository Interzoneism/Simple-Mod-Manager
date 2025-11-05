namespace VintageStoryModManager.Services;

public readonly struct ModUsageTrackingEntry
{
    public ModUsageTrackingEntry(
        string? modId,
        string? modVersion,
        string? gameVersion,
        bool canSubmitVote,
        bool hasUserVote)
    {
        ModId = string.IsNullOrWhiteSpace(modId) ? string.Empty : modId.Trim();
        ModVersion = string.IsNullOrWhiteSpace(modVersion) ? string.Empty : modVersion.Trim();
        GameVersion = string.IsNullOrWhiteSpace(gameVersion) ? string.Empty : gameVersion.Trim();
        CanSubmitVote = canSubmitVote;
        HasUserVote = hasUserVote;
    }

    public string ModId { get; }

    public string ModVersion { get; }

    public string GameVersion { get; }

    public bool CanSubmitVote { get; }

    public bool HasUserVote { get; }
}
