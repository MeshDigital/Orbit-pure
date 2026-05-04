using System;
using System.Reactive;
using System.Reactive.Disposables;
using Avalonia.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ReactiveUI;
using SLSKDONET.Services.Audio;

namespace SLSKDONET.ViewModels;

// ─── DeckSlotViewModel ────────────────────────────────────────────────────────

/// <summary>
/// ViewModel wrapper for a single <see cref="DeckEngine"/>. Exposes all bindable
/// properties and ReactiveCommands for one DJ deck (A or B).
/// </summary>
public sealed class DeckSlotViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();

    public DeckEngine Engine { get; }

    // ─── Track info ───────────────────────────────────────────────────────────

    private string? _loadedFilePath;
    public string?  LoadedFilePath
    {
        get => _loadedFilePath;
        private set => this.RaiseAndSetIfChanged(ref _loadedFilePath, value);
    }

    private double _trackBpm;
    /// <summary>
    /// Native (file) BPM of the loaded track as reported by the analysis database.
    /// Required by <see cref="BpmSyncService"/> and for beat-length loop calculations.
    /// </summary>
    public double TrackBpm
    {
        get => _trackBpm;
        set => this.RaiseAndSetIfChanged(ref _trackBpm, value);
    }

    // ─── Playback state ───────────────────────────────────────────────────────

    private DeckState _deckState = DeckState.Stopped;
    public DeckState DeckState
    {
        get => _deckState;
        private set
        {
            this.RaiseAndSetIfChanged(ref _deckState, value);
            this.RaisePropertyChanged(nameof(IsPlaying));
        }
    }

    public bool IsPlaying => DeckState == DeckState.Playing;

    private double _positionSeconds;
    public double  PositionSeconds
    {
        get => _positionSeconds;
        private set => this.RaiseAndSetIfChanged(ref _positionSeconds, value);
    }

    private double _durationSeconds;
    public double  DurationSeconds
    {
        get => _durationSeconds;
        private set => this.RaiseAndSetIfChanged(ref _durationSeconds, value);
    }

    // ─── Pitch / Tempo (Task 5.2) ─────────────────────────────────────────────

    private double _tempoPercent;
    /// <summary>Pitch-fader value in percent. Clamped to ±<see cref="PitchRange"/>.</summary>
    public double TempoPercent
    {
        get => _tempoPercent;
        set
        {
            double clamped = Math.Clamp(value, -(double)PitchRange, (double)PitchRange);
            this.RaiseAndSetIfChanged(ref _tempoPercent, clamped);
            Engine.TempoPercent = clamped;
        }
    }

    private PitchRange _pitchRange = PitchRange.Narrow;
    /// <summary>Hardware-fader throw: Narrow ±8%, Medium ±16%, Wide ±50%.</summary>
    public PitchRange PitchRange
    {
        get => _pitchRange;
        set
        {
            this.RaiseAndSetIfChanged(ref _pitchRange, value);
            // Re-clamp tempo to new range limits
            TempoPercent = TempoPercent;
        }
    }

    private bool _keyLock;
    /// <summary>
    /// Key-lock: changes tempo without changing musical key.
    /// Implemented via SmbPitchShiftingSampleProvider with inverse pitch factor.
    /// </summary>
    public bool KeyLock
    {
        get => _keyLock;
        set
        {
            this.RaiseAndSetIfChanged(ref _keyLock, value);
            Engine.KeyLock = value;
        }
    }

    private int _semitoneShift;
    /// <summary>Pitch shift in semitones (−12 to +12). 0 = original key. Works independently of key-lock.</summary>
    public int SemitoneShift
    {
        get => _semitoneShift;
        set
        {
            this.RaiseAndSetIfChanged(ref _semitoneShift, value);
            Engine.SemitoneShift = value;
        }
    }

    private bool _vinylMode;
    /// <summary>Vinyl mode vs CDJ mode toggle. When true, jog-wheel scratches like vinyl.</summary>
    public bool VinylMode
    {
        get => _vinylMode;
        set => this.RaiseAndSetIfChanged(ref _vinylMode, value);
    }

    private bool _headphoneCue;
    /// <summary>Whether this deck is routed to the headphone monitor (PFL).</summary>
    public bool HeadphoneCue
    {
        get => _headphoneCue;
        set => this.RaiseAndSetIfChanged(ref _headphoneCue, value);
    }

    // ─── Loop state (Task 5.4) ────────────────────────────────────────────────

    private double _selectedLoopBeats = 4.0;
    /// <summary>Loop-size selection in beats. Valid values: 0.5, 1, 2, 4, 8, 16.</summary>
    public double SelectedLoopBeats
    {
        get => _selectedLoopBeats;
        set => this.RaiseAndSetIfChanged(ref _selectedLoopBeats, value);
    }

    private bool _isLoopActive;
    public bool  IsLoopActive
    {
        get => _isLoopActive;
        private set => this.RaiseAndSetIfChanged(ref _isLoopActive, value);
    }

    // ─── Hot cues (Task 5.5) ──────────────────────────────────────────────────

    /// <summary>8-slot array; null = unset. Bound to the pad grid in the deck strip.</summary>
    public HotCue?[] HotCues => Engine.HotCues;

    // ─── Focus / keyboard routing ─────────────────────────────────────────────

    private bool _isFocused;
    /// <summary>
    /// When true, keyboard 1–8 presses are routed to this deck's hot-cue pads.
    /// </summary>
    public bool IsFocused
    {
        get => _isFocused;
        set => this.RaiseAndSetIfChanged(ref _isFocused, value);
    }

    // ─── Commands ─────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit>      PlayPauseCommand       { get; }
    public ReactiveCommand<Unit, Unit>      CueCommand             { get; }
    public ReactiveCommand<double, Unit>    SeekCommand            { get; }

    // Pitch / key-lock
    public ReactiveCommand<PitchRange, Unit> SetPitchRangeCommand  { get; }
    public ReactiveCommand<Unit, Unit>       ToggleKeyLockCommand   { get; }

    // Loop controls
    public ReactiveCommand<double, Unit>    SetLoopBeatsCommand    { get; }
    public ReactiveCommand<Unit, Unit>      SetLoopCommand         { get; }
    public ReactiveCommand<Unit, Unit>      ExitLoopCommand        { get; }
    public ReactiveCommand<Unit, Unit>      HalfLoopCommand        { get; }
    public ReactiveCommand<Unit, Unit>      DoubleLoopCommand      { get; }
    public ReactiveCommand<int, Unit>       MoveLoopCommand        { get; }
    public ReactiveCommand<Unit, Unit>      LoopRollCommand        { get; }

    // Hot cues (slot is 0-based, 0–7)
    public ReactiveCommand<int, Unit>       SetHotCueCommand       { get; }
    public ReactiveCommand<int, Unit>       JumpToHotCueCommand    { get; }
    public ReactiveCommand<int, Unit>       DeleteHotCueCommand    { get; }

    // ─── Constructor ──────────────────────────────────────────────────────────

    public DeckSlotViewModel(string deckName, DeckEngine engine)
    {
        Engine = engine;

        engine.StateChanged += (_, _) =>
            Dispatcher.UIThread.Post(() => DeckState = engine.State);

        engine.LoopChanged += (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                IsLoopActive = engine.Loop?.IsActive == true;
                this.RaisePropertyChanged(nameof(HotCues));
            });

        engine.HotCueChanged += (_, _) =>
            Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(HotCues)));

        // ── Playback
        PlayPauseCommand = ReactiveCommand.Create(() =>
        {
            if (engine.State == DeckState.Playing) engine.Pause(); else engine.Play();
        });
        CueCommand  = ReactiveCommand.Create(() => engine.Cue());
        SeekCommand = ReactiveCommand.Create<double>(pos => engine.Seek(pos));

        // ── Pitch / key-lock
        SetPitchRangeCommand = ReactiveCommand.Create<PitchRange>(r => PitchRange = r);
        ToggleKeyLockCommand = ReactiveCommand.Create(() => { KeyLock = !KeyLock; });

        // ── Loop
        SetLoopBeatsCommand = ReactiveCommand.Create<double>(b => SelectedLoopBeats = b);

        SetLoopCommand = ReactiveCommand.Create(() =>
        {
            if (_trackBpm <= 0) return;
            double effectiveBpm = _trackBpm * (1 + _tempoPercent / 100.0);
            double beatSecs     = 60.0 / effectiveBpm;
            engine.SetLoop(SelectedLoopBeats * beatSecs);
        });

        ExitLoopCommand   = ReactiveCommand.Create(() => engine.ExitLoop());
        HalfLoopCommand   = ReactiveCommand.Create(() => engine.HalfLoop());
        DoubleLoopCommand = ReactiveCommand.Create(() => engine.DoubleLoop());
        MoveLoopCommand   = ReactiveCommand.Create<int>(dir => engine.MoveLoop(dir));

        LoopRollCommand = ReactiveCommand.Create(() =>
        {
            if (_trackBpm <= 0) return;
            double effectiveBpm = _trackBpm * (1 + _tempoPercent / 100.0);
            double beatSecs     = 60.0 / effectiveBpm;
            engine.ActivateLoopRoll(SelectedLoopBeats * beatSecs);
        });

        // ── Hot cues
        SetHotCueCommand    = ReactiveCommand.Create<int>(slot => engine.SetHotCue(slot));
        JumpToHotCueCommand = ReactiveCommand.Create<int>(slot => engine.JumpToHotCue(slot));
        DeleteHotCueCommand = ReactiveCommand.Create<int>(slot => engine.DeleteHotCue(slot));
    }

    // ─── Public methods ───────────────────────────────────────────────────────

    /// <summary>
    /// Loads a track file and stores the analyzed BPM for beat-sync calculations.
    /// </summary>
    public void LoadTrack(string filePath, double trackBpm = 0)
    {
        Engine.LoadFile(filePath);
        LoadedFilePath  = filePath;
        TrackBpm        = trackBpm;
        DurationSeconds = Engine.DurationSeconds;
        PositionSeconds = 0;
    }

    /// <summary>Called by the position-poll timer; updates PositionSeconds on the UI thread.</summary>
    internal void PollPosition()
    {
        double pos = Engine.PositionSeconds;
        if (Math.Abs(pos - _positionSeconds) > 0.01)
            Dispatcher.UIThread.Post(() => PositionSeconds = pos);
    }

    /// <summary>
    /// Routes a keyboard hot-cue press to this deck (1-based slot, 1–8).
    /// </summary>
    public void HandleHotKeyPress(int oneBasedSlot) =>
        Engine.JumpToHotCue(oneBasedSlot - 1);

    public void Dispose()
    {
        _disposables.Dispose();
        Engine.Dispose();
    }
}

