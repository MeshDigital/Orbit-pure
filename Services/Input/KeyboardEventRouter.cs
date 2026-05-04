using System;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Services.Audio;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Workstation;
using SLSKDONET.Views;
using Unit = System.Reactive.Unit;

namespace SLSKDONET.Services.Input;

/// <summary>
/// Subscribes to the main window's key-down tunnel, maps events via
/// <see cref="IKeyboardMappingService"/>, and dispatches to the relevant
/// ViewModel commands (Epic #119, Tasks 5-8).
///
/// Text inputs (TextBox, AutoCompleteBox) suppress routing so keyboard
/// typing is never interrupted.
/// </summary>
public sealed class KeyboardEventRouter : IDisposable
{
    private readonly IKeyboardMappingService      _mapping;
    private readonly WorkstationViewModel         _workstation;
    private readonly Lazy<MainViewModel>          _mainVm;
    private readonly ILogger<KeyboardEventRouter> _logger;

    private TopLevel? _topLevel;
    private bool      _disposed;

    public KeyboardEventRouter(
        IKeyboardMappingService      mapping,
        WorkstationViewModel         workstation,
        Lazy<MainViewModel>          mainVm,
        ILogger<KeyboardEventRouter> logger)
    {
        _mapping     = mapping;
        _workstation = workstation;
        _mainVm      = mainVm;
        _logger      = logger;
    }

    // ─── Attachment ───────────────────────────────────────────────────────────

    /// <summary>
    /// Attach to <paramref name="topLevel"/> (main window) using a tunnel handler.
    /// Safe to call multiple times — only the first call attaches.
    /// </summary>
    public void Attach(TopLevel topLevel)
    {
        if (_topLevel != null) return;
        _topLevel = topLevel;
        topLevel.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _logger.LogInformation("[KeyboardRouter] Attached to TopLevel");
    }

    /// <summary>
    /// Process a key-down event directly (e.g. from <see cref="GlobalHotkeyService"/>).
    /// Returns true when the event was consumed.
    /// </summary>
    public bool HandleKeyDown(KeyEventArgs e)
    {
        OnKeyDown(null, e);
        return e.Handled;
    }

    // ─── Core routing ─────────────────────────────────────────────────────────

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Suppress when a text control has focus
        if (_topLevel?.FocusManager?.GetFocusedElement() is TextBox or AutoCompleteBox)
            return;

        var focusedDeck = _workstation.FocusedDeck;
        int focusedDeckNumber = focusedDeck?.DeckLabel switch
        {
            "A" => 1, "B" => 2, "C" => 3, "D" => 4, _ => 0
        };

        // Use ResolveBinding so deck-specific bindings (Rekordbox 4-deck preset)
        // are routed to their hardwired deck regardless of focus.
        var binding = _mapping.ResolveBinding(e.Key, e.KeyModifiers, focusedDeckNumber);
        if (binding == null) return;

        var targetDeck = binding.Deck == 0
            ? focusedDeck
            : GetDeckByNumber(binding.Deck);

