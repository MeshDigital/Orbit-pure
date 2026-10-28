using Avalonia.Controls;
using Avalonia.Input;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class TrackCueEditorStrip : UserControl
{
    public TrackCueEditorStrip()
    {
        InitializeComponent();
        // Same rename contract as Cue Forge: Enter / focus out applies, Esc cancels.
        CueNameBox.KeyDown += OnCueNameKeyDown;
        CueNameBox.LostFocus += (_, _) => Session?.CommitCueName();
    }

    private CueEditorSession? Session => (DataContext as TrackCueEditorViewModel)?.Session;

    private void OnCueNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (Session is not { } session) return;
        if (e.Key == Key.Enter)
        {
            session.CommitCueName();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            session.RevertCueName();
            e.Handled = true;
        }
    }
}
