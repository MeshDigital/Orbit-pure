using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Playlist;
using SLSKDONET.Services.Repositories;
using SLSKDONET.Services.Timeline;
using SLSKDONET.Utils;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Backs both the "Mix" CONTEXT-sidepanel tab (preset picker, bar-length selector, Custom-mode
/// parameter rows) and the resurrected <see cref="Views.Avalonia.Controls.MixPreviewComponent"/>
/// (dual waveform + preview/commit controls) it hosts — one ViewModel for the whole Spotify-Mix-
/// style transition editor, loaded for one adjacent track pair at a time via
/// <see cref="LoadPairAsync"/>.
/// </summary>
public class MixTransitionViewModel : ReactiveObject, IDisposable
{
    private readonly ILogger<MixTransitionViewModel> _logger;
    private readonly ILibraryService _libraryService;
    private readonly IEventBus _eventBus;
    private readonly ArtworkCacheService _artworkCache;
    private readonly ITransitionRepository _transitionRepository;
    private readonly ITransitionPreviewPlayer _previewPlayer;
    private readonly SLSKDONET.Services.Repositories.ITrackRepository _trackRepository;
    private readonly ICuePointService _cuePointService;
    // Resolved lazily via IServiceProvider, not constructor-injected: CueForgeViewModel itself
    // depends on PlayerViewModel, which depends on this ViewModel (MixTransitionViewModel is part
    // of the player's Mix sidepanel), so a direct constructor dependency here closes a cycle —
    // PlayerViewModel -> MixTransitionViewModel -> CueForgeViewModel -> PlayerViewModel — that
    // Microsoft.Extensions.DependencyInjection refuses to resolve at startup. IServiceProvider
    // itself has no dependency edges, so this breaks the cycle; same lazy-resolution pattern
    // already used by LibraryViewModel for the same kind of cross-feature reference.
    private readonly IServiceProvider _serviceProvider;
    // Optional: a click on the waveform background (not a cue, not a drag) previews that exact
    // point of the individual track — separate from _previewPlayer, which only ever plays the
    // rendered crossfade between both tracks, not either one alone. Nullable/optional (matching
    // FlowBuilderViewModel's own trailing-optional injection of the same service) so this never
    // becomes a hard DI dependency for a "nice to have" audition feature.
    private readonly SLSKDONET.Services.Audio.ILibraryPreviewPlayer? _libraryPreviewPlayer;
    private static readonly SLSKDONET.Engine.Transitions.TransitionEngine _pointSuggestionEngine = new();

    public MixTransitionViewModel(
        ILogger<MixTransitionViewModel> logger,
        ILibraryService libraryService,
        IEventBus eventBus,
        ArtworkCacheService artworkCache,
        ITransitionRepository transitionRepository,
        ITransitionPreviewPlayer previewPlayer,
        SLSKDONET.Services.Repositories.ITrackRepository trackRepository,
        ICuePointService cuePointService,
        IServiceProvider serviceProvider,
        SLSKDONET.Services.Audio.ILibraryPreviewPlayer? libraryPreviewPlayer = null)
    {
        _logger = logger;
        _libraryService = libraryService;
        _eventBus = eventBus;
        _artworkCache = artworkCache;
        _transitionRepository = transitionRepository;
        _previewPlayer = previewPlayer;
        _trackRepository = trackRepository;
        _cuePointService = cuePointService;
        _serviceProvider = serviceProvider;
        _libraryPreviewPlayer = libraryPreviewPlayer;

        // In-place cue editors for the two decks: the same CueEditorSession engine as Cue Forge,
        // auditioning through the library preview player (see TrackCueEditorViewModel).
        var editorLogger = (ILogger?)serviceProvider.GetService(typeof(ILogger<TrackCueEditorViewModel>))
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var cueSessions = serviceProvider.GetService(typeof(CueSessionRegistry)) as CueSessionRegistry;
        OutgoingEditor = new TrackCueEditorViewModel("Outgoing", cuePointService, libraryPreviewPlayer, editorLogger, cueSessions);
        IncomingEditor = new TrackCueEditorViewModel("Incoming", cuePointService, libraryPreviewPlayer, editorLogger, cueSessions);
        // Every edit (add, drag, rename, delete, undo, reload…) bumps CueRevision: republish the
        // decks' cues so the waveforms and the mix-out / mix-in cue choices follow live.
        OutgoingEditor.Session.WhenAnyValue(s => s.CueRevision).Subscribe(_ => RefreshCueViews());
        IncomingEditor.Session.WhenAnyValue(s => s.CueRevision).Subscribe(_ => RefreshCueViews());
        OutgoingEditor.TriggerRequested += (_, t) => { RecordMixPointUndo(); NudgeSeconds = 0; SourceTriggerSeconds = t; };
        IncomingEditor.TriggerRequested += (_, t) => { RecordMixPointUndo(); TargetTriggerSeconds = t; };
        // Moving the cue a trigger sits on moves the trigger with it.
        OutgoingEditor.CueMoved += (_, m) => { if (Math.Abs(SourceTriggerSeconds - m.From) < 0.01) SourceTriggerSeconds = m.To; };
        IncomingEditor.CueMoved += (_, m) => { if (Math.Abs(TargetTriggerSeconds - m.From) < 0.01) TargetTriggerSeconds = m.To; };

        // The drop cue template is one app-wide setting held by the CueSessionRegistry, so both
        // decks, Cue Forge and cue generation always agree on it.
        foreach (var editor in new[] { OutgoingEditor, IncomingEditor })
        {
            // Generate cues from the deck strips — the same service as the library's "Regenerate Cues".
            editor.CueGenerator = GenerateCuesForDeckAsync;
            editor.CuesChanged += async (_, _) => await ReplanFromCuesAsync();
            // The deck you last listened to is the one the D / 1 / 2 keys place a drop on.
            // One audio source at a time: auditioning a deck stops a running transition preview.
            editor.Auditioned += (sender, _) =>
            {
                ActiveEditor = (TrackCueEditorViewModel)sender!;
                if (IsPlaying) _previewPlayer.StopPreview();
            };
            // Auditioning a cue holds the main mix (resumed when the cues are saved).
            editor.HoldMainPlayback = () =>
            {
                var player = serviceProvider.GetService(typeof(PlayerViewModel)) as PlayerViewModel;
                if (player is not { IsPlaying: true }) return false;
                player.TogglePlayPauseCommand.Execute(null);
                return true;
            };
            editor.ResumeMainPlayback = () =>
            {
                var player = serviceProvider.GetService(typeof(PlayerViewModel)) as PlayerViewModel;
                if (player is { IsPlaying: false }) player.TogglePlayPauseCommand.Execute(null);
            };
            var deck = editor;
            editor.Session.WhenAnyValue(s => s.SelectedCue).Subscribe(c => { if (c != null) ActiveEditor = deck; });
        }

        // Arrow keys (MixPreviewComponent key bindings): nudge the last-selected cue and re-audition.
        NudgeActiveCueCommand = ReactiveCommand.Create<string>(step =>
        {
            if (IsCueEditMode && ActiveEditor?.Session.SelectedCue != null) ActiveEditor.Session.NudgeCueCommand.Execute(step).Subscribe();
        });

        // D / 1 / 2 (MixPreviewComponent key bindings): a drop where you are listening on the last
        // auditioned deck — D numbers it by position, 1 / 2 make it Drop 1 / Drop 2.
        DropKeyCommand = ReactiveCommand.Create<string>(key =>
        {
            if (!IsCueEditMode || ActiveEditor is not { } editor) return;
            if (key == "1" || key == "2") editor.Session.SetNumberedDropAtPlayheadCommand.Execute(int.Parse(key)).Subscribe();
            else editor.Session.SetDropAtPlayheadCommand.Execute().Subscribe();
        });

        ToggleCueEditModeCommand = ReactiveCommand.Create(() => { IsCueEditMode = !IsCueEditMode; });
        // Clicking a cue flag (waveform, seconds) or a cue chip (the cue): select it and play the
        // deck from exactly that cue (clicking it again stops). In edit mode that's all — the cue is
        // selected for editing; otherwise the click also makes it the mix-out / mix-in point.
        OutgoingCueClickedCommand = ReactiveCommand.Create<double>(t => OnCueClicked(OutgoingEditor, FindCue(OutgoingEditor, t), t, isOutgoing: true));
        IncomingCueClickedCommand = ReactiveCommand.Create<double>(t => OnCueClicked(IncomingEditor, FindCue(IncomingEditor, t), t, isOutgoing: false));
        OutgoingCueChipCommand = ReactiveCommand.Create<OrbitCue>(c => OnCueClicked(OutgoingEditor, c, c.Timestamp, isOutgoing: true));
        IncomingCueChipCommand = ReactiveCommand.Create<OrbitCue>(c => OnCueClicked(IncomingEditor, c, c.Timestamp, isOutgoing: false));

        // Overlay drag: B's new mix-in point (already snapped by the overlay). Undoable.
        SetMixInFromOverlayCommand = ReactiveCommand.Create<double>(target =>
        {
            if (!HasLoadedPair || Math.Abs(target - TargetTriggerSeconds) < 1e-6) return;
            RecordMixPointUndo();
            TargetTriggerSeconds = Math.Max(0, target);
            StatusMessage = $"Mix-in moved to {FormatTimestamp(TargetTriggerSeconds)} · Ctrl+Z to undo";
        });
        UndoMixPointCommand = ReactiveCommand.Create(UndoMixPoint, this.WhenAnyValue(x => x.CanUndoMixPoint));

        _previewPlayer.PreviewStopped += OnPreviewStopped;
        if (_libraryPreviewPlayer != null) _libraryPreviewPlayer.PreviewStopped += OnLibraryPreviewStopped;

        SelectPresetCommand = ReactiveCommand.Create<string>(preset => SelectedPreset = preset);
        SelectBarsCommand = ReactiveCommand.Create<int>(bars => DurationBars = bars);
        // DurationBars is also set straight on the field by the structure planner and pair loading,
        // which then raise DurationBars — follow that notification rather than every assignment.
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DurationBars)) RaiseBarsFlags(); };
        // One beat at the outgoing tempo (falls back to 0.25 s when the BPM is unknown), so a mix
        // point that sits on a cue stays on the beat grid when nudged.
        NudgeBackCommand = ReactiveCommand.Create(() => { RecordMixPointUndo(); NudgeSeconds -= BeatSeconds(OutgoingBpm); });
        NudgeForwardCommand = ReactiveCommand.Create(() => { RecordMixPointUndo(); NudgeSeconds += BeatSeconds(OutgoingBpm); });
        NudgeIncomingBackCommand = ReactiveCommand.Create(() => { RecordMixPointUndo(); TargetTriggerSeconds = Math.Max(0, TargetTriggerSeconds - BeatSeconds(IncomingBpm)); });
        NudgeIncomingForwardCommand = ReactiveCommand.Create(() => { RecordMixPointUndo(); TargetTriggerSeconds += BeatSeconds(IncomingBpm); });
        PlayCommand = ReactiveCommand.CreateFromTask(PlayPreviewAsync, this.WhenAnyValue(x => x.IsPlaying, playing => !playing));
        PauseCommand = ReactiveCommand.Create(() => _previewPlayer.StopPreview());
        TogglePreviewCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (IsPlaying) _previewPlayer.StopPreview();
            else await PlayPreviewAsync();
        });
        SelectLeadInBarsCommand = ReactiveCommand.Create<int>(bars => PreviewLeadInBars = bars);
        ForgetSavedTransitionCommand = ReactiveCommand.CreateFromTask(ForgetSavedTransitionAsync,
            this.WhenAnyValue(x => x.HasSavedTransition));
        CancelCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            _previewPlayer.StopPreview();
            await FlushCueEditsAsync();
            Closed?.Invoke(this, EventArgs.Empty);
        });
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync,
            this.WhenAnyValue(x => x.SaveBlockedReason, blocked => string.IsNullOrEmpty(blocked)));
        // Also reachable from the "No transition loaded" empty state (no pair picked yet, so
        // _playlistId is still Guid.Empty) — MainViewModel's handler treats Guid.Empty as "just
        // navigate, nothing to preload" rather than silently no-opping here.
        OpenInFlowBuilderCommand = ReactiveCommand.Create(() =>
        {
            _eventBus.Publish(new OpenFlowBuilderForPlaylistEvent(_playlistId));
        });
        // "Fix in Cue Forge" — same load-then-navigate sequence TrackOperationsViewModel uses to
        // jump into Cue Forge for a specific track (ExecuteOpenInCueForge), so a mix that looks
        // wrong here has a one-click path back to the tool that actually edits cue points.
        OpenOutgoingInCueForgeCommand = ReactiveCommand.CreateFromTask(() => OpenTrackInCueForgeAsync(OutgoingTrack));
        OpenIncomingInCueForgeCommand = ReactiveCommand.CreateFromTask(() => OpenTrackInCueForgeAsync(IncomingTrack));
        // "Fix & analyse" — opens the track in Cue Forge and immediately queues a full analysis,
        // so a pair with missing analysis or no cues has a one-click path to being corrected.
        FixOutgoingInCueForgeCommand = ReactiveCommand.CreateFromTask(() => OpenTrackInCueForgeAsync(OutgoingTrack, startFullAnalysis: true));
        FixIncomingInCueForgeCommand = ReactiveCommand.CreateFromTask(() => OpenTrackInCueForgeAsync(IncomingTrack, startFullAnalysis: true));
        // Waveform right-click → "Add cue here": jumps to Cue Forge, adds a real cue at the
        // clicked time, and selects it — a purely ad-hoc waveform click can't set a *good*
        // transition point (it's not beat/phrase aligned), but it's exactly how a user finds
        // where a good cue point SHOULD go, so this is the bridge from "I found the spot" to
        // "now it's a real, manageable cue."
        // Waveform right-click → "Add cue here" now adds the cue right here (switching to edit mode
        // so it can be named and placed) instead of leaving for Cue Forge.
        AddCueToOutgoingInCueForgeCommand = ReactiveCommand.Create<double>(seconds => { IsCueEditMode = true; OutgoingEditor.Session.AddCueAtCommand.Execute(seconds).Subscribe(); });
        AddCueToIncomingInCueForgeCommand = ReactiveCommand.Create<double>(seconds => { IsCueEditMode = true; IncomingEditor.Session.AddCueAtCommand.Execute(seconds).Subscribe(); });
        SelectSourceCueCommand = ReactiveCommand.Create<double>(timestamp => { RecordMixPointUndo(); SourceTriggerSeconds = timestamp; });
        SelectTargetCueCommand = ReactiveCommand.Create<double>(timestamp => { RecordMixPointUndo(); TargetTriggerSeconds = timestamp; });
        // Plain click (not a drag, not a cue hit) on either waveform's background — auditions
        // that exact point of the individual track so the user can find where a drop/phrase
        // actually lands before deciding where a cue belongs. Independent of the trigger point
        // and of the full crossfade Preview/Pause transport below.
        PreviewSeekOutgoingCommand = ReactiveCommand.Create<double>(seconds => PreviewSeekTrack(OutgoingTrack, seconds));
        PreviewSeekIncomingCommand = ReactiveCommand.Create<double>(seconds => PreviewSeekTrack(IncomingTrack, seconds));
        StopPreviewSeekCommand = ReactiveCommand.Create(StopPreviewSeek);
        ZoomInCommand = ReactiveCommand.Create(() => { WaveformZoomLevel *= 1.5; });
        ZoomOutCommand = ReactiveCommand.Create(() => { WaveformZoomLevel /= 1.5; });
        SaveOutgoingBpmCommand = ReactiveCommand.CreateFromTask(() => SaveBpmAsync(isOutgoing: true));
        SaveIncomingBpmCommand = ReactiveCommand.CreateFromTask(() => SaveBpmAsync(isOutgoing: false));
        ShareMixOutCommand = ReactiveCommand.CreateFromTask(() => ShareMixPointAsync(isOutgoing: true));
        ShareMixInCommand = ReactiveCommand.CreateFromTask(() => ShareMixPointAsync(isOutgoing: false));
    }

    /// <summary>Raised when the user dismisses the editor (Cancel/Dismiss button).</summary>
    public event EventHandler? Closed;

    // TransitionPreviewPlayer.PreviewStopped fires from NAudio's internal WasapiOut playback
    // thread, not the UI thread — setting a bound ReactiveObject property directly from there
    // throws (Avalonia's dispatcher verifies the calling thread) and crashes the whole process
    // the moment a preview finishes playing on its own, not just on an explicit Pause click.
    private void OnPreviewStopped(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(EndPreviewPlayback);

    private void EndPreviewPlayback()
    {
        IsPlaying = false;
        _previewPositionPoll?.Dispose();
        _previewPositionPoll = null;
        _previewPositionSeconds = null;
        RaisePreviewPositionChanged();
    }

    private void OnLibraryPreviewStopped(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsPreviewSeekPlaying = false;
            PreviewSeekStatusText = string.Empty;
        });

    public void Dispose()
    {
        _previewPositionPoll?.Dispose();
        _previewPositionPoll = null;
        _previewPlayer.PreviewStopped -= OnPreviewStopped;
        if (_libraryPreviewPlayer != null) _libraryPreviewPlayer.PreviewStopped -= OnLibraryPreviewStopped;
        OutgoingEditor.Dispose();
        IncomingEditor.Dispose();
    }

    public PlaylistTrackViewModel? OutgoingTrack { get; private set; }
    public PlaylistTrackViewModel? IncomingTrack { get; private set; }

    /// <summary>True until a track pair is actually loaded — the Mix tab is reachable directly
    /// (it's sticky, like Similar Tracks) without a pair ever having been picked, which used to
    /// render as a silently-empty panel with no indication of what to do next.</summary>
    public bool HasLoadedPair => OutgoingTrack != null && IncomingTrack != null;

    public WaveformAnalysisData? WaveformDataA => OutgoingTrack?.WaveformData;
    public WaveformAnalysisData? WaveformDataB => IncomingTrack?.WaveformData;

    // Live scrub position within each waveform during preview playback isn't wired yet
    // (ITransitionPreviewPlayer doesn't expose a position stream) — instead these mark WHERE
    // the suggested/saved mix-out (A) and mix-in (B) points fall as a fraction of each track's
    // duration, so the waveform playhead visually shows the system's pick rather than sitting
    // at a meaningless fixed spot.
    public float ProgressA
    {
        get
        {
            var duration = OutgoingDurationSeconds;
            var at = PreviewPlayheadOutgoingSeconds ?? EffectiveSourceTriggerSeconds;
            return duration > 0 ? (float)Math.Clamp(at / duration, 0.0, 1.0) : 1f;
        }
    }

    public float ProgressB
    {
        get
        {
            var duration = IncomingDurationSeconds;
            var at = PreviewPlayheadIncomingSeconds ?? TargetTriggerSeconds;
            return duration > 0 ? (float)Math.Clamp(at / duration, 0.0, 1.0) : 0f;
        }
    }

    private double _waveformZoomLevel = 4.0;
    /// <summary>Single source of truth for both waveforms' zoom — MixPreviewComponent.axaml binds
    /// its two WaveformControls' ZoomLevel here instead of each managing its own, so it can never
    /// drift out of sync with the OutgoingViewOffset/IncomingViewOffset math below (which needs
    /// the exact same number), and the two tracks stay directly comparable at the same zoom when
    /// lined up via their independent pan sliders. User-adjustable via ZoomInCommand/
    /// ZoomOutCommand (and the zoom slider in MixPreviewComponent.axaml), clamped to
    /// WaveformControl's own [1,16] range.</summary>
    public double WaveformZoomLevel
    {
        get => _waveformZoomLevel;
        set
        {
            this.RaiseAndSetIfChanged(ref _waveformZoomLevel, Math.Clamp(value, 1.0, 16.0));
            this.RaisePropertyChanged(nameof(WaveformViewOffsetMaximum));
            this.RaisePropertyChanged(nameof(OutgoingViewOffset));
            this.RaisePropertyChanged(nameof(IncomingViewOffset));
        }
    }

    /// <summary>Highest valid ViewOffset at the current zoom — the pan slider's Maximum.</summary>
    public double WaveformViewOffsetMaximum => Math.Max(0.0, 1.0 - (1.0 / WaveformZoomLevel));

    private double? _outgoingManualOffset;
    /// <summary>Scrolls the zoomed OUTGOING waveform. Defaults to centering on the current mix-out
    /// trigger point (replacing a binding that bound a bool (ObjectConverters.IsNull on a non-
    /// nullable float) into this double property — which always evaluated to a fixed 0, showing
    /// only the track's first 1/ZoomLevel regardless of where the actual trigger point was), but
    /// the user can drag the pan slider under the waveform (MixPreviewComponent.axaml) to scroll
    /// it independently and line up a feature against the INCOMING waveform below for a by-eye
    /// alignment check. Any new trigger point (cue click/Nudge) clears the manual pan so the view
    /// re-centers rather than leaving a stale scroll position pointing at the old trigger.</summary>
    public double OutgoingViewOffset
    {
        // Re-clamped on read, not just on write: a manual offset set at one zoom level can exceed
        // WaveformViewOffsetMaximum after the user zooms back out, since ZoomInCommand/
        // ZoomOutCommand change zoom without touching the stored offset.
        get => _outgoingManualOffset.HasValue ? Math.Clamp(_outgoingManualOffset.Value, 0.0, WaveformViewOffsetMaximum) : CenteredViewOffset(ProgressA);
        set
        {
            _outgoingManualOffset = Math.Clamp(value, 0.0, WaveformViewOffsetMaximum);
            this.RaisePropertyChanged();
        }
    }

    private double? _incomingManualOffset;
    /// <summary>Same as <see cref="OutgoingViewOffset"/>, for the INCOMING waveform.</summary>
    public double IncomingViewOffset
    {
        get => _incomingManualOffset.HasValue ? Math.Clamp(_incomingManualOffset.Value, 0.0, WaveformViewOffsetMaximum) : CenteredViewOffset(ProgressB);
        set
        {
            _incomingManualOffset = Math.Clamp(value, 0.0, WaveformViewOffsetMaximum);
            this.RaisePropertyChanged();
        }
    }

    private double CenteredViewOffset(float progress)
    {
        var halfWindow = 1.0 / (2.0 * WaveformZoomLevel);
        var maxOffset = Math.Max(0.0, 1.0 - (1.0 / WaveformZoomLevel));
        return Math.Clamp(progress - halfWindow, 0.0, maxOffset);
    }

    private string _statusMessage = "Select a preset to preview the transition.";
    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set => this.RaiseAndSetIfChanged(ref _isPlaying, value);
    }

    // ── Waveform click-to-audition (distinct from IsPlaying/PlayCommand above, which is the
    // full rendered crossfade Preview) ──────────────────────────────────────────────────────
    private bool _isPreviewSeekPlaying;
    public bool IsPreviewSeekPlaying
    {
        get => _isPreviewSeekPlaying;
        set => this.RaiseAndSetIfChanged(ref _isPreviewSeekPlaying, value);
    }

    private string _previewSeekStatusText = string.Empty;
    /// <summary>Which track/time is currently auditioning — shown next to the Stop button so it's
    /// never ambiguous where the sound is coming from.</summary>
    public string PreviewSeekStatusText
    {
        get => _previewSeekStatusText;
        set => this.RaiseAndSetIfChanged(ref _previewSeekStatusText, value);
    }

    private TrackPairCompatibilityScorer.PairScore? _pairScore;

    /// <summary>Pixel width (0-140) for the phase-confidence meter in MixPreviewComponent, driven
    /// by the same harmonic/energy compatibility score the inline track-list badge uses.</summary>
    public double PhaseConfidenceWidth => 140.0 * ((_pairScore?.CombinedScore ?? 0) / 100.0);

    public static IReadOnlyList<string> PresetNames { get; } = TransitionPresetLibrary.PresetNames;
    public static IReadOnlyList<int> DurationBarOptions { get; } = new[] { 4, 8, 16, 32 };

    // Structure of both decks (sections, drops, vocals), for the planned presets. Null until
    // loaded or when a track isn't analysed — then those presets fall back to the old suggestion.
    private SLSKDONET.Engine.Transitions.TrackStructure? _outStructure, _inStructure;

    private string _selectedPreset = "Auto";
    public string SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPreset, value);
            this.RaisePropertyChanged(nameof(IsCustomMode));
            this.RaisePropertyChanged(nameof(SelectedPresetDescription));
            // Auto / Rolling / Relaxed / Drop Sync are timed from the track structure: picking one
            // moves the mix-out/mix-in points and length to where that transition belongs.
            if (TransitionPresetLibrary.IsStructurePlanned(value) && ApplyStructurePlan(value)) return;
            RebuildLiveModel();
        }
    }

    public bool IsCustomMode => SelectedPreset == "Custom";

    // Two-way IsChecked targets for the 4/8/16/32 segmented control (compiled bindings, no converter).
    public bool IsBars4 { get => DurationBars == 4; set { if (value) DurationBars = 4; } }
    public bool IsBars8 { get => DurationBars == 8; set { if (value) DurationBars = 8; } }
    public bool IsBars16 { get => DurationBars == 16; set { if (value) DurationBars = 16; } }
    public bool IsBars32 { get => DurationBars == 32; set { if (value) DurationBars = 32; } }

    private void RaiseBarsFlags()
    {
        this.RaisePropertyChanged(nameof(IsBars4));
        this.RaisePropertyChanged(nameof(IsBars8));
        this.RaisePropertyChanged(nameof(IsBars16));
        this.RaisePropertyChanged(nameof(IsBars32));
    }

    private int _durationBars = 16;
    public int DurationBars
    {
        get => _durationBars;
        set { this.RaiseAndSetIfChanged(ref _durationBars, value); RebuildLiveModel(); }
    }

    private double _nudgeSeconds;
    /// <summary>Fine offset (seconds) applied on top of <see cref="SourceTriggerSeconds"/> —
    /// Spotify's Nudge Back/Forward, for hand-correcting the system's suggested mix-out point.</summary>
    public double NudgeSeconds
    {
        get => _nudgeSeconds;
        set
        {
            this.RaiseAndSetIfChanged(ref _nudgeSeconds, value);
            _outgoingManualOffset = null;
            this.RaisePropertyChanged(nameof(EffectiveSourceTriggerSeconds));
            this.RaisePropertyChanged(nameof(SourceTriggerDisplay));
            this.RaisePropertyChanged(nameof(ProgressA));
            this.RaisePropertyChanged(nameof(OutgoingViewOffset));
            RaiseRegionChanged();
            RefreshDirtyState();
        }
    }

    private double _sourceTriggerSeconds;
    /// <summary>Where in the outgoing track (seconds) the mix-out begins — from
    /// <see cref="SLSKDONET.Engine.Transitions.TransitionEngine.OptimizeTransition"/>'s
    /// cue/tempo/key/vocal-aware suggestion, or a saved override.</summary>
    public double SourceTriggerSeconds
    {
        get => _sourceTriggerSeconds;
        set
        {
            this.RaiseAndSetIfChanged(ref _sourceTriggerSeconds, value);
            _outgoingManualOffset = null;
            this.RaisePropertyChanged(nameof(EffectiveSourceTriggerSeconds));
            this.RaisePropertyChanged(nameof(SourceTriggerDisplay));
            this.RaisePropertyChanged(nameof(ProgressA));
            this.RaisePropertyChanged(nameof(OutgoingViewOffset));
            RaiseRegionChanged();
            RefreshDirtyState();
        }
    }

    /// <summary>SourceTriggerSeconds plus any hand Nudge — what Preview/Save actually use.</summary>
    public double EffectiveSourceTriggerSeconds => SourceTriggerSeconds + NudgeSeconds;

    private double _targetTriggerSeconds;
    /// <summary>Where in the incoming track (seconds) playback starts — same suggestion engine;
    /// may land on the track's intro, a Mix-In cue, or its first Drop cue.</summary>
    public double TargetTriggerSeconds
    {
        get => _targetTriggerSeconds;
        set
        {
            this.RaiseAndSetIfChanged(ref _targetTriggerSeconds, value);
            _incomingManualOffset = null;
            this.RaisePropertyChanged(nameof(TargetTriggerDisplay));
            this.RaisePropertyChanged(nameof(ProgressB));
            this.RaisePropertyChanged(nameof(IncomingViewOffset));
            RaiseRegionChanged();
            RefreshDirtyState();
        }
    }

    public string SourceTriggerDisplay => FormatTimestamp(EffectiveSourceTriggerSeconds);
    public string TargetTriggerDisplay => FormatTimestamp(TargetTriggerSeconds);

    private static string FormatTimestamp(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    private string _transitionPointReasoning = string.Empty;
    /// <summary>Human-readable explanation of why these particular trigger points were chosen
    /// (tempo jump, harmonic clash, vocal overlap, etc.) — surfaces the system's reasoning
    /// instead of a black-box "Auto" pick.</summary>
    public string TransitionPointReasoning
    {
        get => _transitionPointReasoning;
        set => this.RaiseAndSetIfChanged(ref _transitionPointReasoning, value);
    }

    // ── Custom-mode overrides (null = use the preset's default) ──────────────────────────
    private float? _customEchoDecayFactor;
    public float? CustomEchoDecayFactor { get => _customEchoDecayFactor; set { this.RaiseAndSetIfChanged(ref _customEchoDecayFactor, value); RebuildLiveModel(); } }

    private float? _customFilterStartFrequency;
    public float? CustomFilterStartFrequency { get => _customFilterStartFrequency; set { this.RaiseAndSetIfChanged(ref _customFilterStartFrequency, value); RebuildLiveModel(); } }

    private float? _customFilterEndFrequency;
    public float? CustomFilterEndFrequency { get => _customFilterEndFrequency; set { this.RaiseAndSetIfChanged(ref _customFilterEndFrequency, value); RebuildLiveModel(); } }

    private float? _customWaveDuckDepth;
    /// <summary>Custom-mode override for the "Wave" preset's rhythmic duck depth (0-1); null = preset default.</summary>
    public float? CustomWaveDuckDepth { get => _customWaveDuckDepth; set { this.RaiseAndSetIfChanged(ref _customWaveDuckDepth, value); RebuildLiveModel(); } }

    private bool? _customFilterSweepRising;
    /// <summary>Custom-mode override for the "Rise" preset's sweep direction — true sweeps the
    /// incoming track up (energy build into the drop) instead of the outgoing track down; null =
    /// preset default.</summary>
    public bool? CustomFilterSweepRising { get => _customFilterSweepRising; set { this.RaiseAndSetIfChanged(ref _customFilterSweepRising, value); RebuildLiveModel(); } }

    // ── Custom-mode EQ band swap ("Blend" preset's bass-handover technique) — which bands swap
    // from outgoing to incoming, and where the crossovers sit. Null = preset default (Low only).
    private bool? _customEqSwapLow;
    public bool? CustomEqSwapLow { get => _customEqSwapLow; set { this.RaiseAndSetIfChanged(ref _customEqSwapLow, value); RebuildLiveModel(); } }

    private bool? _customEqSwapMid;
    public bool? CustomEqSwapMid { get => _customEqSwapMid; set { this.RaiseAndSetIfChanged(ref _customEqSwapMid, value); RebuildLiveModel(); } }

    private bool? _customEqSwapHigh;
    public bool? CustomEqSwapHigh { get => _customEqSwapHigh; set { this.RaiseAndSetIfChanged(ref _customEqSwapHigh, value); RebuildLiveModel(); } }

    private float? _customEqLowCrossoverHz;
    public float? CustomEqLowCrossoverHz { get => _customEqLowCrossoverHz; set { this.RaiseAndSetIfChanged(ref _customEqLowCrossoverHz, value); RebuildLiveModel(); } }

    private float? _customEqHighCrossoverHz;
    public float? CustomEqHighCrossoverHz { get => _customEqHighCrossoverHz; set { this.RaiseAndSetIfChanged(ref _customEqHighCrossoverHz, value); RebuildLiveModel(); } }

    // ── Custom-mode "Double Drop" loop — loops a bar-aligned tail of the outgoing track once or
    // twice before crossfading into the incoming track's own drop. Null/false = off (plain
    // crossfade at this window). ──────────────────────────────────────────────────────────
    private bool? _customLoopEnabled;
    public bool? CustomLoopEnabled { get => _customLoopEnabled; set { this.RaiseAndSetIfChanged(ref _customLoopEnabled, value); RebuildLiveModel(); } }

    private int? _customLoopBars;
    public int? CustomLoopBars { get => _customLoopBars; set { this.RaiseAndSetIfChanged(ref _customLoopBars, value); RebuildLiveModel(); } }

    private int? _customLoopRepeats;
    public int? CustomLoopRepeats { get => _customLoopRepeats; set { this.RaiseAndSetIfChanged(ref _customLoopRepeats, value); RebuildLiveModel(); } }

    // ── Cue-point picking (click a marker on either waveform to set it as the transition's
    // trigger point, overriding the suggestion engine) ─────────────────────────────────────
    private IEnumerable<OrbitCue> _outgoingCues = Array.Empty<OrbitCue>();
    public IEnumerable<OrbitCue> OutgoingCues { get => _outgoingCues; private set => this.RaiseAndSetIfChanged(ref _outgoingCues, value); }

    private IEnumerable<OrbitCue> _incomingCues = Array.Empty<OrbitCue>();
    public IEnumerable<OrbitCue> IncomingCues { get => _incomingCues; private set => this.RaiseAndSetIfChanged(ref _incomingCues, value); }

    private bool _mixCuesOnly = true;
    /// <summary>Focused transition view: show only shared Mix-In / Mix-Out cues (including legacy
    /// generated labels). All Workstation cues remain intact and return in edit mode or on toggle.</summary>
    public bool MixCuesOnly
    {
        get => _mixCuesOnly;
        set
        {
            this.RaiseAndSetIfChanged(ref _mixCuesOnly, value);
            this.RaisePropertyChanged(nameof(CueFilterButtonText));
            RefreshCueViews();
        }
    }

    public string CueFilterButtonText => MixCuesOnly ? "Mix cues only" : "All cues";

    private void RefreshCueViews()
    {
        OutgoingCues = VisibleCues(OutgoingEditor.Session.Cues).ToList();
        IncomingCues = VisibleCues(IncomingEditor.Session.Cues).ToList();
        RefreshMissingCues();
    }

    private IEnumerable<OrbitCue> VisibleCues(IEnumerable<OrbitCue> cues) =>
        MixCuesOnly && !IsCueEditMode ? cues.Where(c => c.IsMixPoint) : cues;

    // ── In-place cue editing ─────────────────────────────────────────────────────────────
    public TrackCueEditorViewModel OutgoingEditor { get; }
    public TrackCueEditorViewModel IncomingEditor { get; }

    private bool _isCueEditMode;
    /// <summary>Edit mode: cues can be dragged, clicking one selects it for editing, and each deck
    /// shows its cue inspector. Off: cues are trigger-point pickers, as before.</summary>
    public bool IsCueEditMode
    {
        get => _isCueEditMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isCueEditMode, value);
            this.RaisePropertyChanged(nameof(CueFlagTip));
            RefreshCueViews();
            if (!value) { OutgoingEditor.EndAudition(); IncomingEditor.EndAudition(); }
        }
    }

    public ReactiveCommand<Unit, Unit> ToggleCueEditModeCommand { get; }
    public ReactiveCommand<Unit, Unit> ShareMixOutCommand { get; }
    public ReactiveCommand<Unit, Unit> ShareMixInCommand { get; }

    /// <summary>The deck whose cue was selected last — the one the arrow keys fine-tune.</summary>
    public TrackCueEditorViewModel? ActiveEditor { get; private set; }

    /// <summary>"-beat"/"+beat" (←/→), "-bar"/"+bar" (Shift), "-fine"/"+fine" (Ctrl, 10 ms).</summary>
    public ReactiveCommand<string, Unit> NudgeActiveCueCommand { get; }
    /// <summary>"d", "1" or "2" — see the constructor.</summary>
    public ReactiveCommand<string, Unit> DropKeyCommand { get; }
    public ReactiveCommand<double, Unit> OutgoingCueClickedCommand { get; }
    public ReactiveCommand<double, Unit> IncomingCueClickedCommand { get; }
    /// <summary>Cue chip under a deck (the cue): same as clicking its flag.</summary>
    public ReactiveCommand<OrbitCue, Unit> OutgoingCueChipCommand { get; }
    public ReactiveCommand<OrbitCue, Unit> IncomingCueChipCommand { get; }

    /// <summary>Tooltip of the deck waveforms' cue flags.</summary>
    public string CueFlagTip => IsCueEditMode
        ? "Click to play from here · drag to move"
        : "Click to play from here and mix there";

    private static OrbitCue? FindCue(TrackCueEditorViewModel deck, double seconds) =>
        deck.Session.Cues.Where(c => Math.Abs(c.Timestamp - seconds) < 0.01).OrderBy(c => Math.Abs(c.Timestamp - seconds)).FirstOrDefault();

    /// <summary>A cue flag or chip was clicked: select it and prelisten from it through the deck's
    /// own audition (the library preview player). Outside edit mode the cue also becomes the mix
    /// point. A running transition preview stops first — one audio source at a time.</summary>
    private void OnCueClicked(TrackCueEditorViewModel deck, OrbitCue? cue, double seconds, bool isOutgoing)
    {
        if (!IsCueEditMode)
        {
            bool changes = isOutgoing
                ? Math.Abs(EffectiveSourceTriggerSeconds - seconds) > 1e-6
                : Math.Abs(TargetTriggerSeconds - seconds) > 1e-6;
            if (changes) RecordMixPointUndo();
            if (isOutgoing) { _nudgeSeconds = 0; this.RaisePropertyChanged(nameof(NudgeSeconds)); SourceTriggerSeconds = seconds; }
            else TargetTriggerSeconds = seconds;
        }
        if (cue == null) return;
        if (IsPlaying) _previewPlayer.StopPreview();
        (isOutgoing ? IncomingEditor : OutgoingEditor).Session.StopPrelisten();
        deck.Session.PrelistenCue(cue);
        IsPreviewSeekPlaying = deck.Session.PrelisteningCue != null;
        PreviewSeekStatusText = IsPreviewSeekPlaying ? $"Playing {deck.TrackTitle} from {cue.Name} ({FormatTimestamp(cue.Timestamp)})" : string.Empty;
    }

    /// <summary>Space while a cue is being prelistened: stop it. False when nothing was prelistening.</summary>
    public bool StopCuePrelisten()
    {
        bool stopped = OutgoingEditor.Session.StopPrelisten() | IncomingEditor.Session.StopPrelisten();
        if (stopped) { IsPreviewSeekPlaying = false; PreviewSeekStatusText = string.Empty; }
        return stopped;
    }

    // ── Mix-point undo (overlay drag, nudges, cue picks) ─────────────────────────────────
    private readonly Stack<(double Source, double Nudge, double Target)> _mixPointUndo = new();

    private bool _canUndoMixPoint;
    public bool CanUndoMixPoint { get => _canUndoMixPoint; private set => this.RaiseAndSetIfChanged(ref _canUndoMixPoint, value); }

    /// <summary>Undo the last mix-out / mix-in change (Ctrl+Z in the transition editor).</summary>
    public ReactiveCommand<Unit, Unit> UndoMixPointCommand { get; }
    /// <summary>The overlay's drag result: B's new mix-in point (seconds in B, snapped).</summary>
    public ReactiveCommand<double, Unit> SetMixInFromOverlayCommand { get; }

    private void RecordMixPointUndo()
    {
        if (!HasLoadedPair) return;
        if (_mixPointUndo.Count >= 50)
        {
            var keep = _mixPointUndo.Reverse().Skip(1).ToList();
            _mixPointUndo.Clear();
            foreach (var s in keep) _mixPointUndo.Push(s);
        }
        _mixPointUndo.Push((SourceTriggerSeconds, NudgeSeconds, TargetTriggerSeconds));
        CanUndoMixPoint = true;
    }

    private void UndoMixPoint()
    {
        if (_mixPointUndo.Count == 0) return;
        var (source, nudge, target) = _mixPointUndo.Pop();
        _nudgeSeconds = nudge;
        this.RaisePropertyChanged(nameof(NudgeSeconds));
        SourceTriggerSeconds = source;
        TargetTriggerSeconds = target;
        CanUndoMixPoint = _mixPointUndo.Count > 0;
        StatusMessage = "Mix points restored.";
    }

    private void ClearMixPointUndo()
    {
        _mixPointUndo.Clear();
        CanUndoMixPoint = false;
    }

    // ── Overlay view (A out + B in on one timeline) ──────────────────────────────────────
    private TransitionOverlayModel? _overlay;
    /// <summary>Both tracks on one tempo-corrected timeline, aligned by the mix points; rebuilt
    /// whenever a mix point, nudge, length, preset or tempo changes.</summary>
    public TransitionOverlayModel? Overlay { get => _overlay; private set => this.RaiseAndSetIfChanged(ref _overlay, value); }

    /// <summary>The preview position on the overlay's timeline (0 = mix-out), null when not previewing.</summary>
    public double? OverlayPlayheadSeconds => IsPlaying && _previewPositionSeconds is double p ? p - _activePreviewLeadInSeconds : null;

    /// <summary>"16 bars · 30.0 s · B +1.6 %" under the overlay header.</summary>
    public string OverlaySummary => Overlay is not { } o ? string.Empty
        : $"{o.WindowBars.ToString(System.Globalization.CultureInfo.InvariantCulture)} bars blend · " +
          $"A out {FormatTimestamp(o.SourceTrigger)} · B in {FormatTimestamp(o.TargetTrigger)}" +
          (OutgoingBpm > 0 && IncomingBpm > 0 ? $" · B at {o.IncomingRate.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}×" : string.Empty);

    private void RefreshOverlay()
    {
        if (!HasLoadedPair) { Overlay = null; this.RaisePropertyChanged(nameof(OverlaySummary)); return; }
        Overlay = TransitionOverlayModel.Build(
            OutgoingBpm, IncomingBpm, EffectiveSourceTriggerSeconds, TargetTriggerSeconds, TransitionWindowSeconds,
            PreviewLeadInSeconds, OutgoingDurationSeconds, IncomingDurationSeconds, IncomingEditor.Session.GridOffset,
            OutgoingAutomationCurve as IReadOnlyList<float>, IncomingAutomationCurve as IReadOnlyList<float>, OverlayMarkers(),
            IncomingPlaybackRate);
        this.RaisePropertyChanged(nameof(OverlaySummary));
    }

    /// <summary>EQ hand-over points of the current blend (drawn on the overlay).</summary>
    private IReadOnlyList<OverlayMarker> OverlayMarkers()
    {
        double w = TransitionWindowSeconds;
        if (LiveModel.Type.ToAutomationType() != SLSKDONET.Services.Audio.TransitionType.EqSwap || w <= 0) return Array.Empty<OverlayMarker>();
        if (LiveModel.EqSwapLow && LiveModel.EqHardLowSwap && LiveModel.EqHardLowSwapAtEnd)
            return new[] { new OverlayMarker(w, "Bass swap") };
        if (LiveModel.EqSwapLow && LiveModel.EqHardLowSwap) return new[] { new OverlayMarker(w / 2, "Bass swap") };
        var bands = new List<string>();
        if (LiveModel.EqSwapLow) bands.Add("low");
        if (LiveModel.EqSwapMid) bands.Add("mid");
        if (LiveModel.EqSwapHigh) bands.Add("high");
        return bands.Count == 0 ? Array.Empty<OverlayMarker>() : new[] { new OverlayMarker(w / 2, "EQ " + string.Join("/", bands) + " cross") };
    }

    /// <summary>Saves unsaved cue edits on both decks (auto-save when leaving a pair or closing).</summary>
    public async Task FlushCueEditsAsync()
    {
        var saved = new List<string>();
        if (await OutgoingEditor.SaveIfDirtyAsync()) saved.Add(OutgoingEditor.TrackTitle);
        if (await IncomingEditor.SaveIfDirtyAsync()) saved.Add(IncomingEditor.TrackTitle);
        if (saved.Count > 0) StatusMessage = $"✓ Auto-saved cues for {string.Join(" and ", saved)}";
        OutgoingEditor.EndAudition();
        IncomingEditor.EndAudition();
    }

    // ── BPM editing ──────────────────────────────────────────────────────────────────────
    private double _outgoingBpm;
    public double OutgoingBpm { get => _outgoingBpm; set { this.RaiseAndSetIfChanged(ref _outgoingBpm, value); RaiseTempoChanged(); } }

    private double _incomingBpm;
    public double IncomingBpm { get => _incomingBpm; set { this.RaiseAndSetIfChanged(ref _incomingBpm, value); RaiseTempoChanged(); } }

    /// <summary>The DSP model built from the current preset/bars/custom-overrides — what Preview and Save both act on.</summary>
    public TransitionModel LiveModel { get; private set; } = new();

    // Automation-curve overlay for the two stacked waveforms (the pink/yellow envelope lines
    // in the reference screenshots), sampled from the same TransitionEngine automation math the
    // live playback engine (AudioPlayerService.AdvanceCrossfade) uses.
    private IEnumerable<float>? _outgoingAutomationCurve;
    public IEnumerable<float>? OutgoingAutomationCurve { get => _outgoingAutomationCurve; private set => this.RaiseAndSetIfChanged(ref _outgoingAutomationCurve, value); }

    private IEnumerable<float>? _incomingAutomationCurve;
    public IEnumerable<float>? IncomingAutomationCurve { get => _incomingAutomationCurve; private set => this.RaiseAndSetIfChanged(ref _incomingAutomationCurve, value); }

    public ReactiveCommand<string, Unit> SelectPresetCommand { get; }
    public ReactiveCommand<int, Unit> SelectBarsCommand { get; }
    public ReactiveCommand<Unit, Unit> NudgeBackCommand { get; }
    public ReactiveCommand<Unit, Unit> NudgeForwardCommand { get; }
    public ReactiveCommand<Unit, Unit> NudgeIncomingBackCommand { get; }
    public ReactiveCommand<Unit, Unit> NudgeIncomingForwardCommand { get; }
    /// <summary>Play/stop in one button (the Flow Builder transport).</summary>
    public ReactiveCommand<Unit, Unit> TogglePreviewCommand { get; }
    public ReactiveCommand<int, Unit> SelectLeadInBarsCommand { get; }
    public ReactiveCommand<Unit, Unit> ForgetSavedTransitionCommand { get; }
    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> PauseCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    /// <summary>"Open in Flow Builder" link — this editor only handles one adjacent pair at a
    /// time; Flow Builder is the full-option editor for the whole playlist. Publishes
    /// <see cref="OpenFlowBuilderForPlaylistEvent"/>, which MainViewModel picks up to navigate
    /// there and preload this playlist.</summary>
    public ReactiveCommand<Unit, Unit> OpenInFlowBuilderCommand { get; }

    /// <summary>"Fix in Cue Forge" links — load the outgoing/incoming track into the CueForgeViewModel
    /// singleton and navigate there, for when this transition's trigger points look wrong and the
    /// underlying cue data needs hand-correcting.</summary>
    public ReactiveCommand<Unit, Unit> OpenOutgoingInCueForgeCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenIncomingInCueForgeCommand { get; }

    /// <summary>Opens a deck in Cue Forge and starts a full analysis for it (see <see cref="AttentionMessage"/>).</summary>
    public ReactiveCommand<Unit, Unit> FixOutgoingInCueForgeCommand { get; }
    public ReactiveCommand<Unit, Unit> FixIncomingInCueForgeCommand { get; }
    public ReactiveCommand<double, Unit> AddCueToOutgoingInCueForgeCommand { get; }
    public ReactiveCommand<double, Unit> AddCueToIncomingInCueForgeCommand { get; }
    public ReactiveCommand<double, Unit> PreviewSeekOutgoingCommand { get; }
    public ReactiveCommand<double, Unit> PreviewSeekIncomingCommand { get; }
    public ReactiveCommand<Unit, Unit> StopPreviewSeekCommand { get; }
    public ReactiveCommand<double, Unit> SelectSourceCueCommand { get; }
    public ReactiveCommand<double, Unit> SelectTargetCueCommand { get; }

    /// <summary>Waveform zoom — shared between both tracks (see <see cref="WaveformZoomLevel"/>)
    /// so a comparison stays apples-to-apples; each ×1.5/÷1.5 step is clamped by the property
    /// setter to WaveformControl's own [1,16] zoom range.</summary>
    public ReactiveCommand<Unit, Unit> ZoomInCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomOutCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveOutgoingBpmCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveIncomingBpmCommand { get; }

    private Guid _playlistId;

    public async Task LoadPairAsync(Guid playlistId, Guid outgoingPlaylistTrackId, Guid incomingPlaylistTrackId)
    {
        StatusMessage = "Loading tracks…";
        var tracks = await _libraryService.LoadPlaylistTracksAsync(playlistId);
        var outgoing = tracks.FirstOrDefault(t => t.Id == outgoingPlaylistTrackId);
        var incoming = tracks.FirstOrDefault(t => t.Id == incomingPlaylistTrackId);

        if (outgoing == null || incoming == null)
        {
            _logger.LogWarning("MixTransitionViewModel: could not resolve track pair {Outgoing}->{Incoming} in playlist {Playlist}",
                outgoingPlaylistTrackId, incomingPlaylistTrackId, playlistId);
            StatusMessage = "Couldn't load this track pair.";
            return;
        }

        await LoadPairAsync(playlistId, outgoing, incoming);
    }

    /// <summary>
    /// Loads a pair from track models the caller already holds (Flow Builder's rows). Unlike the
    /// id overload this also works when the two tracks belong to different playlists, e.g. a
    /// bridge track pulled in from another playlist that isn't saved into this one yet.
    /// </summary>
    public async Task LoadPairAsync(Guid playlistId, PlaylistTrack outgoing, PlaylistTrack incoming)
    {
        var generation = ++_loadGeneration;
        ClearMixPointUndo();
        // Leaving the previous pair keeps its cue edits; a running preview belongs to the old pair.
        if (IsPlaying) _previewPlayer.StopPreview();
        StopPreviewSeek();
        await FlushCueEditsAsync();
        if (generation != _loadGeneration) return;
        _playlistId = playlistId;
        StatusMessage = "Loading tracks…";

        OutgoingTrack = new PlaylistTrackViewModel(outgoing, _eventBus, _libraryService, _artworkCache);
        IncomingTrack = new PlaylistTrackViewModel(incoming, _eventBus, _libraryService, _artworkCache);
        this.RaisePropertyChanged(nameof(OutgoingTrack));
        this.RaisePropertyChanged(nameof(IncomingTrack));
        this.RaisePropertyChanged(nameof(HasLoadedPair));

        await Task.WhenAll(OutgoingTrack.LoadAnalysisDataAsync(), IncomingTrack.LoadAnalysisDataAsync());
        if (generation != _loadGeneration) return;
        this.RaisePropertyChanged(nameof(WaveformDataA));
        this.RaisePropertyChanged(nameof(WaveformDataB));
        this.RaisePropertyChanged(nameof(OutgoingDurationSeconds));
        this.RaisePropertyChanged(nameof(IncomingDurationSeconds));

        _pairScore = TrackPairCompatibilityScorer.Score(
            OutgoingTrack.CamelotDisplay, IncomingTrack.CamelotDisplay,
            OutgoingTrack.Energy, IncomingTrack.Energy);
        this.RaisePropertyChanged(nameof(PhaseConfidenceWidth));

        OutgoingBpm = outgoing.BPM ?? 0.0;
        IncomingBpm = incoming.BPM ?? 0.0;

        // Fetched once here (not inside SuggestTransitionPointsAsync) because the cue markers
        // need to render on both waveforms regardless of whether a saved transition already
        // exists — a saved pair still needs to show its cues so the user can pick a different one.
        List<CuePointEntity> sourceCues;
        List<CuePointEntity> targetCues;
        try
        {
            sourceCues = await _cuePointService.GetByTrackIdAsync(outgoing.TrackUniqueHash);
            targetCues = await _cuePointService.GetByTrackIdAsync(incoming.TrackUniqueHash);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load cue points for {Outgoing}->{Incoming}", outgoing.TrackUniqueHash, incoming.TrackUniqueHash);
            sourceCues = new List<CuePointEntity>();
            targetCues = new List<CuePointEntity>();
        }
        await LoadEditorAsync(OutgoingEditor, outgoing, OutgoingTrack, sourceCues);
        await LoadEditorAsync(IncomingEditor, incoming, IncomingTrack, targetCues);

        PlaylistTrackTransition? saved = null;
        try
        {
            saved = await _transitionRepository.GetTransitionAsync(outgoing.Id, incoming.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't read the saved transition for {Outgoing}->{Incoming}", outgoing.Id, incoming.Id);
        }
        if (generation != _loadGeneration) return;
        HasSavedTransition = saved != null;
        _customEchoDecayFactor = saved?.EchoDecayFactor;
        _customFilterStartFrequency = saved?.FilterStartFrequency;
        _customFilterEndFrequency = saved?.FilterEndFrequency;
        _customWaveDuckDepth = saved?.WaveDuckDepth;
        _customFilterSweepRising = saved?.FilterSweepRising;
        _customEqSwapLow = saved?.EqSwapLow;
        _customEqSwapMid = saved?.EqSwapMid;
        _customEqSwapHigh = saved?.EqSwapHigh;
        _customEqLowCrossoverHz = saved?.EqLowCrossoverHz;
        _customEqHighCrossoverHz = saved?.EqHighCrossoverHz;
        // LoopEnabled isn't its own saved field — Type=="DoubleDrop" IS the saved "loop was on"
        // signal, same as every other preset here.
        _customLoopEnabled = saved?.Type == SLSKDONET.Models.Timeline.TransitionType.DoubleDrop;
        _customLoopBars = saved?.LoopBars;
        _customLoopRepeats = saved?.LoopRepeats;
        this.RaisePropertyChanged(nameof(CustomWaveDuckDepth));
        this.RaisePropertyChanged(nameof(CustomFilterSweepRising));
        this.RaisePropertyChanged(nameof(CustomEqSwapLow));
        this.RaisePropertyChanged(nameof(CustomEqSwapMid));
        this.RaisePropertyChanged(nameof(CustomEqSwapHigh));
        this.RaisePropertyChanged(nameof(CustomEqLowCrossoverHz));
        this.RaisePropertyChanged(nameof(CustomEqHighCrossoverHz));
        this.RaisePropertyChanged(nameof(CustomLoopEnabled));
        this.RaisePropertyChanged(nameof(CustomLoopBars));
        this.RaisePropertyChanged(nameof(CustomLoopRepeats));
        _durationBars = saved?.DurationBars ?? 16;
        this.RaisePropertyChanged(nameof(DurationBars));
        _selectedPreset = saved?.PresetName ?? "Auto";
        this.RaisePropertyChanged(nameof(SelectedPreset));
        this.RaisePropertyChanged(nameof(IsCustomMode));
        _nudgeSeconds = 0;
        this.RaisePropertyChanged(nameof(NudgeSeconds));

        _outStructure = _inStructure = null;
        if (_serviceProvider.GetService(typeof(SLSKDONET.Services.Transitions.TransitionPlanService)) is SLSKDONET.Services.Transitions.TransitionPlanService planService)
        {
            try
            {
                _outStructure = await planService.LoadStructureAsync(outgoing.TrackUniqueHash);
                _inStructure = await planService.LoadStructureAsync(incoming.TrackUniqueHash);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No structure for {Outgoing}->{Incoming}; planned presets use the cue suggestion", outgoing.TrackUniqueHash, incoming.TrackUniqueHash);
            }
        }

        if (saved?.SourceTriggerSeconds is double savedSource && saved.TargetTriggerSeconds is double savedTarget)
        {
            SourceTriggerSeconds = savedSource;
            TargetTriggerSeconds = savedTarget;
            TransitionPointReasoning = "Using your saved mix-out/mix-in points.";
        }
        else if (!ApplyStructurePlan("Auto"))
        {
            await SuggestTransitionPointsAsync(outgoing.TrackUniqueHash, incoming.TrackUniqueHash, outgoing.Duration, sourceCues, targetCues);
        }

        if (generation != _loadGeneration) return;
        RebuildLiveModel();
        // The state as loaded is the clean baseline: the saved transition, or the Auto plan.
        _baselineSnapshot = BuildSnapshot();
        RefreshDirtyState();
        RefreshMissingCues();
        StatusMessage = $"{OutgoingTrack.Title} → {IncomingTrack.Title}";
    }

    /// <summary>
    /// Analysis-driven mix-out/mix-in point picker: pulls each track's cue points (Mix-Out/
    /// Mix-In/Intro/Drop, generated from phrase segments, sub-bass return timestamps, and energy
    /// curve analysis by CueGenerationService) plus BPM/key/vocal timing, and runs
    /// TransitionEngine.OptimizeTransition — the same tempo-jump / harmonic-clash / vocal-overlap
    /// aware logic already used to auto-adjust cues across a whole playlist, just newly wired to
    /// actually drive where a Mix transition starts instead of always "near the literal end of
    /// track A / the literal start of track B".
    /// </summary>
    private async Task SuggestTransitionPointsAsync(
        string outgoingHash, string incomingHash, double outgoingDurationSeconds,
        List<CuePointEntity> sourceCues, List<CuePointEntity> targetCues)
    {
        try
        {
            var sourceEntity = await _trackRepository.FindTrackAsync(outgoingHash);
            var targetEntity = await _trackRepository.FindTrackAsync(incomingHash);

            if (sourceEntity == null || targetEntity == null)
            {
                SourceTriggerSeconds = Math.Max(0, outgoingDurationSeconds - 30.0);
                TargetTriggerSeconds = 0.0;
                TransitionPointReasoning = "No analysis data for one of these tracks yet — using generic tail/head points.";
                return;
            }

            var suggestion = _pointSuggestionEngine.OptimizeTransition(sourceEntity, targetEntity, sourceCues, targetCues);

            SourceTriggerSeconds = Math.Max(0, suggestion.SourceTriggerTime);
            TargetTriggerSeconds = Math.Max(0, suggestion.TargetTriggerTime);
            TransitionPointReasoning = suggestion.Description;

            // Mark which cue (if any) the algorithm actually picked, so the waveform can
            // distinguish "this is what Auto recommended" from wherever the live trigger point
            // currently sits (which the playhead already shows, and which Nudge/a different cue
            // click can move independently afterward). Only meaningful here — a saved transition's
            // trigger points are the user's own prior choice, not an algorithm suggestion.
            if (suggestion.SelectedSourceCue != null)
            {
                var match = OutgoingEditor.Session.Cues.FirstOrDefault(c => Math.Abs(c.Timestamp - suggestion.SelectedSourceCue.TimestampInSeconds) < 0.01);
                if (match != null) match.IsSuggested = true;
            }
            if (suggestion.SelectedTargetCue != null)
            {
                var match = IncomingEditor.Session.Cues.FirstOrDefault(c => Math.Abs(c.Timestamp - suggestion.SelectedTargetCue.TimestampInSeconds) < 0.01);
                if (match != null) match.IsSuggested = true;
            }
            this.RaisePropertyChanged(nameof(OutgoingCues));
            this.RaisePropertyChanged(nameof(IncomingCues));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transition point suggestion failed for {Outgoing}->{Incoming}", outgoingHash, incomingHash);
            SourceTriggerSeconds = Math.Max(0, outgoingDurationSeconds - 30.0);
            TargetTriggerSeconds = 0.0;
            TransitionPointReasoning = "Couldn't compute a suggestion — using generic tail/head points.";
        }
    }

    private async Task<(bool Ok, string Message)> GenerateCuesForDeckAsync(string hash, string? filePath, SLSKDONET.Engine.Analysis.CueDetr.CueSourceMode mode)
    {
        if (_serviceProvider.GetService(typeof(SLSKDONET.Services.AudioAnalysis.CueDetrCueService)) is not SLSKDONET.Services.AudioAnalysis.CueDetrCueService service)
            return (false, "Cue generation isn't available");
        var r = await Task.Run(() => service.RegenerateAsync(hash, filePath, mode));
        if (!r.Success) return (false, $"✗ {r.Error}");
        return (true, mode switch
        {
            SLSKDONET.Engine.Analysis.CueDetr.CueSourceMode.Compare => $"✓ {r.CueCount} cues · {r.Agreed} confirmed by CUE-DETR (✓AI), its other points in cyan",
            SLSKDONET.Engine.Analysis.CueDetr.CueSourceMode.AiOnly => $"✓ {r.CueCount} CUE-DETR cues (cyan)",
            _ => $"✓ Generated {r.CueCount} cues",
        });
    }

    /// <summary>After a deck's cues were regenerated or saved: reload both structures and, when the
    /// preset is timed from structure, move the mix points to match (e.g. to a Drop you just set).</summary>
    private async Task ReplanFromCuesAsync()
    {
        string? outHash = OutgoingEditor.TrackHash, inHash = IncomingEditor.TrackHash;
        if (outHash == null || inHash == null) return;
        if (_serviceProvider.GetService(typeof(SLSKDONET.Services.Transitions.TransitionPlanService)) is not SLSKDONET.Services.Transitions.TransitionPlanService planService) return;
        try
        {
            var outStructure = await planService.LoadStructureAsync(outHash);
            var inStructure = await planService.LoadStructureAsync(inHash);
            // Another transition was opened meanwhile — don't apply this pair's plan to it.
            if (OutgoingEditor.TrackHash != outHash || IncomingEditor.TrackHash != inHash) return;
            _outStructure = outStructure;
            _inStructure = inStructure;
            if (TransitionPresetLibrary.IsStructurePlanned(SelectedPreset) && ApplyStructurePlan(SelectedPreset))
                TransitionPointReasoning = "Re-planned from the updated cues · " + TransitionPointReasoning;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Re-planning after a cue change failed");
        }
    }

    /// <summary>
    /// Times the transition from both tracks' structure (TransitionPlanner) — the same plan real
    /// playback uses for an unsaved pair, so what the editor shows is what will play. "Auto" lets
    /// the planner choose and then shows its choice as the selected preset. False when either
    /// track lacks analysis or the chosen type doesn't fit these tracks.
    /// </summary>
    private bool ApplyStructurePlan(string presetName)
    {
        if (_outStructure == null || _inStructure == null) return false;
        var kind = SLSKDONET.Engine.Transitions.TransitionPlanner.KindFromPresetName(presetName);
        var plan = kind is { } k
            ? SLSKDONET.Engine.Transitions.TransitionPlanner.PlanKind(_outStructure, _inStructure, k)
            : SLSKDONET.Engine.Transitions.TransitionPlanner.Plan(_outStructure, _inStructure, _pairScore?.CombinedScore ?? 50);
        if (plan == null)
        {
            TransitionPointReasoning = $"{presetName} doesn't fit these tracks (needs a drop with enough build-up before it).";
            return false;
        }

        _nudgeSeconds = 0;
        this.RaisePropertyChanged(nameof(NudgeSeconds));
        SourceTriggerSeconds = plan.SourceTriggerSeconds;
        TargetTriggerSeconds = plan.TargetTriggerSeconds;
        _durationBars = plan.DurationBars;
        this.RaisePropertyChanged(nameof(DurationBars));
        _selectedPreset = plan.PresetName;
        this.RaisePropertyChanged(nameof(SelectedPreset));
        this.RaisePropertyChanged(nameof(IsCustomMode));
        TransitionPointReasoning = (kind == null ? "Auto picked " : "") + plan.Reason
            + (plan.VocalClash && !plan.Reason.Contains("vocals") ? " · vocals overlap" : "");
        RebuildLiveModel();
        return true;
    }

    /// <summary>Loads a deck's cue editor, snapping on the track's analysed grid (BPM and downbeat,
    /// the same grid auto cues are placed on), falling back to the displayed BPM.</summary>
    private async Task LoadEditorAsync(TrackCueEditorViewModel editor, PlaylistTrack track, PlaylistTrackViewModel vm, List<CuePointEntity> cues)
    {
        double bpm = track.BPM ?? 0, downbeat = 0;
        string? genre = track.DetectedSubGenre;
        List<PhraseSegment>? phrases = null;
        try
        {
            if (await _libraryService.GetAudioFeaturesByHashAsync(track.TrackUniqueHash) is { Bpm: > 0 } f)
            {
                bpm = f.Bpm;
                downbeat = f.DownbeatOffsetSeconds;
                genre = !string.IsNullOrWhiteSpace(f.DetectedSubGenre) ? f.DetectedSubGenre : f.ElectronicSubgenre;
                phrases = ParsePhrases(f.PhraseSegmentsJson, track.TrackUniqueHash);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No analysis grid for {Hash}; snapping on displayed BPM", track.TrackUniqueHash);
        }
        editor.Load(track.TrackUniqueHash, vm.Title, track.ResolvedFilePath, bpm, downbeat, track.Duration, cues, genre);
        // Phrase analysis (read-only) feeds the deck's Intro end / Outro suggestions.
        editor.Session.SetStructureHints(phrases);
    }

    private List<PhraseSegment>? ParsePhrases(string? json, string hash)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<PhraseSegment>>(json); }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogDebug(ex, "Unreadable phrase data for {Hash}; no section suggestions", hash);
            return null;
        }
    }

    private void RebuildLiveModel()
    {
        LiveModel = TransitionPresetLibrary.Build(SelectedPreset, _pairScore, DurationBars);
        if (IsCustomMode)
        {
            TransitionPresetLibrary.ApplyCustomOverrides(
                LiveModel, CustomEchoDecayFactor, CustomFilterStartFrequency, CustomFilterEndFrequency,
                CustomWaveDuckDepth, CustomFilterSweepRising,
                CustomEqSwapLow, CustomEqSwapMid, CustomEqSwapHigh, CustomEqLowCrossoverHz, CustomEqHighCrossoverHz,
                CustomLoopEnabled, CustomLoopBars, CustomLoopRepeats);
        }
        this.RaisePropertyChanged(nameof(LiveModel));
        RebuildAutomationCurves();
        RaiseRegionChanged();
        RefreshDirtyState();
    }

    /// <summary>Samples TransitionEngine's automation math across the transition window into two
    /// 0-1 gain curves for the waveform overlay — the same formulas AudioPlayerService.AdvanceCrossfade
    /// uses during real playback, so the editor's preview visually matches what will actually play.</summary>
    private void RebuildAutomationCurves()
    {
        const int samplePoints = 64;
        var engine = new TransitionEngine();
        var region = new TransitionRegion
        {
            OutgoingTrackId = "A",
            IncomingTrackId = "B",
            StartSample = 0,
            EndSample = samplePoints,
            Type = LiveModel.Type.ToAutomationType(),
            Curve = SLSKDONET.Services.Audio.TransitionCurve.SCurve,
            WaveDuckDepth = LiveModel.WaveDuckDepth,
            EchoDecayFactor = LiveModel.EchoDecayFactor,
            EqConfig = new EqBandSwapConfig
            {
                SwapLow = LiveModel.EqSwapLow,
                SwapMid = LiveModel.EqSwapMid,
                SwapHigh = LiveModel.EqSwapHigh,
                LowCrossover = LiveModel.EqLowCrossoverHz,
                HighCrossover = LiveModel.EqHighCrossoverHz,
                HardLowSwap = LiveModel.EqHardLowSwap,
                HardLowSwapAtEnd = LiveModel.EqHardLowSwapAtEnd,
            },
        };
        engine.AddTransition(region);

        var outgoingCurve = new float[samplePoints];
        var incomingCurve = new float[samplePoints];
        for (int i = 0; i < samplePoints; i++)
        {
            var automation = engine.CalculateAutomation(region, i);
            outgoingCurve[i] = automation.OutgoingGain;
            incomingCurve[i] = automation.IncomingGain;
        }

        OutgoingAutomationCurve = outgoingCurve;
        IncomingAutomationCurve = incomingCurve;
    }

    private async Task PlayPreviewAsync()
    {
        if (OutgoingTrack?.Model == null || IncomingTrack?.Model == null) return;
        if (string.IsNullOrEmpty(OutgoingTrack.Model.ResolvedFilePath) || string.IsNullOrEmpty(IncomingTrack.Model.ResolvedFilePath))
        {
            StatusMessage = "One of these tracks isn't downloaded yet — can't preview.";
            return;
        }

        var projectBpm = ProjectBpm;

        // Only one audio source at a time — a waveform click-preview must not keep playing
        // underneath the real crossfade preview.
        StopPreviewSeek();

        IsPlaying = true;
        StatusMessage = $"Rendering {SelectedPreset} preview…";
        var leadIn = Math.Min(PreviewLeadInSeconds, Math.Max(0, EffectiveSourceTriggerSeconds));
        _activePreviewLeadInSeconds = leadIn;
        _activePreviewSourceSeconds = EffectiveSourceTriggerSeconds;
        _activePreviewTargetSeconds = TargetTriggerSeconds;
        _activePreviewWindowSeconds = TransitionWindowSeconds;
        _activePreviewIncomingRate = IncomingPlaybackRate;
        try
        {
            await _previewPlayer.StartTransitionPreviewAsync(
                OutgoingTrack.Title, OutgoingTrack.Model.ResolvedFilePath, EffectiveSourceTriggerSeconds,
                IncomingTrack.Title, IncomingTrack.Model.ResolvedFilePath, TargetTriggerSeconds,
                LiveModel, projectBpm, default, leadIn, PreviewLeadInSeconds, IncomingPlaybackRate);
            StatusMessage = $"Previewing {SelectedPreset} from {PreviewLeadInBars} bars before the mix-out";
            StartPreviewPositionPoll();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transition preview failed");
            StatusMessage = "Preview failed — see log.";
            EndPreviewPlayback();
        }
    }

    // ── Preview transport (lead-in, playhead, scrub) ─────────────────────────────────────
    private IDisposable? _previewPositionPoll;
    private double? _previewPositionSeconds;
    private double _activePreviewLeadInSeconds, _activePreviewSourceSeconds, _activePreviewTargetSeconds, _activePreviewWindowSeconds;
    private double _activePreviewIncomingRate = 1.0;

    public static IReadOnlyList<int> LeadInBarOptions { get; } = new[] { 4, 8, 16 };

    private int _previewLeadInBars = 8;
    /// <summary>How many bars of the outgoing track play before the mix-out point (and of the
    /// incoming track after the blend) in Preview.</summary>
    public int PreviewLeadInBars
    {
        get => _previewLeadInBars;
        set { this.RaiseAndSetIfChanged(ref _previewLeadInBars, Math.Clamp(value, 1, 32)); this.RaisePropertyChanged(nameof(PreviewLeadInSeconds)); RefreshOverlay(); }
    }

    public double PreviewLeadInSeconds => PreviewLeadInBars * 4 * BeatSeconds(ProjectBpm);

    /// <summary>Current position in the rendered preview (seconds); writing it scrubs.</summary>
    public double PreviewPositionSeconds
    {
        get => _previewPositionSeconds ?? 0;
        set
        {
            if (!IsPlaying || _previewPositionSeconds is not double current || Math.Abs(current - value) < 0.2) return;
            _previewPlayer.SeekPreview(TimeSpan.FromSeconds(value));
            _previewPositionSeconds = value;
            RaisePreviewPositionChanged();
        }
    }

    private double _previewLengthSeconds;
    public double PreviewLengthSeconds { get => _previewLengthSeconds; private set => this.RaiseAndSetIfChanged(ref _previewLengthSeconds, value); }

    public string PreviewTimeDisplay => IsPlaying && _previewPositionSeconds.HasValue
        ? $"{FormatTimestamp(PreviewPositionSeconds)} / {FormatTimestamp(PreviewLengthSeconds)}"
        : "0:00 / 0:00";

    /// <summary>Where the preview playhead is in the outgoing track, or null when A isn't sounding.</summary>
    public double? PreviewPlayheadOutgoingSeconds => _previewPositionSeconds is double p
        ? MapPreviewToOutgoing(p, _activePreviewLeadInSeconds, _activePreviewSourceSeconds, _activePreviewWindowSeconds)
        : null;

    /// <summary>Where the preview playhead is in the incoming track, or null when B isn't sounding yet.</summary>
    public double? PreviewPlayheadIncomingSeconds => _previewPositionSeconds is double p
        ? MapPreviewToIncoming(p, _activePreviewLeadInSeconds, _activePreviewTargetSeconds, _activePreviewIncomingRate)
        : null;

    /// <summary>Preview timeline: [lead-in: A alone][window: A+B][tail: B alone].</summary>
    public static double? MapPreviewToOutgoing(double previewSeconds, double leadIn, double sourceTrigger, double window) =>
        previewSeconds <= leadIn + window ? sourceTrigger - leadIn + previewSeconds : null;

    public static double? MapPreviewToIncoming(double previewSeconds, double leadIn, double targetTrigger, double incomingPlaybackRate = 1.0) =>
        previewSeconds >= leadIn ? targetTrigger + (previewSeconds - leadIn) * incomingPlaybackRate : null;

    private void StartPreviewPositionPoll()
    {
        _previewPositionPoll?.Dispose();
        _previewPositionPoll = System.Reactive.Linq.Observable.Interval(TimeSpan.FromMilliseconds(60))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (!IsPlaying) return;
                _previewPositionSeconds = _previewPlayer.PreviewPosition?.TotalSeconds;
                PreviewLengthSeconds = _previewPlayer.PreviewLength?.TotalSeconds ?? 0;
                RaisePreviewPositionChanged();
            });
    }

    private void RaisePreviewPositionChanged()
    {
        this.RaisePropertyChanged(nameof(PreviewPositionSeconds));
        this.RaisePropertyChanged(nameof(PreviewTimeDisplay));
        this.RaisePropertyChanged(nameof(ProgressA));
        this.RaisePropertyChanged(nameof(ProgressB));
        this.RaisePropertyChanged(nameof(OverlayPlayheadSeconds));
    }

    // ── Transition window (overlap region drawn on both waveforms) ──────────────────────
    /// <summary>The BPM the transition is timed in (the outgoing track's), 128 when unknown.</summary>
    public double ProjectBpm => OutgoingBpm > 0 ? OutgoingBpm : 128.0;

    public double TransitionWindowSeconds => LiveModel.DurationBeats * BeatSeconds(ProjectBpm);
    public double OutgoingRegionEndSeconds => EffectiveSourceTriggerSeconds + TransitionWindowSeconds;
    public double IncomingRegionEndSeconds => TargetTriggerSeconds + TransitionWindowSeconds * IncomingPlaybackRate;
    public double OutgoingDurationSeconds => WaveformDataA?.DurationSeconds is > 0 and var d ? d : OutgoingTrack?.Model?.Duration ?? 0;
    public double IncomingDurationSeconds => WaveformDataB?.DurationSeconds is > 0 and var d ? d : IncomingTrack?.Model?.Duration ?? 0;
    public string TransitionWindowDisplay => NumFormat.Seconds(TransitionWindowSeconds);

    private void RaiseRegionChanged()
    {
        this.RaisePropertyChanged(nameof(TransitionWindowSeconds));
        this.RaisePropertyChanged(nameof(OutgoingRegionEndSeconds));
        this.RaisePropertyChanged(nameof(IncomingRegionEndSeconds));
        this.RaisePropertyChanged(nameof(TransitionWindowDisplay));
        RefreshOverlay();
    }

    public static double BeatSeconds(double bpm) => bpm > 0 ? 60.0 / bpm : 0.25;

    // ── Tempo ─────────────────────────────────────────────────────────────────────────────
    /// <summary>Incoming tempo relative to outgoing, e.g. "+1.6%".</summary>
    public string TempoDeltaDisplay => OutgoingBpm > 0 && IncomingBpm > 0
        ? NumFormat.SignedPercent((IncomingBpm - OutgoingBpm) / OutgoingBpm * 100)
        : "-";

    /// <summary>The exact varispeed ratio shared by live playback, the overlay and offline preview.</summary>
    public double IncomingPlaybackRate => AudioPlayerService.TempoSyncRatio(OutgoingBpm, IncomingBpm);

    public string TempoSyncDisplay => OutgoingBpm <= 0 || IncomingBpm <= 0
        ? "Tempo sync unavailable · set both BPM values"
        : HasTempoWarning
            ? $"B at {NumFormat.F(IncomingPlaybackRate, 3)}× · outside the ±6% sync range"
            : $"B at {NumFormat.F(IncomingPlaybackRate, 3)}× · preview and MIX synchronized";

    /// <summary>Outside live playback's ±6% sync range: B intentionally remains at native tempo.</summary>
    public bool HasTempoWarning => OutgoingBpm > 0 && IncomingBpm > 0 &&
        Math.Abs(IncomingPlaybackRate - 1.0) < 0.000001 &&
        Math.Abs(IncomingBpm - OutgoingBpm) / OutgoingBpm > 0.06;

    private void RaiseTempoChanged()
    {
        this.RaisePropertyChanged(nameof(TempoDeltaDisplay));
        this.RaisePropertyChanged(nameof(HasTempoWarning));
        this.RaisePropertyChanged(nameof(IncomingPlaybackRate));
        this.RaisePropertyChanged(nameof(TempoSyncDisplay));
        this.RaisePropertyChanged(nameof(ProjectBpm));
        this.RaisePropertyChanged(nameof(PreviewLeadInSeconds));
        RaiseRegionChanged();
    }

    // ── Saved / dirty state ──────────────────────────────────────────────────────────────
    private int _loadGeneration;
    private string? _baselineSnapshot;

    private bool _hasSavedTransition;
    /// <summary>A transition is stored for this pair (playback uses it instead of the Auto plan).</summary>
    public bool HasSavedTransition
    {
        get => _hasSavedTransition;
        private set { this.RaiseAndSetIfChanged(ref _hasSavedTransition, value); RaiseSaveStateChanged(); }
    }

    private bool _isDirty;
    /// <summary>The editor differs from what was loaded (the saved transition, or the Auto plan).</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set { this.RaiseAndSetIfChanged(ref _isDirty, value); RaiseSaveStateChanged(); }
    }

    public bool IsSavedAndClean => HasSavedTransition && !IsDirty;
    public bool IsAutoPlan => !HasSavedTransition && !IsDirty;

    public string SaveStateText => IsDirty ? "Unsaved changes"
        : HasSavedTransition ? "Saved"
        : "Not saved · playback uses the Auto plan";

    private string? _saveBlockedReason;
    /// <summary>Set by the host when this pair can't be saved yet (e.g. a bridge track that isn't in
    /// the playlist). Null = saving allowed.</summary>
    public string? SaveBlockedReason
    {
        get => _saveBlockedReason;
        set { this.RaiseAndSetIfChanged(ref _saveBlockedReason, value); this.RaisePropertyChanged(nameof(IsSaveBlocked)); }
    }

    public bool IsSaveBlocked => !string.IsNullOrEmpty(SaveBlockedReason);

    /// <summary>Raised after a save or a forget succeeds, so hosts can refresh their saved markers.</summary>
    public event EventHandler? SavedStateChanged;

    private void RaiseSaveStateChanged()
    {
        this.RaisePropertyChanged(nameof(IsSavedAndClean));
        this.RaisePropertyChanged(nameof(IsAutoPlan));
        this.RaisePropertyChanged(nameof(SaveStateText));
    }

    internal string BuildSnapshot() => string.Join("|",
        SelectedPreset, DurationBars,
        EffectiveSourceTriggerSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
        TargetTriggerSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
        IsCustomMode ? string.Join(",", CustomEchoDecayFactor, CustomFilterStartFrequency, CustomFilterEndFrequency, CustomWaveDuckDepth,
            CustomFilterSweepRising, CustomEqSwapLow, CustomEqSwapMid, CustomEqSwapHigh, CustomEqLowCrossoverHz, CustomEqHighCrossoverHz,
            CustomLoopEnabled, CustomLoopBars, CustomLoopRepeats) : string.Empty);

    private void RefreshDirtyState()
    {
        if (_baselineSnapshot == null || !HasLoadedPair) { IsDirty = false; return; }
        IsDirty = !string.Equals(_baselineSnapshot, BuildSnapshot(), StringComparison.Ordinal);
    }

    // ── Missing cues / needs attention ───────────────────────────────────────────────────
    private string _missingCuesMessage = string.Empty;
    /// <summary>"No cues on X yet" when a deck has nothing to pick a mix point from.</summary>
    public string MissingCuesMessage { get => _missingCuesMessage; private set => this.RaiseAndSetIfChanged(ref _missingCuesMessage, value); }
    public bool OutgoingHasNoCues => HasLoadedPair && OutgoingEditor.Session.Cues.Count == 0;
    public bool IncomingHasNoCues => HasLoadedPair && IncomingEditor.Session.Cues.Count == 0;

    private string _attentionMessage = string.Empty;
    /// <summary>What is wrong with either deck's analysis/cues, or empty when both are usable.</summary>
    public string AttentionMessage
    {
        get => _attentionMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _attentionMessage, value);
            this.RaisePropertyChanged(nameof(HasAttention));
        }
    }

    public bool HasAttention => !string.IsNullOrEmpty(AttentionMessage);
    public bool OutgoingNeedsAttention => HasLoadedPair && OutgoingTrack != null && (OutgoingTrack.NeedsAnalysis || OutgoingHasNoCues);
    public bool IncomingNeedsAttention => HasLoadedPair && IncomingTrack != null && (IncomingTrack.NeedsAnalysis || IncomingHasNoCues);

    private void RefreshMissingCues()
    {
        this.RaisePropertyChanged(nameof(OutgoingHasNoCues));
        this.RaisePropertyChanged(nameof(IncomingHasNoCues));
        var missing = new List<string>();
        if (OutgoingHasNoCues && OutgoingTrack != null) missing.Add(OutgoingTrack.Title);
        if (IncomingHasNoCues && IncomingTrack != null) missing.Add(IncomingTrack.Title);
        MissingCuesMessage = missing.Count == 0 ? string.Empty
            : $"No cues on {string.Join(" and ", missing)} yet. Generate cues to pick mix points on phrases.";

        RefreshAttention();
    }

    private void RefreshAttention()
    {
        this.RaisePropertyChanged(nameof(OutgoingNeedsAttention));
        this.RaisePropertyChanged(nameof(IncomingNeedsAttention));

        var parts = new List<string>();
        if (OutgoingTrack != null)
        {
            var issues = DescribeAttention(OutgoingTrack, OutgoingHasNoCues);
            if (issues != null) parts.Add($"Outgoing \"{OutgoingTrack.Title}\": {issues}");
        }
        if (IncomingTrack != null)
        {
            var issues = DescribeAttention(IncomingTrack, IncomingHasNoCues);
            if (issues != null) parts.Add($"Incoming \"{IncomingTrack.Title}\": {issues}");
        }

        AttentionMessage = parts.Count == 0 ? string.Empty : string.Join(" · ", parts) + ".";
    }

    private static string? DescribeAttention(PlaylistTrackViewModel track, bool noCues)
    {
        var issues = new List<string>();
        if (track.NeedsAnalysis) issues.Add("analysis missing or incomplete");
        if (noCues) issues.Add("no cues");
        return issues.Count == 0 ? null : string.Join(" and ", issues);
    }

    // ── Presets ──────────────────────────────────────────────────────────────────────────
    public string SelectedPresetDescription => DescribePreset(SelectedPreset);

    public static string DescribePreset(string preset) => preset switch
    {
        "Auto" => "Planned from both tracks' structure (phrases, drops, vocals).",
        "Rolling" => "Long hard bass swap over the phrase.",
        "Relaxed" => "Bass and mids hand over together, gently.",
        "Drop Sync" => "Lines B's drop up with the end of A's build.",
        "1-Bar Drop Swap" => "Holds the bass to the last bar, then swaps hard into B's drop.",
        "Fade" => "Plain equal-power crossfade.",
        "Rise" => "Filter sweep into the incoming track.",
        "Blend" => "EQ bass swap: lows hand over, mids and highs blend.",
        "Wave" => "Rhythmic gain dip on every downbeat.",
        "Melt" => "Echo out the outgoing track.",
        "Custom" => "Your own echo, filter, EQ-swap and loop settings.",
        _ => string.Empty,
    };

    /// <summary>Publishes the current pair-specific trigger as a reusable track-level memory cue.
    /// Explicit by design: another playlist using this track keeps its own saved transition timing.</summary>
    private async Task ShareMixPointAsync(bool isOutgoing)
    {
        if (!HasLoadedPair) return;
        var editor = isOutgoing ? OutgoingEditor : IncomingEditor;
        var role = isOutgoing ? CueRole.MixOut : CueRole.MixIn;
        double seconds = isOutgoing ? EffectiveSourceTriggerSeconds : TargetTriggerSeconds;
        var cue = editor.Session.SetMixMarker(role, seconds);
        if (cue == null || !await editor.Session.SaveAsync())
        {
            StatusMessage = $"Couldn't save the shared {(isOutgoing ? "Mix-Out" : "Mix-In")} cue.";
            return;
        }

        // Use the snapped cue position for this pair too, so the marker and audible transition agree.
        if (isOutgoing)
        {
            NudgeSeconds = 0;
            SourceTriggerSeconds = cue.Timestamp;
        }
        else
        {
            TargetTriggerSeconds = cue.Timestamp;
        }
        RefreshCueViews();
        StatusMessage = $"✓ {cue.Name} saved at {FormatTimestamp(cue.Timestamp)} · reusable in Workstation and Cue Forge";
    }

    private async Task SaveAsync()
    {
        if (OutgoingTrack == null || IncomingTrack == null) return;

        var transition = new PlaylistTrackTransition
        {
            PlaylistId = _playlistId,
            OutgoingPlaylistTrackId = OutgoingTrack.Id,
            IncomingPlaylistTrackId = IncomingTrack.Id,
            PresetName = SelectedPreset,
            Type = LiveModel.Type,
            DurationBars = DurationBars,
            EchoDecayFactor = IsCustomMode ? CustomEchoDecayFactor : null,
            FilterStartFrequency = IsCustomMode ? CustomFilterStartFrequency : null,
            FilterEndFrequency = IsCustomMode ? CustomFilterEndFrequency : null,
            WaveDuckDepth = IsCustomMode ? CustomWaveDuckDepth : null,
            FilterSweepRising = IsCustomMode ? CustomFilterSweepRising : null,
            EqSwapLow = IsCustomMode ? CustomEqSwapLow : null,
            EqSwapMid = IsCustomMode ? CustomEqSwapMid : null,
            EqSwapHigh = IsCustomMode ? CustomEqSwapHigh : null,
            EqLowCrossoverHz = IsCustomMode ? CustomEqLowCrossoverHz : null,
            EqHighCrossoverHz = IsCustomMode ? CustomEqHighCrossoverHz : null,
            LoopBars = IsCustomMode ? CustomLoopBars : null,
            LoopRepeats = IsCustomMode ? CustomLoopRepeats : null,
            SourceTriggerSeconds = EffectiveSourceTriggerSeconds,
            TargetTriggerSeconds = TargetTriggerSeconds,
        };

        try
        {
            await _transitionRepository.UpsertTransitionAsync(transition);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save transition {Outgoing}->{Incoming}", transition.OutgoingPlaylistTrackId, transition.IncomingPlaylistTrackId);
            StatusMessage = "Couldn't save the transition — see log.";
            return;
        }
        _baselineSnapshot = BuildSnapshot();
        HasSavedTransition = true;
        RefreshDirtyState();
        StatusMessage = "Transition saved.";
        SavedStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes this pair's saved transition, so playback goes back to the Auto plan, and
    /// re-plans the editor the same way.</summary>
    private async Task ForgetSavedTransitionAsync()
    {
        if (OutgoingTrack == null || IncomingTrack == null) return;
        try
        {
            await _transitionRepository.DeleteTransitionAsync(OutgoingTrack.Id, IncomingTrack.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete transition {Outgoing}->{Incoming}", OutgoingTrack.Id, IncomingTrack.Id);
            StatusMessage = "Couldn't remove the saved transition — see log.";
            return;
        }
        HasSavedTransition = false;
        SelectedPreset = "Auto";
        _baselineSnapshot = BuildSnapshot();
        RefreshDirtyState();
        StatusMessage = "Saved transition removed · playback uses the Auto plan.";
        SavedStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveBpmAsync(bool isOutgoing)
    {
        var track = isOutgoing ? OutgoingTrack : IncomingTrack;
        var bpm = isOutgoing ? OutgoingBpm : IncomingBpm;
        var hash = track?.Model?.TrackUniqueHash;
        if (string.IsNullOrWhiteSpace(hash) || bpm <= 0) return;

        try
        {
            await _trackRepository.UpdateBpmAsync(hash, bpm);
            track!.ApplyBpmUpdate(bpm);
            // Other live views of this same track (a Flow Builder card, the main track list) hold
            // their own separate PlaylistTrackViewModel instance for it — see LoadPairAsync, which
            // constructs OutgoingTrack/IncomingTrack fresh each time — so they need this event to
            // pick up the change rather than the direct ApplyBpmUpdate call above.
            _eventBus.Publish(new TrackMetadataUpdatedEvent(hash));
            StatusMessage = $"{(isOutgoing ? "Outgoing" : "Incoming")} track BPM updated.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save BPM for {Hash}", hash);
            StatusMessage = "Couldn't save BPM — see log.";
        }
    }

    private async Task OpenTrackInCueForgeAsync(PlaylistTrackViewModel? track, bool startFullAnalysis = false)
    {
        var hash = track?.Model?.TrackUniqueHash;
        if (string.IsNullOrWhiteSpace(hash)) return;

        // Cue Forge loads the stored cues, so save the decks' edits first (see CueSessionRegistry).
        await FlushCueEditsAsync();
        var cueForgeViewModel = _serviceProvider.GetRequiredService<CueForgeViewModel>();
        await cueForgeViewModel.LoadTrackAsync(hash, track!.Title, track.Artist);
        // Cue Forge opens on the playing track by default; this link deliberately targets another
        // side of the mix, so it keeps that track for this visit.
        cueForgeViewModel.KeepLoadedTrackForThisVisit();

        // Gives Cue Forge a "← Back to Mix Transition" link instead of a dead end — both
        // OutgoingTrack/IncomingTrack are already loaded regardless of which side's "Fix in Cue
        // Forge" button was clicked.
        if (OutgoingTrack != null && IncomingTrack != null)
        {
            cueForgeViewModel.SetMixTransitionOrigin(_playlistId, OutgoingTrack.Id, IncomingTrack.Id);
        }

        // The "Fix and analyse" path: Cue Forge now has the track loaded, so a full analysis can
        // be queued immediately — its own pending banner then tracks the run and reloads on completion.
        if (startFullAnalysis)
            await cueForgeViewModel.StartFullAnalysisAsync();

        _eventBus.Publish(new NavigateToPageEvent("CueForge"));
    }

    private void PreviewSeekTrack(PlaylistTrackViewModel? track, double seconds)
    {
        var path = track?.Model?.ResolvedFilePath;
        if (string.IsNullOrEmpty(path)) return;

        // Only one audio source at a time — the real crossfade preview must stop if the user
        // starts auditioning an arbitrary waveform point instead.
        if (IsPlaying) _previewPlayer.StopPreview();

        var deck = track == OutgoingTrack ? OutgoingEditor : IncomingEditor;
        if (IsCueEditMode) deck.Audition(seconds);
        else _libraryPreviewPlayer?.RequestPreview(path, track!.Model?.BPM, seconds);
        deck.LastAuditionSeconds = seconds;
        IsPreviewSeekPlaying = true;
        PreviewSeekStatusText = $"▶ Previewing {track.Title} @ {FormatTimestamp(seconds)}";
    }

    private void StopPreviewSeek()
    {
        _libraryPreviewPlayer?.StopPreview();
        IsPreviewSeekPlaying = false;
        PreviewSeekStatusText = string.Empty;
    }
}
