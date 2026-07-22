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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Audio.Separation;

namespace SLSKDONET.ViewModels.Workstation;

/// <summary>
/// The two workspaces inside the Workstation page. Enum member names kept as
/// Waveform/Flow (matching the primary surface of each workspace) so existing
/// bindings and persisted sessions stay valid; the UI labels them PREP and SET PLAN.
/// </summary>
public enum WorkstationMode
{
    /// <summary>PREP: decks, waveforms, hot cues, loops, and the stem rack.</summary>
    Waveform,
    /// <summary>SET PLAN: playlist sequence, transition scoring, energy arc.</summary>
    Flow,
}

public sealed class WorkstationToolOptionViewModel : ReactiveObject
{
    private bool _isSelected;

    public WorkstationToolOptionViewModel(string shortLabel, string displayName, string description, WorkstationMode? mode, bool isAvailable)
    {
        ShortLabel = shortLabel;
        DisplayName = displayName;
        Description = description;
        Mode = mode;
        IsAvailable = isAvailable;
    }

    public string ShortLabel { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public WorkstationMode? Mode { get; }
    public bool IsAvailable { get; }
    public string StatusText => IsAvailable ? Description : $"{DisplayName} is staged for a later cockpit slice.";

    public bool IsSelected
    {
        get => _isSelected;
        internal set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}

public sealed class PlaylistFlowTransitionViewModel
{
    public PlaylistFlowTransitionViewModel(
        string transitionKey,
        string transitionLabel,
        double canvasLeft,
        double canvasWidth,
        double harmonicCompatibilityScore,
        string harmonicCompatibilityLabel,
        double energyCompatibilityScore,
        string energyCompatibilityLabel,
        double combinedCompatibilityScore,
        int sequenceIndex)
    {
        TransitionKey = transitionKey;
        TransitionLabel = transitionLabel;
        CanvasLeft = canvasLeft;
        CanvasWidth = canvasWidth;
        HarmonicCompatibilityScore = harmonicCompatibilityScore;
        HarmonicCompatibilityLabel = harmonicCompatibilityLabel;
        EnergyCompatibilityScore = energyCompatibilityScore;
        EnergyCompatibilityLabel = energyCompatibilityLabel;
        CombinedCompatibilityScore = combinedCompatibilityScore;
        SequenceIndex = sequenceIndex;
        CompatibilityColor = combinedCompatibilityScore >= 80 ? "#66B8E986"
            : combinedCompatibilityScore >= 60 ? "#66FFD58A"
            : combinedCompatibilityScore >= 40 ? "#66FFA94B"
            : "#66FF6B6B";
    }

    public string TransitionKey { get; }
    public string TransitionLabel { get; }
    public double CanvasLeft { get; }
    public double CanvasWidth { get; }
    public double HarmonicCompatibilityScore { get; }
    public string HarmonicCompatibilityLabel { get; }
    public double EnergyCompatibilityScore { get; }
    public string EnergyCompatibilityLabel { get; }
    public double CombinedCompatibilityScore { get; }
    public int SequenceIndex { get; }
    public string CompatibilityColor { get; }
}

public sealed class FlowTransitionOverlayViewModel
{
    public FlowTransitionOverlayViewModel(
        string transitionKey,
        string transitionLabel,
        string compatibilityLabel,
        double startSeconds,
        double endSeconds,
        double canvasLeft,
        double canvasWidth,
        double phraseGuideSeconds,
        double beatGuideSeconds,
        double phraseGuideCanvasLeft,
        double beatGuideCanvasLeft,
        IReadOnlyList<double> phraseSnapCandidatesSeconds,
        bool isPhraseMarkerExplicit,
        double phraseMarkerConfidenceScore,
        string phraseMarkerConfidenceLabel,
        string phraseMarkerTooltip,
        bool phraseAligned,
        bool beatAligned,
        double lengthSeconds,
        bool isSelected,
        bool isLengthSnapped,
        string? appliedPresetId,
        IReadOnlyList<string> suggestedPresetIds,
        double harmonicCompatibilityScore,
        string harmonicCompatibilityLabel,
        double energyCompatibilityScore,
        string energyCompatibilityLabel,
        double combinedCompatibilityScore,
        IReadOnlyList<FlowPhraseRegion> phraseRegions,
        IReadOnlyList<string> warningFlags)
    {
        TransitionKey = transitionKey;
        TransitionLabel = transitionLabel;
        CompatibilityLabel = compatibilityLabel;
        StartSeconds = startSeconds;
        EndSeconds = endSeconds;
        CanvasLeft = canvasLeft;
        CanvasWidth = canvasWidth;
        PhraseGuideSeconds = phraseGuideSeconds;
        BeatGuideSeconds = beatGuideSeconds;
        PhraseGuideCanvasLeft = phraseGuideCanvasLeft;
        BeatGuideCanvasLeft = beatGuideCanvasLeft;
        PhraseSnapCandidatesSeconds = phraseSnapCandidatesSeconds ?? Array.Empty<double>();
        IsPhraseMarkerExplicit = isPhraseMarkerExplicit;
        PhraseMarkerConfidenceScore = phraseMarkerConfidenceScore;
        PhraseMarkerConfidenceLabel = phraseMarkerConfidenceLabel;
        PhraseMarkerTooltip = phraseMarkerTooltip;
        PhraseAligned = phraseAligned;
        BeatAligned = beatAligned;
        LengthSeconds = lengthSeconds;
        IsSelected = isSelected;
        IsLengthSnapped = isLengthSnapped;
        AppliedPresetId = appliedPresetId;
        SuggestedPresetIds = suggestedPresetIds ?? Array.Empty<string>();
        HarmonicCompatibilityScore = harmonicCompatibilityScore;
        HarmonicCompatibilityLabel = harmonicCompatibilityLabel;
        EnergyCompatibilityScore = energyCompatibilityScore;
        EnergyCompatibilityLabel = energyCompatibilityLabel;
        CombinedCompatibilityScore = combinedCompatibilityScore;
        PhraseRegions = phraseRegions ?? Array.Empty<FlowPhraseRegion>();
        ActivePhraseRegion = GetActivePhraseRegion(PhraseRegions);
        var totalSpanSeconds = PhraseRegions.Sum(r => Math.Max(0.0, r.EndSeconds - r.StartSeconds));
        var provenanceLabel = BuildPhraseRegionProvenanceLabel(PhraseRegions);
        PhraseRegionSpanLabel = PhraseRegions.Count == 0
            ? "Region: none"
            : $"{PhraseRegions.Count} region{(PhraseRegions.Count == 1 ? string.Empty : "s")} · {totalSpanSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s · {provenanceLabel}";
        PhraseRegionTooltip = PhraseRegions.Count == 0
            ? "Phrase regions are scaffolded and not yet edited."
            : $"{PhraseRegionSpanLabel} • {ActivePhraseRegion?.Tooltip ?? "scaffolded"}";
        WarningFlags = warningFlags ?? Array.Empty<string>();
    }

    private static FlowPhraseRegion? GetActivePhraseRegion(IReadOnlyList<FlowPhraseRegion> regions)
    {
        if (regions == null || regions.Count == 0)
        {
            return null;
        }

        return regions.FirstOrDefault(r => r.IsExplicit || r.Provenance == FlowPhraseRegionProvenance.ExplicitUser)
            ?? regions[0];
    }

    private static string BuildPhraseRegionProvenanceLabel(IReadOnlyList<FlowPhraseRegion> regions)
    {
        if (regions == null || regions.Count == 0)
        {
            return "None";
        }

        if (regions.All(r => r.Provenance == FlowPhraseRegionProvenance.ExplicitUser))
        {
            return "Manual";
        }

        if (regions.All(r => r.Provenance == FlowPhraseRegionProvenance.Inferred))
        {
            return "Suggested";
        }

        return "Hybrid";
    }

    public string TransitionKey { get; }
    public string TransitionLabel { get; }
    public string CompatibilityLabel { get; }
    public double StartSeconds { get; }
    public double EndSeconds { get; }
    public double CanvasLeft { get; }
    public double CanvasWidth { get; }
    public double PhraseGuideSeconds { get; }
    public double BeatGuideSeconds { get; }
    public double PhraseGuideCanvasLeft { get; }
    public double BeatGuideCanvasLeft { get; }
    public IReadOnlyList<double> PhraseSnapCandidatesSeconds { get; }
    public bool IsPhraseMarkerExplicit { get; }
    public double PhraseMarkerConfidenceScore { get; }
    public string PhraseMarkerConfidenceLabel { get; }
    public string PhraseMarkerTooltip { get; }
    public double PhraseMarkerOpacity => IsPhraseMarkerExplicit ? 1.0 : 0.58 + (PhraseMarkerConfidenceScore / 100.0) * 0.32;
    public bool PhraseAligned { get; }
    public bool BeatAligned { get; }
    public double LengthSeconds { get; }
    public bool IsSelected { get; }
    public bool IsLengthSnapped { get; }
    public string? AppliedPresetId { get; }
    public bool IsPresetApplied => AppliedPresetId != null;
    public IReadOnlyList<string> SuggestedPresetIds { get; }
    public double HarmonicCompatibilityScore { get; }
    public string HarmonicCompatibilityLabel { get; }
    public double EnergyCompatibilityScore { get; }
    public string EnergyCompatibilityLabel { get; }
    public double CombinedCompatibilityScore { get; }
    public IReadOnlyList<FlowPhraseRegion> PhraseRegions { get; }
    public FlowPhraseRegion? ActivePhraseRegion { get; }
    public string PhraseRegionSpanLabel { get; }
    public string PhraseRegionTooltip { get; }
    public IReadOnlyList<string> WarningFlags { get; }
}

public sealed class FlowPhraseRegion
{
    public FlowPhraseRegion(
        string transitionKey,
        double startSeconds,
        double endSeconds,
        bool isExplicit,
        double confidence,
        IReadOnlyList<string>? sourceCueIds,
        FlowPhraseRegionProvenance provenance)
    {
        TransitionKey = transitionKey ?? string.Empty;
        StartSeconds = Math.Max(0.0, startSeconds);
        EndSeconds = Math.Max(StartSeconds, endSeconds);
        IsExplicit = isExplicit;
        Confidence = Math.Clamp(confidence, 0.0, 1.0);
        SourceCueIds = sourceCueIds ?? Array.Empty<string>();
        Provenance = provenance;
        CanvasLeft = StartSeconds;
        CanvasWidth = Math.Max(0.0, EndSeconds - StartSeconds);
        SpanLabel = $"{StartSeconds:F1}s–{EndSeconds:F1}s";
        Tooltip = $"{SpanLabel} • {Provenance} • {Confidence:P0}";
    }

    public string TransitionKey { get; }
    public double StartSeconds { get; }
    public double EndSeconds { get; }
    public bool IsExplicit { get; }
    public double Confidence { get; }
    public IReadOnlyList<string> SourceCueIds { get; }
    public FlowPhraseRegionProvenance Provenance { get; }
    public double CanvasLeft { get; }
    public double CanvasWidth { get; }
    public string SpanLabel { get; }
    public string Tooltip { get; }
}


public enum FlowTransitionCurveType
{
    EqualPower,
    BassSwap,
    FullSpectrum,
    HardCut,
    Custom
}

public enum FlowFrequencyBandStrategy
{
    Hold,
    Swap,
    Blend,
    Cut,
    Custom
}

public enum FlowPhraseRegionProvenance
{
    ExplicitUser,
    Inferred,
    Mixed
}

public sealed record FlowTransitionPreset(
    string PresetId,
    string DisplayName,
    string Description,
    FlowTransitionCurveType CurveType,
    double DefaultLengthSeconds,
    FlowFrequencyBandStrategy LowBandStrategy,
    FlowFrequencyBandStrategy MidBandStrategy,
    FlowFrequencyBandStrategy HighBandStrategy,
    double MinCompatibilityScore,
    bool RequiresPhraseAlignment,
    double MinSuggestedEnergyDelta,
    double MaxSuggestedEnergyDelta);

public sealed record FlowCompatibilityScore(double Score, string Label);

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
    private readonly OrbSessionBundleService  _orbBundleService;
    private readonly IUndoService             _undoService;
    private readonly AnalyzeTrackStructureJob  _analyzeJob;
    private readonly IEventBus                _eventBus;
    private readonly AppConfig                _appConfig;
    private readonly ConfigManager            _configManager;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<WorkstationViewModel>? _logger;
    private readonly SLSKDONET.Services.Library.PlaylistExportService? _playlistExporter;
    private readonly BpmSyncService           _bpmSync = new();
    private readonly Dictionary<string, double> _flowTransitionLengthOverrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _flowTransitionPhraseMarkerOverrides = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flowTransitionPhraseMarkerExplicitKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _flowTransitionPresetOverrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FlowPhraseRegion> _flowPhraseRegionOverrides = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<FlowTransitionPreset> _flowPresetCatalog = BuildFlowTransitionPresetCatalog();
    private Guid? _pendingActivePlaylistId;

    public ObservableCollection<WorkstationToolOptionViewModel> ToolOptions { get; } = new();
    public WorkstationToolOptionViewModel? ActiveToolOption => ToolOptions.FirstOrDefault(option => option.IsSelected);
    public string ActiveToolSummary => BuildActiveToolSummary(ActiveToolOption?.DisplayName, ActiveToolOption?.StatusText);

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
            _appConfig.WorkstationActivePlaylistId = value?.Id.ToString();
            _ = _configManager.SaveAsync(_appConfig);
            RaiseLaneActionProperties();
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
    public string WorkstationEligibilitySummary => "Workstation view shows only downloaded + analyzed tracks that can load deck waveform and cues. Library keeps all tracks.";
    private string _analysisQueueSummary = "Analysis lane idle • queue prep jobs from the player or flow drawer";
    public string AnalysisQueueSummary
    {
        get => _analysisQueueSummary;
        private set => this.RaiseAndSetIfChanged(ref _analysisQueueSummary, value);
    }
    public string ToolbarHint => BuildToolbarHint(ActiveMode, IsSnapEnabled, IsQuantizeEnabled);
    public string FlowWindowSummary => BuildFlowWindowSummary(TimelineOffsetSeconds, TimelineWindowSeconds);
    public IReadOnlyList<FlowTransitionOverlayViewModel> FlowTransitions => BuildFlowTransitions(Decks, TimelineOffsetSeconds, TimelineWindowSeconds, SelectedFlowTransitionKey, _flowTransitionLengthOverrides, _flowTransitionPresetOverrides, _flowTransitionPhraseMarkerOverrides, _flowTransitionPhraseMarkerExplicitKeys, _flowPhraseRegionOverrides);
    public bool HasFlowTransitions => FlowTransitions.Count > 0;
    public bool IsFlowOverlayVisible => IsFlowMode && HasFlowTransitions;
    public string FlowOverlayHint => HasFlowTransitions
        ? "Passive transition overlays are visible in the timeline."
        : "Load at least two tracks to preview passive transition overlays.";
    public IReadOnlyList<PlaylistFlowTransitionViewModel> FlowPlaylistTransitions => BuildPlaylistFlowTransitions(PlaylistTracks, TimelineOffsetSeconds, TimelineWindowSeconds);
    public bool HasFlowPlaylistTransitions => FlowPlaylistTransitions.Count > 0;
    public bool IsFlowPlaylistOverlayVisible => IsFlowMode && HasFlowPlaylistTransitions;
    private string? _selectedFlowTransitionKey;
    public string? SelectedFlowTransitionKey
    {
        get => _selectedFlowTransitionKey;
        private set => this.RaiseAndSetIfChanged(ref _selectedFlowTransitionKey, value);
    }
    public FlowTransitionOverlayViewModel? SelectedFlowTransition => FlowTransitions.FirstOrDefault(t => t.TransitionKey == SelectedFlowTransitionKey);
    public bool HasSelectedFlowTransition => SelectedFlowTransition != null;
    public string FlowInspectorTransitionLabel => SelectedFlowTransition?.TransitionLabel ?? "Select a timeline transition to inspect Flow detail.";
    public string FlowInspectorCompatibilityLabel => SelectedFlowTransition?.CompatibilityLabel ?? "Compatibility appears here once a transition is selected.";
    public string FlowInspectorPhraseAlignment => SelectedFlowTransition == null
        ? "Phrase alignment: awaiting selection"
        : $"Phrase alignment: {(SelectedFlowTransition.PhraseAligned ? "aligned" : "review")}";
    public string FlowInspectorBeatAlignment => SelectedFlowTransition == null
        ? "Beat alignment: awaiting selection"
        : $"Beat alignment: {(SelectedFlowTransition.BeatAligned ? "aligned" : "review")}";
    public string FlowInspectorLengthLabel => SelectedFlowTransition == null
        ? "Transition length: awaiting selection"
        : $"Transition length: {SelectedFlowTransition.LengthSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s";
    public string FlowInspectorSnapLabel => SelectedFlowTransition == null
        ? "Snap: awaiting selection"
        : $"Snap: {(SelectedFlowTransition.IsLengthSnapped ? "phrase/beat lock" : "free")}";
    public string FlowInspectorPhraseMarkerLabel => SelectedFlowTransition == null
        ? "Marker: awaiting selection"
        : $"Marker: {SelectedFlowTransition.PhraseMarkerConfidenceLabel}";
    public int FlowInspectorPhraseRegionCount => SelectedFlowTransition?.PhraseRegions.Count ?? 0;
    public double FlowInspectorPhraseRegionSpanSeconds => SelectedFlowTransition?.PhraseRegions.Sum(r => Math.Max(0.0, r.EndSeconds - r.StartSeconds)) ?? 0.0;
    public string FlowInspectorPhraseRegionProvenanceLabel => SelectedFlowTransition == null
        ? "awaiting selection"
        : BuildFlowPhraseRegionProvenanceLabel(SelectedFlowTransition.PhraseRegions);
    public string FlowInspectorPhraseRegionSpanLabel => SelectedFlowTransition == null
        ? "Region: awaiting selection"
        : $"{FlowInspectorPhraseRegionCount} region{(FlowInspectorPhraseRegionCount == 1 ? string.Empty : "s")} · {FlowInspectorPhraseRegionSpanSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s · {FlowInspectorPhraseRegionProvenanceLabel}";
    public IReadOnlyList<FlowTransitionPreset> FlowTransitionPresetCatalog => _flowPresetCatalog;
    public string FlowInspectorPresetLabel => SelectedFlowTransition == null
        ? "Preset: awaiting selection"
        : $"Preset: {ResolvePresetName(SelectedFlowTransition.AppliedPresetId)}";
    public string FlowInspectorSuggestedPresetsLabel => SelectedFlowTransition == null
        ? "Suggestions: awaiting selection"
        : $"Suggestions: {BuildSuggestedPresetLabel(SelectedFlowTransition.SuggestedPresetIds)}";
    public string FlowInspectorHarmonicScoreLabel => SelectedFlowTransition == null
        ? "Harmonic score: awaiting selection"
        : $"Harmonic score: {SelectedFlowTransition.HarmonicCompatibilityScore.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} ({SelectedFlowTransition.HarmonicCompatibilityLabel})";
    public string FlowInspectorEnergyScoreLabel => SelectedFlowTransition == null
        ? "Energy score: awaiting selection"
        : $"Energy score: {SelectedFlowTransition.EnergyCompatibilityScore.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} ({SelectedFlowTransition.EnergyCompatibilityLabel})";
    public string FlowInspectorCombinedScoreLabel => SelectedFlowTransition == null
        ? "Combined score: awaiting selection"
        : $"Combined score: {SelectedFlowTransition.CombinedCompatibilityScore.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}";
    public string FlowInspectorWarningLabel => SelectedFlowTransition == null
        ? "Warnings: awaiting selection"
        : BuildWarningLabel(SelectedFlowTransition.WarningFlags);
    public string? FlowInspectorTopSuggestedPresetId => SelectedFlowTransition?.SuggestedPresetIds.Count > 0
        ? SelectedFlowTransition.SuggestedPresetIds[0]
        : null;
    public string FlowInspectorCurveLabel => SelectedFlowTransition == null
        ? "Curve: awaiting selection"
        : ResolveCurveLabel(SelectedFlowTransition.AppliedPresetId);
    public string FlowInspectorBandStrategyLabel => SelectedFlowTransition == null
        ? "Bands: awaiting selection"
        : ResolveBandStrategyLabel(SelectedFlowTransition.AppliedPresetId);
    public bool FlowInspectorHasWarnings => SelectedFlowTransition?.WarningFlags.Count > 0;
    public string FlowInspectorScoreRow => SelectedFlowTransition == null
        ? "Harmonic — · Energy —"
        : $"Harmonic {SelectedFlowTransition.HarmonicCompatibilityScore.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} ({SelectedFlowTransition.HarmonicCompatibilityLabel}) · Energy {SelectedFlowTransition.EnergyCompatibilityScore.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} ({SelectedFlowTransition.EnergyCompatibilityLabel})";
    public string FlowInspectorAlignmentRow => SelectedFlowTransition == null
        ? "Phrase — · Beat — · Snap — · Marker — · Region —"
        : $"Phrase: {(SelectedFlowTransition.PhraseAligned ? "aligned" : "review")} · Beat: {(SelectedFlowTransition.BeatAligned ? "aligned" : "review")} · {SelectedFlowTransition.LengthSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s · Snap: {(SelectedFlowTransition.IsLengthSnapped ? "locked" : "free")} · {FlowInspectorPhraseMarkerLabel} · {FlowInspectorPhraseRegionSpanLabel}";
    public string FlowInspectorCurveDetailRow => SelectedFlowTransition == null
        ? "Curve — · Bands —"
        : $"{ResolveCurveLabel(SelectedFlowTransition.AppliedPresetId)} · {ResolveBandStrategyLabel(SelectedFlowTransition.AppliedPresetId)}";
    public string GlobalTransportSummary => BuildGlobalTransportSummary(IsPlaying, Decks.Count(d => d.IsLoaded));
    public string TransportStatusSummary => BuildTransportStatusSummary(IsPlaying, Decks.Count(d => d.IsLoaded), FocusedDeck?.DeckLabel, FocusedDeck?.Deck.IsLoopActive == true);
    public string FocusedDeckActionSummary => BuildFocusedDeckActionSummary(FocusedDeck?.DeckLabel, FocusedDeck?.IsLoaded == true, FocusedDeck?.CueEditor.Cues.Any() == true, FocusedDeck?.StemsVisible == true);
    public string MixCoachSummary => BuildMixCoachSummary(FocusedDeck?.DeckLabel, FocusedDeck?.HarmonicSuggestionText, FocusedDeck?.TransitionStatusText);
    public bool HasActivePlaylist => ActivePlaylist != null;
    public bool HasReadyPlaylistTracks => PlaylistTracks.Count > 0;
    public bool HasLoadedDecks => Decks.Any(d => d.IsLoaded);
    public bool ShouldShowSelectPlaylistCta => !HasActivePlaylist;
    public bool ShouldShowAcquireCtas => HasActivePlaylist && !HasReadyPlaylistTracks;
    public bool ShouldShowDownloadTracksCta => HasActivePlaylist && !HasReadyPlaylistTracks;
    public bool ShouldShowImportLocalFilesCta => !HasReadyPlaylistTracks;
    public bool ShouldShowReadyTrackCtas => HasReadyPlaylistTracks;
    public string FlowCtaStateSummary => HasReadyPlaylistTracks
        ? "Ready tracks detected. Analyze structure and load into workstation decks."
        : HasActivePlaylist
            ? "No workstation-ready tracks yet. Acquire missing tracks or import local files."
            : "Select a playlist to begin flow prep, then acquire or import tracks.";
    public string TimelineEmptyCanvasSummary => HasReadyPlaylistTracks
        ? "Timeline canvas is ready. Load the first ready track from the drawer to place the opening block."
        : HasActivePlaylist
            ? "Timeline canvas is ready. Acquire or import tracks from the drawer, then load the first track into the mix."
            : "Timeline canvas is ready. Select a playlist in the drawer to begin arranging the mix.";
    public string FlowLaneReadinessSummary => HasReadyPlaylistTracks
        ? $"{PlaylistTracks.Count} ready track{(PlaylistTracks.Count == 1 ? string.Empty : "s")} • {AnalysisQueueSummary}"
        : FlowCtaStateSummary;
    public string SelectPlaylistCtaHint => "Open and focus the playlist selector in the flow drawer.";
    public string DownloadTracksCtaHint => HasActivePlaylist
        ? "Open Downloads to fetch missing tracks for the active playlist."
        : "Select a playlist first, then download its missing tracks.";
    public string ImportLocalFilesCtaHint => "Open Import to add local files and make them available for workstation flow prep.";
    public string LoadIntoWorkstationCtaHint => HasReadyPlaylistTracks
        ? "Open track list overlay for focused loading into workstation decks."
        : "Load into Workstation is available once ready tracks are present.";
    public bool CanAnalyzePlaylistFromLane => HasReadyPlaylistTracks && !IsAnalyzing;
    public bool CanOpenTrackOverlayFromLane => HasReadyPlaylistTracks;
    public bool CanUseFocusedDeckLaneActions => FocusedDeck?.IsLoaded == true;
    public bool CanUseExportLaneActions => ExportPanel.Decks.Count > 0 && !ExportPanel.IsExporting;
    public string FlowLaneAnalyzeHint => IsAnalyzing
        ? "Analysis is currently running."
        : HasReadyPlaylistTracks
            ? "Analyze cue and structure prep for ready tracks in this playlist."
            : "No workstation-ready tracks available. Download/analyze tracks first.";
    public string FlowLaneOverlayHint => HasReadyPlaylistTracks
        ? "Open expanded track overlay for focused browsing and loading."
        : "Track overlay is unavailable until the active playlist has ready tracks.";
    public string StemsLaneActionHint => CanUseFocusedDeckLaneActions
        ? "Run quick stem actions on the focused loaded deck."
        : "Focus and load a deck to enable stems lane actions.";
    public string ExportLaneActionHint => ExportPanel.IsExporting
        ? "Export is currently running."
        : ExportPanel.Decks.Count > 0
            ? "Quick export format presets and one-click export."
            : "Load at least one deck to enable export lane actions.";
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
        set
        {
            this.RaiseAndSetIfChanged(ref _isMetronomeEnabled, value);
            RaiseHeaderProperties();
        }
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
            SynchronizeToolSelection();
            RaiseHeaderProperties();
        }
    }

    public bool IsWaveformMode => ActiveMode == WorkstationMode.Waveform;
    public bool IsFlowMode     => ActiveMode == WorkstationMode.Flow;

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
    /// <summary>Saves the current session + playlist to a .orbsession bundle the user picks.</summary>
    public ReactiveCommand<Unit, Unit>          ExportOrbSessionCommand { get; }

    /// <summary>PREP finish line: exports the active playlist — with every saved hot cue and
    /// loop — as a Rekordbox XML file, ready to import into the user's DJ software.</summary>
    public ReactiveCommand<Unit, Unit>          ExportRekordboxCommand { get; }
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
    public ReactiveCommand<FlowTransitionOverlayViewModel, Unit> SelectFlowTransitionCommand { get; }
    public ReactiveCommand<Unit, Unit>          ClearFlowTransitionSelectionCommand { get; }
    public ReactiveCommand<string, Unit>        ApplyFlowPresetCommand  { get; }
    public ReactiveCommand<Unit, Unit>          ResetFlowPresetCommand  { get; }
    public ReactiveCommand<Unit, Unit>          CycleFlowPresetCommand  { get; }

    // ── Cue auto-analysis ─────────────────────────────────────────────────────

    private bool _isAnalyzing;
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isAnalyzing, value);
            RaiseLaneActionProperties();
        }
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

    private string _hiddenEligibilityBreakdown = string.Empty;
    public string HiddenEligibilityBreakdown
    {
        get => _hiddenEligibilityBreakdown;
        private set => this.RaiseAndSetIfChanged(ref _hiddenEligibilityBreakdown, value);
    }

    private List<PlaylistTrack> _lastHiddenTracks = new();
    public int IncompleteAnalysisTrackCount => _lastHiddenTracks.Count(IsReanalysisCandidate);
    public bool HasIncompleteAnalysisTracks => IncompleteAnalysisTrackCount > 0;
    public string IncompleteAnalysisSummary => IncompleteAnalysisTrackCount == 0
        ? "No incomplete analysis tracks detected in this playlist."
        : $"{IncompleteAnalysisTrackCount} track(s) are incomplete and can be queued for reanalysis.";

    /// <summary>Analyze all tracks in the active playlist that have no cues yet.</summary>
    public ReactiveCommand<Unit, Unit> AnalyzePlaylistCuesCommand  { get; }

    /// <summary>Analyze only the passed tracks (DataGrid selection) that have no cues yet.</summary>
    public ReactiveCommand<IList<PlaylistTrack>, Unit> AnalyzeSelectedCuesCommand { get; }

    /// <summary>Queue all hidden tracks with incomplete analysis data for full reanalysis.</summary>
    public ReactiveCommand<Unit, Unit> ReanalyzeAllIncompleteCommand { get; }

    private CancellationTokenSource? _analysisCts;

    // ── Constructor ───────────────────────────────────────────────────────────

    public WorkstationViewModel(ILibraryService library, DeckViewModel deckPair,
        CachedStemSeparator stemSeparator, ICuePointService cueService,
        StemPreferenceService stemPrefService, MixdownService mixdown,
        WorkstationSessionService sessionService, OrbSessionBundleService orbBundleService,
        IUndoService undoService,
        AnalyzeTrackStructureJob analyzeJob, IEventBus eventBus,
        AppConfig appConfig, ConfigManager configManager,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<WorkstationViewModel>? logger = null,
        SLSKDONET.Services.Library.PlaylistExportService? playlistExporter = null)
    {
        _logger = logger;
        _playlistExporter = playlistExporter;
        _library           = library;
        _deckPair          = deckPair;
        _stemSeparator     = stemSeparator;
        _cueService        = cueService;
        _stemPrefService   = stemPrefService;
        _mixdown           = mixdown;
        _sessionService    = sessionService;
        _orbBundleService  = orbBundleService;
        _undoService       = undoService;
        _analyzeJob        = analyzeJob;
        _eventBus          = eventBus;
        _appConfig         = appConfig;
        _configManager     = configManager;
        _dbFactory         = dbFactory;

        ToolOptions.Add(new WorkstationToolOptionViewModel("Prep", "Prep", "Decks, waveforms, hot cues, loops, and the stem rack — get tracks performance-ready.", WorkstationMode.Waveform, isAvailable: true));
        ToolOptions.Add(new WorkstationToolOptionViewModel("Set Plan", "Set Planning", "Playlist order, transition compatibility scores, and energy arc — plan the set.", WorkstationMode.Flow, isAvailable: true));
        SynchronizeToolSelection();

        ExportPanel = new ExportDialogViewModel(mixdown);
        PlaylistTracks.CollectionChanged += (_, _) => RaiseLaneActionProperties();
        ExportPanel.Decks.CollectionChanged += (_, _) => RaiseLaneActionProperties();
        ExportPanel.WhenAnyValue(x => x.IsExporting)
            .Subscribe(_ => RaiseLaneActionProperties())
            .DisposeWith(_disposables);

        // Wrap existing DeckA / DeckB
        var deckA = new WorkstationDeckViewModel("A", deckPair.DeckA, stemSeparator, cueService, stemPrefService, _dbFactory);
        var deckB = new WorkstationDeckViewModel("B", deckPair.DeckB, stemSeparator, cueService, stemPrefService, _dbFactory);
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
        ExportOrbSessionCommand = ReactiveCommand.CreateFromTask(ExportOrbSessionAsync);
        ExportRekordboxCommand = ReactiveCommand.CreateFromTask(ExportRekordboxAsync,
            this.WhenAnyValue(x => x.ActivePlaylist).Select(playlist => playlist != null));

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

        ReanalyzeAllIncompleteCommand = ReactiveCommand.CreateFromTask(
            QueueIncompleteTracksForReanalysisAsync,
            canAnalyze);

        SetModeCommand = ReactiveCommand.Create<WorkstationMode>(mode =>
        {
            ActiveMode = mode;
            if (mode != WorkstationMode.Flow)
            {
                SelectedFlowTransitionKey = null;
            }
            _ = SaveSessionAsync();
        });

        SelectFlowTransitionCommand = ReactiveCommand.Create<FlowTransitionOverlayViewModel>(transition =>
        {
            if (transition == null)
            {
                return;
            }

            SelectedFlowTransitionKey = string.Equals(SelectedFlowTransitionKey, transition.TransitionKey, StringComparison.Ordinal)
                ? null
                : transition.TransitionKey;
            RaiseFlowSelectionProperties();
        });

        ClearFlowTransitionSelectionCommand = ReactiveCommand.Create(() =>
        {
            if (SelectedFlowTransitionKey == null)
            {
                return;
            }

            SelectedFlowTransitionKey = null;
            RaiseFlowSelectionProperties();
        });

        var canApplyPreset = this.WhenAnyValue(
            x => x.SelectedFlowTransitionKey,
            key => !string.IsNullOrWhiteSpace(key));

        ApplyFlowPresetCommand = ReactiveCommand.Create<string>(presetId =>
        {
            var key = SelectedFlowTransitionKey;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(presetId))
            {
                return;
            }

            var previousPresetId = _flowTransitionPresetOverrides.TryGetValue(key, out var prev) ? prev : null;
            var previousLength = _flowTransitionLengthOverrides.TryGetValue(key, out var prevLen) ? (double?)prevLen : null;
            var preset = _flowPresetCatalog.FirstOrDefault(p => p.PresetId == presetId);
            SetFlowTransitionPresetOverride(key, presetId);
            if (preset != null)
            {
                SetFlowTransitionLengthOverride(key, preset.DefaultLengthSeconds);
            }

            _undoService.Push(new FlowTransitionPresetOperation(this, key, previousPresetId, presetId, previousLength, preset?.DefaultLengthSeconds));
            RaiseFlowSelectionProperties();
        }, canApplyPreset);

        ResetFlowPresetCommand = ReactiveCommand.Create(() =>
        {
            var key = SelectedFlowTransitionKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (!_flowTransitionPresetOverrides.TryGetValue(key, out var previousPresetId) || previousPresetId == null)
            {
                return;
            }

            var previousLength = _flowTransitionLengthOverrides.TryGetValue(key, out var prevLen) ? (double?)prevLen : null;
            SetFlowTransitionPresetOverride(key, null);
            ClearFlowTransitionLengthOverride(key);
            _undoService.Push(new FlowTransitionPresetOperation(this, key, previousPresetId, null, previousLength, null));
            RaiseFlowSelectionProperties();
        }, canApplyPreset);

        CycleFlowPresetCommand = ReactiveCommand.Create(() =>
        {
            var key = SelectedFlowTransitionKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            var transition = SelectedFlowTransition;
            if (transition == null)
            {
                return;
            }

            var suggestions = transition.SuggestedPresetIds;
            if (suggestions.Count == 0)
            {
                return;
            }

            var currentPresetId = _flowTransitionPresetOverrides.TryGetValue(key, out var cur) ? cur : null;
            var currentIndex = currentPresetId != null ? suggestions.ToList().IndexOf(currentPresetId) : -1;
            var nextIndex = (currentIndex + 1) % suggestions.Count;
            var nextPresetId = suggestions[nextIndex];
            var previousLength = _flowTransitionLengthOverrides.TryGetValue(key, out var prevLen) ? (double?)prevLen : null;
            var nextPreset = _flowPresetCatalog.FirstOrDefault(p => p.PresetId == nextPresetId);
            SetFlowTransitionPresetOverride(key, nextPresetId);
            if (nextPreset != null)
            {
                SetFlowTransitionLengthOverride(key, nextPreset.DefaultLengthSeconds);
            }

            _undoService.Push(new FlowTransitionPresetOperation(this, key, currentPresetId, nextPresetId, previousLength, nextPreset?.DefaultLengthSeconds));
            RaiseFlowSelectionProperties();
        }, canApplyPreset);

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

    private static IReadOnlyList<double> BuildPhraseSnapCandidatesSeconds(
        WorkstationDeckViewModel source,
        WorkstationDeckViewModel target,
        double rangeStart,
        double rangeEnd,
        double sourceAnchor,
        double targetAnchor)
    {
        var min = Math.Max(0.0, rangeStart - 16.0);
        var max = rangeEnd + 16.0;

        var phraseRoles = new[]
        {
            CueRole.PhraseStart,
            CueRole.Intro,
            CueRole.Build,
            CueRole.Drop,
            CueRole.Breakdown,
            CueRole.Outro,
        };

        var candidates = source.CueEditor.Cues
            .Concat(target.CueEditor.Cues)
            .Where(c => phraseRoles.Contains(c.Role))
            .Select(c => c.Timestamp)
            .Where(t => t >= min && t <= max)
            .Concat(new[] { sourceAnchor, targetAnchor })
            .Where(t => t >= 0)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        if (candidates.Count == 0)
        {
            candidates.Add(Math.Max(0.0, sourceAnchor));
        }

        return candidates;
    }

    private static IReadOnlyList<FlowPhraseRegion> BuildMergedPhraseRegions(
        string transitionKey,
        WorkstationDeckViewModel source,
        WorkstationDeckViewModel target,
        double rangeStart,
        double rangeEnd,
        double sourceAnchor,
        double targetAnchor,
        IReadOnlyDictionary<string, FlowPhraseRegion>? phraseRegionOverrides)
    {
        var inferred = BuildInferredPhraseRegionsFromCues(transitionKey, source, target, rangeStart, rangeEnd, sourceAnchor, targetAnchor);
        inferred = NormalizePhraseRegions(transitionKey, inferred, rangeStart, rangeEnd);
        inferred = MergeAdjacentOrOverlappingInferredRegions(transitionKey, inferred, rangeStart, rangeEnd);

        FlowPhraseRegion? explicitRegion = null;
        if (phraseRegionOverrides != null && phraseRegionOverrides.TryGetValue(transitionKey, out var overrideRegion))
        {
            explicitRegion = overrideRegion;
        }

        var withExplicit = ApplyExplicitPhraseRegionOverrides(transitionKey, inferred, explicitRegion, rangeStart, rangeEnd);
        return ComputeProvenanceAndConfidence(transitionKey, source, target, withExplicit, rangeStart, rangeEnd);
    }

    private static IReadOnlyList<FlowPhraseRegion> BuildInferredPhraseRegionsFromCues(
        string transitionKey,
        WorkstationDeckViewModel source,
        WorkstationDeckViewModel target,
        double rangeStart,
        double rangeEnd,
        double sourceAnchor,
        double targetAnchor)
    {
        var cues = source.CueEditor.Cues.Concat(target.CueEditor.Cues).ToList();
        var inferred = new List<FlowPhraseRegion>
        {
            // Seed deterministic inferred baseline so explicit edits can shape regions consistently.
            new(transitionKey, rangeStart, rangeEnd, false, 0.55, null, FlowPhraseRegionProvenance.Inferred)
        };

        var cueCandidates = cues
            .Where(c => c.Role is CueRole.PhraseStart or CueRole.Intro or CueRole.Build or CueRole.Drop or CueRole.Breakdown or CueRole.Outro)
            .Select(c => c.Timestamp)
            .Concat(new[] { sourceAnchor, targetAnchor })
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        foreach (var cue in cueCandidates)
        {
            var left = cue - 1.8;
            var right = cue + 1.8;
            inferred.Add(new FlowPhraseRegion(transitionKey, left, right, false, 0.6, null, FlowPhraseRegionProvenance.Inferred));
        }

        return inferred;
    }

    private static IReadOnlyList<FlowPhraseRegion> NormalizePhraseRegions(
        string transitionKey,
        IReadOnlyList<FlowPhraseRegion> regions,
        double rangeStart,
        double rangeEnd)
    {
        var min = Math.Max(0.0, Math.Min(rangeStart, rangeEnd));
        var max = Math.Max(min, Math.Max(rangeStart, rangeEnd));

        return (regions ?? Array.Empty<FlowPhraseRegion>())
            .Select(r =>
            {
                var start = Math.Clamp(r.StartSeconds, min, max);
                var end = Math.Clamp(Math.Max(start, r.EndSeconds), min, max);
                return new FlowPhraseRegion(
                    transitionKey,
                    start,
                    end,
                    r.IsExplicit,
                    r.Confidence,
                    r.SourceCueIds,
                    r.Provenance);
            })
            .Where(r => r.EndSeconds - r.StartSeconds >= 0.05)
            .OrderBy(r => r.StartSeconds)
            .ToList();
    }

    private static IReadOnlyList<FlowPhraseRegion> MergeAdjacentOrOverlappingInferredRegions(
        string transitionKey,
        IReadOnlyList<FlowPhraseRegion> regions,
        double rangeStart,
        double rangeEnd)
    {
        const double adjacencyToleranceSeconds = 0.1;
        var inferred = NormalizePhraseRegions(transitionKey, regions, rangeStart, rangeEnd)
            .Where(r => !r.IsExplicit)
            .OrderBy(r => r.StartSeconds)
            .ToList();

        if (inferred.Count <= 1)
        {
            return inferred;
        }

        var merged = new List<FlowPhraseRegion>();
        var current = inferred[0];
        for (var i = 1; i < inferred.Count; i++)
        {
            var next = inferred[i];
            var shouldMerge = next.StartSeconds <= current.EndSeconds + adjacencyToleranceSeconds;
            if (!shouldMerge)
            {
                merged.Add(current);
                current = next;
                continue;
            }

            var cueIds = current.SourceCueIds.Concat(next.SourceCueIds).Distinct().ToList();
            current = new FlowPhraseRegion(
                transitionKey,
                Math.Min(current.StartSeconds, next.StartSeconds),
                Math.Max(current.EndSeconds, next.EndSeconds),
                false,
                (current.Confidence + next.Confidence) / 2.0,
                cueIds,
                FlowPhraseRegionProvenance.Inferred);
        }

        merged.Add(current);
        return merged;
    }

    private static IReadOnlyList<FlowPhraseRegion> ApplyExplicitPhraseRegionOverrides(
        string transitionKey,
        IReadOnlyList<FlowPhraseRegion> inferredRegions,
        FlowPhraseRegion? explicitRegion,
        double rangeStart,
        double rangeEnd)
    {
        if (explicitRegion == null)
        {
            return inferredRegions;
        }

        var normalizedExplicit = NormalizePhraseRegions(transitionKey, new[]
        {
            new FlowPhraseRegion(
                transitionKey,
                explicitRegion.StartSeconds,
                explicitRegion.EndSeconds,
                true,
                1.0,
                explicitRegion.SourceCueIds,
                FlowPhraseRegionProvenance.ExplicitUser)
        }, rangeStart, rangeEnd).FirstOrDefault();

        if (normalizedExplicit == null)
        {
            return inferredRegions;
        }

        var shaped = new List<FlowPhraseRegion>();
        foreach (var inferred in inferredRegions.Where(r => !r.IsExplicit))
        {
            var overlaps = normalizedExplicit.StartSeconds < inferred.EndSeconds && normalizedExplicit.EndSeconds > inferred.StartSeconds;
            if (!overlaps)
            {
                shaped.Add(inferred);
                continue;
            }

            if (normalizedExplicit.StartSeconds > inferred.StartSeconds + 0.05)
            {
                shaped.Add(new FlowPhraseRegion(
                    transitionKey,
                    inferred.StartSeconds,
                    Math.Min(normalizedExplicit.StartSeconds, inferred.EndSeconds),
                    false,
                    inferred.Confidence,
                    inferred.SourceCueIds,
                    FlowPhraseRegionProvenance.Mixed));
            }

            if (normalizedExplicit.EndSeconds < inferred.EndSeconds - 0.05)
            {
                shaped.Add(new FlowPhraseRegion(
                    transitionKey,
                    Math.Max(normalizedExplicit.EndSeconds, inferred.StartSeconds),
                    inferred.EndSeconds,
                    false,
                    inferred.Confidence,
                    inferred.SourceCueIds,
                    FlowPhraseRegionProvenance.Mixed));
            }
        }

        shaped.Add(normalizedExplicit);
        return NormalizePhraseRegions(transitionKey, shaped, rangeStart, rangeEnd);
    }

    private static IReadOnlyList<FlowPhraseRegion> ComputeProvenanceAndConfidence(
        string transitionKey,
        WorkstationDeckViewModel source,
        WorkstationDeckViewModel target,
        IReadOnlyList<FlowPhraseRegion> regions,
        double rangeStart,
        double rangeEnd)
    {
        var allCues = source.CueEditor.Cues.Concat(target.CueEditor.Cues).ToList();
        var normalized = NormalizePhraseRegions(transitionKey, regions, rangeStart, rangeEnd);

        return normalized
            .Select(r =>
            {
                if (r.IsExplicit)
                {
                    return new FlowPhraseRegion(
                        transitionKey,
                        r.StartSeconds,
                        r.EndSeconds,
                        true,
                        1.0,
                        r.SourceCueIds,
                        FlowPhraseRegionProvenance.ExplicitUser);
                }

                var provenance = r.Provenance == FlowPhraseRegionProvenance.Mixed
                    ? FlowPhraseRegionProvenance.Mixed
                    : FlowPhraseRegionProvenance.Inferred;
                var confidence = ComputeInferredRegionConfidence(r.StartSeconds, r.EndSeconds, allCues, provenance);
                var cueIds = allCues
                    .Where(c => c.Timestamp >= r.StartSeconds && c.Timestamp <= r.EndSeconds)
                    .OrderBy(c => c.Timestamp)
                    .Take(4)
                    .Select(c => $"{c.Role}@{c.Timestamp.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}")
                    .ToList();

                return new FlowPhraseRegion(
                    transitionKey,
                    r.StartSeconds,
                    r.EndSeconds,
                    false,
                    confidence,
                    cueIds,
                    provenance);
            })
            .OrderBy(r => r.StartSeconds)
            .ToList();
    }

    private static double ComputeInferredRegionConfidence(
        double startSeconds,
        double endSeconds,
        IReadOnlyList<OrbitCue> cues,
        FlowPhraseRegionProvenance provenance)
    {
        var cuesInSpan = (cues ?? Array.Empty<OrbitCue>())
            .Where(c => c.Timestamp >= startSeconds && c.Timestamp <= endSeconds)
            .ToList();

        if (cuesInSpan.Count == 0)
        {
            return provenance == FlowPhraseRegionProvenance.Mixed ? 0.55 : 0.45;
        }

        var average = cuesInSpan.Average(c => Math.Clamp(c.Confidence, 0.0, 1.0));
        var adjusted = provenance == FlowPhraseRegionProvenance.Mixed ? average + 0.05 : average;
        return Math.Clamp(adjusted, 0.35, 0.95);
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
        this.RaisePropertyChanged(nameof(FlowTransitions));
        this.RaisePropertyChanged(nameof(HasFlowTransitions));
        this.RaisePropertyChanged(nameof(IsFlowOverlayVisible));
        this.RaisePropertyChanged(nameof(FlowOverlayHint));
    }

    public static IReadOnlyList<FlowTransitionOverlayViewModel> BuildFlowTransitions(
        IEnumerable<WorkstationDeckViewModel> decks,
        double timelineOffsetSeconds,
        double timelineWindowSeconds)
    {
        return BuildFlowTransitions(decks, timelineOffsetSeconds, timelineWindowSeconds, selectedTransitionKey: null, lengthOverrides: null, presetOverrides: null, phraseMarkerOverrides: null, phraseMarkerExplicitKeys: null, phraseRegionOverrides: null);
    }

    public static IReadOnlyList<FlowTransitionOverlayViewModel> BuildFlowTransitions(
        IEnumerable<WorkstationDeckViewModel> decks,
        double timelineOffsetSeconds,
        double timelineWindowSeconds,
        string? selectedTransitionKey,
        IReadOnlyDictionary<string, double>? lengthOverrides,
        IReadOnlyDictionary<string, string>? presetOverrides = null,
        IReadOnlyDictionary<string, double>? phraseMarkerOverrides = null,
        IReadOnlyCollection<string>? phraseMarkerExplicitKeys = null,
        IReadOnlyDictionary<string, FlowPhraseRegion>? phraseRegionOverrides = null)
    {
        const double timelineCanvasWidth = 700.0;
        var overlays = new List<FlowTransitionOverlayViewModel>();

        if (timelineWindowSeconds <= 0)
        {
            return overlays;
        }

        var loadedDecks = (decks ?? Enumerable.Empty<WorkstationDeckViewModel>())
            .Where(deck => deck.IsLoaded)
            .OrderBy(deck => deck.DeckLabel)
            .ToList();

        if (loadedDecks.Count < 2)
        {
            return overlays;
        }

        var presetCatalog = BuildFlowTransitionPresetCatalog();

        for (var i = 0; i < loadedDecks.Count - 1; i++)
        {
            var source = loadedDecks[i];
            var target = loadedDecks[i + 1];

            var sourceCue = SelectAnchorCueForReferenceTrack(source);
            var targetCue = SelectAnchorCueForIncomingTrack(target);

            var transitionKey = BuildTransitionKey(source, target);
            var sourceAnchor = sourceCue?.Timestamp ?? source.Deck.PositionSeconds;
            if (phraseMarkerOverrides != null && phraseMarkerOverrides.TryGetValue(transitionKey, out var phraseMarkerOverride))
            {
                sourceAnchor = Math.Max(0.0, phraseMarkerOverride);
            }

            var targetAnchor = targetCue?.Timestamp ?? target.Deck.PositionSeconds;
            var rangeStart = Math.Max(0.0, Math.Min(sourceAnchor, targetAnchor) - 2.0);
            var baselineEnd = Math.Max(rangeStart + 6.0, Math.Max(sourceAnchor, targetAnchor) + 2.0);
            var baselineLength = Math.Max(0.1, baselineEnd - rangeStart);
            var transitionLength = baselineLength;
            var overriddenLength = 0d;
            var hasOverride = lengthOverrides != null && lengthOverrides.TryGetValue(transitionKey, out overriddenLength);
            if (hasOverride)
            {
                transitionLength = Math.Max(0.1, overriddenLength);
            }

            var rangeEnd = rangeStart + transitionLength;

            var left = ToCanvasX(rangeStart, timelineOffsetSeconds, timelineWindowSeconds, timelineCanvasWidth);
            var right = ToCanvasX(rangeEnd, timelineOffsetSeconds, timelineWindowSeconds, timelineCanvasWidth);
            var width = Math.Max(26.0, right - left);

            var phraseGuideLeft = ToCanvasX(sourceAnchor, timelineOffsetSeconds, timelineWindowSeconds, timelineCanvasWidth);
            var beatGuideLeft = ToCanvasX(targetAnchor, timelineOffsetSeconds, timelineWindowSeconds, timelineCanvasWidth);
            var phraseSnapCandidates = BuildPhraseSnapCandidatesSeconds(source, target, rangeStart, rangeEnd, sourceAnchor, targetAnchor);
            var isPhraseMarkerExplicit = phraseMarkerExplicitKeys?.Contains(transitionKey) == true;
            var phraseRegions = BuildMergedPhraseRegions(
                transitionKey,
                source,
                target,
                rangeStart,
                rangeEnd,
                sourceAnchor,
                targetAnchor,
                phraseRegionOverrides);
            var beatDiff = Math.Abs(source.DisplayBpm - target.DisplayBpm);
            var beatAligned = source.DisplayBpm > 0 && target.DisplayBpm > 0 && beatDiff <= 1.0;
            var phraseAligned = sourceCue != null && targetCue != null;
            var sourceEnergy = EstimateNormalizedEnergyFromBpm(source.DisplayBpm);
            var targetEnergy = EstimateNormalizedEnergyFromBpm(target.DisplayBpm);
            var harmonicScore = ComputeFlowHarmonicCompatibility(source.TrackKey, target.TrackKey, semitoneShift: 0);
            var energyScore = ComputeFlowEnergyCompatibility(sourceEnergy, targetEnergy, transitionLength);
            var combinedScore = ComputeCombinedFlowCompatibilityScore(harmonicScore.Score, energyScore.Score);
            var suggestedPresets = RankFlowPresetSuggestions(
                presetCatalog,
                harmonicScore,
                energyScore,
                phraseAligned,
                Math.Abs(sourceEnergy - targetEnergy),
                topN: 3);
            var warningFlags = BuildFlowWarningFlags(harmonicScore, energyScore, phraseAligned);
            var phraseMarkerConfidenceScore = ComputePhraseMarkerConfidenceScore(sourceCue, targetCue, phraseAligned, isPhraseMarkerExplicit);
            var phraseMarkerConfidenceLabel = BuildPhraseMarkerConfidenceLabel(phraseMarkerConfidenceScore, isPhraseMarkerExplicit);
            var phraseMarkerTooltip = BuildPhraseMarkerTooltip(phraseMarkerConfidenceLabel, isPhraseMarkerExplicit, phraseSnapCandidates.Count);

            var resolvedPresetId = presetOverrides != null && presetOverrides.TryGetValue(transitionKey, out var pid) ? pid : null;

            overlays.Add(new FlowTransitionOverlayViewModel(
                transitionKey,
                BuildTransitionLabel(source, target),
                BuildFlowCompatibilityLabel(source.TrackKey, target.TrackKey, beatDiff),
                rangeStart,
                rangeEnd,
                left,
                width,
                sourceAnchor,
                targetAnchor,
                phraseGuideLeft,
                beatGuideLeft,
                phraseSnapCandidates,
                isPhraseMarkerExplicit,
                phraseMarkerConfidenceScore,
                phraseMarkerConfidenceLabel,
                phraseMarkerTooltip,
                phraseAligned,
                beatAligned,
                transitionLength,
                string.Equals(transitionKey, selectedTransitionKey, StringComparison.Ordinal),
                hasOverride,
                resolvedPresetId,
                suggestedPresets,
                harmonicScore.Score,
                harmonicScore.Label,
                energyScore.Score,
                energyScore.Label,
                combinedScore,
                phraseRegions,
                warningFlags));
        }

        return overlays;
    }

    private static string BuildTransitionLabel(WorkstationDeckViewModel source, WorkstationDeckViewModel target)
    {
        var sourceLabel = string.IsNullOrWhiteSpace(source.TrackTitle) ? source.DeckLabel : source.TrackTitle;
        var targetLabel = string.IsNullOrWhiteSpace(target.TrackTitle) ? target.DeckLabel : target.TrackTitle;
        return $"{sourceLabel} → {targetLabel}";
    }

    private static string BuildTransitionKey(WorkstationDeckViewModel source, WorkstationDeckViewModel target)
    {
        var sourceId = string.IsNullOrWhiteSpace(source.TrackHash) ? source.DeckLabel : source.TrackHash;
        var targetId = string.IsNullOrWhiteSpace(target.TrackHash) ? target.DeckLabel : target.TrackHash;
        return $"{sourceId}->{targetId}";
    }

    private static string BuildFlowCompatibilityLabel(string? sourceKey, string? targetKey, double bpmDifference)
    {
        var keyDistance = CamelotDistance(sourceKey, targetKey);
        var harmonicState = keyDistance switch
        {
            <= 1d => "harmonic lock",
            <= 2d => "safe blend",
            <= 4d => "stretch blend",
            _ => "risky blend"
        };

        var bpmText = bpmDifference <= 0.05
            ? "±0.0 BPM"
            : $"±{bpmDifference.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} BPM";
        return $"{harmonicState} • {bpmText}";
    }

    private string ResolvePresetName(string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
        {
            return "Custom";
        }

        var preset = _flowPresetCatalog.FirstOrDefault(p => string.Equals(p.PresetId, presetId, StringComparison.OrdinalIgnoreCase));
        return preset?.DisplayName ?? "Custom";
    }

    private string ResolveCurveLabel(string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
        {
            return "Curve: default";
        }

        var preset = _flowPresetCatalog.FirstOrDefault(p => string.Equals(p.PresetId, presetId, StringComparison.OrdinalIgnoreCase));
        return preset == null ? "Curve: default" : $"Curve: {CurveTypeDisplayName(preset.CurveType)}";
    }

    private string ResolveBandStrategyLabel(string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
        {
            return "Bands: default";
        }

        var preset = _flowPresetCatalog.FirstOrDefault(p => string.Equals(p.PresetId, presetId, StringComparison.OrdinalIgnoreCase));
        if (preset == null)
        {
            return "Bands: default";
        }

        return $"Bands: Low={BandStrategyDisplayName(preset.LowBandStrategy)} Mid={BandStrategyDisplayName(preset.MidBandStrategy)} High={BandStrategyDisplayName(preset.HighBandStrategy)}";
    }

    private static string CurveTypeDisplayName(FlowTransitionCurveType curveType) => curveType switch
    {
        FlowTransitionCurveType.EqualPower  => "Equal Power",
        FlowTransitionCurveType.BassSwap    => "Bass Swap",
        FlowTransitionCurveType.FullSpectrum => "Full Spectrum",
        FlowTransitionCurveType.HardCut     => "Hard Cut",
        FlowTransitionCurveType.Custom      => "Custom",
        _                                   => "Default"
    };

    private static string BandStrategyDisplayName(FlowFrequencyBandStrategy strategy) => strategy switch
    {
        FlowFrequencyBandStrategy.Blend  => "Blend",
        FlowFrequencyBandStrategy.Swap   => "Swap",
        FlowFrequencyBandStrategy.Hold   => "Hold",
        FlowFrequencyBandStrategy.Cut    => "Cut",
        FlowFrequencyBandStrategy.Custom => "Custom",
        _                               => "Default"
    };

    private string BuildSuggestedPresetLabel(IReadOnlyList<string> suggestedPresetIds)
    {
        if (suggestedPresetIds == null || suggestedPresetIds.Count == 0)
        {
            return "No strong preset suggestions yet";
        }

        var names = suggestedPresetIds
            .Take(3)
            .Select(ResolvePresetName)
            .ToList();
        return string.Join(" • ", names);
    }

    private static string BuildWarningLabel(IReadOnlyList<string> warningFlags)
    {
        if (warningFlags == null || warningFlags.Count == 0)
        {
            return "Warnings: none";
        }

        return $"Warnings: {string.Join(" • ", warningFlags)}";
    }

    private static double ComputePhraseMarkerConfidenceScore(OrbitCue? sourceCue, OrbitCue? targetCue, bool phraseAligned, bool isExplicit)
    {
        var sourceConfidence = sourceCue?.Confidence ?? 0.55;
        var targetConfidence = targetCue?.Confidence ?? 0.55;
        var baseConfidence = (sourceConfidence + targetConfidence) / 2.0;

        if (phraseAligned)
        {
            baseConfidence += 0.1;
        }

        if (isExplicit)
        {
            baseConfidence += 0.1;
        }

        return Math.Clamp(Math.Round(baseConfidence * 100.0, 1), 0.0, 100.0);
    }

    private static string BuildPhraseMarkerConfidenceLabel(double confidenceScore, bool isExplicit)
    {
        var state = isExplicit ? "explicit" : "inferred";
        return $"{state} {confidenceScore.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}%";
    }

    private static string BuildPhraseMarkerTooltip(string confidenceLabel, bool isExplicit, int candidateCount)
    {
        var mode = isExplicit ? "Explicit phrase marker" : "Inferred phrase marker";
        var action = isExplicit ? "Double-click to remove" : "Double-click to pin";
        return $"{mode} · {confidenceLabel} · {candidateCount} candidate{(candidateCount == 1 ? string.Empty : "s")} · {action}";
    }

    private static double EstimateNormalizedEnergyFromBpm(double bpm)
    {
        if (bpm <= 0)
        {
            return 0.5;
        }

        return Math.Clamp((bpm - 80.0) / 80.0, 0.0, 1.0);
    }

    public static IReadOnlyList<FlowTransitionPreset> BuildFlowTransitionPresetCatalog()
    {
        return new List<FlowTransitionPreset>
        {
            new(
                PresetId: "crossfade",
                DisplayName: "Crossfade",
                Description: "Balanced overlap and gain curve.",
                CurveType: FlowTransitionCurveType.EqualPower,
                DefaultLengthSeconds: 8.0,
                LowBandStrategy: FlowFrequencyBandStrategy.Blend,
                MidBandStrategy: FlowFrequencyBandStrategy.Blend,
                HighBandStrategy: FlowFrequencyBandStrategy.Blend,
                MinCompatibilityScore: 20.0,
                RequiresPhraseAlignment: false,
                MinSuggestedEnergyDelta: 0.00,
                MaxSuggestedEnergyDelta: 0.40),
            new(
                PresetId: "bass-swap",
                DisplayName: "Bass Swap",
                Description: "Stage low-band handoff while mids/highs overlap.",
                CurveType: FlowTransitionCurveType.BassSwap,
                DefaultLengthSeconds: 10.0,
                LowBandStrategy: FlowFrequencyBandStrategy.Swap,
                MidBandStrategy: FlowFrequencyBandStrategy.Blend,
                HighBandStrategy: FlowFrequencyBandStrategy.Blend,
                MinCompatibilityScore: 55.0,
                RequiresPhraseAlignment: true,
                MinSuggestedEnergyDelta: 0.05,
                MaxSuggestedEnergyDelta: 0.35),
            new(
                PresetId: "full",
                DisplayName: "Full",
                Description: "Full-spectrum overlap for energetic lifts.",
                CurveType: FlowTransitionCurveType.FullSpectrum,
                DefaultLengthSeconds: 12.0,
                LowBandStrategy: FlowFrequencyBandStrategy.Blend,
                MidBandStrategy: FlowFrequencyBandStrategy.Blend,
                HighBandStrategy: FlowFrequencyBandStrategy.Blend,
                MinCompatibilityScore: 70.0,
                RequiresPhraseAlignment: true,
                MinSuggestedEnergyDelta: 0.00,
                MaxSuggestedEnergyDelta: 0.22),
            new(
                PresetId: "none",
                DisplayName: "None",
                Description: "Hard handoff with minimal overlap.",
                CurveType: FlowTransitionCurveType.HardCut,
                DefaultLengthSeconds: 3.0,
                LowBandStrategy: FlowFrequencyBandStrategy.Cut,
                MidBandStrategy: FlowFrequencyBandStrategy.Cut,
                HighBandStrategy: FlowFrequencyBandStrategy.Cut,
                MinCompatibilityScore: 0.0,
                RequiresPhraseAlignment: false,
                MinSuggestedEnergyDelta: 0.00,
                MaxSuggestedEnergyDelta: 1.00),
            new(
                PresetId: "custom",
                DisplayName: "Custom",
                Description: "User-defined transition behavior.",
                CurveType: FlowTransitionCurveType.Custom,
                DefaultLengthSeconds: 8.0,
                LowBandStrategy: FlowFrequencyBandStrategy.Custom,
                MidBandStrategy: FlowFrequencyBandStrategy.Custom,
                HighBandStrategy: FlowFrequencyBandStrategy.Custom,
                MinCompatibilityScore: 0.0,
                RequiresPhraseAlignment: false,
                MinSuggestedEnergyDelta: 0.00,
                MaxSuggestedEnergyDelta: 1.00)
        };
    }

    public static FlowCompatibilityScore ComputeFlowHarmonicCompatibility(string? sourceCamelotKey, string? targetCamelotKey, int semitoneShift)
    {
        var distance = CamelotDistance(sourceCamelotKey, targetCamelotKey);
        var baseline = distance switch
        {
            <= 1d => 96d - (distance * 6d),
            <= 2d => 82d - ((distance - 1d) * 12d),
            <= 4d => 62d - ((distance - 2d) * 8d),
            _ => 44d - ((distance - 4d) * 8d)
        };

        var shiftPenalty = Math.Min(18d, Math.Abs(semitoneShift) * 3d);
        var finalScore = Math.Clamp(baseline - shiftPenalty, 0d, 100d);
        var label = finalScore switch
        {
            >= 90d => "lock",
            >= 70d => "safe",
            >= 45d => "stretch",
            _ => "risky"
        };

        return new FlowCompatibilityScore(Math.Round(finalScore, 1), label);
    }

    public static FlowCompatibilityScore ComputeFlowEnergyCompatibility(double? sourceEnergy, double? targetEnergy, double transitionLengthSeconds)
    {
        var source = Math.Clamp(sourceEnergy ?? 0.5, 0.0, 1.0);
        var target = Math.Clamp(targetEnergy ?? 0.5, 0.0, 1.0);
        var delta = Math.Abs(source - target);

        var baseline = delta switch
        {
            <= 0.10 => 92d,
            <= 0.20 => 78d,
            <= 0.35 => 58d,
            _ => 28d
        };

        var length = Math.Max(2.0, transitionLengthSeconds);
        var lengthAdjustment = delta switch
        {
            <= 0.10 when length <= 10.0 => 4d,
            <= 0.10 when length > 18.0 => -5d,
            > 0.35 when length < 8.0 => -10d,
            > 0.20 when length >= 12.0 => 4d,
            _ => 0d
        };

        var finalScore = Math.Clamp(baseline + lengthAdjustment, 0d, 100d);
        var label = delta switch
        {
            <= 0.10 => "smooth",
            <= 0.20 => "lift",
            <= 0.35 => "aggressive",
            _ => "mismatch"
        };

        return new FlowCompatibilityScore(Math.Round(finalScore, 1), label);
    }

    public static double ComputeCombinedFlowCompatibilityScore(double harmonicScore, double energyScore)
    {
        return Math.Round(Math.Clamp((harmonicScore * 0.6) + (energyScore * 0.4), 0.0, 100.0), 1);
    }

    public static IReadOnlyList<string> RankFlowPresetSuggestions(
        IReadOnlyList<FlowTransitionPreset> presets,
        FlowCompatibilityScore harmonicScore,
        FlowCompatibilityScore energyScore,
        bool phraseAligned,
        double energyDelta,
        int topN)
    {
        if (presets == null || presets.Count == 0 || topN <= 0)
        {
            return Array.Empty<string>();
        }

        var blendScore = ComputeCombinedFlowCompatibilityScore(harmonicScore.Score, energyScore.Score);
        var ranked = presets
            .Select(preset => new
            {
                preset.PresetId,
                Score = ScorePresetCandidate(preset, harmonicScore, energyScore, phraseAligned, energyDelta, blendScore)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.PresetId, StringComparer.Ordinal)
            .Take(topN)
            .Select(x => x.PresetId)
            .ToList();

        return ranked;
    }

    private static double ScorePresetCandidate(
        FlowTransitionPreset preset,
        FlowCompatibilityScore harmonicScore,
        FlowCompatibilityScore energyScore,
        bool phraseAligned,
        double energyDelta,
        double blendScore)
    {
        if (blendScore < preset.MinCompatibilityScore)
        {
            return -100d;
        }

        if (preset.RequiresPhraseAlignment && !phraseAligned)
        {
            return -100d;
        }

        var score = 50d + blendScore;

        if (energyDelta < preset.MinSuggestedEnergyDelta || energyDelta > preset.MaxSuggestedEnergyDelta)
        {
            score -= 12d;
        }

        if (harmonicScore.Label == "lock" && (preset.PresetId == "bass-swap" || preset.PresetId == "full"))
        {
            score += 12d;
        }

        if (harmonicScore.Label == "risky" && preset.PresetId == "full")
        {
            score -= 35d;
        }

        if (energyScore.Label == "mismatch")
        {
            if (preset.PresetId is "crossfade" or "bass-swap")
            {
                score += 14d;
            }

            if (preset.PresetId == "crossfade")
            {
                score += 6d;
            }

            if (preset.PresetId == "none")
            {
                score -= 8d;
            }

            if (preset.PresetId == "full")
            {
                score -= 20d;
            }
        }

        if (energyScore.Label == "smooth" && preset.PresetId is "none" or "crossfade")
        {
            score += 8d;
        }

        // Keep "Custom" available for manual choice, but avoid auto-prioritizing it
        // over curated presets in suggestion chips.
        if (preset.PresetId == "custom")
        {
            score -= 25d;
        }

        return score;
    }

    private static IReadOnlyList<string> BuildFlowWarningFlags(
        FlowCompatibilityScore harmonicScore,
        FlowCompatibilityScore energyScore,
        bool phraseAligned)
    {
        var warnings = new List<string>();

        if (harmonicScore.Label == "risky")
        {
            warnings.Add("Harmonic mismatch risk");
        }

        if (energyScore.Label == "mismatch")
        {
            warnings.Add("Energy jump is aggressive");
        }

        if (!phraseAligned)
        {
            warnings.Add("Phrase anchors missing");
        }

        return warnings;
    }

    private static double ToCanvasX(double seconds, double timelineOffsetSeconds, double timelineWindowSeconds, double timelineCanvasWidth)
    {
        if (timelineWindowSeconds <= 0)
        {
            return 0;
        }

        var ratio = (seconds - timelineOffsetSeconds) / timelineWindowSeconds;
        return Math.Clamp(ratio, 0.0, 1.0) * timelineCanvasWidth;
    }

    public void PreviewFlowTransitionLengthFromCanvasDelta(string transitionKey, double initialLengthSeconds, double deltaCanvasPixels)
    {
        if (string.IsNullOrWhiteSpace(transitionKey))
        {
            return;
        }

        var nextLength = ComputeTransitionLengthFromCanvasDelta(transitionKey, initialLengthSeconds, deltaCanvasPixels);
        SetFlowTransitionLengthOverride(transitionKey, nextLength);
    }

    public void CommitFlowTransitionLengthFromCanvasDelta(string transitionKey, double initialLengthSeconds, double deltaCanvasPixels)
    {
        if (string.IsNullOrWhiteSpace(transitionKey))
        {
            return;
        }

        var nextLength = ComputeTransitionLengthFromCanvasDelta(transitionKey, initialLengthSeconds, deltaCanvasPixels);
        if (Math.Abs(nextLength - initialLengthSeconds) < 0.05)
        {
            SetFlowTransitionLengthOverride(transitionKey, initialLengthSeconds);
            return;
        }

        SetFlowTransitionLengthOverride(transitionKey, nextLength);
        _undoService.Push(new FlowTransitionLengthOperation(this, transitionKey, initialLengthSeconds, nextLength));
        RaiseHeaderProperties();
    }

    public void PreviewFlowPhraseMarkerFromCanvasDelta(string transitionKey, double initialPhraseSeconds, double deltaCanvasPixels)
    {
        if (string.IsNullOrWhiteSpace(transitionKey))
        {
            return;
        }

        var nextPhraseSeconds = ComputeFlowPhraseMarkerSecondsFromCanvasDelta(transitionKey, initialPhraseSeconds, deltaCanvasPixels);
        SetFlowTransitionPhraseMarkerState(transitionKey, nextPhraseSeconds, isExplicit: true);
    }

    public void CommitFlowPhraseMarkerFromCanvasDelta(string transitionKey, double initialPhraseSeconds, double deltaCanvasPixels)
    {
        if (string.IsNullOrWhiteSpace(transitionKey))
        {
            return;
        }

        var nextPhraseSeconds = ComputeFlowPhraseMarkerSecondsFromCanvasDelta(transitionKey, initialPhraseSeconds, deltaCanvasPixels);
        if (Math.Abs(nextPhraseSeconds - initialPhraseSeconds) < 0.03)
        {
            SetFlowTransitionPhraseMarkerState(transitionKey, initialPhraseSeconds, isExplicit: true);
            return;
        }

        SetFlowTransitionPhraseMarkerState(transitionKey, nextPhraseSeconds, isExplicit: true);
        _undoService.Push(new FlowTransitionPhraseMarkerOperation(this, transitionKey, initialPhraseSeconds, false, nextPhraseSeconds, true));
        RaiseHeaderProperties();
    }

    public void ToggleFlowPhraseMarkerEditState(string transitionKey)
    {
        if (string.IsNullOrWhiteSpace(transitionKey) || !TryGetFlowTransition(transitionKey, out var overlay) || overlay == null)
        {
            return;
        }

        var fromPhraseSeconds = overlay.PhraseGuideSeconds;
        var fromExplicit = overlay.IsPhraseMarkerExplicit;
        var toExplicit = !fromExplicit;
        var toPhraseSeconds = toExplicit ? overlay.PhraseGuideSeconds : overlay.PhraseGuideSeconds;

        SetFlowTransitionPhraseMarkerState(transitionKey, toPhraseSeconds, toExplicit);
        _undoService.Push(new FlowTransitionPhraseMarkerOperation(this, transitionKey, fromPhraseSeconds, fromExplicit, toPhraseSeconds, toExplicit));
        RaiseHeaderProperties();
    }

    private double ComputeTransitionLengthFromCanvasDelta(string transitionKey, double initialLengthSeconds, double deltaCanvasPixels)
    {
        var deltaSeconds = (deltaCanvasPixels / 700.0) * Math.Max(1.0, TimelineWindowSeconds);
        var candidateLength = Math.Max(2.0, initialLengthSeconds + deltaSeconds);
        if (!TryGetFlowTransition(transitionKey, out var overlay) || overlay == null)
        {
            return candidateLength;
        }

        var endTime = overlay.StartSeconds + candidateLength;
        var snapThresholdSeconds = Math.Max(0.18, (Math.Max(1.0, TimelineWindowSeconds) / 700.0) * 9.0);
        var snappedEnd = ComputePhraseAwareSnapEndSeconds(
            endTime,
            overlay.BeatGuideSeconds,
            overlay.PhraseSnapCandidatesSeconds,
            snapThresholdSeconds,
            overlay.PhraseRegions);

        var snappedLength = Math.Max(2.0, snappedEnd - overlay.StartSeconds);
        return Math.Clamp(snappedLength, 2.0, Math.Max(8.0, TimelineWindowSeconds * 0.95));
    }

    private double ComputeFlowPhraseMarkerSecondsFromCanvasDelta(string transitionKey, double initialPhraseSeconds, double deltaCanvasPixels)
    {
        var deltaSeconds = (deltaCanvasPixels / 700.0) * Math.Max(1.0, TimelineWindowSeconds);
        var candidate = Math.Max(0.0, initialPhraseSeconds + deltaSeconds);
        if (!TryGetFlowTransition(transitionKey, out var overlay) || overlay == null)
        {
            return candidate;
        }

        var min = Math.Max(0.0, overlay.StartSeconds - 24.0);
        var max = overlay.EndSeconds + 24.0;
        return Math.Clamp(candidate, min, max);
    }

    public static double ComputePhraseAwareSnapEndSeconds(
        double desiredEndSeconds,
        double beatGuideSeconds,
        IReadOnlyList<double>? phraseSnapCandidatesSeconds,
        double snapThresholdSeconds,
        IReadOnlyList<FlowPhraseRegion>? phraseRegions = null)
    {
        if (snapThresholdSeconds <= 0)
        {
            return desiredEndSeconds;
        }

        if (phraseRegions != null && phraseRegions.Count > 0)
        {
            var explicitBoundaries = phraseRegions
                .Where(r => r.IsExplicit || r.Provenance == FlowPhraseRegionProvenance.ExplicitUser)
                .SelectMany(r => new[] { r.StartSeconds, r.EndSeconds })
                .Distinct()
                .ToList();
            var mixedBoundaries = phraseRegions
                .Where(r => r.Provenance == FlowPhraseRegionProvenance.Mixed)
                .SelectMany(r => new[] { r.StartSeconds, r.EndSeconds })
                .Distinct()
                .ToList();
            var inferredBoundaries = phraseRegions
                .Where(r => !r.IsExplicit && r.Provenance == FlowPhraseRegionProvenance.Inferred)
                .SelectMany(r => new[] { r.StartSeconds, r.EndSeconds })
                .Distinct()
                .ToList();

            var prioritizedBoundaryGroups = new[] { explicitBoundaries, mixedBoundaries, inferredBoundaries };
            foreach (var group in prioritizedBoundaryGroups)
            {
                if (group.Count == 0)
                {
                    continue;
                }

                var boundaryMatch = FindClosestCandidateWithinThreshold(desiredEndSeconds, group, snapThresholdSeconds);
                if (boundaryMatch.HasValue)
                {
                    return boundaryMatch.Value;
                }
            }
        }

        if (phraseSnapCandidatesSeconds != null && phraseSnapCandidatesSeconds.Count > 0)
        {
            var closestPhrase = FindClosestCandidateWithinThreshold(desiredEndSeconds, phraseSnapCandidatesSeconds, snapThresholdSeconds);
            if (closestPhrase.HasValue)
            {
                return closestPhrase.Value;
            }
        }

        return Math.Abs(desiredEndSeconds - beatGuideSeconds) <= snapThresholdSeconds
            ? beatGuideSeconds
            : desiredEndSeconds;
    }

    private static double? FindClosestCandidateWithinThreshold(double desiredSeconds, IEnumerable<double> candidates, double thresholdSeconds)
    {
        double? closest = null;
        var closestDistance = double.MaxValue;
        foreach (var candidate in candidates)
        {
            var distance = Math.Abs(desiredSeconds - candidate);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closest = candidate;
            }
        }

        return closest.HasValue && closestDistance <= thresholdSeconds
            ? closest
            : null;
    }

    private static string BuildFlowPhraseRegionProvenanceLabel(IReadOnlyList<FlowPhraseRegion>? regions)
    {
        if (regions == null || regions.Count == 0)
        {
            return "None";
        }

        if (regions.All(r => r.Provenance == FlowPhraseRegionProvenance.ExplicitUser))
        {
            return "Manual";
        }

        if (regions.All(r => r.Provenance == FlowPhraseRegionProvenance.Inferred))
        {
            return "Suggested";
        }

        return "Hybrid";
    }

    private bool TryGetFlowTransition(string transitionKey, out FlowTransitionOverlayViewModel? overlay)
    {
        overlay = FlowTransitions.FirstOrDefault(t => string.Equals(t.TransitionKey, transitionKey, StringComparison.Ordinal));
        return overlay != null;
    }

    private void SetFlowTransitionLengthOverride(string transitionKey, double lengthSeconds)
    {
        var defaultLength = GetDefaultFlowTransitionLength(transitionKey);
        if (defaultLength.HasValue && Math.Abs(lengthSeconds - defaultLength.Value) < 0.05)
        {
            _flowTransitionLengthOverrides.Remove(transitionKey);
        }
        else
        {
            _flowTransitionLengthOverrides[transitionKey] = Math.Max(2.0, lengthSeconds);
        }

        RaiseFlowSelectionProperties();
    }

    internal void SetFlowTransitionPhraseMarkerState(string transitionKey, double phraseSeconds, bool isExplicit)
    {
        var baseline = BuildFlowTransitions(Decks, TimelineOffsetSeconds, TimelineWindowSeconds, selectedTransitionKey: null, lengthOverrides: null, presetOverrides: null, phraseMarkerOverrides: null, phraseMarkerExplicitKeys: null)
            .FirstOrDefault(t => string.Equals(t.TransitionKey, transitionKey, StringComparison.Ordinal));
        if (baseline != null && Math.Abs(phraseSeconds - baseline.PhraseGuideSeconds) < 0.03 && !isExplicit)
        {
            _flowTransitionPhraseMarkerOverrides.Remove(transitionKey);
            _flowTransitionPhraseMarkerExplicitKeys.Remove(transitionKey);
        }
        else
        {
            _flowTransitionPhraseMarkerOverrides[transitionKey] = Math.Max(0.0, phraseSeconds);
            if (isExplicit)
            {
                _flowTransitionPhraseMarkerExplicitKeys.Add(transitionKey);
            }
            else
            {
                _flowTransitionPhraseMarkerExplicitKeys.Remove(transitionKey);
            }
        }

        RaiseFlowSelectionProperties();
    }

    internal void ClearFlowTransitionPhraseMarkerOverride(string transitionKey)
    {
        _flowTransitionPhraseMarkerOverrides.Remove(transitionKey);
        _flowTransitionPhraseMarkerExplicitKeys.Remove(transitionKey);
        RaiseFlowSelectionProperties();
    }

    internal void SetFlowTransitionPresetOverride(string transitionKey, string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
        {
            _flowTransitionPresetOverrides.Remove(transitionKey);
        }
        else
        {
            _flowTransitionPresetOverrides[transitionKey] = presetId;
        }

        RaiseFlowSelectionProperties();
    }

    internal void CreateFlowPhraseRegionForTransition(string transitionKey)
    {
        if (string.IsNullOrWhiteSpace(transitionKey) || !TryGetFlowTransition(transitionKey, out var overlay) || overlay == null)
        {
            return;
        }

        _flowPhraseRegionOverrides.TryGetValue(transitionKey, out var previousRegion);
        var defaultSpanSeconds = (60.0 / Math.Max(1.0, overlay.PhraseGuideSeconds > 0 ? 1.0 : 1.0)) * 4.0 * (overlay.PhraseGuideSeconds > 0 ? 1.0 : 1.0);
        // Default span: 4 bars at 120 BPM ≈ 8 s; clamp to transition window
        var spanSeconds = Math.Clamp(8.0, 4.0, Math.Max(8.0, overlay.EndSeconds - overlay.StartSeconds));
        var startSec = Math.Max(overlay.StartSeconds, overlay.PhraseGuideSeconds - spanSeconds / 2.0);
        var endSec = Math.Min(overlay.EndSeconds, startSec + spanSeconds);
        var newRegion = new FlowPhraseRegion(
            transitionKey,
            startSec,
            endSec,
            true,
            1.0,
            null,
            FlowPhraseRegionProvenance.ExplicitUser);
        _undoService.Push(new FlowPhraseRegionOperation(this, transitionKey, previousRegion, newRegion));
        SetFlowPhraseRegionOverride(transitionKey, newRegion);
        RaiseHeaderProperties();
    }

    internal void RemoveFlowPhraseRegionForTransition(string transitionKey)
    {
        if (string.IsNullOrWhiteSpace(transitionKey))
        {
            return;
        }

        _flowPhraseRegionOverrides.TryGetValue(transitionKey, out var previousRegion);
        if (previousRegion == null)
        {
            return;
        }

        _undoService.Push(new FlowPhraseRegionOperation(this, transitionKey, previousRegion, null));
        ClearFlowPhraseRegionOverride(transitionKey);
        RaiseHeaderProperties();
    }

    internal void PreviewFlowPhraseRegionBoundary(string transitionKey, bool isStartHandle, double initialBoundarySeconds, double deltaCanvasPixels)
    {
        if (string.IsNullOrWhiteSpace(transitionKey) || !_flowPhraseRegionOverrides.TryGetValue(transitionKey, out var existing))
        {
            return;
        }

        var deltaSeconds = (deltaCanvasPixels / 700.0) * Math.Max(1.0, TimelineWindowSeconds);
        var newBoundary = Math.Max(0.0, initialBoundarySeconds + deltaSeconds);
        var updated = isStartHandle
            ? new FlowPhraseRegion(transitionKey, newBoundary, existing.EndSeconds, existing.IsExplicit, existing.Confidence, existing.SourceCueIds, existing.Provenance)
            : new FlowPhraseRegion(transitionKey, existing.StartSeconds, newBoundary, existing.IsExplicit, existing.Confidence, existing.SourceCueIds, existing.Provenance);
        SetFlowPhraseRegionOverride(transitionKey, updated);
    }

    internal void CommitFlowPhraseRegionBoundary(string transitionKey, bool isStartHandle, double initialBoundarySeconds, double deltaCanvasPixels)
    {
        if (string.IsNullOrWhiteSpace(transitionKey) || !_flowPhraseRegionOverrides.TryGetValue(transitionKey, out var existing))
        {
            return;
        }

        var deltaSeconds = (deltaCanvasPixels / 700.0) * Math.Max(1.0, TimelineWindowSeconds);
        var newBoundary = Math.Max(0.0, initialBoundarySeconds + deltaSeconds);
        if (Math.Abs(newBoundary - initialBoundarySeconds) < 0.03)
        {
            return;
        }

        // Take snapshot before mutation for undo
        var priorRegion = isStartHandle
            ? new FlowPhraseRegion(transitionKey, initialBoundarySeconds, existing.EndSeconds, existing.IsExplicit, existing.Confidence, existing.SourceCueIds, existing.Provenance)
            : new FlowPhraseRegion(transitionKey, existing.StartSeconds, initialBoundarySeconds, existing.IsExplicit, existing.Confidence, existing.SourceCueIds, existing.Provenance);
        var updated = isStartHandle
            ? new FlowPhraseRegion(transitionKey, newBoundary, existing.EndSeconds, existing.IsExplicit, existing.Confidence, existing.SourceCueIds, existing.Provenance)
            : new FlowPhraseRegion(transitionKey, existing.StartSeconds, newBoundary, existing.IsExplicit, existing.Confidence, existing.SourceCueIds, existing.Provenance);
        _undoService.Push(new FlowPhraseRegionOperation(this, transitionKey, priorRegion, updated));
        SetFlowPhraseRegionOverride(transitionKey, updated);
        RaiseHeaderProperties();
    }

    internal void SetFlowPhraseRegionOverride(string transitionKey, FlowPhraseRegion? region)
    {
        if (string.IsNullOrWhiteSpace(transitionKey))
        {
            return;
        }

        if (region == null)
        {
            _flowPhraseRegionOverrides.Remove(transitionKey);
        }
        else
        {
            _flowPhraseRegionOverrides[transitionKey] = region;
        }

        RaiseFlowSelectionProperties();
    }

    internal void ClearFlowPhraseRegionOverride(string transitionKey)
    {
        _flowPhraseRegionOverrides.Remove(transitionKey);
        RaiseFlowSelectionProperties();
    }

    internal void ClearFlowTransitionLengthOverride(string transitionKey)
    {
        _flowTransitionLengthOverrides.Remove(transitionKey);
        RaiseFlowSelectionProperties();
    }

    private double? GetDefaultFlowTransitionLength(string transitionKey)
    {
        var baseline = BuildFlowTransitions(Decks, TimelineOffsetSeconds, TimelineWindowSeconds, selectedTransitionKey: null, lengthOverrides: null, presetOverrides: null, phraseMarkerOverrides: null, phraseMarkerExplicitKeys: null)
            .FirstOrDefault(t => string.Equals(t.TransitionKey, transitionKey, StringComparison.Ordinal));
        return baseline?.LengthSeconds;
    }

    private sealed class FlowTransitionLengthOperation : IUndoableOperation
    {
        private readonly WorkstationViewModel _owner;
        private readonly string _transitionKey;
        private readonly double _fromLength;
        private readonly double _toLength;

        public FlowTransitionLengthOperation(WorkstationViewModel owner, string transitionKey, double fromLength, double toLength)
        {
            _owner = owner;
            _transitionKey = transitionKey;
            _fromLength = fromLength;
            _toLength = toLength;
        }

        public string Description => "Resize flow transition";

        public void Execute()
        {
            _owner.SetFlowTransitionLengthOverride(_transitionKey, _toLength);
        }

        public void Undo()
        {
            _owner.SetFlowTransitionLengthOverride(_transitionKey, _fromLength);
        }
    }

    private sealed class FlowTransitionPhraseMarkerOperation : IUndoableOperation
    {
        private readonly WorkstationViewModel _owner;
        private readonly string _transitionKey;
        private readonly double _fromPhraseSeconds;
        private readonly bool _fromExplicit;
        private readonly double _toPhraseSeconds;
        private readonly bool _toExplicit;

        public FlowTransitionPhraseMarkerOperation(WorkstationViewModel owner, string transitionKey, double fromPhraseSeconds, bool fromExplicit, double toPhraseSeconds, bool toExplicit)
        {
            _owner = owner;
            _transitionKey = transitionKey;
            _fromPhraseSeconds = fromPhraseSeconds;
            _fromExplicit = fromExplicit;
            _toPhraseSeconds = toPhraseSeconds;
            _toExplicit = toExplicit;
        }

        public string Description => "Edit phrase marker";

        public void Execute()
        {
            _owner.SetFlowTransitionPhraseMarkerState(_transitionKey, _toPhraseSeconds, _toExplicit);
        }

        public void Undo()
        {
            _owner.SetFlowTransitionPhraseMarkerState(_transitionKey, _fromPhraseSeconds, _fromExplicit);
        }
    }

    private sealed class FlowPhraseRegionOperation : IUndoableOperation
    {
        private readonly WorkstationViewModel _owner;
        private readonly string _transitionKey;
        private readonly FlowPhraseRegion? _fromRegion;
        private readonly FlowPhraseRegion? _toRegion;

        public FlowPhraseRegionOperation(WorkstationViewModel owner, string transitionKey, FlowPhraseRegion? fromRegion, FlowPhraseRegion? toRegion)
        {
            _owner = owner;
            _transitionKey = transitionKey;
            _fromRegion = fromRegion;
            _toRegion = toRegion;
        }

        public string Description => "Edit phrase region";

        public void Execute()
        {
            _owner.SetFlowPhraseRegionOverride(_transitionKey, _toRegion);
        }

        public void Undo()
        {
            _owner.SetFlowPhraseRegionOverride(_transitionKey, _fromRegion);
        }
    }

    private sealed class FlowTransitionPresetOperation : IUndoableOperation
    {
        private readonly WorkstationViewModel _owner;
        private readonly string _transitionKey;
        private readonly string? _fromPresetId;
        private readonly string? _toPresetId;
        private readonly double? _fromLengthSeconds;
        private readonly double? _toLengthSeconds;

        public FlowTransitionPresetOperation(WorkstationViewModel owner, string transitionKey, string? fromPresetId, string? toPresetId, double? fromLengthSeconds = null, double? toLengthSeconds = null)
        {
            _owner = owner;
            _transitionKey = transitionKey;
            _fromPresetId = fromPresetId;
            _toPresetId = toPresetId;
            _fromLengthSeconds = fromLengthSeconds;
            _toLengthSeconds = toLengthSeconds;
        }

        public string Description => "Apply flow transition preset";

        public void Execute()
        {
            _owner.SetFlowTransitionPresetOverride(_transitionKey, _toPresetId);
            if (_toLengthSeconds.HasValue)
                _owner.SetFlowTransitionLengthOverride(_transitionKey, _toLengthSeconds.Value);
        }

        public void Undo()
        {
            _owner.SetFlowTransitionPresetOverride(_transitionKey, _fromPresetId);
            if (_fromLengthSeconds.HasValue)
                _owner.SetFlowTransitionLengthOverride(_transitionKey, _fromLengthSeconds.Value);
            else if (_toLengthSeconds.HasValue)
                _owner.ClearFlowTransitionLengthOverride(_transitionKey);
        }
    }

    private static double CamelotDistance(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return 4d;

        var parsedA = ParseCamelot(a);
        var parsedB = ParseCamelot(b);
        if (parsedA == null || parsedB == null)
            return 4d;

        var (numberA, letterA) = parsedA.Value;
        var (numberB, letterB) = parsedB.Value;

        var circleDiff = Math.Abs(numberA - numberB);
        circleDiff = Math.Min(circleDiff, 12 - circleDiff);
        var letterPenalty = letterA == letterB ? 0d : 1d;
        return circleDiff + letterPenalty;
    }

    private static (int number, char letter)? ParseCamelot(string value)
    {
        var trimmed = value.Trim().ToUpperInvariant();
        if (trimmed.Length < 2)
            return null;

        var letter = trimmed[^1];
        if (letter != 'A' && letter != 'B')
            return null;

        if (!int.TryParse(trimmed[..^1], out var number))
            return null;

        if (number is < 1 or > 12)
            return null;

        return (number, letter);
    }

    public static string BuildDeckStatusSummary(int loadedDecks, int totalDecks, string? focusedDeckLabel, double masterBpm)
    {
        var focus = string.IsNullOrWhiteSpace(focusedDeckLabel) ? "Focus —" : $"Focus {focusedDeckLabel}";
        var bpmText = masterBpm > 0 ? $"{masterBpm.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} BPM" : "Master BPM pending";
        return $"{loadedDecks}/{Math.Max(1, totalDecks)} decks live • {focus} • {bpmText}";
    }

    public static IReadOnlyList<PlaylistFlowTransitionViewModel> BuildPlaylistFlowTransitions(
        IEnumerable<PlaylistTrack> tracks,
        double timelineOffsetSeconds,
        double timelineWindowSeconds)
    {
        const double canvasWidth = 700.0;
        const double defaultDurationSeconds = 180.0;
        const double transitionWindowSeconds = 8.0;

        var result = new List<PlaylistFlowTransitionViewModel>();

        if (timelineWindowSeconds <= 0)
        {
            return result;
        }

        var ordered = (tracks ?? Enumerable.Empty<PlaylistTrack>())
            .OrderBy(t => t.TrackNumber)
            .ThenBy(t => t.SortOrder)
            .ToList();

        if (ordered.Count < 2)
        {
            return result;
        }

        // Build cumulative start positions from durations
        var positions = new double[ordered.Count];
        positions[0] = 0;
        for (var i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1];
            var dur = prev.CanonicalDuration.HasValue
                ? Math.Max(30.0, prev.CanonicalDuration.Value / 1000.0)
                : defaultDurationSeconds;
            positions[i] = positions[i - 1] + dur;
        }

        for (var i = 0; i < ordered.Count - 1; i++)
        {
            var source = ordered[i];
            var target = ordered[i + 1];

            var transitionPoint = positions[i + 1];
            var blockStart = transitionPoint - transitionWindowSeconds;
            var blockEnd = transitionPoint + transitionWindowSeconds * 0.5;

            var left = ToCanvasX(blockStart, timelineOffsetSeconds, timelineWindowSeconds, canvasWidth);
            var right = ToCanvasX(blockEnd, timelineOffsetSeconds, timelineWindowSeconds, canvasWidth);
            var blockWidth = Math.Max(4.0, right - left);

            var sourceEnergy = source.Energy ?? EstimateNormalizedEnergyFromBpm(source.BPM ?? 0);
            var targetEnergy = target.Energy ?? EstimateNormalizedEnergyFromBpm(target.BPM ?? 0);
            var harmonicScore = ComputeFlowHarmonicCompatibility(source.MusicalKey, target.MusicalKey, semitoneShift: 0);
            var energyScore = ComputeFlowEnergyCompatibility(sourceEnergy, targetEnergy, transitionWindowSeconds);
            var combined = ComputeCombinedFlowCompatibilityScore(harmonicScore.Score, energyScore.Score);

            var sourceTitle = !string.IsNullOrWhiteSpace(source.Title) ? source.Title : $"Track {i + 1}";
            var targetTitle = !string.IsNullOrWhiteSpace(target.Title) ? target.Title : $"Track {i + 2}";
            var key = $"pl:{source.Id}->{target.Id}";

            result.Add(new PlaylistFlowTransitionViewModel(
                key,
                $"{sourceTitle} → {targetTitle}",
                left,
                blockWidth,
                harmonicScore.Score,
                harmonicScore.Label,
                energyScore.Score,
                energyScore.Label,
                combined,
                i));
        }

        return result;
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
        var mode = activeMode switch
        {
            WorkstationMode.Flow => "set plan active",
            _ => "prep active"
        };

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
            WorkstationMode.Flow => "Set Plan",
            _ => "Prep"
        };

        return $"{modeLabel} mode • Snap {(snapEnabled ? "on" : "off")} • Quantize {(quantizeEnabled ? "on" : "off")} • F1 shortcuts";
    }

    public static string BuildActiveToolSummary(string? toolName, string? statusText)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return "Cockpit tools • select a workstation tool to change inspector and overlay context";
        }

        return $"Cockpit tool • {toolName} • {statusText}";
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

    public static string BuildGlobalTransportSummary(bool isPlaying, int loadedDecks)
    {
        var state = isPlaying ? "Live transport" : "Transport cued";
        var deckText = $"{Math.Max(0, loadedDecks)} deck{(loadedDecks == 1 ? string.Empty : "s")} {(isPlaying ? "rolling" : "ready")}";
        return $"{state} • {deckText}";
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
        RefreshExportInspector();
        this.RaisePropertyChanged(nameof(DeckStatusSummary));
        this.RaisePropertyChanged(nameof(DeckFocusSummary));
        this.RaisePropertyChanged(nameof(ActivePlaylistFlowSummary));
        this.RaisePropertyChanged(nameof(HasLoadedDecks));
        this.RaisePropertyChanged(nameof(AnalysisQueueSummary));
        this.RaisePropertyChanged(nameof(ToolbarHint));
        this.RaisePropertyChanged(nameof(FlowWindowSummary));
        this.RaisePropertyChanged(nameof(FlowTransitions));
        this.RaisePropertyChanged(nameof(HasFlowTransitions));
        this.RaisePropertyChanged(nameof(IsFlowOverlayVisible));
        this.RaisePropertyChanged(nameof(FlowOverlayHint));
        this.RaisePropertyChanged(nameof(FlowPlaylistTransitions));
        this.RaisePropertyChanged(nameof(HasFlowPlaylistTransitions));
        this.RaisePropertyChanged(nameof(IsFlowPlaylistOverlayVisible));
        RaiseFlowSelectionProperties();
        this.RaisePropertyChanged(nameof(GlobalTransportSummary));
        this.RaisePropertyChanged(nameof(TransportStatusSummary));
        this.RaisePropertyChanged(nameof(FocusedDeckActionSummary));
        this.RaisePropertyChanged(nameof(MixCoachSummary));
        this.RaisePropertyChanged(nameof(ActiveToolOption));
        this.RaisePropertyChanged(nameof(ActiveToolSummary));
        RaiseLaneActionProperties();
    }

    private void RaiseLaneActionProperties()
    {
        this.RaisePropertyChanged(nameof(HasActivePlaylist));
        this.RaisePropertyChanged(nameof(HasReadyPlaylistTracks));
        this.RaisePropertyChanged(nameof(HasLoadedDecks));
        this.RaisePropertyChanged(nameof(ShouldShowSelectPlaylistCta));
        this.RaisePropertyChanged(nameof(ShouldShowAcquireCtas));
        this.RaisePropertyChanged(nameof(ShouldShowDownloadTracksCta));
        this.RaisePropertyChanged(nameof(ShouldShowImportLocalFilesCta));
        this.RaisePropertyChanged(nameof(ShouldShowReadyTrackCtas));
        this.RaisePropertyChanged(nameof(FlowCtaStateSummary));
        this.RaisePropertyChanged(nameof(TimelineEmptyCanvasSummary));
        this.RaisePropertyChanged(nameof(FlowLaneReadinessSummary));
        this.RaisePropertyChanged(nameof(FlowTransitions));
        this.RaisePropertyChanged(nameof(HasFlowTransitions));
        this.RaisePropertyChanged(nameof(IsFlowOverlayVisible));
        this.RaisePropertyChanged(nameof(FlowOverlayHint));
        this.RaisePropertyChanged(nameof(FlowPlaylistTransitions));
        this.RaisePropertyChanged(nameof(HasFlowPlaylistTransitions));
        this.RaisePropertyChanged(nameof(IsFlowPlaylistOverlayVisible));
        RaiseFlowSelectionProperties();
        this.RaisePropertyChanged(nameof(SelectPlaylistCtaHint));
        this.RaisePropertyChanged(nameof(DownloadTracksCtaHint));
        this.RaisePropertyChanged(nameof(ImportLocalFilesCtaHint));
        this.RaisePropertyChanged(nameof(LoadIntoWorkstationCtaHint));
        this.RaisePropertyChanged(nameof(CanAnalyzePlaylistFromLane));
        this.RaisePropertyChanged(nameof(CanOpenTrackOverlayFromLane));
        this.RaisePropertyChanged(nameof(CanUseFocusedDeckLaneActions));
        this.RaisePropertyChanged(nameof(CanUseExportLaneActions));
        this.RaisePropertyChanged(nameof(FlowLaneAnalyzeHint));
        this.RaisePropertyChanged(nameof(FlowLaneOverlayHint));
        this.RaisePropertyChanged(nameof(StemsLaneActionHint));
        this.RaisePropertyChanged(nameof(ExportLaneActionHint));
    }

    private void RefreshExportInspector()
    {
        ExportPanel.SetDecks(Decks);
    }

    private void SynchronizeToolSelection()
    {
        foreach (var option in ToolOptions)
        {
            option.IsSelected = option.Mode == ActiveMode;
        }

        this.RaisePropertyChanged(nameof(ActiveToolOption));
        this.RaisePropertyChanged(nameof(ActiveToolSummary));
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
        this.RaisePropertyChanged(nameof(FlowTransitions));
        this.RaisePropertyChanged(nameof(HasFlowTransitions));
        this.RaisePropertyChanged(nameof(IsFlowOverlayVisible));
        this.RaisePropertyChanged(nameof(FlowOverlayHint));
        RaiseFlowSelectionProperties();
    }

    private void RaiseFlowSelectionProperties()
    {
        this.RaisePropertyChanged(nameof(FlowTransitions));
        this.RaisePropertyChanged(nameof(SelectedFlowTransition));
        this.RaisePropertyChanged(nameof(HasSelectedFlowTransition));
        this.RaisePropertyChanged(nameof(FlowInspectorTransitionLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorCompatibilityLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorPhraseAlignment));
        this.RaisePropertyChanged(nameof(FlowInspectorBeatAlignment));
        this.RaisePropertyChanged(nameof(FlowInspectorLengthLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorSnapLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorPhraseMarkerLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorPhraseRegionCount));
        this.RaisePropertyChanged(nameof(FlowInspectorPhraseRegionSpanSeconds));
        this.RaisePropertyChanged(nameof(FlowInspectorPhraseRegionProvenanceLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorPhraseRegionSpanLabel));
        this.RaisePropertyChanged(nameof(FlowTransitionPresetCatalog));
        this.RaisePropertyChanged(nameof(FlowInspectorPresetLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorSuggestedPresetsLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorHarmonicScoreLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorEnergyScoreLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorCombinedScoreLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorWarningLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorTopSuggestedPresetId));
        this.RaisePropertyChanged(nameof(FlowInspectorCurveLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorBandStrategyLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorHasWarnings));
        this.RaisePropertyChanged(nameof(FlowInspectorScoreRow));
        this.RaisePropertyChanged(nameof(FlowInspectorAlignmentRow));
        this.RaisePropertyChanged(nameof(FlowInspectorCurveDetailRow));
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
                ActivePlaylistId    = ActivePlaylist?.Id,
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
        catch (Exception ex)
        {
            // Never crash the UI thread over a save failure, but a silent failure here means the
            // user's deck/timeline session is lost on next launch with zero diagnostic trail.
            _logger?.LogWarning(ex, "Failed to save workstation session");
        }
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
                // Sessions saved before the 6→2 mode collapse may carry indexes 2-5 (Stems/Export/
                // Automation/Samples) — fold anything out of range back to Prep.
                ActiveMode            = session.ActiveModeIndex == (int)WorkstationMode.Flow
                    ? WorkstationMode.Flow
                    : WorkstationMode.Waveform;
                _pendingActivePlaylistId = session.ActivePlaylistId;
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

            if (Playlists.Count == 0)
            {
                ActivePlaylist = null;
                return;
            }

            Guid? desiredId = _pendingActivePlaylistId;
            if (!desiredId.HasValue && Guid.TryParse(_appConfig.WorkstationActivePlaylistId, out var configuredId))
            {
                desiredId = configuredId;
            }

            if (desiredId.HasValue)
            {
                var matched = Playlists.FirstOrDefault(p => p.Id == desiredId.Value);
                if (matched != null)
                {
                    ActivePlaylist = matched;
                    _pendingActivePlaylistId = null;
                    return;
                }
            }

            if (ActivePlaylist == null)
            {
                ActivePlaylist = Playlists[0];
            }
        });
    }

    private async Task LoadPlaylistTracksAsync(PlaylistJob job)
    {
        var tracks = await _library.GetPagedPlaylistTracksAsync(
            job.Id, skip: 0, take: 1000);

        var readyTracks = tracks
            .Where(WorkstationDeckViewModel.IsTrackReadyForWorkstation)
            .ToList();

        var hiddenTracks = tracks
            .Where(track => !WorkstationDeckViewModel.IsTrackReadyForWorkstation(track))
            .ToList();

        var hiddenCount = Math.Max(0, tracks.Count - readyTracks.Count);
        var hiddenBreakdown = BuildHiddenEligibilityBreakdown(hiddenTracks);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _lastHiddenTracks = hiddenTracks;
            PlaylistTracks.Clear();
            foreach (var t in readyTracks) PlaylistTracks.Add(t);
            this.RaisePropertyChanged(nameof(ActivePlaylistFlowSummary));
            HiddenEligibilityBreakdown = hiddenBreakdown;
            this.RaisePropertyChanged(nameof(IncompleteAnalysisTrackCount));
            this.RaisePropertyChanged(nameof(HasIncompleteAnalysisTracks));
            this.RaisePropertyChanged(nameof(IncompleteAnalysisSummary));

            AnalysisStatusText = readyTracks.Count == 0
                ? "No ready tracks in this playlist. Next: open Track List, download missing tracks, then run Analyze Playlist."
                : hiddenCount > 0
                    ? $"{readyTracks.Count} workstation-ready tracks shown • {hiddenCount} hidden (missing local file, waveform, cues, or library hash)."
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

    private static string BuildHiddenEligibilityBreakdown(IReadOnlyCollection<PlaylistTrack> hiddenTracks)
    {
        if (hiddenTracks.Count == 0)
        {
            return string.Empty;
        }

        var missingDownload = 0;
        var missingFile = 0;
        var missingHash = 0;
        var missingWaveform = 0;
        var missingCues = 0;
        var other = 0;

        foreach (var track in hiddenTracks)
        {
            switch (WorkstationDeckViewModel.GetTrackEligibilityIssue(track))
            {
                case WorkstationTrackEligibilityIssue.NotDownloaded:
                    missingDownload++;
                    break;
                case WorkstationTrackEligibilityIssue.MissingFile:
                    missingFile++;
                    break;
                case WorkstationTrackEligibilityIssue.MissingHash:
                    missingHash++;
                    break;
                case WorkstationTrackEligibilityIssue.MissingWaveform:
                    missingWaveform++;
                    break;
                case WorkstationTrackEligibilityIssue.MissingCues:
                    missingCues++;
                    break;
                case WorkstationTrackEligibilityIssue.MissingAnalysis:
                case WorkstationTrackEligibilityIssue.NoTrack:
                    other++;
                    break;
            }
        }

        var segments = new List<string>();
        if (missingDownload > 0) segments.Add($"{missingDownload} not downloaded");
        if (missingFile > 0) segments.Add($"{missingFile} missing file");
        if (missingHash > 0) segments.Add($"{missingHash} missing hash");
        if (missingWaveform > 0) segments.Add($"{missingWaveform} missing waveform");
        if (missingCues > 0) segments.Add($"{missingCues} missing cues");
        if (other > 0) segments.Add($"{other} other prep issue");

        return segments.Count == 0
            ? string.Empty
            : $"Hidden breakdown: {string.Join(" • ", segments)}.";
    }

    private static bool IsReanalysisCandidate(PlaylistTrack track)
    {
        return WorkstationDeckViewModel.GetTrackEligibilityIssue(track) switch
        {
            WorkstationTrackEligibilityIssue.MissingWaveform => true,
            WorkstationTrackEligibilityIssue.MissingCues => true,
            WorkstationTrackEligibilityIssue.MissingAnalysis => true,
            _ => false,
        };
    }

    private async Task QueueIncompleteTracksForReanalysisAsync()
    {
        if (ActivePlaylist == null)
        {
            AnalysisStatusText = "Select a playlist before queuing incomplete tracks for reanalysis.";
            return;
        }

        var sourceHiddenTracks = _lastHiddenTracks;
        if (sourceHiddenTracks.Count == 0)
        {
            var allTracks = await _library.GetPagedPlaylistTracksAsync(ActivePlaylist.Id, skip: 0, take: 1000);
            sourceHiddenTracks = allTracks
                .Where(track => !WorkstationDeckViewModel.IsTrackReadyForWorkstation(track))
                .ToList();
            _lastHiddenTracks = sourceHiddenTracks;
            this.RaisePropertyChanged(nameof(IncompleteAnalysisTrackCount));
            this.RaisePropertyChanged(nameof(HasIncompleteAnalysisTracks));
            this.RaisePropertyChanged(nameof(IncompleteAnalysisSummary));
        }

        var candidates = sourceHiddenTracks
            .Where(IsReanalysisCandidate)
            .Where(track => !string.IsNullOrWhiteSpace(track.TrackUniqueHash))
            .ToList();

        foreach (var track in candidates)
        {
            _eventBus.Publish(new TrackAnalysisRequestedEvent(track.TrackUniqueHash));
        }

        AnalysisStatusText = candidates.Count == 0
            ? "No incomplete tracks are currently eligible for reanalysis queueing."
            : $"Queued {candidates.Count} incomplete track(s) for full reanalysis.";
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

        // The stem rack lives inside the Prep workspace now, so both paths land on Prep.
        ActiveMode = WorkstationMode.Waveform;

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

    private async Task ExportRekordboxAsync()
    {
        var playlist = ActivePlaylist;
        if (playlist == null) return;

        if (_playlistExporter == null)
        {
            AnalysisStatusText = "Rekordbox export unavailable (export service not initialized).";
            return;
        }

        try
        {
            AnalysisStatusText = $"Exporting \"{playlist.SourceTitle}\" to Rekordbox XML…";

            // Export the playlist's saved order, not just the workstation-ready subset —
            // the handoff to DJ software should carry the whole crate.
            var tracks = await _library.LoadPlaylistTracksAsync(playlist.Id);
            var ordered = tracks.OrderBy(t => t.SortOrder).ThenBy(t => t.TrackNumber).ToList();

            var safeName = string.Join("_", playlist.SourceTitle.Split(System.IO.Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (safeName.Length == 0) safeName = "orbit-playlist";
            var outputPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                $"{safeName}-rekordbox-{DateTime.Now:yyyyMMdd-HHmmss}.xml");

            await _playlistExporter.ExportToRekordboxXmlAsync(playlist.SourceTitle, ordered, outputPath);

            AnalysisStatusText = $"Rekordbox XML exported with cues → {outputPath}";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Rekordbox export failed for playlist {Playlist}", playlist.SourceTitle);
            AnalysisStatusText = $"Rekordbox export failed: {ex.Message}";
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

    private async Task ExportOrbSessionAsync()
    {
        var owner = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lt
            ? lt.MainWindow : null;
        if (owner == null) return;

        var dialog = new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save Orbit Session Bundle",
            SuggestedFileName = $"orbit-session-{DateTime.Now:yyyyMMdd-HHmm}.orbsession",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("Orbit Session Bundle")
                {
                    Patterns = ["*.orbsession"],
                },
            ],
        };

        var file = await owner.StorageProvider.SaveFilePickerAsync(dialog);
        if (file == null) return;

        var session = new Models.Stem.WorkstationSession
        {
            ActiveModeIndex       = (int)ActiveMode,
            ActivePlaylistId      = ActivePlaylist?.Id,
            TimelineOffsetSeconds = TimelineOffsetSeconds,
            TimelineWindowSeconds = TimelineWindowSeconds,
        };
        foreach (var deck in Decks)
        {
            session.Decks.Add(new Models.Stem.WorkstationDeckState
            {
                DeckLabel        = deck.DeckLabel,
                FilePath         = deck.Deck.LoadedFilePath,
                TrackUniqueHash  = deck.TrackHash,
                TrackTitle       = deck.TrackTitle,
                TrackArtist      = deck.TrackArtist,
                Bpm              = deck.DisplayBpm,
                Key              = deck.TrackKey,
                PositionSeconds  = deck.Deck.Engine.PositionSeconds,
            });
        }

        await _orbBundleService.ExportAsync(session, PlaylistTracks.ToList(), file.Path.LocalPath);
    }

    public void Dispose()
    {
        // Synchronously block for a brief window so the session is flushed
        // even when the OS terminates the process after the window closes.
        // Bounded rather than unconditional — SaveSessionAsync already catches its own
        // exceptions, but a stuck DB write (lock contention, disk stall) shouldn't be able to
        // hang the whole window-close indefinitely.
        if (!SaveSessionAsync().Wait(TimeSpan.FromSeconds(5)))
        {
            _logger?.LogWarning("Workstation session save timed out during close — session may not have flushed");
        }
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
