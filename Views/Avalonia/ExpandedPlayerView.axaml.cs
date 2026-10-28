using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia;

/// <summary>
/// Fullscreen player. Code-behind handles what bindings can't: the seek slider (a live
/// Position binding fought the user's drag every tick), keyboard control, keeping the queue
/// scrolled to the playing track, and fading the controls out while the mouse is idle.
/// </summary>
public partial class ExpandedPlayerView : UserControl
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3.5);

    private readonly Slider? _seekSlider;
    private readonly ListBox? _queueList;
    private readonly Panel?[] _fadeables;
    private readonly DispatcherTimer _idleTimer;
    private PlayerViewModel? _vm;
    private bool _seeking;

    public ExpandedPlayerView()
    {
        InitializeComponent();
        _seekSlider = this.FindControl<Slider>("SeekSlider");
        _queueList = this.FindControl<ListBox>("QueueList");
        _fadeables = new[] { this.FindControl<Panel>("Chrome"), this.FindControl<Panel>("Transport"), this.FindControl<Panel>("QueueColumn") };

        _idleTimer = new DispatcherTimer { Interval = IdleDelay };
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            if (_vm?.IsPlaying == true) SetIdle(true);
        };

        if (_seekSlider != null)
        {
            // Tunnel + handledEventsToo: the slider's thumb marks pointer events handled, so a
            // plain PointerReleased handler never fired after a drag (the old seek-on-release bug).
            _seekSlider.AddHandler(PointerPressedEvent, (_, _) => _seeking = true, RoutingStrategies.Tunnel, handledEventsToo: true);
            _seekSlider.AddHandler(PointerReleasedEvent, OnSeekReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            _seekSlider.AddHandler(PointerCaptureLostEvent, (_, _) => CommitSeek(), RoutingStrategies.Bubble, handledEventsToo: true);
        }

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (_, _) => WakeChrome(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => WakeChrome(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as PlayerViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            SyncSeekSlider();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.Position):
                SyncSeekSlider();
                break;
            case nameof(PlayerViewModel.IsExpandedPlayerOpen) when _vm?.IsExpandedPlayerOpen == true:
                // Take keyboard focus and show the controls whenever the player opens.
                Dispatcher.UIThread.Post(() =>
                {
                    Focus();
                    WakeChrome();
                    ScrollQueueToCurrent();
                }, DispatcherPriority.Background);
                break;
            case nameof(PlayerViewModel.CurrentQueueIndex):
                Dispatcher.UIThread.Post(ScrollQueueToCurrent, DispatcherPriority.Background);
                break;
            case nameof(PlayerViewModel.IsPlaying) when _vm?.IsPlaying == false:
                WakeChrome(); // paused: keep the controls visible
                break;
        }
    }

    private void SyncSeekSlider()
    {
        if (_seeking || _seekSlider == null || _vm == null) return;
        _seekSlider.Value = _vm.Position;
    }

    private void OnSeekReleased(object? sender, PointerReleasedEventArgs e) => CommitSeek();

    private void CommitSeek()
    {
        if (!_seeking) return;
        _seeking = false;
        if (_vm == null || _seekSlider == null) return;
        var value = (float)Math.Clamp(_seekSlider.Value, 0, 1);
        if (_vm.SeekCommand.CanExecute(value)) _vm.SeekCommand.Execute(value);
    }

    private void ScrollQueueToCurrent()
    {
        if (_vm == null || _queueList == null || !_queueList.IsEffectivelyVisible) return;
        var index = _vm.CurrentQueueIndex;
        if (index >= 0 && index < _vm.Queue.Count)
            _queueList.ScrollIntoView(index);
    }

    private void WakeChrome()
    {
        SetIdle(false);
        _idleTimer.Stop();
        if (_vm?.IsExpandedPlayerOpen == true) _idleTimer.Start();
    }

    private void SetIdle(bool idle)
    {
        foreach (var panel in _fadeables)
            panel?.Classes.Set("idle", idle);
        Cursor = idle ? new Cursor(StandardCursorType.None) : Cursor.Default;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm == null || !_vm.IsExpandedPlayerOpen) return;
        // Let text boxes (none today) keep their keys.
        if (e.Source is TextBox) return;

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Escape:
                Execute(_vm.ToggleExpandedPlayerCommand);
                break;
            case Key.Space:
                Execute(_vm.TogglePlayPauseCommand);
                break;
            case Key.Left:
                Execute(shift ? _vm.PreviousTrackCommand : _vm.SeekBackwardCommand);
                break;
            case Key.Right:
                Execute(shift ? _vm.NextTrackCommand : _vm.SeekForwardCommand);
                break;
            case Key.Up:
                _vm.Volume = Math.Min(100, _vm.Volume + 5);
                break;
            case Key.Down:
                _vm.Volume = Math.Max(0, _vm.Volume - 5);
                break;
            case Key.M:
                Execute(_vm.ToggleMuteCommand);
                break;
            case Key.Q:
                Execute(_vm.ToggleExpandedQueueCommand);
                break;
            case Key.F:
                Execute(_vm.ToggleTheaterModeCommand);
                break;
            case Key.V:
                if (shift) _vm.PreviousVisualizerPresetCommand.Execute().Subscribe();
                else _vm.CycleVisualizerPresetCommand.Execute().Subscribe();
                break;
            default:
                return;
        }
        WakeChrome();
        e.Handled = true;
    }

    private static void Execute(System.Windows.Input.ICommand? command)
    {
        if (command?.CanExecute(null) == true) command.Execute(null);
    }
}