        _logger.LogDebug("[KeyboardRouter] {Action} (deck={Deck})", binding.Action, binding.Deck);
        e.Handled = DispatchAction(binding.Action, targetDeck);
    }

    private WorkstationDeckViewModel? GetDeckByNumber(int deckNumber)
    {
        string label = deckNumber switch { 1 => "A", 2 => "B", 3 => "C", 4 => "D", _ => "" };
        return _workstation.Decks.FirstOrDefault(d => d.DeckLabel == label);
    }

    // ─── Action dispatch ──────────────────────────────────────────────────────

    private bool DispatchAction(KeyboardAction action, WorkstationDeckViewModel? deck)
    {
        var slot = deck?.Deck;

        switch (action)
        {
            // ── Playback ─────────────────────────────────────────────────────
            case KeyboardAction.PlayPause: return ExecUnit(slot?.PlayPauseCommand);
            case KeyboardAction.Cue:       return ExecUnit(slot?.CueCommand);

            // ── Hot cues – jump ──────────────────────────────────────────────
            case KeyboardAction.HotCue1: return ExecParam(slot?.JumpToHotCueCommand, 0);
            case KeyboardAction.HotCue2: return ExecParam(slot?.JumpToHotCueCommand, 1);
            case KeyboardAction.HotCue3: return ExecParam(slot?.JumpToHotCueCommand, 2);
            case KeyboardAction.HotCue4: return ExecParam(slot?.JumpToHotCueCommand, 3);
            case KeyboardAction.HotCue5: return ExecParam(slot?.JumpToHotCueCommand, 4);
            case KeyboardAction.HotCue6: return ExecParam(slot?.JumpToHotCueCommand, 5);
            case KeyboardAction.HotCue7: return ExecParam(slot?.JumpToHotCueCommand, 6);
            case KeyboardAction.HotCue8: return ExecParam(slot?.JumpToHotCueCommand, 7);

            // ── Hot cues – set ───────────────────────────────────────────────
            case KeyboardAction.SetHotCue1: return ExecParam(slot?.SetHotCueCommand, 0);
            case KeyboardAction.SetHotCue2: return ExecParam(slot?.SetHotCueCommand, 1);
            case KeyboardAction.SetHotCue3: return ExecParam(slot?.SetHotCueCommand, 2);
            case KeyboardAction.SetHotCue4: return ExecParam(slot?.SetHotCueCommand, 3);
            case KeyboardAction.SetHotCue5: return ExecParam(slot?.SetHotCueCommand, 4);
            case KeyboardAction.SetHotCue6: return ExecParam(slot?.SetHotCueCommand, 5);
            case KeyboardAction.SetHotCue7: return ExecParam(slot?.SetHotCueCommand, 6);
            case KeyboardAction.SetHotCue8: return ExecParam(slot?.SetHotCueCommand, 7);

            // ── Loop ─────────────────────────────────────────────────────────
            case KeyboardAction.LoopIn:          return ExecUnit(slot?.SetLoopCommand);
            case KeyboardAction.LoopOut:         return ExecUnit(slot?.ExitLoopCommand);
            case KeyboardAction.LoopExit:        return ExecUnit(_workstation.ExitLoopFocusedCommand);
            case KeyboardAction.HalfLoop:        return ExecUnit(slot?.HalfLoopCommand);
            case KeyboardAction.DoubleLoop:      return ExecUnit(slot?.DoubleLoopCommand);
            case KeyboardAction.LoopMoveForward: return ExecParam(slot?.MoveLoopCommand, 1);
            case KeyboardAction.LoopMoveBack:    return ExecParam(slot?.MoveLoopCommand, -1);
            case KeyboardAction.LoopRoll1:  return ActivateLoopRoll(slot, 1);
            case KeyboardAction.LoopRoll2:  return ActivateLoopRoll(slot, 2);
            case KeyboardAction.LoopRoll4:  return ActivateLoopRoll(slot, 4);
            case KeyboardAction.LoopRoll8:  return ActivateLoopRoll(slot, 8);

            // ── Beat jump (cont.) ────────────────────────────────────────────
            case KeyboardAction.BeatJumpForward4: return BeatJump(slot, +4);

            // ── Beat jump (BPM-aware Seek) ───────────────────────────────────
            case KeyboardAction.BeatJumpForward1:  return BeatJump(slot, +1);
            case KeyboardAction.BeatJumpBack1:     return BeatJump(slot, -1);
            case KeyboardAction.BeatJumpForward2:  return BeatJump(slot, +2);
            case KeyboardAction.BeatJumpBack2:     return BeatJump(slot, -2);
            case KeyboardAction.BeatJumpBack4:     return BeatJump(slot, -4);
            case KeyboardAction.BeatJumpForward8:  return BeatJump(slot, +8);
            case KeyboardAction.BeatJumpBack8:     return BeatJump(slot, -8);
            case KeyboardAction.BeatJumpForward16: return BeatJump(slot, +16);
            case KeyboardAction.BeatJumpBack16:    return BeatJump(slot, -16);
            case KeyboardAction.BeatJumpForward32: return BeatJump(slot, +32);
            case KeyboardAction.BeatJumpBack32:    return BeatJump(slot, -32);

            // ── Pitch / tempo ────────────────────────────────────────────────
            case KeyboardAction.PitchBendUp:    return AdjustTempo(slot, +0.5);
            case KeyboardAction.PitchBendDown:  return AdjustTempo(slot, -0.5);
            case KeyboardAction.TempoUpSmall:   return AdjustTempo(slot, +0.1);
            case KeyboardAction.TempoDownSmall: return AdjustTempo(slot, -0.1);
            case KeyboardAction.TempoUpLarge:   return AdjustTempo(slot, +1.0);
            case KeyboardAction.TempoDownLarge: return AdjustTempo(slot, -1.0);
            case KeyboardAction.TempoRange:     return CycleTempoRange(slot);

            // ── Key / semitone ──────────────────────────────────────────────
            case KeyboardAction.KeyShiftUp:   return AdjustSemitone(slot, +1);
            case KeyboardAction.KeyShiftDown: return AdjustSemitone(slot, -1);
            case KeyboardAction.KeyReset:     return ResetSemitone(slot);
            case KeyboardAction.KeyLock:
                return ExecUnit(slot?.ToggleKeyLockCommand);
            case KeyboardAction.Sync:
            case KeyboardAction.MasterSync:
                return ExecUnit(_workstation.SyncBpmCommand);

            // ── Extended deck controls (Rekordbox parity) ────────────────────
            case KeyboardAction.JumpToBeginning:
                if (slot == null) return false;
                slot.Engine.Seek(0);
                slot.Engine.Cue();
                return true;

            case KeyboardAction.JumpForward:  return BeatJump(slot, +32);
            case KeyboardAction.JumpBackward: return BeatJump(slot, -32);

            case KeyboardAction.AutoCue: return ExecUnit(slot?.CueCommand);

            case KeyboardAction.VinylMode:
                if (slot == null) return false;
                slot.VinylMode = !slot.VinylMode;
                return true;

            case KeyboardAction.HeadphoneCue:
                if (slot == null) return false;
                slot.HeadphoneCue = !slot.HeadphoneCue;
                return true;

            case KeyboardAction.AutoBeatLoop: return AutoBeatLoop(slot);

            // ── Quantize (snap toggle) ───────────────────────────────────────
            case KeyboardAction.Quantize:
                _workstation.IsSnapEnabled = !_workstation.IsSnapEnabled;
                return true;

            // ── Browser / library (Task 7) ───────────────────────────────────
            // BrowseUp/Down use native arrow-key list navigation; return false so
            // Avalonia's default focus handling still fires.
            case KeyboardAction.BrowseUp:
            case KeyboardAction.BrowseDown:
                return false;

            case KeyboardAction.SearchFocus:
                ExecICommand(_mainVm.Value.FocusSearchCommand);
                return true;

            case KeyboardAction.LoadToDeck1:
                // LibraryViewModel exposes no SelectedTrack public property;
                // fire ActionTriggered so subscribers can handle the load.
                return false;

            case KeyboardAction.LoadToDeck2:
                return false;

            // ── Global navigation (Task 8) ────────────────────────────────────
            case KeyboardAction.NavigateWorkstation:
                return ExecICommand(_mainVm.Value.NavigateWorkstationCommand);

            case KeyboardAction.NavigateLibrary:
                return ExecICommand(_mainVm.Value.NavigateLibraryCommand);

            case KeyboardAction.NavigateDownloads:
                return ExecICommand(_mainVm.Value.NavigateDecksCommand);

            case KeyboardAction.NavigateSearch:
                return ExecICommand(_mainVm.Value.NavigateSearchCommand);

            case KeyboardAction.NavigateSettings:
            case KeyboardAction.OpenSettings:
                return ExecICommand(_mainVm.Value.NavigateSettingsCommand);

            // ── Overlay ───────────────────────────────────────────────────────
            case KeyboardAction.ShowKeyboardOverlay:
                _workstation.KeyboardOverlay.Toggle(_mapping);
                return true;

            default:
                return false;
        }
    }

    // ─── Dispatch helpers ─────────────────────────────────────────────────────

    /// Execute a ReactiveCommand&lt;Unit,Unit&gt;.
    private static bool ExecUnit(ReactiveCommand<Unit, Unit>? cmd)
    {
        if (cmd == null) return false;
        cmd.Execute().Subscribe();
        return true;
    }

    /// Execute a ReactiveCommand&lt;T,Unit&gt; with a parameter.
    private static bool ExecParam<T>(ReactiveCommand<T, Unit>? cmd, T param)
    {
        if (cmd == null) return false;
        cmd.Execute(param).Subscribe();
        return true;
    }

    /// Execute an ICommand (MainViewModel navigation commands).
    private static bool ExecICommand(ICommand? cmd, object? param = null)
    {
        if (cmd == null || !cmd.CanExecute(param)) return false;
        cmd.Execute(param);
        return true;
    }

    // ─── Audio helpers ────────────────────────────────────────────────────────

    /// Jump <paramref name="beats"/> beats forward (+) or backward (-) using
    /// BPM-adjusted Seek. Falls back gracefully when BPM is unknown.
    private static bool BeatJump(DeckSlotViewModel? slot, int beats)
    {
        if (slot == null || !slot.Engine.IsLoaded) return false;

        double bpm = slot.TrackBpm > 0
            ? slot.TrackBpm * (1.0 + slot.TempoPercent / 100.0)
            : 120.0;                             // safe fallback

        double beatSeconds = 60.0 / bpm;
        double newPos = Math.Clamp(
            slot.Engine.PositionSeconds + beats * beatSeconds,
            0,
            slot.Engine.DurationSeconds);

        slot.Engine.Seek(newPos);
        return true;
    }

    /// Set loop size and start a loop-roll on the active deck.
    private static bool ActivateLoopRoll(DeckSlotViewModel? slot, double beats)
    {
        if (slot == null) return false;
        slot.SetLoopBeatsCommand.Execute(beats).Subscribe();
        slot.LoopRollCommand.Execute().Subscribe();
        return true;
    }

    private static bool AdjustSemitone(DeckSlotViewModel? slot, int semitones)
    {
        if (slot == null) return false;
        slot.SemitoneShift = Math.Clamp(slot.SemitoneShift + semitones, -12, 12);
        return true;
    }

    private static bool ResetSemitone(DeckSlotViewModel? slot)
    {
        if (slot == null) return false;
        slot.SemitoneShift = 0;
        return true;
    }

    private static bool AutoBeatLoop(DeckSlotViewModel? slot)
    {
        if (slot == null || !slot.Engine.IsLoaded) return false;
        if (slot.Engine.Loop?.IsActive == true)
            slot.Engine.ExitLoop();
        else
        {
            double bpm      = slot.TrackBpm > 0 ? slot.TrackBpm * (1.0 + slot.TempoPercent / 100.0) : 120.0;
            double beatSecs = 60.0 / bpm;
            slot.Engine.SetLoop(beatSecs * slot.SelectedLoopBeats);
        }
        return true;
    }

    private static bool CycleTempoRange(DeckSlotViewModel? slot)
    {
        if (slot == null) return false;
        slot.PitchRange = slot.PitchRange switch
        {
            PitchRange.Narrow => PitchRange.Medium,
            PitchRange.Medium => PitchRange.Wide,
            _                 => PitchRange.Narrow,
        };
        return true;
    }

    private static bool AdjustTempo(DeckSlotViewModel? slot, double delta)
    {
        if (slot == null) return false;
        slot.TempoPercent = Math.Clamp(slot.TempoPercent + delta, -50.0, 50.0);
        return true;
    }

    // ─── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_topLevel != null)
        {
            _topLevel.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            _topLevel = null;
        }
    }
}
