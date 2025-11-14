namespace VintageStoryModManager.Models;

/// <summary>
///     Represents the available user report options for a mod version.
/// </summary>
public enum ModVersionVoteOption
{
    FullyFunctional,
    NoIssuesSoFar,
    SomeIssuesButWorks,
    NotFunctional,
    CrashesOrFreezesGame
}

/// <summary>
///     Aggregated vote counts for each <see cref="ModVersionVoteOption" /> value.
/// </summary>
public sealed class ModVersionVoteCounts
{
    public ModVersionVoteCounts(
        int fullyFunctional,
        int noIssuesSoFar,
        int someIssuesButWorks,
        int notFunctional,
        int crashesOrFreezesGame)
    {
        FullyFunctional = Math.Max(0, fullyFunctional);
        NoIssuesSoFar = Math.Max(0, noIssuesSoFar);
        SomeIssuesButWorks = Math.Max(0, someIssuesButWorks);
        NotFunctional = Math.Max(0, notFunctional);
        CrashesOrFreezesGame = Math.Max(0, crashesOrFreezesGame);
    }

    public int FullyFunctional { get; }

    public int NoIssuesSoFar { get; }

    public int SomeIssuesButWorks { get; }

    public int NotFunctional { get; }

    public int CrashesOrFreezesGame { get; }

    public int Total => FullyFunctional + NoIssuesSoFar + SomeIssuesButWorks + NotFunctional + CrashesOrFreezesGame;

    public static ModVersionVoteCounts Empty { get; } = new(0, 0, 0, 0, 0);

    public int GetCount(ModVersionVoteOption option)
    {
        return option switch
        {
            ModVersionVoteOption.FullyFunctional => FullyFunctional,
            ModVersionVoteOption.NoIssuesSoFar => NoIssuesSoFar,
            ModVersionVoteOption.SomeIssuesButWorks => SomeIssuesButWorks,
            ModVersionVoteOption.NotFunctional => NotFunctional,
            ModVersionVoteOption.CrashesOrFreezesGame => CrashesOrFreezesGame,
            _ => 0
        };
    }
}

/// <summary>
///     Captures the user report summary for a mod version at a particular Vintage Story version.
/// </summary>
public sealed class ModVersionVoteSummary
{
    public ModVersionVoteSummary(
        string modId,
        string modVersion,
        string? vintageStoryVersion,
        ModVersionVoteCounts counts,
        ModVersionVoteComments comments,
        ModVersionVoteOption? userVote,
        string? userComment)
    {
        ModId = modId ?? throw new ArgumentNullException(nameof(modId));
        ModVersion = modVersion ?? throw new ArgumentNullException(nameof(modVersion));
        VintageStoryVersion = vintageStoryVersion;
        Counts = counts ?? throw new ArgumentNullException(nameof(counts));
        Comments = comments ?? throw new ArgumentNullException(nameof(comments));
        UserVote = userVote;
        UserComment = userComment;
    }

    public string ModId { get; }

    public string ModVersion { get; }

    public string? VintageStoryVersion { get; }

    public ModVersionVoteCounts Counts { get; }

    public ModVersionVoteOption? UserVote { get; }

    public string? UserComment { get; }

    public ModVersionVoteComments Comments { get; }

    public int TotalVotes => Counts.Total;

    public ModVersionVoteOption? GetMajorityOption()
    {
        var fullyFunctional = Counts.FullyFunctional;
        var noIssues = Counts.NoIssuesSoFar;
        var issues = Counts.SomeIssuesButWorks;
        var notFunctional = Counts.NotFunctional;
        var crashes = Counts.CrashesOrFreezesGame;

        var max = Math.Max(
            fullyFunctional,
            Math.Max(
                noIssues,
                Math.Max(issues, Math.Max(notFunctional, crashes))));
        if (max == 0) return null;

        var fullyFunctionalIsMax = fullyFunctional == max;
        var noIssuesIsMax = noIssues == max;
        var issuesIsMax = issues == max;
        var notFunctionalIsMax = notFunctional == max;
        var crashesIsMax = crashes == max;

        var duplicates = 0;
        ModVersionVoteOption? candidate = null;

        if (fullyFunctionalIsMax)
        {
            duplicates++;
            candidate = ModVersionVoteOption.FullyFunctional;
        }

        if (noIssuesIsMax)
        {
            duplicates++;
            candidate = ModVersionVoteOption.NoIssuesSoFar;
        }

        if (issuesIsMax)
        {
            duplicates++;
            candidate = ModVersionVoteOption.SomeIssuesButWorks;
        }

        if (notFunctionalIsMax)
        {
            duplicates++;
            candidate = ModVersionVoteOption.NotFunctional;
        }

        if (crashesIsMax)
        {
            duplicates++;
            candidate = ModVersionVoteOption.CrashesOrFreezesGame;
        }

        if (duplicates == 1) return candidate;

        if (duplicates == 2 && fullyFunctionalIsMax && noIssuesIsMax) return ModVersionVoteOption.FullyFunctional;

        return null;
    }
}

public static class ModVersionVoteOptionExtensions
{
    public static string ToDisplayString(this ModVersionVoteOption option)
    {
        return option switch
        {
            ModVersionVoteOption.FullyFunctional => "Fully functional",
            ModVersionVoteOption.NoIssuesSoFar => "No issues noticed",
            ModVersionVoteOption.SomeIssuesButWorks => "Some issues but works",
            ModVersionVoteOption.NotFunctional => "Not functional",
            ModVersionVoteOption.CrashesOrFreezesGame => "Crashes/Freezes game",
            _ => option.ToString() ?? string.Empty
        };
    }

    public static bool RequiresComment(this ModVersionVoteOption option)
    {
        return option switch
        {
            ModVersionVoteOption.NotFunctional => true,
            ModVersionVoteOption.CrashesOrFreezesGame => true,
            _ => false
        };
    }
}

public sealed class ModVersionVoteComments
{
    public ModVersionVoteComments(IReadOnlyList<string> notFunctional, IReadOnlyList<string> crashesOrFreezesGame)
    {
        NotFunctional = notFunctional ?? Array.Empty<string>();
        CrashesOrFreezesGame = crashesOrFreezesGame ?? Array.Empty<string>();
    }

    public IReadOnlyList<string> NotFunctional { get; }

    public IReadOnlyList<string> CrashesOrFreezesGame { get; }

    public static ModVersionVoteComments Empty { get; } = new(Array.Empty<string>(), Array.Empty<string>());
}