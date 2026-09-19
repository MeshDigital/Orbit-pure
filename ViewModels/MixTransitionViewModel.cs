using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
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

        _previewPlayer.PreviewStopped += OnPreviewStopped;
        if (_libraryPreviewPlayer != null) _libraryPreviewPlayer.PreviewStopped += OnLibraryPreviewStopped;

        SelectPresetCommand = ReactiveCommand.Create<string>(preset => SelectedPreset = preset);
        SelectBarsCommand = ReactiveCommand.Create<int>(bars => DurationBars = bars);
        NudgeBackCommand = ReactiveCommand.Create(() => { NudgeSeconds -= 0.25; });
        NudgeForwardCommand = ReactiveCommand.Create(() => { NudgeSeconds += 0.25; });
        PlayCommand = ReactiveCommand.CreateFromTask(PlayPreviewAsync, this.WhenAnyValue(x => x.IsPlaying, playing => !playing));
        PauseCommand = ReactiveCommand.Create(() => _previewPlayer.StopPreview());
        CancelCommand = ReactiveCommand.Create(() => { _previewPlayer.StopPreview(); Closed?.Invoke(this, EventArgs.Empty); });
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
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
        // Waveform right-click → "Add cue here": jumps to Cue Forge, adds a real cue at the
        // clicked time, and selects it — a purely ad-hoc waveform click can't set a *good*
        // transition point (it's not beat/phrase aligned), but it's exactly how a user finds
        // where a good cue point SHOULD go, so this is the bridge from "I found the spot" to
        // "now it's a real, manageable cue."
        AddCueToOutgoingInCueForgeCommand = ReactiveCommand.CreateFromTask<double>(seconds => OpenTrackInCueForgeAsync(OutgoingTrack, seconds));
        AddCueToIncomingInCueForgeCommand = ReactiveCommand.CreateFromTask<double>(seconds => OpenTrackInCueForgeAsync(IncomingTrack, seconds));
        SelectSourceCueCommand = ReactiveCommand.Create<double>(timestamp => SourceTriggerSeconds = timestamp);
        SelectTargetCueCommand = ReactiveCommand.Create<double>(timestamp => TargetTriggerSeconds = timestamp);
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
    }

    /// <summary>Raised when the user dismisses the editor (Cancel/Dismiss button).</summary>
    public event EventHandler? Closed;

    // TransitionPreviewPlayer.PreviewStopped fires from NAudio's internal WasapiOut playback
    // thread, not the UI thread — setting a bound ReactiveObject property directly from there
    // throws (Avalonia's dispatcher verifies the calling thread) and crashes the whole process
    // the moment a preview finishes playing on its own, not just on an explicit Pause click.
    private void OnPreviewStopped(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsPlaying = false);

    private void OnLibraryPreviewStopped(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsPreviewSeekPlaying = false;
            PreviewSeekStatusText = string.Empty;
        });

    public void Dispose()
    {
        _previewPlayer.PreviewStopped -= OnPreviewStopped;
        if (_libraryPreviewPlayer != null) _libraryPreviewPlayer.PreviewStopped -= OnLibraryPreviewStopped;
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
            var duration = OutgoingTrack?.Model?.Duration ?? 0.0;
            return duration > 0 ? (float)Math.Clamp(EffectiveSourceTriggerSeconds / duration, 0.0, 1.0) : 1f;
        }
    }

    public float ProgressB
    {
        get
        {
            var duration = IncomingTrack?.Model?.Duration ?? 0.0;
            return duration > 0 ? (float)Math.Clamp(TargetTriggerSeconds / duration, 0.0, 1.0) : 0f;
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
    public static IReadOnlyList<int> DurationBarOptions { get; } = new[] { 4, 8, 16 };

    private string _selectedPreset = "Auto";
    public string SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPreset, value);
            this.RaisePropertyChanged(nameof(IsCustomMode));
            RebuildLiveModel();
        }
    }

    public bool IsCustomMode => SelectedPreset == "Custom";

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

    // ── Cue-point picking (click a marker on either waveform to set it as the transition's
    // trigger point, overriding the suggestion engine) ─────────────────────────────────────
    private IEnumerable<OrbitCue> _outgoingCues = Array.Empty<OrbitCue>();
    public IEnumerable<OrbitCue> OutgoingCues { get => _outgoingCues; private set => this.RaiseAndSetIfChanged(ref _outgoingCues, value); }

    private IEnumerable<OrbitCue> _incomingCues = Array.Empty<OrbitCue>();
    public IEnumerable<OrbitCue> IncomingCues { get => _incomingCues; private set => this.RaiseAndSetIfChanged(ref _incomingCues, value); }

    // ── BPM editing ──────────────────────────────────────────────────────────────────────
    private double _outgoingBpm;
    public double OutgoingBpm { get => _outgoingBpm; set => this.RaiseAndSetIfChanged(ref _outgoingBpm, value); }

    private double _incomingBpm;
    public double IncomingBpm { get => _incomingBpm; set => this.RaiseAndSetIfChanged(ref _incomingBpm, value); }

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
        _playlistId = playlistId;
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

        OutgoingTrack = new PlaylistTrackViewModel(outgoing, _eventBus, _libraryService, _artworkCache);
        IncomingTrack = new PlaylistTrackViewModel(incoming, _eventBus, _libraryService, _artworkCache);
        this.RaisePropertyChanged(nameof(OutgoingTrack));
        this.RaisePropertyChanged(nameof(IncomingTrack));
        this.RaisePropertyChanged(nameof(HasLoadedPair));

        await Task.WhenAll(OutgoingTrack.LoadAnalysisDataAsync(), IncomingTrack.LoadAnalysisDataAsync());
        this.RaisePropertyChanged(nameof(WaveformDataA));
        this.RaisePropertyChanged(nameof(WaveformDataB));

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
        OutgoingCues = sourceCues.Select(OrbitCue.FromEntity).ToList();
        IncomingCues = targetCues.Select(OrbitCue.FromEntity).ToList();

        var saved = await _transitionRepository.GetTransitionAsync(outgoingPlaylistTrackId, incomingPlaylistTrackId);
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
        this.RaisePropertyChanged(nameof(CustomWaveDuckDepth));
        this.RaisePropertyChanged(nameof(CustomFilterSweepRising));
        this.RaisePropertyChanged(nameof(CustomEqSwapLow));
        this.RaisePropertyChanged(nameof(CustomEqSwapMid));
        this.RaisePropertyChanged(nameof(CustomEqSwapHigh));
        this.RaisePropertyChanged(nameof(CustomEqLowCrossoverHz));
        this.RaisePropertyChanged(nameof(CustomEqHighCrossoverHz));
        _durationBars = saved?.DurationBars ?? 16;
        this.RaisePropertyChanged(nameof(DurationBars));
        _selectedPreset = saved?.PresetName ?? "Auto";
        this.RaisePropertyChanged(nameof(SelectedPreset));
        this.RaisePropertyChanged(nameof(IsCustomMode));
        _nudgeSeconds = 0;
        this.RaisePropertyChanged(nameof(NudgeSeconds));

        if (saved?.SourceTriggerSeconds is double savedSource && saved.TargetTriggerSeconds is double savedTarget)
        {
            SourceTriggerSeconds = savedSource;
            TargetTriggerSeconds = savedTarget;
            TransitionPointReasoning = "Using your saved mix-out/mix-in points.";
        }
        else
        {
            await SuggestTransitionPointsAsync(outgoing.TrackUniqueHash, incoming.TrackUniqueHash, outgoing.Duration, sourceCues, targetCues);
        }

        RebuildLiveModel();
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
                var match = OutgoingCues.FirstOrDefault(c => Math.Abs(c.Timestamp - suggestion.SelectedSourceCue.TimestampInSeconds) < 0.01);
                if (match != null) match.IsSuggested = true;
            }
            if (suggestion.SelectedTargetCue != null)
            {
                var match = IncomingCues.FirstOrDefault(c => Math.Abs(c.Timestamp - suggestion.SelectedTargetCue.TimestampInSeconds) < 0.01);
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

    private void RebuildLiveModel()
    {
        LiveModel = TransitionPresetLibrary.Build(SelectedPreset, _pairScore, DurationBars);
        if (IsCustomMode)
        {
            TransitionPresetLibrary.ApplyCustomOverrides(
                LiveModel, CustomEchoDecayFactor, CustomFilterStartFrequency, CustomFilterEndFrequency,
                CustomWaveDuckDepth, CustomFilterSweepRising,
                CustomEqSwapLow, CustomEqSwapMid, CustomEqSwapHigh, CustomEqLowCrossoverHz, CustomEqHighCrossoverHz);
        }
        this.RaisePropertyChanged(nameof(LiveModel));
        RebuildAutomationCurves();
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

        var projectBpm = OutgoingTrack.Model.BPM is > 0 ? OutgoingTrack.Model.BPM.Value : 128.0;

        // Only one audio source at a time — a waveform click-preview must not keep playing
        // underneath the real crossfade preview.
        StopPreviewSeek();

        IsPlaying = true;
        StatusMessage = $"Previewing {SelectedPreset}…";
        try
        {
            await _previewPlayer.StartTransitionPreviewAsync(
                OutgoingTrack.Title, OutgoingTrack.Model.ResolvedFilePath, EffectiveSourceTriggerSeconds,
                IncomingTrack.Title, IncomingTrack.Model.ResolvedFilePath, TargetTriggerSeconds,
                LiveModel, projectBpm);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transition preview failed");
            StatusMessage = "Preview failed — see log.";
            IsPlaying = false;
        }
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
            SourceTriggerSeconds = EffectiveSourceTriggerSeconds,
            TargetTriggerSeconds = TargetTriggerSeconds,
        };

        await _transitionRepository.UpsertTransitionAsync(transition);
        StatusMessage = "Transition saved.";
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

    /// <param name="addCueAtSeconds">When set (the waveform's right-click "Add cue here"), a new
    /// cue is added at this exact time and selected before navigating, so it's ready to manage
    /// the moment Cue Forge opens.</param>
    private async Task OpenTrackInCueForgeAsync(PlaylistTrackViewModel? track, double? addCueAtSeconds = null)
    {
        var hash = track?.Model?.TrackUniqueHash;
        if (string.IsNullOrWhiteSpace(hash)) return;

        var cueForgeViewModel = _serviceProvider.GetRequiredService<CueForgeViewModel>();
        await cueForgeViewModel.LoadTrackAsync(hash, track!.Title, track.Artist);

        if (addCueAtSeconds.HasValue)
        {
            await cueForgeViewModel.AddCueAtTimeAsync(addCueAtSeconds.Value);
        }

        // Gives Cue Forge a "← Back to Mix Transition" link instead of a dead end — both
        // OutgoingTrack/IncomingTrack are already loaded regardless of which side's "Fix in Cue
        // Forge" button was clicked.
        if (OutgoingTrack != null && IncomingTrack != null)
        {
            cueForgeViewModel.SetMixTransitionOrigin(_playlistId, OutgoingTrack.Id, IncomingTrack.Id);
        }

        _eventBus.Publish(new NavigateToPageEvent("CueForge"));
    }

    private void PreviewSeekTrack(PlaylistTrackViewModel? track, double seconds)
    {
        var path = track?.Model?.ResolvedFilePath;
        if (string.IsNullOrEmpty(path)) return;

        // Only one audio source at a time — the real crossfade preview must stop if the user
        // starts auditioning an arbitrary waveform point instead.
        if (IsPlaying) _previewPlayer.StopPreview();

        _libraryPreviewPlayer?.RequestPreview(path, track!.Model?.BPM, seconds);
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
