using System.Windows;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.Views.Dialogs;

public partial class ModlistGroupConflictDialog : Window
{
    public ModlistGroupConflictDialog(Window owner, IReadOnlyList<ModlistGroupConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);

        InitializeComponent();
        Owner = owner;
        Selections = conflicts.Select(conflict => new ConflictSelection(conflict)).ToList();
        DataContext = this;
    }

    public IReadOnlyList<ConflictSelection> Selections { get; }

    public IReadOnlyDictionary<string, string> Resolutions => Selections.ToDictionary(
        selection => selection.ModId,
        selection => selection.SelectedChoice.MemberReference,
        StringComparer.OrdinalIgnoreCase);

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    public sealed class ConflictSelection
    {
        public ConflictSelection(ModlistGroupConflict conflict)
        {
            ModId = conflict.ModId;
            Choices = conflict.Choices;
            SelectedChoice = conflict.Choices.FirstOrDefault(choice => string.Equals(
                                 choice.MemberReference,
                                 conflict.SelectedMemberReference,
                                 StringComparison.OrdinalIgnoreCase))
                             ?? conflict.Choices[0];
        }

        public string ModId { get; }

        public IReadOnlyList<ModlistGroupConflictChoice> Choices { get; }

        public ModlistGroupConflictChoice SelectedChoice { get; set; }
    }
}
