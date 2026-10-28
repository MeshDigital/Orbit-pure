using System;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia;

public partial class CueForgePage : UserControl
{
    private IDisposable? _trackSubscription;
    private IDisposable? _cueSelectionSubscription;
    private IDisposable? _dockSubscription;
    private CueForgeViewModel? _vm;
    private bool _usingTabs;
    private bool _heightCollapsed;
    private double _preparationHeight = 272;
    private RowDefinition PreparationSplitterRow => WorkspaceGrid.RowDefinitions[4];
    private RowDefinition PreparationRow => WorkspaceGrid.RowDefinitions[5];

    public CueForgePage()
    {
        InitializeComponent();
        PreparationTabs.Items.Add(new TabItem { Header = "Hot cues & loops" });
        PreparationTabs.Items.Add(new TabItem { Header = "Cues" });
        PreparationTabs.Items.Add(new TabItem { Header = "Selected cue" });

        CueNameBox.KeyDown += OnCueNameKeyDown;
        CueNameBox.LostFocus += (_, _) => _vm?.Session.CommitCueName();
        Waveform.ScrubbingChanged += (_, scrubbing) => _vm?.SetScrubbing(scrubbing);
        Waveform.ContextRequested += OnWaveformContextRequested;
        WorkspaceGrid.SizeChanged += (_, _) => ApplyResponsiveLayout();
        LayersButton.Click += (_, _) => PreparationButton.Flyout?.Hide();
        PreparationButton.Click += (_, _) => LayersButton.Flyout?.Hide();
        PreparationSplitter.DragCompleted += (_, _) =>
        {
            if (PreparationRow.Height.Value >= 160) _preparationHeight = PreparationRow.Height.Value;
        };
        // Clicking anywhere that isn't a text field returns keyboard focus to the page, so the
        // shortcuts (Space, A, D, 1–8…) keep working after using a button or the waveform.
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public CueForgePage(CueForgeViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.Session.RenameRequested -= OnRenameRequested;
        _vm = DataContext as CueForgeViewModel;
        if (_vm != null) _vm.Session.RenameRequested += OnRenameRequested;
    }

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _trackSubscription?.Dispose();
        _cueSelectionSubscription?.Dispose();
        _dockSubscription?.Dispose();
        if (_vm != null)
        {
            _trackSubscription = _vm.WhenAnyValue(x => x.TrackHash)
                .Skip(1)
                .Subscribe(_ => Dispatcher.UIThread.Post(() => Focus()));
            _cueSelectionSubscription = _vm.WhenAnyValue(x => x.Session.SelectedCue)
                .Subscribe(cue => Dispatcher.UIThread.Post(() => { if (cue != null) CueList.ScrollIntoView(cue); }));
            _dockSubscription = _vm.WhenAnyValue(x => x.IsPreparationDockOpen)
                .Subscribe(_ => ApplyDockVisibility());
        }
        ApplyResponsiveLayout();
        Dispatcher.UIThread.Post(() => Focus());
    }

