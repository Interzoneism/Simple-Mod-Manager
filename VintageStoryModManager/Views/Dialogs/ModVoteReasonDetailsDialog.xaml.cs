using System.Globalization;
using System.Windows;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.Views.Dialogs;

public partial class ModVoteReasonDetailsDialog : Window
{
    public ModVoteReasonDetailsDialog(string modName, ModVersionVoteSummary summary)
    {
        InitializeComponent();
        ArgumentNullException.ThrowIfNull(summary);

        var modVersion = string.IsNullOrWhiteSpace(summary.ModVersion) ? "unknown" : summary.ModVersion;
        var gameVersion = string.IsNullOrWhiteSpace(summary.VintageStoryVersion)
            ? "unknown"
            : summary.VintageStoryVersion;

        Title = string.Format(CultureInfo.CurrentCulture, "User report reasons for {0}", modName);
        VersionDescription = string.Format(
            CultureInfo.CurrentCulture,
            "{0} {1} on Vintage Story {2}",
            modName,
            modVersion,
            gameVersion);
        ReasonGroups = BuildReasonGroups(summary);
        DataContext = this;
    }

    public string VersionDescription { get; }

    public IReadOnlyList<NegativeVoteReasonGroup> ReasonGroups { get; }

    private static IReadOnlyList<NegativeVoteReasonGroup> BuildReasonGroups(ModVersionVoteSummary summary)
    {
        List<NegativeVoteReasonGroup> groups = new(2);
        AddGroup(
            groups,
            ModVersionVoteOption.NotFunctional.ToDisplayString(),
            summary.Counts.NotFunctional,
            summary.Comments.NotFunctional);
        AddGroup(
            groups,
            ModVersionVoteOption.CrashesOrFreezesGame.ToDisplayString(),
            summary.Counts.CrashesOrFreezesGame,
            summary.Comments.CrashesOrFreezesGame);
        return groups;
    }

    private static void AddGroup(
        ICollection<NegativeVoteReasonGroup> groups,
        string category,
        int voteCount,
        IReadOnlyList<string> comments)
    {
        if (voteCount <= 0) return;

        List<NegativeVoteReason> reasons = new();
        Dictionary<string, NegativeVoteReason> reasonsByText = new(StringComparer.Ordinal);

        foreach (var comment in comments)
        {
            var reasonText = comment?.Trim();
            if (string.IsNullOrWhiteSpace(reasonText)) continue;

            if (reasonsByText.TryGetValue(reasonText, out var existing))
            {
                existing.Count++;
                continue;
            }

            NegativeVoteReason reason = new(reasonText, 1);
            reasons.Add(reason);
            reasonsByText.Add(reasonText, reason);
        }

        var reasonsProvided = reasons.Sum(reason => reason.Count);
        if (reasonsProvided < voteCount)
            reasons.Add(new NegativeVoteReason("No reason was provided.", voteCount - reasonsProvided));

        var voteLabel = voteCount == 1 ? "1 vote" : $"{voteCount} votes";
        groups.Add(new NegativeVoteReasonGroup($"{category} — {voteLabel}", reasons));
    }
}

public sealed record NegativeVoteReasonGroup(string Heading, IReadOnlyList<NegativeVoteReason> Reasons);

public sealed class NegativeVoteReason(string text, int count)
{
    public string Text { get; } = text;

    public int Count { get; set; } = count;

    public string CountDisplay => string.Format(CultureInfo.CurrentCulture, "×{0}", Count);
}