// ─── DeckViewModel ────────────────────────────────────────────────────────────

/// <summary>
/// Dual-deck DJ ViewModel: owns Deck A + Deck B, the crossfader, BPM sync engine,
/// and the NAudio output device that mixes both decks.
///
/// Register as <c>AddSingleton&lt;DeckViewModel&gt;</c> in DI.
/// Call <see cref="StartOutputCommand"/> once to open the audio device.
/// </summary>
public sealed class DeckViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable    _disposables  = new();
    private readonly BpmSyncService         _syncService  = new();
    private readonly MixingSampleProvider   _mixer;
    private WaveOutEvent?                   _waveOut;
    private readonly System.Timers.Timer    _positionPoll;
    private bool                            _isDisposed;

    public DeckSlotViewModel DeckA { get; }
    public DeckSlotViewModel DeckB { get; }

    // ─── Crossfader ───────────────────────────────────────────────────────────

    private float _crossfaderPosition = 0.5f;
    /// <summary>
    /// Hardware crossfader: 0.0 = full Deck A, 0.5 = center (equal power), 1.0 = full Deck B.
    /// Uses cos/sin equal-power law to maintain constant perceived loudness.
    /// </summary>
    public float CrossfaderPosition
    {
        get => _crossfaderPosition;
        set
        {
            float clamped = Math.Clamp(value, 0f, 1f);
            this.RaiseAndSetIfChanged(ref _crossfaderPosition, clamped);
            ApplyCrossfader(clamped);
        }
    }

    // ─── Sync (Task 5.3) ──────────────────────────────────────────────────────

    private DeckSide _masterDeck = DeckSide.A;
    /// <summary>Which deck sets the master tempo for SYNC operations.</summary>
    public DeckSide MasterDeck
    {
        get => _masterDeck;
        set => this.RaiseAndSetIfChanged(ref _masterDeck, value);
    }

    private double _phaseOffsetBeats;
    /// <summary>
    /// Phase difference A−B in fractional beats, wrapped to [−0.5, +0.5].
    /// 0 = perfectly aligned; ±0.5 = half a beat apart.
    /// Bind to a phase-indicator waveform or needle control.
    /// </summary>
    public double PhaseOffsetBeats
    {
        get => _phaseOffsetBeats;
        private set => this.RaiseAndSetIfChanged(ref _phaseOffsetBeats, value);
    }

    // ─── Output state ─────────────────────────────────────────────────────────

    private bool _isOutputActive;
    public bool  IsOutputActive
    {
        get => _isOutputActive;
        private set => this.RaiseAndSetIfChanged(ref _isOutputActive, value);
    }

    // ─── Commands ─────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> StartOutputCommand { get; }
    public ReactiveCommand<Unit, Unit> StopOutputCommand  { get; }

    /// <summary>Beat-matches Deck A's tempo to Deck B (master).</summary>
    public ReactiveCommand<Unit, Unit> SyncAToMasterCommand        { get; }
    /// <summary>Beat-matches Deck B's tempo to Deck A (master).</summary>
    public ReactiveCommand<Unit, Unit> SyncBToMasterCommand        { get; }
    /// <summary>Phase-aligns Deck A to the master deck's downbeat.</summary>
    public ReactiveCommand<Unit, Unit> PhaseAlignAToMasterCommand  { get; }
    /// <summary>Phase-aligns Deck B to the master deck's downbeat.</summary>
    public ReactiveCommand<Unit, Unit> PhaseAlignBToMasterCommand  { get; }

    // ─── Constructor ──────────────────────────────────────────────────────────

    public DeckViewModel()
    {
        var engineA = new DeckEngine();
        var engineB = new DeckEngine();

        DeckA = new DeckSlotViewModel("A", engineA);
        DeckB = new DeckSlotViewModel("B", engineB);

        DeckA.IsFocused = true; // default keyboard focus on Deck A

        // MixingSampleProvider sums both deck outputs; crossfader weights their volume
        _mixer = new MixingSampleProvider(new ISampleProvider[] { engineA, engineB });
        ApplyCrossfader(0.5f);

        // ── Output commands
        StartOutputCommand = ReactiveCommand.Create(StartOutput);
        StopOutputCommand  = ReactiveCommand.Create(StopOutput);

        // ── Sync commands (Task 5.3)
        SyncAToMasterCommand = ReactiveCommand.Create(() =>
        {
            if (MasterDeck == DeckSide.B && DeckB.TrackBpm > 0 && DeckA.TrackBpm > 0)
                _syncService.BeatMatch(DeckB.Engine, DeckB.TrackBpm, DeckA.Engine, DeckA.TrackBpm);
        });

        SyncBToMasterCommand = ReactiveCommand.Create(() =>
        {
            if (MasterDeck == DeckSide.A && DeckA.TrackBpm > 0 && DeckB.TrackBpm > 0)
                _syncService.BeatMatch(DeckA.Engine, DeckA.TrackBpm, DeckB.Engine, DeckB.TrackBpm);
        });

        PhaseAlignAToMasterCommand = ReactiveCommand.Create(() =>
        {
            if (MasterDeck == DeckSide.B && DeckB.TrackBpm > 0 && DeckA.TrackBpm > 0)
                _syncService.PhaseAlign(DeckB.Engine, DeckB.TrackBpm, DeckA.Engine, DeckA.TrackBpm);
        });

        PhaseAlignBToMasterCommand = ReactiveCommand.Create(() =>
        {
            if (MasterDeck == DeckSide.A && DeckA.TrackBpm > 0 && DeckB.TrackBpm > 0)
                _syncService.PhaseAlign(DeckA.Engine, DeckA.TrackBpm, DeckB.Engine, DeckB.TrackBpm);
        });

        // ── Position / phase poll at ~33 fps
        _positionPoll = new System.Timers.Timer(30) { AutoReset = true };
        _positionPoll.Elapsed += (_, _) =>
        {
            DeckA.PollPosition();
            DeckB.PollPosition();

            if (DeckA.TrackBpm > 0 && DeckB.TrackBpm > 0)
            {
                double offset = _syncService.GetPhaseOffsetBeats(
                    DeckA.Engine, DeckA.TrackBpm,
                    DeckB.Engine, DeckB.TrackBpm);
                Dispatcher.UIThread.Post(() => PhaseOffsetBeats = offset);
            }
        };
        _positionPoll.Start();
    }

    // ─── Crossfader ───────────────────────────────────────────────────────────

    /// <summary>
    /// Equal-power crossfader: DeckA.VolumeLevel = cos(xf·π/2), DeckB = sin(xf·π/2).
    /// At center (xf=0.5) each deck is at −3 dB; combined power is constant.
    /// </summary>
    private void ApplyCrossfader(float xf)
    {
        double angle = xf * Math.PI / 2.0;
        DeckA.Engine.VolumeLevel = (float)Math.Cos(angle);
        DeckB.Engine.VolumeLevel = (float)Math.Sin(angle);
    }

    // ─── Audio output ─────────────────────────────────────────────────────────

    private void StartOutput()
    {
        if (_isOutputActive) return;
        _waveOut = new WaveOutEvent { DesiredLatency = 100 };
        _waveOut.Init(_mixer);
        _waveOut.Play();
        IsOutputActive = true;
    }

    private void StopOutput()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        IsOutputActive = false;
    }

    // ─── Keyboard routing (Task 5.5) ──────────────────────────────────────────

    /// <summary>
    /// Routes a keyboard hot-cue press (1-indexed, 1–8) to the currently focused deck.
    /// Call this from a KeyDown handler in the View that hosts the deck strip.
    /// </summary>
    public void HandleHotKeyPress(int oneBasedSlot)
    {
        if (DeckA.IsFocused)       DeckA.HandleHotKeyPress(oneBasedSlot);
        else if (DeckB.IsFocused)  DeckB.HandleHotKeyPress(oneBasedSlot);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _positionPoll.Stop();
        _positionPoll.Dispose();
        StopOutput();
        DeckA.Dispose();
        DeckB.Dispose();
        _disposables.Dispose();
        _isDisposed = true;
    }
}