    protected override void OnDetachedFromVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _trackSubscription?.Dispose();
        _trackSubscription = null;
        _cueSelectionSubscription?.Dispose();
        _cueSelectionSubscription = null;
        _dockSubscription?.Dispose();
        _dockSubscription = null;
        // Leaving Cue Forge: stop loop audition and hand the shared player back.
        _vm?.OnViewDeactivated();
    }

    private void ApplyResponsiveLayout()
    {
        bool tabs = CueForgeResponsiveLayout.UseTabs(WorkspaceGrid.Bounds.Width);
        if (tabs != _usingTabs)
        {
            _usingTabs = tabs;
            ReparentPreparationPanels();
        }

        if (_vm?.HasTrackLoaded != true || WorkspaceGrid.Bounds.Height <= 0)
        {
            ApplyDockVisibility();
            return;
        }

        switch (CueForgeResponsiveLayout.HeightAction(WorkspaceGrid.Bounds.Height, _heightCollapsed))
        {
            case -1 when _vm.IsPreparationDockOpen:
                if (PreparationRow.Height.Value >= 160) _preparationHeight = PreparationRow.Height.Value;
                _heightCollapsed = true;
                _vm.IsPreparationDockOpen = false;
                break;
            case 1:
                _heightCollapsed = false;
                _vm.IsPreparationDockOpen = true;
                break;
        }
        ApplyDockVisibility();
    }

    private void ReparentPreparationPanels()
    {
        if (_usingTabs)
        {
            PreparationColumns.Children.Clear();
            for (int i = 0; i < PreparationTabs.Items.Count; i++)
                ((TabItem)PreparationTabs.Items[i]!).Content = new[] { PadsPanel, CueListPanel, InspectorPanel }[i];
        }
        else
        {
            foreach (var item in PreparationTabs.Items) ((TabItem)item!).Content = null;
            PreparationColumns.Children.Add(PadsPanel);
            PreparationColumns.Children.Add(CueListPanel);
            PreparationColumns.Children.Add(InspectorPanel);
            Grid.SetColumn(PadsPanel, 0);
            Grid.SetColumn(CueListPanel, 1);
            Grid.SetColumn(InspectorPanel, 2);
        }
        ApplyDockVisibility();
    }

    private void ApplyDockVisibility()
    {
        bool open = _vm?.IsPreparationDockOpen == true;
        double height = _preparationHeight;
        if (open && WorkspaceGrid.Bounds.Height > 0)
        {
            // A manual dock request must not push the panels below the viewport. Preserve at
            // least the detail waveform's minimum height, then give the remaining space to the dock.
            double fixedHeight = WorkspaceGrid.RowDefinitions[0].ActualHeight
                + WorkspaceGrid.RowDefinitions[1].ActualHeight
                + WorkspaceGrid.RowDefinitions[3].ActualHeight
                + 182; // 160px waveform + vertical margins + splitter.
            height = Math.Max(160, Math.Min(_preparationHeight, WorkspaceGrid.Bounds.Height - fixedHeight));
        }
        PreparationRow.Height = open ? new GridLength(height) : new GridLength(0);
        PreparationSplitterRow.Height = open ? new GridLength(6) : new GridLength(0);
        PreparationColumns.IsVisible = open && !_usingTabs;
        PreparationTabs.IsVisible = open && _usingTabs;
    }

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Interactive controls keep their focus. Pulling focus back to the page on PointerPressed
        // can close a Button flyout before its layers, preparation actions or checkboxes are used.
        if (e.Source is global::Avalonia.Visual v &&
            (v.FindAncestorOfType<TextBox>(includeSelf: true) != null ||
             v.FindAncestorOfType<Button>(includeSelf: true) != null ||
             v.FindAncestorOfType<CheckBox>(includeSelf: true) != null ||
             v.FindAncestorOfType<ComboBox>(includeSelf: true) != null ||
             v.FindAncestorOfType<Slider>(includeSelf: true) != null ||
             v.FindAncestorOfType<global::Avalonia.Controls.Primitives.ScrollBar>(includeSelf: true) != null ||
             v.FindAncestorOfType<TabItem>(includeSelf: true) != null)) return;
        Dispatcher.UIThread.Post(() => { if (!IsTextInputFocused()) Focus(); });
    }

    private bool IsTextInputFocused() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    // ── Keyboard ───────────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _vm is not { } vm) return;
        // Typing in a text field (cue name, filter) must never trigger editing shortcuts.
        if (e.Source is TextBox || IsTextInputFocused()) return;
        if (!vm.HasTrackLoaded) return;

        var mods = e.KeyModifiers;
        bool none = mods == KeyModifiers.None;
        bool shift = mods == KeyModifiers.Shift;
        bool ctrl = mods == KeyModifiers.Control;
        bool handled = true;

        int pad = PadIndex(e.Key);
        if (pad >= 0 && none)
        {
            vm.Session.TriggerPadCommand.Execute(vm.Session.HotCuePads[pad]).Subscribe();
        }
        else if ((e.Key is Key.D1 or Key.NumPad1 or Key.D2 or Key.NumPad2) && shift)
        {
            vm.Session.SetNumberedDropAtPlayheadCommand.Execute(e.Key is Key.D1 or Key.NumPad1 ? 1 : 2).Subscribe();
        }
        else switch (e.Key)
        {
            case Key.Left or Key.Right when vm.Session.SelectedCue != null && (none || shift || ctrl):
                string sign = e.Key == Key.Left ? "-" : "+";
                string unit = ctrl ? "fine" : shift ? "bar" : "beat";
                vm.Session.NudgeCueCommand.Execute(sign + unit).Subscribe();
                break;
            case Key.Up when none:
            case Key.Left when none:
                vm.PreviousCueCommand.Execute().Subscribe();
                break;
            case Key.Down when none:
            case Key.Right when none:
                vm.NextCueCommand.Execute().Subscribe();
                break;
            case Key.Space when none:
                // A cue being prelistened (clicked flag) stops; otherwise normal play / pause.
                if (!vm.Session.StopPrelisten()) vm.PlayPauseCommand.Execute().Subscribe();
                break;
            case Key.I when shift:
                vm.Session.SetIntroEndAtPlayheadCommand.Execute().Subscribe();
                break;
            case Key.O when shift:
                vm.Session.SetOutroStartAtPlayheadCommand.Execute().Subscribe();
                break;
            case Key.C when none:
                vm.CueButtonCommand.Execute().Subscribe();
                break;
            case Key.A when none:
                vm.Session.AddCueAtPlayheadCommand.Execute().Subscribe();
                break;
            case Key.D when none:
                vm.Session.SetDropAtPlayheadCommand.Execute().Subscribe();
                break;
            case Key.I when none:
                vm.Session.SetLoopInCommand.Execute().Subscribe();
                break;
            case Key.O when none:
                vm.Session.SetLoopOutCommand.Execute().Subscribe();
                break;
            case Key.OemOpenBrackets when none:
                vm.BeatJumpCommand.Execute(-1).Subscribe();
                break;
            case Key.OemCloseBrackets when none:
                vm.BeatJumpCommand.Execute(1).Subscribe();
                break;
            case Key.F when none:
                vm.FollowPlayhead = !vm.FollowPlayhead;
                break;
            case Key.OemPlus or Key.Add when none || shift:
                vm.ZoomInCommand.Execute().Subscribe();
                break;
            case Key.OemMinus or Key.Subtract when none:
                vm.ZoomOutCommand.Execute().Subscribe();
                break;
            case Key.Delete or Key.Back when none && vm.Session.SelectedCue != null:
                vm.Session.DeleteSelectedCueCommand.Execute().Subscribe();
                break;
            case Key.Enter when none && vm.Session.SelectedCue != null:
                vm.AuditionCueCommand.Execute(vm.Session.SelectedCue).Subscribe();
                break;
            case Key.F2 when none && vm.Session.SelectedCue != null:
                vm.Session.RequestRename(vm.Session.SelectedCue);
                break;
            case Key.Escape when none:
                vm.Session.SelectedCue = null;
                break;
            case Key.S when ctrl:
                vm.Session.SaveCommand.Execute().Subscribe();
                break;
            case Key.Z when ctrl:
                vm.Session.UndoCommand.Execute().Subscribe();
                break;
            case Key.Y when ctrl:
                vm.Session.RedoCommand.Execute().Subscribe();
                break;
            default:
                handled = false;
                break;
        }
        if (handled) e.Handled = true;
    }

    private static int PadIndex(Key key) => key switch
    {
        >= Key.D1 and <= Key.D8 => key - Key.D1,
        >= Key.NumPad1 and <= Key.NumPad8 => key - Key.NumPad1,
        _ => -1,
    };

    // ── Rename ─────────────────────────────────────────────────────────────

    private void OnRenameRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        CueNameBox.Focus();
        CueNameBox.SelectAll();
    }, DispatcherPriority.Background);

    private void OnCueNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key == Key.Enter)
        {
            _vm.Session.CommitCueName();
            Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.Session.RevertCueName();
            Focus();
            e.Handled = true;
        }
    }

    // ── Track picker ───────────────────────────────────────────────────────

    private void OnPickTrackClick(object? sender, RoutedEventArgs e)
    {
        if (_vm != null) _vm.IsBrowserOpen = true;
    }

    // ── Context menus (built here so every item talks to the view model directly) ──────

    private void OnPadContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is not { } vm || sender is not Control { DataContext: HotCuePadInfo pad } target) return;
        var menu = new ContextMenu();
        if (pad.Cue is { } cue)
        {
            menu.Items.Add(Item("Play from here", () => vm.Session.TriggerPadCommand.Execute(pad).Subscribe()));
            menu.Items.Add(Item("Rename…", () => vm.Session.RequestRename(cue)));
            menu.Items.Add(ColourMenu(vm, cue));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Clear pad (keep as memory cue)", () => vm.Session.ClearPadCommand.Execute(pad).Subscribe()));
            menu.Items.Add(Item("Delete cue", () => vm.Session.DeleteCueCommand.Execute(cue).Subscribe()));
        }
        else
        {
            menu.Items.Add(Item($"Set hot cue {pad.PadLabel} at playhead", () => vm.Session.TriggerPadCommand.Execute(pad).Subscribe()));
        }
        menu.Open(target);
        e.Handled = true;
    }

    private void OnCueRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is not { } vm || sender is not Control { DataContext: OrbitCue cue } target) return;
        vm.Session.SelectedCue = cue;
        var menu = new ContextMenu();
        menu.Items.Add(Item("Audition", () => vm.AuditionCueCommand.Execute(cue).Subscribe()));
        menu.Items.Add(Item("Move playhead here", () => vm.SeekPlayheadCommand.Execute(cue.Timestamp).Subscribe()));
        menu.Items.Add(Item("Rename…", () => vm.Session.RequestRename(cue)));
        menu.Items.Add(ColourMenu(vm, cue));
        if (!cue.IsLoop)
        {
            var slots = new MenuItem { Header = "Pad" };
            foreach (var option in CueEditorSession.CueSlotOptions)
            {
                var o = option;
                slots.Items.Add(Item(o.Label, () => { vm.Session.SelectedCue = cue; vm.Session.SetSelectedCueSlotCommand.Execute(o.Value).Subscribe(); }));
            }
            menu.Items.Add(slots);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete", () => vm.Session.DeleteCueCommand.Execute(cue).Subscribe()));
        menu.Open(target);
        e.Handled = true;
    }

    private void OnCueRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is { } vm && sender is Control { DataContext: OrbitCue cue })
            vm.SeekPlayheadCommand.Execute(cue.Timestamp).Subscribe();
    }

    private void OnWaveformContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is not { } vm || !vm.HasTrackLoaded) return;
        double t = e.TryGetPosition(Waveform, out var p) ? Waveform.TimeAt(p) : vm.CurrentPlayPosition;
        var menu = new ContextMenu();
        menu.Items.Add(Item($"Add cue here ({CueForgeGrid.FormatTime(vm.Session.SnapTime(t))})", () => vm.Session.AddCueAtCommand.Execute(t).Subscribe()));
        menu.Items.Add(Item("Mark drop here", () => vm.Session.SetDropAtCommand.Execute(t).Subscribe()));
        menu.Items.Add(Item($"Intro ends here ({CueForgeGrid.FormatTime(vm.Session.SnapSectionTime(t))})", () => vm.Session.SetIntroEndAtCommand.Execute(t).Subscribe()));
        menu.Items.Add(Item($"Outro starts here ({CueForgeGrid.FormatTime(vm.Session.SnapSectionTime(t))})", () => vm.Session.SetOutroStartAtCommand.Execute(t).Subscribe()));
        menu.Items.Add(Item("Move playhead here", () => vm.SeekPlayheadCommand.Execute(t).Subscribe()));
        menu.Open(Waveform);
        e.Handled = true;
    }

    private static MenuItem ColourMenu(CueForgeViewModel vm, OrbitCue cue)
    {
        var colours = new MenuItem { Header = "Colour" };
        foreach (var hex in CueEditorSession.CueColorSwatches)
        {
            var h = hex;
            var swatch = new Border
            {
                Width = 14, Height = 14, CornerRadius = new global::Avalonia.CornerRadius(2),
                Background = new SolidColorBrush(Color.Parse(h)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            colours.Items.Add(new MenuItem
            {
                Header = h,
                Icon = swatch,
                Command = ReactiveCommand.Create(() =>
                {
                    vm.Session.SelectedCue = cue;
                    vm.Session.SetSelectedCueColorCommand.Execute(h).Subscribe();
                }),
            });
        }
        return colours;
    }

    private static MenuItem Item(string header, Action action) =>
        new() { Header = header, Command = ReactiveCommand.Create(action) };
}
