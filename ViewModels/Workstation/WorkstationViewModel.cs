using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Audio.Separation;

namespace SLSKDONET.ViewModels.Workstation;

/// <summary>The creative canvas inside the Workstation page.</summary>
public enum WorkstationMode
{
    /// <summary>Multi-deck waveform view: zoom, cue editing, beat-matching.</summary>
    Waveform,
    /// <summary>Flow Builder: playlist sequence, transition scoring, energy arc.</summary>
    Flow,
    /// <summary>Stem separation: per-instrument faders, mute/solo.</summary>
    Stems,
    /// <summary>Mixdown + export: normalisation, dither, export profiles.</summary>
    Export,
}

/// <summary>
/// Root ViewModel for the Workstation page — modelled after the DJ.Studio layout.
/// Manages decks, timeline position, global BPM, and the active playlist.
/// </summary>
public sealed class WorkstationViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly ILibraryService          _library;
    private readonly DeckViewModel            _deckPair;
    private readonly CachedStemSeparator      _stemSeparator;
    private readonly ICuePointService         _cueService;
    private readonly StemPreferenceService    _stemPrefService;
    private readonly MixdownService           _mixdown;
    private readonly WorkstationSessionService _sessionService;
    private readonly IUndoService             _undoService;
    private readonly AnalyzeTrackStructureJob  _analyzeJob;
    private readonly IEventBus                _eventBus;
    private readonly BpmSyncService           _bpmSync = new();

    // ── Decks ─────────────────────────────────────────────────────────────────

    public ObservableCollection<WorkstationDeckViewModel> Decks { get; } = new();

    private WorkstationDeckViewModel? _focusedDeck;
    public WorkstationDeckViewModel? FocusedDeck
    {
        get => _focusedDeck;
        set
        {
            this.RaiseAndSetIfChanged(ref _focusedDeck, value);
            foreach (var deck in Decks)
            {
                deck.IsFocusedDeck = ReferenceEquals(deck, value);
            }

            RefreshDeckTransitionGuidance();
            RaiseHeaderProperties();
            this.RaisePropertyChanged(nameof(IsDeckAFocused));
            this.RaisePropertyChanged(nameof(IsDeckBFocused));
            this.RaisePropertyChanged(nameof(IsDeckCFocused));
            this.RaisePropertyChanged(nameof(IsDeckDFocused));
        }
    }

    // ── Playlist selector ─────────────────────────────────────────────────────

    public ObservableCollection<PlaylistJob> Playlists { get; } = new();

    private PlaylistJob? _activePlaylist;
    public PlaylistJob? ActivePlaylist
    {
        get => _activePlaylist;
        set
        {
            this.RaiseAndSetIfChanged(ref _activePlaylist, value);
            this.RaisePropertyChanged(nameof(ActivePlaylistFlowSummary));
            if (value != null)
                _ = LoadPlaylistTracksAsync(value);
        }
    }

    // Track rows shown in the bottom track list
    public ObservableCollection<PlaylistTrack> PlaylistTracks { get; } = new();

    // ── Timeline / scroll ─────────────────────────────────────────────────────

    /// <summary>Visible timeline window start in seconds.</summary>
    private double _timelineOffsetSeconds;
    public double TimelineOffsetSeconds
    {
        get => _timelineOffsetSeconds;
        set
        {
            this.RaiseAndSetIfChanged(ref _timelineOffsetSeconds, Math.Clamp(value, 0, MaxTimelineOffsetSeconds));
            ApplyTimelineViewportToDecks();
            RaiseTimelineTickLabels();
            this.RaisePropertyChanged(nameof(FlowWindowSummary));
        }
    }

    /// <summary>Seconds visible in the timeline viewport (zoom level).</summary>
    private double _timelineWindowSeconds = 60.0;
    public double TimelineWindowSeconds
    {
        get => _timelineWindowSeconds;
        set
        {
            this.RaiseAndSetIfChanged(ref _timelineWindowSeconds,
                   Math.Clamp(value, 10.0, 3600.0));
            this.RaisePropertyChanged(nameof(MaxTimelineOffsetSeconds));
            TimelineOffsetSeconds = TimelineOffsetSeconds;
            ApplyTimelineViewportToDecks();
            RaiseTimelineTickLabels();
            this.RaisePropertyChanged(nameof(FlowWindowSummary));
        }
    }

    public double MaxTimelineOffsetSeconds
    {
        get
        {
            var maxDuration = Decks
                .Select(d => d.Deck.DurationSeconds)
                .DefaultIfEmpty(0)
                .Max();

            return Math.Max(0, maxDuration - TimelineWindowSeconds);
        }
    }

    public string TimelineTick0 => FormatTick(TimelineOffsetSeconds + (TimelineWindowSeconds * 0.0 / 5.0));
    public string TimelineTick1 => FormatTick(TimelineOffsetSeconds + (TimelineWindowSeconds * 1.0 / 5.0));
    public string TimelineTick2 => FormatTick(TimelineOffsetSeconds + (TimelineWindowSeconds * 2.0 / 5.0));
    public string TimelineTick3 => FormatTick(TimelineOffsetSeconds + (TimelineWindowSeconds * 3.0 / 5.0));
    public string TimelineTick4 => FormatTick(TimelineOffsetSeconds + (TimelineWindowSeconds * 4.0 / 5.0));
    public string TimelineTick5 => FormatTick(TimelineOffsetSeconds + (TimelineWindowSeconds * 5.0 / 5.0));

    private bool _isSnapGuideVisible;
    public bool IsSnapGuideVisible
    {
        get => _isSnapGuideVisible;
        private set => this.RaiseAndSetIfChanged(ref _isSnapGuideVisible, value);
    }

    private string _snapGuideLabel = string.Empty;
    public string SnapGuideLabel
    {
        get => _snapGuideLabel;
        private set => this.RaiseAndSetIfChanged(ref _snapGuideLabel, value);
    }

    private double _snapGuideTimeSeconds;
    public double SnapGuideTimeSeconds
    {
        get => _snapGuideTimeSeconds;
        private set
        {
            this.RaiseAndSetIfChanged(ref _snapGuideTimeSeconds, value);
            this.RaisePropertyChanged(nameof(SnapGuideCanvasLeft));
        }
    }

    // Timeline ruler is currently drawn in a fixed 700px canvas.
    public double SnapGuideCanvasLeft
    {
        get
        {
            if (TimelineWindowSeconds <= 0)
            {
                return 0;
            }

            var ratio = (SnapGuideTimeSeconds - TimelineOffsetSeconds) / TimelineWindowSeconds;
            return Math.Clamp(ratio, 0.0, 1.0) * 700.0;
        }
    }

    // ── Global BPM (master) ───────────────────────────────────────────────────

    private double _masterBpm;
    public double MasterBpm
    {
        get => _masterBpm;
        set
        {
            if (Math.Abs(_masterBpm - value) < 0.001)
                return;

            this.RaiseAndSetIfChanged(ref _masterBpm, value);
            this.RaisePropertyChanged(nameof(MasterBpmDisplay));
            RaiseHeaderProperties();
        }
    }

    public string MasterBpmDisplay => MasterBpm > 0 ? $"{MasterBpm:F1}" : "—";
    public string DeckStatusSummary => BuildDeckStatusSummary(Decks.Count(d => d.IsLoaded), Decks.Count, FocusedDeck?.DeckLabel, MasterBpm);
    public string DeckFocusSummary => BuildDeckFocusSummary(Decks.Select(d => d.DeckLabel), Decks.Where(d => d.IsLoaded).Select(d => d.DeckLabel), FocusedDeck?.DeckLabel);
    public string ActivePlaylistFlowSummary => BuildPlaylistFlowSummary(ActivePlaylist?.SourceTitle, PlaylistTracks.Count, Decks.Count(d => d.IsLoaded), ActiveMode);
    private string _analysisQueueSummary = "Analysis lane idle • queue prep jobs from the player or flow drawer";
    public string AnalysisQueueSummary
    {
        get => _analysisQueueSummary;
        private set => this.RaiseAndSetIfChanged(ref _analysisQueueSummary, value);
    }
    public string ToolbarHint => BuildToolbarHint(ActiveMode, IsSnapEnabled, IsQuantizeEnabled);
    public string FlowWindowSummary => BuildFlowWindowSummary(TimelineOffsetSeconds, TimelineWindowSeconds);
    public string TransportStatusSummary => BuildTransportStatusSummary(IsPlaying, Decks.Count(d => d.IsLoaded), FocusedDeck?.DeckLabel, FocusedDeck?.Deck.IsLoopActive == true);
    public string FocusedDeckActionSummary => BuildFocusedDeckActionSummary(FocusedDeck?.DeckLabel, FocusedDeck?.IsLoaded == true, FocusedDeck?.CueEditor.Cues.Any() == true, FocusedDeck?.StemsVisible == true);
    public string MixCoachSummary => BuildMixCoachSummary(FocusedDeck?.DeckLabel, FocusedDeck?.HarmonicSuggestionText, FocusedDeck?.TransitionStatusText);
    public bool IsDeckAFocused => string.Equals(FocusedDeck?.DeckLabel, "A", StringComparison.OrdinalIgnoreCase);
    public bool IsDeckBFocused => string.Equals(FocusedDeck?.DeckLabel, "B", StringComparison.OrdinalIgnoreCase);
    public bool IsDeckCFocused => string.Equals(FocusedDeck?.DeckLabel, "C", StringComparison.OrdinalIgnoreCase);
    public bool IsDeckDFocused => string.Equals(FocusedDeck?.DeckLabel, "D", StringComparison.OrdinalIgnoreCase);

    // ── Crossfader ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Equal-power crossfader: 0.0 = Deck A only, 0.5 = centre, 1.0 = Deck B only.
    /// Drives DeckViewModel.CrossfaderPosition which applies cos/sin volume law.
    /// </summary>
    public float CrossfaderPosition
    {
        get => _deckPair.CrossfaderPosition;
        set
        {
            _deckPair.CrossfaderPosition = value;
            this.RaisePropertyChanged();
        }
    }

    // ── Keyboard overlay (F1 shortcut cheat-sheet) ───────────────────────────

    public KeyboardOverlayViewModel KeyboardOverlay { get; } = new();

    // ── Global cockpit toggles ───────────────────────────────────────────────

    private bool _isSnapEnabled = true;
    public bool IsSnapEnabled
    {
        get => _isSnapEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSnapEnabled, value);
            if (!value)
            {
                HideSnapGuide();
            }

            RaiseHeaderProperties();
        }
    }

    private bool _isQuantizeEnabled = true;
    public bool IsQuantizeEnabled
    {
        get => _isQuantizeEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _isQuantizeEnabled, value);
            RaiseHeaderProperties();
        }
    }

    private bool _isMetronomeEnabled;
    public bool IsMetronomeEnabled
    {
        get => _isMetronomeEnabled;
        set => this.RaiseAndSetIfChanged(ref _isMetronomeEnabled, value);
    }

    // ── Tap Tempo ─────────────────────────────────────────────────────────────

    private readonly System.Collections.Generic.List<DateTime> _tapTimes = new();
    public ReactiveCommand<Unit, Unit> TapTempoCommand { get; }

    // ── Active mode ───────────────────────────────────────────────────────────

    private WorkstationMode _activeMode = WorkstationMode.Waveform;
    public WorkstationMode ActiveMode
    {
        get => _activeMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _activeMode, value);
            this.RaisePropertyChanged(nameof(IsWaveformMode));
            this.RaisePropertyChanged(nameof(IsFlowMode));
            this.RaisePropertyChanged(nameof(IsStemsMode));
            this.RaisePropertyChanged(nameof(IsExportMode));
            RaiseHeaderProperties();
        }
    }

    public bool IsWaveformMode => ActiveMode == WorkstationMode.Waveform;
    public bool IsFlowMode     => ActiveMode == WorkstationMode.Flow;
    public bool IsStemsMode    => ActiveMode == WorkstationMode.Stems;
    public bool IsExportMode   => ActiveMode == WorkstationMode.Export;

    // ── Inline Export panel (shown in Export mode) ────────────────────────────

    /// <summary>
    /// Shared Export configuration shown in the inline Export mode panel.
    /// The same instance also populates the popup Export dialog when
    /// <see cref="ExportMixCommand"/> is invoked from the toolbar.
    /// </summary>
    public ExportDialogViewModel ExportPanel { get; }

    // ── Master play transport ─────────────────────────────────────────────────

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        private set => this.RaiseAndSetIfChanged(ref _isPlaying, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit>          PlayPauseAllCommand   { get; }
    public ReactiveCommand<Unit, Unit>          StopAllCommand        { get; }
    public ReactiveCommand<Unit, Unit>          AddDeckCommand        { get; }
    public ReactiveCommand<WorkstationDeckViewModel, Unit> RemoveDeckCommand { get; }
    public ReactiveCommand<PlaylistJob, Unit>   LoadPlaylistCommand   { get; }
    public ReactiveCommand<Unit, Unit>          ZoomInCommand         { get; }
    public ReactiveCommand<Unit, Unit>          ZoomOutCommand        { get; }
    public ReactiveCommand<Unit, Unit>          PanLeftCommand        { get; }
    public ReactiveCommand<Unit, Unit>          PanRightCommand       { get; }
    public ReactiveCommand<Unit, Unit>          UndoCommand           { get; }
    public ReactiveCommand<Unit, Unit>          RedoCommand           { get; }
    /// <summary>Opens the Export Mix dialog for the active decks.</summary>
    public ReactiveCommand<Unit, Unit>          ExportMixCommand      { get; }
    /// <summary>Load a playlist track into the focused deck (or Deck A if none focused).</summary>
    public ReactiveCommand<PlaylistTrack, Unit> LoadToFocusedDeckCommand { get; }
    public ReactiveCommand<string, Unit>        FocusDeckCommand      { get; }
    /// <summary>Load a playlist track into Deck A.</summary>
    public ReactiveCommand<PlaylistTrack, Unit> LoadToDeckACommand    { get; }
    /// <summary>Load a playlist track into Deck B.</summary>
    public ReactiveCommand<PlaylistTrack, Unit> LoadToDeckBCommand    { get; }
    /// <summary>Beat-match + phase-align Deck B to Deck A.</summary>
    public ReactiveCommand<Unit, Unit>          SyncBpmCommand        { get; }
    /// <summary>Toggle loop on the focused deck.</summary>
    public ReactiveCommand<Unit, Unit>          ToggleLoopCommand     { get; }
    /// <summary>Set focused deck to 1-beat loop and activate.</summary>
    public ReactiveCommand<Unit, Unit>          Loop1Command          { get; }
    /// <summary>Set focused deck to 2-beat loop and activate.</summary>
    public ReactiveCommand<Unit, Unit>          Loop2Command          { get; }
    /// <summary>Set focused deck to 4-beat loop and activate.</summary>
    public ReactiveCommand<Unit, Unit>          Loop4Command          { get; }
    /// <summary>Set focused deck to 8-beat loop and activate.</summary>
    public ReactiveCommand<Unit, Unit>          Loop8Command          { get; }
    /// <summary>Exit the active loop on the focused deck.</summary>
    public ReactiveCommand<Unit, Unit>          ExitLoopFocusedCommand { get; }
    /// <summary>Switch to a Workstation mode (Waveform / Flow / Stems / Export).</summary>
    public ReactiveCommand<WorkstationMode, Unit> SetModeCommand      { get; }

    // ── Cue auto-analysis ─────────────────────────────────────────────────────

    private bool _isAnalyzing;
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set => this.RaiseAndSetIfChanged(ref _isAnalyzing, value);
    }

    private string _analysisStatusText = string.Empty;
    public string AnalysisStatusText
    {
        get => _analysisStatusText;
        private set => this.RaiseAndSetIfChanged(ref _analysisStatusText, value);
    }

    private int _analysisProgress;
    public int AnalysisProgress
    {
        get => _analysisProgress;
        private set => this.RaiseAndSetIfChanged(ref _analysisProgress, value);
    }

    /// <summary>Analyze all tracks in the active playlist that have no cues yet.</summary>
    public ReactiveCommand<Unit, Unit> AnalyzePlaylistCuesCommand  { get; }

    /// <summary>Analyze only the passed tracks (DataGrid selection) that have no cues yet.</summary>
    public ReactiveCommand<IList<PlaylistTrack>, Unit> AnalyzeSelectedCuesCommand { get; }

    private CancellationTokenSource? _analysisCts;

    // ── Constructor ───────────────────────────────────────────────────────────

    public WorkstationViewModel(ILibraryService library, DeckViewModel deckPair,
        CachedStemSeparator stemSeparator, ICuePointService cueService,
        StemPreferenceService stemPrefService, MixdownService mixdown,
        WorkstationSessionService sessionService, IUndoService undoService,
        AnalyzeTrackStructureJob analyzeJob, IEventBus eventBus)
    {
        _library         = library;
        _deckPair        = deckPair;
        _stemSeparator   = stemSeparator;
        _cueService      = cueService;
        _stemPrefService = stemPrefService;
        _mixdown         = mixdown;
        _sessionService  = sessionService;
        _undoService     = undoService;
        _analyzeJob      = analyzeJob;
        _eventBus        = eventBus;

        ExportPanel = new ExportDialogViewModel(mixdown);

        // Wrap existing DeckA / DeckB
        var deckA = new WorkstationDeckViewModel("A", deckPair.DeckA, stemSeparator, cueService, stemPrefService);
        var deckB = new WorkstationDeckViewModel("B", deckPair.DeckB, stemSeparator, cueService, stemPrefService);
        deckA.OnTrackLoaded = async () => { RefreshDeckTransitionGuidance(); await SaveSessionAsync(); };
        deckB.OnTrackLoaded = async () => { RefreshDeckTransitionGuidance(); await SaveSessionAsync(); };
        deckA.OnDeckStateChanged = RefreshDeckTransitionGuidance;
        deckB.OnDeckStateChanged = RefreshDeckTransitionGuidance;
        Decks.Add(deckA);
        Decks.Add(deckB);

        FocusedDeck = Decks.FirstOrDefault();

        _eventBus.GetEvent<OpenStemWorkspaceRequestEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt => _ = HandleWorkspaceOpenRequestAsync(evt))
            .DisposeWith(_disposables);

        _eventBus.GetEvent<AddToTimelineRequestEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt => _ = HandleFlowLaunchRequestAsync(evt))
            .DisposeWith(_disposables);

        _eventBus.GetEvent<AnalysisQueueStatusChangedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt =>
            {
                AnalysisQueueSummary = BuildAnalysisQueueSummary(
                    evt.QueuedCount,
                    evt.ProcessedCount,
                    evt.CurrentTrackHash,
                    evt.IsPaused,
                    evt.PerformanceMode,
                    evt.MaxConcurrency);
            })
            .DisposeWith(_disposables);

        PlayPauseAllCommand = ReactiveCommand.Create(() =>
        {
            IsPlaying = !IsPlaying;
            foreach (var d in Decks)
            {
                if (IsPlaying) d.Deck.Engine.Play();
                else           d.Deck.Engine.Pause();
            }

            RaiseHeaderProperties();
        });

        StopAllCommand = ReactiveCommand.Create(() =>
        {
            IsPlaying = false;
            foreach (var d in Decks)
            {
                d.Deck.Engine.Pause();
                d.Deck.Engine.Cue();
            }

            RaiseHeaderProperties();
        });

        AddDeckCommand = ReactiveCommand.Create(() =>
        {
            // Max 4 decks
            if (Decks.Count >= 4) return;
            string label = Decks.Count switch { 2 => "C", 3 => "D", _ => "?" };
            var engine = new DeckEngine();
            var slot   = new DeckSlotViewModel(label, engine);
            var newDeck = new WorkstationDeckViewModel(label, slot, _stemSeparator, _cueService, _stemPrefService);
            newDeck.OnTrackLoaded = async () => { RefreshDeckTransitionGuidance(); await SaveSessionAsync(); };
            newDeck.OnDeckStateChanged = RefreshDeckTransitionGuidance;
            Decks.Add(newDeck);
            newDeck.UpdateWaveformViewport(TimelineWindowSeconds, TimelineOffsetSeconds);
            RefreshDeckTransitionGuidance();
            this.RaisePropertyChanged(nameof(MaxTimelineOffsetSeconds));
            RaiseHeaderProperties();
        });

        RemoveDeckCommand = ReactiveCommand.Create<WorkstationDeckViewModel>(deck =>
        {
            if (Decks.Count <= 1) return;
            Decks.Remove(deck);
            deck.Dispose();
            FocusedDeck = Decks.FirstOrDefault();
            RefreshDeckTransitionGuidance();
            this.RaisePropertyChanged(nameof(MaxTimelineOffsetSeconds));
            TimelineOffsetSeconds = TimelineOffsetSeconds;
            RaiseHeaderProperties();
        });

        LoadPlaylistCommand = ReactiveCommand.CreateFromTask<PlaylistJob>(async job =>
        {
            ActivePlaylist = job;
        });

        ZoomInCommand  = ReactiveCommand.Create(() => { TimelineWindowSeconds /= 1.5; });
        ZoomOutCommand = ReactiveCommand.Create(() => { TimelineWindowSeconds *= 1.5; });
        PanLeftCommand = ReactiveCommand.Create(() =>
        {
            var step = Math.Max(1, TimelineWindowSeconds * 0.1);
            TimelineOffsetSeconds -= step;
        });
        PanRightCommand = ReactiveCommand.Create(() =>
        {
            var step = Math.Max(1, TimelineWindowSeconds * 0.1);
            TimelineOffsetSeconds += step;
        });

        UndoCommand = ReactiveCommand.Create(() => _undoService.Undo());
        RedoCommand = ReactiveCommand.Create(() => _undoService.Redo());

        ExportMixCommand = ReactiveCommand.CreateFromTask(OpenExportDialogAsync);

        LoadToFocusedDeckCommand = ReactiveCommand.CreateFromTask<PlaylistTrack>(async t =>
        {
            var target = FocusedDeck ?? Decks.FirstOrDefault();
            await LoadTrackIntoDeckAsync(target, t);
        });

        FocusDeckCommand = ReactiveCommand.Create<string>(deckLabel =>
        {
            var targetDeck = Decks.FirstOrDefault(deck => string.Equals(deck.DeckLabel, deckLabel, StringComparison.OrdinalIgnoreCase));
            if (targetDeck != null)
            {
                FocusedDeck = targetDeck;
                AnalysisStatusText = $"Deck {targetDeck.DeckLabel} focused for the next handoff.";
            }
        });

        LoadToDeckACommand = ReactiveCommand.CreateFromTask<PlaylistTrack>(async t =>
        {
            var deck = Decks.FirstOrDefault(d => d.DeckLabel == "A");
            await LoadTrackIntoDeckAsync(deck, t);
        });

        LoadToDeckBCommand = ReactiveCommand.CreateFromTask<PlaylistTrack>(async t =>
        {
            var deck = Decks.FirstOrDefault(d => d.DeckLabel == "B");
            await LoadTrackIntoDeckAsync(deck, t);
        });

        SyncBpmCommand = ReactiveCommand.Create(() =>
        {
            var deckA = Decks.FirstOrDefault(d => d.DeckLabel == "A");
            var deckB = Decks.FirstOrDefault(d => d.DeckLabel == "B");
            if (deckA == null || deckB == null) return;
            if (deckA.DisplayBpm <= 0 || deckB.DisplayBpm <= 0) return;
            _bpmSync.BeatMatch(deckA.Deck.Engine, deckA.DisplayBpm, deckB.Deck.Engine, deckB.DisplayBpm);
            _bpmSync.PhaseAlign(deckA.Deck.Engine, deckA.DisplayBpm, deckB.Deck.Engine, deckB.DisplayBpm);
            RaiseHeaderProperties();
        });

        ToggleLoopCommand = ReactiveCommand.Create(() =>
        {
            var deck = FocusedDeck?.Deck;
            if (deck == null) return;
            if (deck.IsLoopActive) deck.ExitLoopCommand.Execute().Subscribe();
            else                   deck.SetLoopCommand.Execute().Subscribe();
        });

        Loop1Command = ReactiveCommand.Create(() => { var d = FocusedDeck?.Deck; if (d == null) return; d.SelectedLoopBeats = 1; d.SetLoopCommand.Execute().Subscribe(); RaiseHeaderProperties(); });
        Loop2Command = ReactiveCommand.Create(() => { var d = FocusedDeck?.Deck; if (d == null) return; d.SelectedLoopBeats = 2; d.SetLoopCommand.Execute().Subscribe(); RaiseHeaderProperties(); });
        Loop4Command = ReactiveCommand.Create(() => { var d = FocusedDeck?.Deck; if (d == null) return; d.SelectedLoopBeats = 4; d.SetLoopCommand.Execute().Subscribe(); RaiseHeaderProperties(); });
        Loop8Command = ReactiveCommand.Create(() => { var d = FocusedDeck?.Deck; if (d == null) return; d.SelectedLoopBeats = 8; d.SetLoopCommand.Execute().Subscribe(); RaiseHeaderProperties(); });
        ExitLoopFocusedCommand = ReactiveCommand.Create(() => { FocusedDeck?.Deck.ExitLoopCommand.Execute().Subscribe(); RaiseHeaderProperties(); });

        TapTempoCommand = ReactiveCommand.Create(() =>
        {
            var now = DateTime.UtcNow;
            // Discard taps older than 3 seconds (reset after pause)
            _tapTimes.RemoveAll(t => (now - t).TotalSeconds > 3.0);
            _tapTimes.Add(now);
            if (_tapTimes.Count >= 2)
            {
                double totalSeconds = (_tapTimes[^1] - _tapTimes[0]).TotalSeconds;
                double avgInterval  = totalSeconds / (_tapTimes.Count - 1);
                MasterBpm = Math.Round(60.0 / avgInterval, 1);
            }
        });

        var canAnalyze = this.WhenAnyValue(x => x.IsAnalyzing, busy => !busy);

        AnalyzePlaylistCuesCommand = ReactiveCommand.CreateFromTask(
            () => RunCueAnalysisAsync(PlaylistTracks.ToList()), canAnalyze);

        AnalyzeSelectedCuesCommand = ReactiveCommand.CreateFromTask<IList<PlaylistTrack>>(
            tracks => RunCueAnalysisAsync(tracks?.ToList() ?? new List<PlaylistTrack>()), canAnalyze);

        SetModeCommand = ReactiveCommand.Create<WorkstationMode>(mode =>
        {
            ActiveMode = mode;
            _ = SaveSessionAsync();
        });

        // Update MasterBpm from focused deck
        this.WhenAnyValue(x => x.FocusedDeck)
            .Subscribe(d =>
            {
                if (d != null)
                    MasterBpm = d.Deck.TrackBpm;
            })
            .DisposeWith(_disposables);

        _ = LoadPlaylistsAsync();
        _ = RestoreSessionAsync();

        ApplyTimelineViewportToDecks();
        RaiseTimelineTickLabels();
    }

    private void ApplyTimelineViewportToDecks()
    {
        foreach (var deck in Decks)
        {
            deck.UpdateWaveformViewport(TimelineWindowSeconds, TimelineOffsetSeconds);
        }

        this.RaisePropertyChanged(nameof(SnapGuideCanvasLeft));
    }

    public void ApplySmartSnapForDeckDrop(WorkstationDeckViewModel targetDeck)
    {
        if (!IsSnapEnabled || targetDeck == null || !targetDeck.IsLoaded)
        {
            HideSnapGuide();
            return;
        }

        var referenceDeck = Decks
            .Where(d => !ReferenceEquals(d, targetDeck) && d.IsLoaded)
            .OrderByDescending(d => d.IsFocusedDeck)
            .ThenByDescending(d => d.Deck.PositionSeconds)
            .FirstOrDefault();

        if (referenceDeck == null)
        {
            HideSnapGuide();
            return;
        }

        var targetCue = SelectAnchorCueForIncomingTrack(targetDeck);
        var referenceCue = SelectAnchorCueForReferenceTrack(referenceDeck);
        if (targetCue == null || referenceCue == null)
        {
            HideSnapGuide();
            return;
        }

        var desiredStart = QuantizeIfEnabled(targetCue.Timestamp, targetDeck.DisplayBpm);
        var maxSeek = Math.Max(0.0, targetDeck.Deck.DurationSeconds - 0.05);
        var clampedStart = Math.Clamp(desiredStart, 0.0, maxSeek);

        targetDeck.Deck.SeekCommand.Execute(clampedStart).Subscribe();

        SnapGuideTimeSeconds = referenceCue.Timestamp;
        SnapGuideLabel = WorkstationDeckViewModel.BuildTransitionStatus(
            targetDeck.DeckLabel,
            targetDeck.TrackKey,
            targetDeck.DisplayBpm,
            targetDeck.CueEditor.Cues,
            referenceDeck.DeckLabel,
            referenceDeck.TrackKey,
            referenceDeck.DisplayBpm,
            referenceDeck.CueEditor.Cues);
        IsSnapGuideVisible = true;
    }

    private static OrbitCue? SelectAnchorCueForIncomingTrack(WorkstationDeckViewModel deck)
    {
        var cues = deck.CueEditor.Cues;
        if (cues.Count == 0)
        {
            return null;
        }

        return FindFirstByRoles(cues, CueRole.Intro, CueRole.PhraseStart, CueRole.Build, CueRole.Drop)
               ?? cues.OrderBy(c => c.Timestamp).FirstOrDefault();
    }

    private static OrbitCue? SelectAnchorCueForReferenceTrack(WorkstationDeckViewModel deck)
    {
        var cues = deck.CueEditor.Cues;
        if (cues.Count == 0)
        {
            return null;
        }

        var preferred = FindLatestByRoles(cues, CueRole.Outro, CueRole.Breakdown, CueRole.PhraseStart);
        if (preferred != null)
        {
            return preferred;
        }

        var current = deck.Deck.PositionSeconds;
        return cues
            .OrderBy(c => Math.Abs(c.Timestamp - current))
            .FirstOrDefault();
    }

    private static OrbitCue? FindFirstByRoles(IEnumerable<OrbitCue> cues, params CueRole[] roles)
    {
        foreach (var role in roles)
        {
            var match = cues
                .Where(c => c.Role == role)
                .OrderBy(c => c.Timestamp)
                .FirstOrDefault();

            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static OrbitCue? FindLatestByRoles(IEnumerable<OrbitCue> cues, params CueRole[] roles)
    {
        foreach (var role in roles)
        {
            var match = cues
                .Where(c => c.Role == role)
                .OrderByDescending(c => c.Timestamp)
                .FirstOrDefault();

            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private double QuantizeIfEnabled(double seconds, double bpm)
    {
        if (!IsQuantizeEnabled || bpm <= 0)
        {
            return seconds;
        }

        var beatSeconds = 60.0 / bpm;
        if (beatSeconds <= 0)
        {
            return seconds;
        }

        return Math.Round(seconds / beatSeconds) * beatSeconds;
    }

    private void RefreshDeckTransitionGuidance()
    {
        foreach (var deck in Decks)
        {
            var referenceDeck = Decks
                .Where(d => !ReferenceEquals(d, deck) && d.IsLoaded)
                .OrderByDescending(d => d.IsFocusedDeck)
                .FirstOrDefault();

            if (referenceDeck == null || !deck.IsLoaded)
            {
                deck.UpdateTransitionStatus("Load another deck for live transition guidance");
                deck.UpdateHarmonicGuidance(
                    WorkstationDeckViewModel.BuildHarmonicSuggestionText(deck.TrackKey, null, deck.Deck.SemitoneShift));
                continue;
            }

            deck.UpdateTransitionStatus(
                WorkstationDeckViewModel.BuildTransitionStatus(
                    deck.DeckLabel,
                    deck.TrackKey,
                    deck.DisplayBpm,
                    deck.CueEditor.Cues,
                    referenceDeck.DeckLabel,
                    referenceDeck.TrackKey,
                    referenceDeck.DisplayBpm,
                    referenceDeck.CueEditor.Cues));
            deck.UpdateHarmonicGuidance(
                WorkstationDeckViewModel.BuildHarmonicSuggestionText(
                    deck.TrackKey,
                    referenceDeck.TrackKey,
                    deck.Deck.SemitoneShift));
        }

        RaiseHeaderProperties();
    }

    public static string BuildDeckStatusSummary(int loadedDecks, int totalDecks, string? focusedDeckLabel, double masterBpm)
    {
        var focus = string.IsNullOrWhiteSpace(focusedDeckLabel) ? "Focus —" : $"Focus {focusedDeckLabel}";
        var bpmText = masterBpm > 0 ? $"{masterBpm.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} BPM" : "Master BPM pending";
        return $"{loadedDecks}/{Math.Max(1, totalDecks)} decks live • {focus} • {bpmText}";
    }

    public static string BuildDeckFocusSummary(IEnumerable<string> deckLabels, IEnumerable<string> liveDeckLabels, string? focusedDeckLabel)
    {
        var live = new HashSet<string>(liveDeckLabels ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var ordered = (deckLabels ?? Enumerable.Empty<string>()).Take(4).ToList();
        if (ordered.Count == 0)
        {
            return "Deck targets pending • add or load a deck to start routing";
        }

        var labels = ordered.Select(label =>
        {
            var state = live.Contains(label)
                ? (string.Equals(label, focusedDeckLabel, StringComparison.OrdinalIgnoreCase) ? "focused" : "live")
                : "open";
            return $"{label} {state}";
        });

        return $"Deck targets • {string.Join(" • ", labels)}";
    }

    public static string BuildPlaylistFlowSummary(string? playlistTitle, int readyTrackCount, int liveDeckCount, WorkstationMode activeMode)
    {
        var title = string.IsNullOrWhiteSpace(playlistTitle) ? "No playlist selected" : playlistTitle;
        var mode = activeMode == WorkstationMode.Flow ? "flow active" : activeMode == WorkstationMode.Stems ? "stems active" : "workstation active";
        return $"{title} • {Math.Max(0, readyTrackCount)} flow-ready track{(readyTrackCount == 1 ? string.Empty : "s")} • {Math.Max(0, liveDeckCount)} live deck{(liveDeckCount == 1 ? string.Empty : "s")} • {mode}";
    }

    public static string BuildAnalysisQueueSummary(int queuedCount, int processedCount, string? currentTrackHash, bool isPaused, string? performanceMode, int maxConcurrency)
    {
        var mode = string.IsNullOrWhiteSpace(performanceMode) ? "Standard" : performanceMode;
        var concurrency = maxConcurrency > 0 ? $"{maxConcurrency} lane{(maxConcurrency == 1 ? string.Empty : "s")}" : "auto lanes";

        if (queuedCount <= 0 && string.IsNullOrWhiteSpace(currentTrackHash))
        {
            return $"Analysis idle • {processedCount} prepped • {mode} • {concurrency}";
        }

        if (isPaused)
        {
            return $"Analysis paused • {queuedCount} queued • {processedCount} prepped • {mode}";
        }

        return $"Analysis rolling • {queuedCount} queued • {processedCount} prepped • {mode} • {concurrency}";
    }

    public static string BuildToolbarHint(WorkstationMode activeMode, bool snapEnabled, bool quantizeEnabled)
    {
        var modeLabel = activeMode switch
        {
            WorkstationMode.Waveform => "Waveform",
            WorkstationMode.Flow => "Flow",
            WorkstationMode.Stems => "Stems",
            WorkstationMode.Export => "Export",
            _ => "Workstation"
        };

        return $"{modeLabel} mode • Snap {(snapEnabled ? "on" : "off")} • Quantize {(quantizeEnabled ? "on" : "off")} • F1 shortcuts";
    }

    public static string BuildFlowWindowSummary(double timelineOffsetSeconds, double timelineWindowSeconds)
    {
        var start = FormatTick(timelineOffsetSeconds);
        var end = FormatTick(timelineOffsetSeconds + Math.Max(0, timelineWindowSeconds));
        return $"Viewport {start} → {end} • {timelineWindowSeconds:F0}s window";
    }

    public static string BuildTransportStatusSummary(bool isPlaying, int loadedDecks, string? focusedDeckLabel, bool loopArmed)
    {
        var state = isPlaying ? "Live transport" : "Transport cued";
        var deckText = $"{Math.Max(0, loadedDecks)} deck{(loadedDecks == 1 ? string.Empty : "s")} {(isPlaying ? "rolling" : "ready")}";
        var focus = string.IsNullOrWhiteSpace(focusedDeckLabel) ? "Focus —" : $"Focus {focusedDeckLabel}";
        var loop = loopArmed ? "loop armed" : "loop open";
        return $"{state} • {deckText} • {focus} • {loop}";
    }

    public static string BuildFocusedDeckActionSummary(string? focusedDeckLabel, bool isLoaded, bool hasJumpCues, bool stemsReady)
    {
        if (string.IsNullOrWhiteSpace(focusedDeckLabel) || !isLoaded)
            return "Focus a live deck to sync, jump cues, and shape stems";

        var cueText = hasJumpCues ? "jump cues ready" : "prep cues next";
        var stemText = stemsReady ? "stems live" : "separate stems next";
        return $"Deck {focusedDeckLabel} • {cueText} • {stemText}";
    }

    public static string BuildMixCoachSummary(string? focusedDeckLabel, string? harmonicHint, string? transitionHint)
    {
        if (string.IsNullOrWhiteSpace(focusedDeckLabel))
            return "Mix coach • focus a loaded deck for harmonic and transition guidance";

        var harmonic = string.IsNullOrWhiteSpace(harmonicHint) ? "harmonic guidance pending" : harmonicHint;
        var transition = string.IsNullOrWhiteSpace(transitionHint) ? "transition guidance pending" : transitionHint;
        return $"Mix coach • Deck {focusedDeckLabel} • {harmonic} • {transition}";
    }

    private void RaiseHeaderProperties()
    {
        this.RaisePropertyChanged(nameof(DeckStatusSummary));
        this.RaisePropertyChanged(nameof(DeckFocusSummary));
        this.RaisePropertyChanged(nameof(ActivePlaylistFlowSummary));
        this.RaisePropertyChanged(nameof(AnalysisQueueSummary));
        this.RaisePropertyChanged(nameof(ToolbarHint));
        this.RaisePropertyChanged(nameof(FlowWindowSummary));
        this.RaisePropertyChanged(nameof(TransportStatusSummary));
        this.RaisePropertyChanged(nameof(FocusedDeckActionSummary));
        this.RaisePropertyChanged(nameof(MixCoachSummary));
    }

    private void HideSnapGuide()
    {
        IsSnapGuideVisible = false;
        SnapGuideLabel = string.Empty;
    }

    private static string FormatTick(double seconds)
    {
        var s = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)s.TotalMinutes}:{s.Seconds:00}";
    }

    private void RaiseTimelineTickLabels()
    {
        this.RaisePropertyChanged(nameof(TimelineTick0));
        this.RaisePropertyChanged(nameof(TimelineTick1));
        this.RaisePropertyChanged(nameof(TimelineTick2));
        this.RaisePropertyChanged(nameof(TimelineTick3));
        this.RaisePropertyChanged(nameof(TimelineTick4));
        this.RaisePropertyChanged(nameof(TimelineTick5));
    }

    // ── Session persistence ───────────────────────────────────────────────────

    /// <summary>
    /// Captures current deck state and writes it atomically to disk.
    /// Safe to fire-and-forget — errors are silently swallowed.
    /// </summary>
    public async Task SaveSessionAsync()
    {
        try
        {
            var session = new WorkstationSession
            {
                ActiveModeIndex     = (int)ActiveMode,
                TimelineOffsetSeconds = TimelineOffsetSeconds,
                TimelineWindowSeconds = TimelineWindowSeconds,
            };

            foreach (var deck in Decks)
            {
                session.Decks.Add(new WorkstationDeckState
                {
                    DeckLabel       = deck.DeckLabel,
                    FilePath        = deck.Deck.LoadedFilePath,
                    TrackUniqueHash = deck.TrackHash,
                    TrackTitle      = deck.TrackTitle,
                    TrackArtist     = deck.TrackArtist,
                    Bpm             = deck.DisplayBpm,
                    Key             = deck.TrackKey,
                    PositionSeconds = deck.Deck.PositionSeconds,
                });
            }

            await _sessionService.SaveAsync(session);
        }
        catch { /* Never crash the UI thread over a save failure */ }
    }

    /// <summary>
    /// Called once on startup. Restores the last session if one exists.
    /// Tracks are reloaded by file path; cue points are re-fetched from the DB
    /// by <see cref="WorkstationDeckViewModel.LoadPlaylistTrackCommand"/>.
    /// </summary>
    private async Task RestoreSessionAsync()
    {
        var session = await _sessionService.LoadAsync();
        if (session == null) return;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                ActiveMode            = (WorkstationMode)session.ActiveModeIndex;
                TimelineOffsetSeconds = session.TimelineOffsetSeconds;
                TimelineWindowSeconds = session.TimelineWindowSeconds;

                foreach (var deckState in session.Decks)
                {
                    if (string.IsNullOrEmpty(deckState.FilePath)) continue;
                    if (!System.IO.File.Exists(deckState.FilePath)) continue;

                    var deck = Decks.FirstOrDefault(d => d.DeckLabel == deckState.DeckLabel)
                               ?? Decks.FirstOrDefault();
                    if (deck == null) continue;

                    // Use raw path load; cue points load via hash when available
                    var track = new Models.PlaylistTrack
                    {
                        Title            = deckState.TrackTitle ?? string.Empty,
                        Artist           = deckState.TrackArtist ?? string.Empty,
                        ResolvedFilePath = deckState.FilePath,
                        TrackUniqueHash  = deckState.TrackUniqueHash ?? string.Empty,
                        BPM              = deckState.Bpm > 0 ? deckState.Bpm : null,
                        MusicalKey       = deckState.Key,
                    };
                    await deck.LoadPlaylistTrackCommand.Execute(track).FirstAsync();
                }
            }
            catch { /* Corrupt session — proceed with blank workstation */ }
        });
    }

    private async Task LoadPlaylistsAsync()
    {
        var jobs = await _library.LoadAllPlaylistJobsAsync();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Playlists.Clear();
            foreach (var j in jobs) Playlists.Add(j);
            if (Playlists.Count > 0 && ActivePlaylist == null)
                ActivePlaylist = Playlists[0];
        });
    }

    private async Task LoadPlaylistTracksAsync(PlaylistJob job)
    {
        var tracks = await _library.GetPagedPlaylistTracksAsync(
            job.Id, skip: 0, take: 200);

        var readyTracks = tracks
            .Where(WorkstationDeckViewModel.IsTrackReadyForWorkstation)
            .ToList();

        var hiddenCount = Math.Max(0, tracks.Count - readyTracks.Count);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PlaylistTracks.Clear();
            foreach (var t in readyTracks) PlaylistTracks.Add(t);
            this.RaisePropertyChanged(nameof(ActivePlaylistFlowSummary));

            AnalysisStatusText = readyTracks.Count == 0
                ? "No workstation-ready tracks in this playlist yet. Download and analyze tracks first."
                : hiddenCount > 0
                    ? $"{readyTracks.Count} tracks ready for workstation • {hiddenCount} hidden until downloaded and analyzed."
                    : $"{readyTracks.Count} tracks ready for workstation.";
        });
    }

    private async Task LoadTrackIntoDeckAsync(WorkstationDeckViewModel? deck, PlaylistTrack track)
    {
        if (deck == null)
        {
            return;
        }

        var readinessMessage = WorkstationDeckViewModel.GetTrackLoadReadinessMessage(track);
        if (!string.IsNullOrWhiteSpace(readinessMessage))
        {
            deck.TrackLoadError = readinessMessage;
            AnalysisStatusText = readinessMessage;
            return;
        }

        await deck.LoadPlaylistTrackCommand.Execute(track).FirstAsync();

        if (string.IsNullOrWhiteSpace(deck.TrackLoadError))
        {
            ApplySmartSnapForDeckDrop(deck);
            AnalysisStatusText = $"Loaded {track.Artist} — {track.Title} into Deck {deck.DeckLabel}.";
        }
    }

    private async Task HandleFlowLaunchRequestAsync(AddToTimelineRequestEvent request)
    {
        var tracks = request.Tracks?.Where(track => track != null).ToList();
        if (tracks == null || tracks.Count == 0)
        {
            return;
        }

        ActiveMode = WorkstationMode.Flow;

        var targetDeck = FocusedDeck ?? Decks.FirstOrDefault(deck => deck.DeckLabel == "A") ?? Decks.FirstOrDefault();
        if (targetDeck != null)
        {
            FocusedDeck = targetDeck;

            if (!targetDeck.IsLoaded)
            {
                await LoadTrackIntoDeckAsync(targetDeck, tracks[0]);
            }

            AnalysisStatusText = tracks.Count == 1
                ? $"Flow launch ready for {tracks[0].Artist} — {tracks[0].Title} on Deck {targetDeck.DeckLabel}."
                : $"Flow launch ready for {tracks.Count} selected tracks.";
        }
    }

    private async Task HandleWorkspaceOpenRequestAsync(OpenStemWorkspaceRequestEvent request)
    {
        if (request.Track == null)
        {
            return;
        }

        ActiveMode = request.OpenStemRack ? WorkstationMode.Stems : WorkstationMode.Waveform;

        var targetDeck = !string.IsNullOrWhiteSpace(request.PreferredDeck)
            ? Decks.FirstOrDefault(deck => string.Equals(deck.DeckLabel, request.PreferredDeck, StringComparison.OrdinalIgnoreCase))
            : FocusedDeck ?? Decks.FirstOrDefault(deck => deck.DeckLabel == "A") ?? Decks.FirstOrDefault();

        if (targetDeck == null)
        {
            return;
        }

        FocusedDeck = targetDeck;
        await LoadTrackIntoDeckAsync(targetDeck, request.Track);

        if (request.OpenStemRack && string.IsNullOrWhiteSpace(targetDeck.TrackLoadError))
        {
            AnalysisStatusText = $"Opening stems for {request.Track.Artist} — {request.Track.Title} on Deck {targetDeck.DeckLabel}.";
            await targetDeck.SeparateStemsCommand.Execute().FirstAsync();
        }
    }

    private async Task OpenExportDialogAsync()
    {
        var vm = new ExportDialogViewModel(_mixdown);
        vm.SetDecks(Decks);
        // Show from UI thread — caller must be on UI thread (ReactiveCommand is)
        var owner = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lt
            ? lt.MainWindow : null;
        if (owner != null)
            await Views.Avalonia.Workstation.ExportDialog.ShowForWorkstationAsync(owner, vm);
        vm.Dispose();
    }

    public void Dispose()
    {
        // Synchronously block for a brief window so the session is flushed
        // even when the OS terminates the process after the window closes.
        SaveSessionAsync().GetAwaiter().GetResult();
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        ExportPanel.Dispose();
        _disposables.Dispose();
        foreach (var d in Decks) d.Dispose();
    }

    // ── Cue auto-analysis ─────────────────────────────────────────────────────

    /// <summary>
    /// For each track in <paramref name="tracks"/>: skip those that already have
    /// cue points, then run <see cref="AnalyzeTrackStructureJob"/> for the rest.
    /// Progress and busy-state are reported on the UI thread so the button can
    /// show a spinner / progress text while running.
    /// </summary>
    private async Task RunCueAnalysisAsync(List<PlaylistTrack> tracks)
    {
        if (tracks.Count == 0) return;

        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _analysisCts = new CancellationTokenSource();
        var ct = _analysisCts.Token;

        IsAnalyzing = true;
        AnalysisProgress = 0;

        int done = 0;
        int skipped = 0;
        int total = tracks.Count;

        try
        {
            foreach (var track in tracks)
            {
                if (ct.IsCancellationRequested) break;

                string? hash = track.TrackUniqueHash;
                if (string.IsNullOrWhiteSpace(hash))
                {
                    done++;
                    continue;
                }

                // Skip tracks that already have cue data
                var existing = await _cueService.GetByTrackIdAsync(hash, ct).ConfigureAwait(false);
                if (existing.Count > 0)
                {
                    skipped++;
                    done++;
                    AnalysisProgress = (int)(done * 100.0 / total);
                    AnalysisStatusText = $"Skipped {skipped} · {done}/{total}";
                    continue;
                }

                AnalysisStatusText = $"Analyzing {track.Title ?? hash[..8]}… ({done + 1}/{total})";

                await _analyzeJob.ExecuteAsync(hash, ct).ConfigureAwait(false);

                done++;
                AnalysisProgress = (int)(done * 100.0 / total);
            }

            AnalysisStatusText = ct.IsCancellationRequested
                ? $"Cancelled — {done} processed"
                : $"Done — {done - skipped} analyzed · {skipped} already had cues";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }
}
