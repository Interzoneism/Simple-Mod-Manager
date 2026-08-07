using System.Windows;
using VintageStoryModManager.ViewModels;

namespace VintageStoryModManager.Views.Dialogs;

public enum ModUsageNoIssuesDialogResult
{
    RemindLater,
    SubmitVotes,
    DisableTracking
}

public partial class ModUsageNoIssuesDialog : Window
{
    private readonly List<ModUsageVoteCandidateViewModel> _candidates;

    public ModUsageNoIssuesDialog(IEnumerable<ModUsageVoteCandidateViewModel> candidates)
    {
        InitializeComponent();

        if (candidates is null) throw new ArgumentNullException(nameof(candidates));

        _candidates = candidates.Where(candidate => candidate is not null).ToList();
        DataContext = this;
    }

    public IReadOnlyList<ModUsageVoteCandidateViewModel> Candidates => _candidates;

    public IReadOnlyList<ModUsageVoteCandidateViewModel> SelectedCandidates { get; private set; } =
        Array.Empty<ModUsageVoteCandidateViewModel>();

    public ModUsageNoIssuesDialogResult Result { get; private set; } = ModUsageNoIssuesDialogResult.RemindLater;

    private void SubmitButton_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = _candidates.Where(candidate => candidate.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusTextBlock.Text = "Select at least one mod to submit votes.";
            return;
        }

        SelectedCandidates = selected;
        Result = ModUsageNoIssuesDialogResult.SubmitVotes;
        DialogResult = true;
    }

    private void RemindMeLaterButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedCandidates = Array.Empty<ModUsageVoteCandidateViewModel>();
        Result = ModUsageNoIssuesDialogResult.RemindLater;
        DialogResult = false;
    }

    private void NeverAskAgainButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedCandidates = Array.Empty<ModUsageVoteCandidateViewModel>();
        Result = ModUsageNoIssuesDialogResult.DisableTracking;
        DialogResult = true;
    }
}