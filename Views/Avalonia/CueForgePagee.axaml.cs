using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ReactiveUI;
using System.Reactive.Linq;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia;

public partial class CueForgePagee : UserControl
{
    public CueForgePagee()
    {
        InitializeComponent();
    }

    public CueForgePagee(CueForgeViewModel viewModel) : this()
    {
        DataContext = viewModel;

        // Keyboard shortcuts (Space, arrows, A, Ctrl+Z/Y...) are handled in OnKeyDown below,
        // which only fires while this control has focus. Without an explicit grab, focus stays
        // wherever it last was (e.g. a playlist filter TextBox) and shortcuts silently do nothing.
        // Both this page and CueForgeViewModel are DI singletons, so this subscription lives for
        // the app's lifetime — no disposal needed.
        viewModel.WhenAnyValue(x => x.TrackHash)
            .Skip(1)
            .Subscribe(_ => Dispatcher.UIThread.Post(() => Focus()));
    }

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => Focus());
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not CueForgeViewModel vm) return;

        var shift = e.KeyModifiers == KeyModifiers.Shift;
        var none  = e.KeyModifiers == KeyModifiers.None;

        switch (e.Key)
        {
            // Shift+Left/Right → nudge selected cue by ±1 beat
            case Key.Left when shift:
                vm.NudgeCueCommand.Execute(-1).Subscribe();
                e.Handled = true;
                break;
            case Key.Right when shift:
                vm.NudgeCueCommand.Execute(1).Subscribe();
                e.Handled = true;
                break;

            // Left/Right → jump playhead to previous/next cue
            case Key.Left when none:
                vm.PreviousCueCommand.Execute().Subscribe();
                e.Handled = true;
                break;
            case Key.Right when none:
                vm.NextCueCommand.Execute().Subscribe();
                e.Handled = true;
                break;

            // Delete → remove selected cue
            case Key.Delete when none:
                if (vm.SelectedCue != null)
                    vm.DeleteCueCommand.Execute(vm.SelectedCue).Subscribe();
                e.Handled = true;
                break;

            // Enter → audition selected cue with pre-roll
            case Key.Enter when none:
                if (vm.SelectedCue != null)
                    vm.AuditionCueCommand.Execute(vm.SelectedCue).Subscribe();
                e.Handled = true;
                break;

            // Space → toggle playback
            case Key.Space when none:
                vm.PlaybackToggleCommand?.Execute(null);
                e.Handled = true;
                break;

            // A → add cue at playhead
            case Key.A when none:
                vm.AddCueAtPlayheadCommand.Execute().Subscribe();
                e.Handled = true;
                break;

            // Z/Y → undo/redo
            case Key.Z when e.KeyModifiers == KeyModifiers.Control:
                vm.UndoCommand.Execute().Subscribe();
                e.Handled = true;
                break;
            case Key.Y when e.KeyModifiers == KeyModifiers.Control:
                vm.RedoCommand.Execute().Subscribe();
                e.Handled = true;
                break;
        }
    }

    private void OnCueRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is CueForgeViewModel vm && sender is Control { DataContext: OrbitCue cue })
            vm.SelectCueCommand.Execute(cue).Subscribe();
    }

    private void OnCueFieldLostFocus(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is CueForgeViewModel vm && sender is Control { DataContext: OrbitCue cue })
            vm.MarkCueFieldEdited(cue);
    }
}
