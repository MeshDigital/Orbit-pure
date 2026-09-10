using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
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
    private static readonly SLSKDONET.Engine.Transitions.TransitionEngine _pointSuggestionEngine = new();

    public MixTransitionViewModel(
        ILogger<MixTransitionViewModel> logger,
        ILibraryService libraryService,
        IEventBus eventBus,
        ArtworkCacheService artworkCache,
        ITransitionRepository transitionRepository,
        ITransitionPreviewPlayer previewPlayer,
        SLSKDONET.Services.Repositories.ITrackRepository trackRepository,
        ICuePointService cuePointService)
    {
        _logger = logger;
        _libraryService = libraryService;
        _eventBus = eventBus;
        _artworkCache = artworkCache;
        _transitionRepository = transitionRepository;
        _previewPlayer = previewPlayer;
        _trackRepository = trackRepository;
        _cuePointService = cuePointService;

        _previewPlayer.PreviewStopped += OnPreviewStopped;

        SelectPresetCommand = ReactiveCommand.Create<string>(preset => SelectedPreset = preset);
        SelectBarsCommand = ReactiveCommand.Create<int>(bars => DurationBars = bars);
        NudgeBackCommand = ReactiveCommand.Create(() => { NudgeSeconds -= 0.25; });
        NudgeForwardCommand = ReactiveCommand.Create(() => { NudgeSeconds += 0.25; });
        PlayCommand = ReactiveCommand.CreateFromTask(PlayPreviewAsync, this.WhenAnyValue(x => x.IsPlaying, playing => !playing));
        PauseCommand = ReactiveCommand.Create(() => _previewPlayer.StopPreview());
        CancelCommand = ReactiveCommand.Create(() => { _previewPlayer.StopPreview(); Closed?.Invoke(this, EventArgs.Empty); });
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
    }

    /// <summary>Raised when the user dismisses the editor (Cancel/Dismiss button).</summary>
    public event EventHandler? Closed;

    // TransitionPreviewPlayer.PreviewStopped fires from NAudio's internal WasapiOut playback
    // thread, not the UI thread — setting a bound ReactiveObject property directly from there
    // throws (Avalonia's dispatcher verifies the calling thread) and crashes the whole process
    // the moment a preview finishes playing on its own, not just on an explicit Pause click.
    private void OnPreviewStopped(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsPlaying = false);

    public void Dispose()
    {
        _previewPlayer.PreviewStopped -= OnPreviewStopped;
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
            this.RaisePropertyChanged(nameof(EffectiveSourceTriggerSeconds));
            this.RaisePropertyChanged(nameof(SourceTriggerDisplay));
            this.RaisePropertyChanged(nameof(ProgressA));
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
            this.RaisePropertyChanged(nameof(EffectiveSourceTriggerSeconds));
            this.RaisePropertyChanged(nameof(SourceTriggerDisplay));
            this.RaisePropertyChanged(nameof(ProgressA));
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
            this.RaisePropertyChanged(nameof(TargetTriggerDisplay));
            this.RaisePropertyChanged(nameof(ProgressB));
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

        var saved = await _transitionRepository.GetTransitionAsync(outgoingPlaylistTrackId, incomingPlaylistTrackId);
        _customEchoDecayFactor = saved?.EchoDecayFactor;
        _customFilterStartFrequency = saved?.FilterStartFrequency;
        _customFilterEndFrequency = saved?.FilterEndFrequency;
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
            await SuggestTransitionPointsAsync(outgoing.TrackUniqueHash, incoming.TrackUniqueHash, outgoing.Duration);
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
    private async Task SuggestTransitionPointsAsync(string outgoingHash, string incomingHash, double outgoingDurationSeconds)
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

            var sourceCues = await _cuePointService.GetByTrackIdAsync(outgoingHash);
            var targetCues = await _cuePointService.GetByTrackIdAsync(incomingHash);

            var suggestion = _pointSuggestionEngine.OptimizeTransition(sourceEntity, targetEntity, sourceCues, targetCues);

            SourceTriggerSeconds = Math.Max(0, suggestion.SourceTriggerTime);
            TargetTriggerSeconds = Math.Max(0, suggestion.TargetTriggerTime);
            TransitionPointReasoning = suggestion.Description;
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
            TransitionPresetLibrary.ApplyCustomOverrides(LiveModel, CustomEchoDecayFactor, CustomFilterStartFrequency, CustomFilterEndFrequency);
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
            SourceTriggerSeconds = EffectiveSourceTriggerSeconds,
            TargetTriggerSeconds = TargetTriggerSeconds,
        };

        await _transitionRepository.UpsertTransitionAsync(transition);
        StatusMessage = "Transition saved.";
    }
}
