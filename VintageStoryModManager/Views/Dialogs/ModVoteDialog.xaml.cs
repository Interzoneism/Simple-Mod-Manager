using System.Globalization;
using System.Windows;
using VintageStoryModManager.Models;
using VintageStoryModManager.Services;
using VintageStoryModManager.ViewModels;
using WpfButton = System.Windows.Controls.Button;

namespace VintageStoryModManager.Views.Dialogs;

public partial class ModVoteDialog : Window
{
    private static readonly IReadOnlyList<string> NotFunctionalReasons = new[]
    {
        "The main function of the mod doesn't work",
        "It has major performance issues",
        "GUI or visual issues that makes it unplayable",
        "Supposed to work in multiplayer but doesn't",
        "Other"
    };

    private static readonly IReadOnlyList<string> CrashReasons = new[]
    {
        "Crash with error on game start",
        "Crash with error during gameplay",
        "Crash/freeze straight to desktop on game start",
        "Crash/freeze straight to desktop during gameplay",
        "Breaks so much you might as well call it a crash"
    };

    private readonly Func<ModVersionVoteOption?, string?, Task<ModVersionVoteSummary?>> _submitVoteAsync;
    private bool _isSubmitting;

    private ModVersionVoteSummary _summary;

    public ModVoteDialog(
        ModListItemViewModel mod,
        ModVersionVoteSummary summary,
        Func<ModVersionVoteOption?, string?, Task<ModVersionVoteSummary?>> submitVoteAsync)
    {
        InitializeComponent();

        ArgumentNullException.ThrowIfNull(mod);
        _summary = summary ?? throw new ArgumentNullException(nameof(summary));
        _submitVoteAsync = submitVoteAsync ?? throw new ArgumentNullException(nameof(submitVoteAsync));

        Title = string.Format(CultureInfo.CurrentCulture, "User reports for {0}", mod.DisplayName);
        TitleTextBlock.Text = Title;
        ModVersionTextBlock.Text = string.IsNullOrWhiteSpace(mod.VersionDisplay)
            ? "Mod version: Unknown"
            : string.Format(CultureInfo.CurrentCulture, "Mod version: {0}", mod.VersionDisplay);

        var gameVersion = _summary.VintageStoryVersion;
        if (string.IsNullOrWhiteSpace(gameVersion))
            GameVersionTextBlock.Text = "Vintage Story version: Unknown";
        else
            GameVersionTextBlock.Text =
                string.Format(CultureInfo.CurrentCulture, "Vintage Story version: {0}", gameVersion);

        UpdateOptionButtons();
        StatusTextBlock.Text = BuildStatusText();
    }

    private void UpdateOptionButtons()
    {
        UpdateOptionButton(FullyFunctionalButton, ModVersionVoteOption.FullyFunctional);
        UpdateOptionButton(NoIssuesButton, ModVersionVoteOption.NoIssuesSoFar);
        UpdateOptionButton(SomeIssuesButton, ModVersionVoteOption.SomeIssuesButWorks);
        UpdateOptionButton(NotFunctionalButton, ModVersionVoteOption.NotFunctional);
        UpdateOptionButton(CrashesButton, ModVersionVoteOption.CrashesOrFreezesGame);
    }

    private void UpdateOptionButton(WpfButton button, ModVersionVoteOption option)
    {
        var count = _summary.Counts.GetCount(option);
        var countLabel = count == 1
            ? "1 vote"
            : string.Format(CultureInfo.CurrentCulture, "{0} votes", count);
        button.Content = string.Format(
            CultureInfo.CurrentCulture,
            "{0} — {1}",
            option.ToDisplayString(),
            countLabel);

        button.FontWeight = _summary.UserVote == option ? FontWeights.Bold : FontWeights.Normal;
        button.IsEnabled = !_isSubmitting;
        button.ToolTip = option.RequiresComment()
            ? "Selecting this option will ask you to choose a reason."
            : null;
    }

    private string BuildStatusText()
    {
        var total = _summary.TotalVotes;
        var totalLabel = total == 1
            ? "1 total vote"
            : string.Format(CultureInfo.CurrentCulture, "{0} total votes", total);
        return string.Format(
            CultureInfo.CurrentCulture,
            "{0}. Click an option to submit or update your vote.",
            totalLabel);
    }

    private async void VoteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || button.Tag is not ModVersionVoteOption option) return;

        if (_isSubmitting) return;

        ModVersionVoteOption? requestedOption = option;
        var isRemovingVote = _summary.UserVote == option;
        if (isRemovingVote) requestedOption = null;

        string? comment = null;
        if (requestedOption.HasValue && requestedOption.Value.RequiresComment())
        {
            comment = PromptForReason(requestedOption.Value, _summary.UserComment);
            if (comment is null)
            {
                StatusTextBlock.Text = "Vote cancelled.";
                return;
            }
        }

        _isSubmitting = true;
        UpdateOptionButtons();
        StatusTextBlock.Text = isRemovingVote ? "Removing vote…" : "Submitting vote…";

        try
        {
            var result = await _submitVoteAsync(requestedOption, comment).ConfigureAwait(true);
            if (result is not null) _summary = result;

            var statusPrefix = requestedOption.HasValue
                ? "Your vote has been recorded."
                : "Your vote has been removed.";

            StatusTextBlock.Text = string.Format(
                CultureInfo.CurrentCulture,
                "{0} {1}",
                statusPrefix,
                BuildStatusText());
        }
        catch (InternetAccessDisabledException ex)
        {
            StatusTextBlock.Text = ex.Message;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = string.Format(
                CultureInfo.CurrentCulture,
                "Failed to submit vote: {0}",
                ex.Message);
        }
        finally
        {
            _isSubmitting = false;
            UpdateOptionButtons();
        }
    }

    private string? PromptForReason(ModVersionVoteOption option, string? existingReason)
    {
        var reasons = option switch
        {
            ModVersionVoteOption.NotFunctional => NotFunctionalReasons,
            ModVersionVoteOption.CrashesOrFreezesGame => CrashReasons,
            _ => Array.Empty<string>()
        };

        var title = option switch
        {
            ModVersionVoteOption.NotFunctional => "Why is the mod not functional?",
            ModVersionVoteOption.CrashesOrFreezesGame => "Tell us about the crash",
            _ => "Share more details"
        };

        var description = option switch
        {
            ModVersionVoteOption.NotFunctional =>
                "Pick the option that best matches why the mod isn't working for you.",
            ModVersionVoteOption.CrashesOrFreezesGame =>
                "Pick the option that best matches the crash or freeze you experienced.",
            _ => "Pick the option that best matches your experience."
        };

        string? initialSelection = null;
        if (!string.IsNullOrWhiteSpace(existingReason) && reasons.IndexOf(existingReason) >= 0)
            initialSelection = existingReason;

        ModVoteReasonDialog dialog = new(title, description, reasons, initialSelection)
        {
            Owner = this
        };

        var result = dialog.ShowDialog();
        return result == true ? dialog.SelectedReason : null;
    }
}