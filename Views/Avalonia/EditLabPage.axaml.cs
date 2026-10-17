using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SLSKDONET.Services.EditLab;
using SLSKDONET.ViewModels.EditLab;

namespace SLSKDONET.Views.Avalonia;

public partial class EditLabPage : UserControl
{
    private EditLabViewModel? _vm;

    public EditLabPage()
    {
        InitializeComponent();
        foreach (var box in new[] { ProjectNameBox, BpmBox, KeyBox })
            box.KeyDown += OnCommitBoxKeyDown;
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public EditLabPage(EditLabViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm = DataContext as EditLabViewModel;
    }

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm != null) _ = _vm.RefreshRecentAsync();
        Dispatcher.UIThread.Post(() => Focus());
    }

    protected override void OnDetachedFromVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Leaving the Edit Lab: stop the preview and save.
        _vm?.OnViewDeactivated();
    }

    // ── Pickers and lists ──────────────────────────────────────────────────

    private void OnRecentProjectClick(object? sender, RoutedEventArgs e)
    {
        if (_vm != null && (sender as Control)?.DataContext is EditProjectSummary summary)
            _vm.OpenProjectCommand.Execute(summary).Subscribe();
    }

    private void OnDeleteRecentProjectClick(object? sender, RoutedEventArgs e)
    {
        if (_vm != null && (sender as Control)?.DataContext is EditProjectSummary summary)
            _vm.DeleteProjectCommand.Execute(summary).Subscribe();
    }

    private void OnPickerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || PickerList.SelectedItem is not EditLibraryHit hit) return;
        PickerList.SelectedItem = null;
        AddTrackButton.Flyout?.Hide();
        _vm.AddTrackCommand.Execute(hit).Subscribe();
    }

    private void OnOpenPickerClick(object? sender, RoutedEventArgs e)
    {
        AddTrackButton.Flyout?.ShowAt(AddTrackButton);
        Dispatcher.UIThread.Post(() => PickerSearchBox.Focus(), DispatcherPriority.Background);
    }

    // ── Keyboard ───────────────────────────────────────────────────────────

    private void OnCommitBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape)
        {
            Focus(); // LostFocus commits the binding
            e.Handled = true;
        }
    }

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is global::Avalonia.Visual v &&
            (v.FindAncestorOfType<TextBox>(includeSelf: true) != null ||
             v.FindAncestorOfType<NumericUpDown>(includeSelf: true) != null ||
             v.FindAncestorOfType<ComboBox>(includeSelf: true) != null))
            return;
        if (e.Source is global::Avalonia.Visual a && a.FindAncestorOfType<global::SLSKDONET.Views.Avalonia.Controls.EditArrangerControl>(includeSelf: true) != null)
            return; // the arranger takes focus itself (Esc cancels a drag)
        Dispatcher.UIThread.Post(() => { if (!IsTextInputFocused()) Focus(); });
    }

    private bool IsTextInputFocused() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _vm is not { HasProject: true } vm) return;
        if (e.Source is TextBox || IsTextInputFocused()) return;

        var mods = e.KeyModifiers;
        bool none = mods == KeyModifiers.None;
        bool ctrl = mods == KeyModifiers.Control;
        bool ctrlShift = mods == (KeyModifiers.Control | KeyModifiers.Shift);
        bool handled = true;

        switch (e.Key)
        {
            case Key.Space when none:
                vm.PlayPauseCommand.Execute().Subscribe();
                break;
            case Key.Delete or Key.Back when none && vm.HasSelectedClip:
                vm.DeleteClipCommand.Execute().Subscribe();
                break;
            case Key.Z when ctrl:
                vm.UndoCommand.Execute().Subscribe();
                break;
            case Key.Y when ctrl:
            case Key.Z when ctrlShift:
                vm.RedoCommand.Execute().Subscribe();
                break;
            case Key.D when ctrl && vm.HasSelectedClip:
                vm.DuplicateClipCommand.Execute().Subscribe();
                break;
            case Key.S when none && vm.HasSelectedClip:
                vm.SplitClipCommand.Execute().Subscribe();
                break;
            case Key.L when none && vm.HasSelectedClip:
                vm.LoopToSelectionCommand.Execute().Subscribe();
                break;
            case Key.Home when none:
                vm.StopCommand.Execute().Subscribe();
                break;
            case Key.Escape when none:
                vm.SelectClip(null);
                break;
            default:
                handled = false;
                break;
        }
        if (handled) e.Handled = true;
    }
}
