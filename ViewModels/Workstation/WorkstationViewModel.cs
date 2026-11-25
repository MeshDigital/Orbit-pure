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
using SLSKDONET.Services.Similarity;
using SLSKDONET.Views;
using SLSKDONET.Utils;

namespace SLSKDONET.ViewModels.Workstation;

/// <summary>
/// Root ViewModel for the Workstation page — modelled after the DJ.Studio layout.
/// Manages decks, timeline position, global BPM, and the active playlist.
/// </summary>
public sealed partial class WorkstationViewModel : ReactiveObject, IDisposable
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
    private readonly IDialogService? _dialogService;
    private readonly INotificationService? _notificationService;
    private readonly IFileInteractionService? _fileInteractionService;
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
            if (ReferenceEquals(_activePlaylist, value)) return;
            this.RaiseAndSetIfChanged(ref _activePlaylist, value);
            this.RaisePropertyChanged(nameof(ActivePlaylistFlowSummary));
            _appConfig.WorkstationActivePlaylistId = value?.Id.ToString();
            _ = _configManager.SaveAsync(_appConfig);
            RaiseLaneActionProperties();
            if (value != null)
            {
                _ = LoadPlaylistTracksSafelyAsync(value);
            }
            else
            {
                PlaylistTracks.Clear();
                _lastHiddenTracks = new();
            }
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

    public string MasterBpmDisplay => MasterBpm > 0 ? NumFormat.Ui($"{MasterBpm:F1}") : "—";
    public string DeckStatusSummary => BuildDeckStatusSummary(Decks.Count(d => d.IsLoaded), Decks.Count, FocusedDeck?.DeckLabel, MasterBpm);
    public string DeckFocusSummary => BuildDeckFocusSummary(Decks.Select(d => d.DeckLabel), Decks.Where(d => d.IsLoaded).Select(d => d.DeckLabel), FocusedDeck?.DeckLabel);
    public string ActivePlaylistFlowSummary => BuildPlaylistFlowSummary(ActivePlaylist?.SourceTitle, ReadyPlaylistTrackCount, Decks.Count(d => d.IsLoaded), ActiveMode);
    public string WorkstationEligibilitySummary => "Every playlist track stays visible. Double-click a ready row to load it or a flagged row to repair missing files and analysis in place.";
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
    public int ReadyPlaylistTrackCount => PlaylistTracks.Count(WorkstationDeckViewModel.IsTrackReadyForWorkstation);
    public int PlaylistAttentionTrackCount => Math.Max(0, PlaylistTracks.Count - ReadyPlaylistTrackCount);
    public bool HasPlaylistAttention => PlaylistAttentionTrackCount > 0;
    public int PlaylistFileIssueCount => PlaylistTracks.Count(track =>
        WorkstationDeckViewModel.GetTrackEligibilityIssue(track) is
            WorkstationTrackEligibilityIssue.NoTrack or
            WorkstationTrackEligibilityIssue.NotDownloaded or
            WorkstationTrackEligibilityIssue.MissingFile);
    public int PlaylistPrepOnlyIssueCount => PlaylistTracks.Count(track =>
        WorkstationDeckViewModel.GetTrackEligibilityIssue(track) is
            WorkstationTrackEligibilityIssue.MissingHash or
            WorkstationTrackEligibilityIssue.MissingWaveform or
            WorkstationTrackEligibilityIssue.MissingCues or
            WorkstationTrackEligibilityIssue.MissingAnalysis);
    public bool IsPlaylistReadyForExport => HasActivePlaylist && PlaylistTracks.Count > 0 && !HasPlaylistAttention;
    public string AnalysisWorkflowStatus => IsAnalyzing
        ? $"Analyzing {AnalysisProgress}%"
        : IncompleteAnalysisTrackCount > 0
            ? $"{IncompleteAnalysisTrackCount} need analysis"
            : "Analysis checked";
    public string PreparationWorkflowStatus => HasLoadedDecks
        ? "Cues & grids in prep"
        : ReadyPlaylistTrackCount > 0
            ? $"{ReadyPlaylistTrackCount} ready to prep"
            : "Repair tracks first";
    public string ExportWorkflowStatus => !HasActivePlaylist
        ? "Select a playlist"
        : PlaylistTracks.Count == 0
            ? "Playlist is empty"
            : PlaylistFileIssueCount > 0
                ? $"{PlaylistFileIssueCount} need file repair"
                : PlaylistPrepOnlyIssueCount > 0
                    ? $"{PlaylistPrepOnlyIssueCount} prep warnings"
                    : "Rekordbox prep complete";
    public bool HasReadyPlaylistTracks => ReadyPlaylistTrackCount > 0;
    public bool HasLoadedDecks => Decks.Any(d => d.IsLoaded);
    public bool ShouldShowSelectPlaylistCta => !HasActivePlaylist;
    public bool ShouldShowAcquireCtas => HasActivePlaylist && !HasReadyPlaylistTracks;
    public bool ShouldShowDownloadTracksCta => HasActivePlaylist && !HasReadyPlaylistTracks;
    public bool ShouldShowImportLocalFilesCta => !HasReadyPlaylistTracks;
    public bool ShouldShowReadyTrackCtas => HasActivePlaylist;
    public string FlowCtaStateSummary => HasReadyPlaylistTracks
        ? "Load ready rows into decks. Export reports missing files separately from incomplete cue preparation."
        : HasActivePlaylist
            ? "Repair the flagged rows here so every track is ready for professional DJ preparation."
            : "Select a playlist to verify files, analysis, beat grids, cues and loops.";
    public string TimelineEmptyCanvasSummary => HasReadyPlaylistTracks
        ? "Deck workspace is ready. Load the first ready track from the Prep Playlist to inspect and prepare it."
        : HasActivePlaylist
            ? "Deck workspace is ready. Acquire or import tracks from the Prep Playlist, then load one for preparation."
            : "Deck workspace is ready. Select a playlist in the Prep Playlist to begin professional DJ preparation.";
    public string FlowLaneReadinessSummary => HasReadyPlaylistTracks
        ? $"{ReadyPlaylistTrackCount} ready track{(ReadyPlaylistTrackCount == 1 ? string.Empty : "s")} • {AnalysisQueueSummary}"
        : FlowCtaStateSummary;
    public string SelectPlaylistCtaHint => "Open and focus the playlist selector in the prep drawer.";
    public string DownloadTracksCtaHint => HasActivePlaylist
        ? "Open Downloads to fetch missing tracks for the active playlist."
        : "Select a playlist first, then download its missing tracks.";
    public string ImportLocalFilesCtaHint => "Open Import to add local files and make them available for professional DJ preparation.";
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

    // ── Tap Tempo ─────────────────────────────────────────────────────────────

    private readonly Services.AudioAnalysis.TapTempoTracker _tapTempo = new();
    public ReactiveCommand<Unit, Unit> TapTempoCommand { get; }

    // ── Active mode ───────────────────────────────────────────────────────────

    private WorkstationMode _activeMode = WorkstationMode.Waveform;
    public WorkstationMode ActiveMode
    {
        get => _activeMode;
        set
        {
            // Workstation is track prep only. Legacy Flow requests fold back to Prep (in-app MIX
            // lives in Flow Builder); StoryGraph and TransitionLab are kept as enum names for
            // session compatibility but now live elsewhere: sets on the Sets page, transitions in
            // the shared transition editor (SETS_AND_TRANSITIONS_REBUILD_PLAN P2.10 / P3).
            if (value is WorkstationMode.Flow or WorkstationMode.StoryGraph or WorkstationMode.TransitionLab)
                value = WorkstationMode.Waveform;
            if (_activeMode == value) return;
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
    /// Export configuration used to gate header actions (e.g. <see cref="CanUseExportLaneActions"/>).
    /// <see cref="ExportMixCommand"/> constructs its own separate <c>ExportDialogViewModel</c> for
    /// the popup dialog rather than reusing this instance.
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
    /// <summary>Runs the appropriate local repair or acquisition route for one track row.</summary>
    public ReactiveCommand<PlaylistTrack, Unit> RepairTrackCommand { get; }
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
    /// <summary>Compatibility command for callers that still request a legacy workstation mode.</summary>
    public ReactiveCommand<WorkstationMode, Unit> SetModeCommand      { get; }
    /// <summary>Open the active playlist in the separate in-app MIX transition workspace.</summary>
    public ReactiveCommand<Unit, Unit> OpenFlowBuilderCommand { get; }
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
        private set
        {
            this.RaiseAndSetIfChanged(ref _analysisProgress, value);
            this.RaisePropertyChanged(nameof(AnalysisWorkflowStatus));
        }
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

    /// <summary>Whether another deck can be added (max 4, mirrors AddDeckCommand's own guard).</summary>
    public bool CanAddDeck => Decks.Count < 4;

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
        SLSKDONET.Services.Library.PlaylistExportService? playlistExporter = null,
        IDialogService? dialogService = null,
        INotificationService? notificationService = null,
        IFileInteractionService? fileInteractionService = null)
    {
        _logger = logger;
        _playlistExporter = playlistExporter;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _fileInteractionService = fileInteractionService;
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

        ToolOptions.Add(new WorkstationToolOptionViewModel("Prep Tracks", "Prep Tracks", "Decks, waveforms, hot cues, loops, and stems — repair and prepare each track.", WorkstationMode.Waveform, isAvailable: true));
        SynchronizeToolSelection();

        ExportPanel = new ExportDialogViewModel(mixdown);
        PlaylistTracks.CollectionChanged += (_, _) => RaiseLaneActionProperties();
        ExportPanel.Decks.CollectionChanged += (_, _) => RaiseLaneActionProperties();
        ExportPanel.WhenAnyValue(x => x.IsExporting)
            .Subscribe(_ => RaiseLaneActionProperties())
            .DisposeWith(_disposables);

        // Wrap existing DeckA / DeckB
        var deckA = new WorkstationDeckViewModel("A", deckPair.DeckA, stemSeparator, cueService, stemPrefService, _dbFactory, _dialogService, _notificationService);
        var deckB = new WorkstationDeckViewModel("B", deckPair.DeckB, stemSeparator, cueService, stemPrefService, _dbFactory, _dialogService, _notificationService);
        deckA.OnTrackLoaded = async () => { RefreshDeckTransitionGuidance(); RaiseHeaderProperties(); await SaveSessionAsync(); };
        deckB.OnTrackLoaded = async () => { RefreshDeckTransitionGuidance(); RaiseHeaderProperties(); await SaveSessionAsync(); };
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
            var newDeck = new WorkstationDeckViewModel(label, slot, _stemSeparator, _cueService, _stemPrefService, _dbFactory, _dialogService, _notificationService);
            newDeck.OnTrackLoaded = async () => { RefreshDeckTransitionGuidance(); RaiseHeaderProperties(); await SaveSessionAsync(); };
            newDeck.OnDeckStateChanged = RefreshDeckTransitionGuidance;
            Decks.Add(newDeck);
            newDeck.UpdateWaveformViewport(TimelineWindowSeconds, TimelineOffsetSeconds);
            RefreshDeckTransitionGuidance();
            this.RaisePropertyChanged(nameof(MaxTimelineOffsetSeconds));
            this.RaisePropertyChanged(nameof(CanAddDeck));
            RaiseHeaderProperties();
        });

        RemoveDeckCommand = ReactiveCommand.CreateFromTask<WorkstationDeckViewModel>(async deck =>
        {
            if (Decks.Count <= 1) return;

            // Only nag when there's actually something to lose — an empty/unused deck slot is
            // safe to remove instantly, matching how other destructive confirms in this codebase
            // (e.g. Force Redownload) only gate on there being real state at stake.
            if (deck.IsLoaded && _dialogService != null)
            {
                var confirmed = await _dialogService.ConfirmAsync(
                    "Remove Deck",
                    $"Deck {deck.DeckLabel} has a loaded track with its cues/loop state. Removing it discards that state — this can't be undone. Continue?",
                    confirmLabel: "Remove Deck",
                    cancelLabel: "Cancel");
                if (!confirmed) return;
            }

            Decks.Remove(deck);
            deck.Dispose();
            FocusedDeck = Decks.FirstOrDefault();
            RefreshDeckTransitionGuidance();
            this.RaisePropertyChanged(nameof(MaxTimelineOffsetSeconds));
            this.RaisePropertyChanged(nameof(CanAddDeck));
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

        RepairTrackCommand = ReactiveCommand.CreateFromTask<PlaylistTrack>(RepairTrackAsync);

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
            var tapped = _tapTempo.Tap(DateTime.UtcNow);
            if (tapped is { } bpm && bpm > 0)
                MasterBpm = Math.Round(bpm, 1);
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
            _ = SaveSessionAsync();
        });

        OpenFlowBuilderCommand = ReactiveCommand.Create(() =>
        {
            _eventBus.Publish(new OpenFlowBuilderForPlaylistEvent(ActivePlaylist?.Id ?? Guid.Empty));
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

}
