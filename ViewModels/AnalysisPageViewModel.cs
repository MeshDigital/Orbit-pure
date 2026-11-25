using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Library;
using SLSKDONET.Utils;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Processing state for the analysis queue.
/// </summary>
public enum AnalysisProcessingState
{
    Idle,
    Processing,
    Completed,
    Error
}

public enum AnalysisPaneMode
{
    Library,
    Queue
}


/// <summary>
/// ViewModel for the Analysis page.
/// Manages the library track list, the analysis queue and processing state. Sample data is only
/// used when no library service exists (designer/tests), never as a stand-in for a failed load.
/// </summary>
public partial class AnalysisPageViewModel : ReactiveObject, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly ILibraryService? _libraryService;
    private readonly ILifecycleProjectionService _lifecycleProjectionService;
    private readonly IClipboardService? _clipboardService;
    private readonly ILibraryCorruptionScanService? _corruptionScanService;
    private readonly ICorruptFileRemediationService? _remediationService;
    private readonly IDialogService? _dialogService;
    private readonly AnalyzeTrackStructureJob? _cueStructureJob;
    private readonly AppConfig? _config;
    private readonly ConfigManager? _configManager;
    private readonly CompositeDisposable _disposables = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _analysisSessionStopwatch = new();
    private readonly Dictionary<string, DateTime> _trackAnalysisStartedAtUtc = new(StringComparer.Ordinal);
    // Hashes whose library entry is being looked up after a TrackAnalysisRequestedEvent for a
    // track this page hasn't loaded yet (see OnTrackAnalysisRequested) — dedupes repeat requests.
    private readonly HashSet<string> _pendingQueueResolutions = new(StringComparer.Ordinal);
    private readonly System.Reactive.Subjects.Subject<System.Reactive.Unit> _refreshRequestSubject = new();
    private readonly System.Reactive.Subjects.Subject<string?> _filterRequestSubject = new();

    private AnalysisProcessingState _processingState = AnalysisProcessingState.Idle;
    private string? _currentProcessingTrackId;
    private string _searchText = string.Empty;
    private bool _isCompactLayout;
    private AnalysisPaneMode _activePaneMode = AnalysisPaneMode.Queue;
    private int _completedAnalysisRuns;
    private double _totalAnalysisSeconds;
    private double _lastFilterMilliseconds;
    private double _averageFilterMilliseconds;
    private int _filterSampleCount;
    private int _filteredTrackCount;
    private double _lastQueueMutationMilliseconds;
    private string? _performanceProbeSummary;
    private int _indexedCatalogCount;
    private int _staleIndexedCount;
    private int _desiredDownloadCount;
    private int _ingestionBacklogCount;
    private int _onDiskIndexedTrackCount;
    private bool _isIntegrityScanRunning;
    private int _integrityScanProgress;
    private string _integrityScanStatusText = string.Empty;
    private bool _isRemediating;
    private string _remediationStatusText = string.Empty;
    private IReadOnlyList<CorruptFileScanEntry> _scanIssues = Array.Empty<CorruptFileScanEntry>();

    // ── Collections ─────────────────────────────────────────────────────────────

    /// <summary>All tracks available in the library (source-of-truth list).</summary>
    public ObservableCollection<AnalysisTrackItem> LibraryTracks { get; } = new();

    /// <summary>Filtered view of LibraryTracks based on the search box.</summary>
    public ObservableCollection<AnalysisTrackItem> FilteredLibraryTracks { get; } = new();

    /// <summary>Tracks staged for analysis by the user.</summary>
    public ObservableCollection<AnalysisTrackItem> AnalysisQueue { get; } = new();
    public ObservableCollection<PerformanceProbeRun> PerformanceProbeRuns { get; } = new();
    public ObservableCollection<AnalysisTrackItem> PlaylistTracks { get; } = new();
    public AutomixConstraints AutomixConstraints { get; } = new();

    /// <summary>Option labels for the per-run BPM/key backend ComboBox. Member names of the
    /// <see cref="SLSKDONET.Models.AnalysisAudioEngine"/> enum.</summary>
    public static string[] AvailableAnalysisAudioEngines { get; } = { "Essentia", "InProcess" };

    /// <summary>Which backend produces BPM, key and the beat grid for the next analysis run.
    /// Mirrors the Settings value; editing it here persists it too.</summary>
    public string AnalysisAudioEngine
    {
        get => _config?.AnalysisAudioEngine ?? "Essentia";
        set
        {
            if (_config is not null && !string.Equals(_config.AnalysisAudioEngine, value, StringComparison.Ordinal))
            {
                _config.AnalysisAudioEngine = value;
                this.RaisePropertyChanged();
                _configManager?.Save(_config);
            }
        }
    }

    // ── State ────────────────────────────────────────────────────────────────────

    public AnalysisProcessingState ProcessingState
    {
        get => _processingState;
        private set
        {
            this.RaiseAndSetIfChanged(ref _processingState, value);
            this.RaisePropertyChanged(nameof(IsIdle));
            this.RaisePropertyChanged(nameof(IsProcessing));
            this.RaisePropertyChanged(nameof(CanStartAnalysis));
        }
    }

    public string? CurrentProcessingTrackId
    {
        get => _currentProcessingTrackId;
        private set => this.RaiseAndSetIfChanged(ref _currentProcessingTrackId, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchText, value);
        }
    }

    private string? _libraryLoadErrorMessage;
    /// <summary>Set when the real library fails to load and the page falls back to demo data, so that fallback is never silent — the sample tracks look identical to real ones otherwise.</summary>
    public string? LibraryLoadErrorMessage
    {
        get => _libraryLoadErrorMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _libraryLoadErrorMessage, value);
            this.RaisePropertyChanged(nameof(HasLibraryLoadError));
            this.RaisePropertyChanged(nameof(ShowNoLibraryTracks));
        }
    }

    private bool _isLibraryLoading;

    /// <summary>True while the library snapshot is being read; drives the pane's loading state.</summary>
    public bool IsLibraryLoading
    {
        get => _isLibraryLoading;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isLibraryLoading, value);
            this.RaisePropertyChanged(nameof(ShowNoLibraryTracks));
            this.RaisePropertyChanged(nameof(ShowNoSearchMatches));
        }
    }

    public bool HasLibraryLoadError => !string.IsNullOrEmpty(LibraryLoadErrorMessage);

    /// <summary>Library loaded fine but holds no tracks at all.</summary>
    public bool ShowNoLibraryTracks => !IsLibraryLoading && !HasLibraryLoadError && LibraryTracks.Count == 0;

    /// <summary>The library has tracks but the current search hides all of them.</summary>
    public bool ShowNoSearchMatches => !IsLibraryLoading && LibraryTracks.Count > 0 && FilteredLibraryTracks.Count == 0;

    /// <summary>Re-reads the library after a failed load (the error state's Retry button).</summary>
    public ReactiveCommand<Unit, Unit> RetryLoadLibraryCommand { get; private set; } = null!;

    public bool IsIdle => ProcessingState == AnalysisProcessingState.Idle;
    public bool IsProcessing => ProcessingState == AnalysisProcessingState.Processing;
    public bool CanStartAnalysis => !IsProcessing && AnalysisQueue.Any(t => t.AnalysisStatus != AnalysisRunStatus.Completed);
    public bool IsCompactLayout
    {
        get => _isCompactLayout;
        private set
        {
            if (_isCompactLayout == value)
                return;

            this.RaiseAndSetIfChanged(ref _isCompactLayout, value);
            this.RaisePropertyChanged(nameof(ShowLibraryPane));
            this.RaisePropertyChanged(nameof(ShowQueuePane));
            this.RaisePropertyChanged(nameof(IsLibraryPaneSelected));
            this.RaisePropertyChanged(nameof(IsQueuePaneSelected));
            this.RaisePropertyChanged(nameof(QueuePaneColumn));
            this.RaisePropertyChanged(nameof(QueuePaneColumnSpan));
            this.RaisePropertyChanged(nameof(LibraryPaneColumnSpan));
        }
    }

    public AnalysisPaneMode ActivePaneMode
    {
        get => _activePaneMode;
        private set
        {
            if (_activePaneMode == value)
                return;

            this.RaiseAndSetIfChanged(ref _activePaneMode, value);
            this.RaisePropertyChanged(nameof(ShowLibraryPane));
            this.RaisePropertyChanged(nameof(ShowQueuePane));
            this.RaisePropertyChanged(nameof(IsLibraryPaneSelected));
            this.RaisePropertyChanged(nameof(IsQueuePaneSelected));
        }
    }

    public bool ShowLibraryPane => !IsCompactLayout || ActivePaneMode == AnalysisPaneMode.Library;
    public bool ShowQueuePane => !IsCompactLayout || ActivePaneMode == AnalysisPaneMode.Queue;
    public bool IsLibraryPaneSelected => ActivePaneMode == AnalysisPaneMode.Library;
    public bool IsQueuePaneSelected => ActivePaneMode == AnalysisPaneMode.Queue;
    public int QueuePaneColumn => IsCompactLayout ? 0 : 2;
    public int QueuePaneColumnSpan => IsCompactLayout ? 3 : 1;
    public int LibraryPaneColumnSpan => IsCompactLayout ? 3 : 1;

    /// <summary>True when the filtered library list is empty (e.g. search returned no results).</summary>
    public bool IsLibraryEmpty => FilteredLibraryTracks.Count == 0;

    /// <summary>True when no tracks have been staged for analysis yet.</summary>
    public bool IsQueueEmpty => AnalysisQueue.Count == 0;

    public int TotalTrackCount => LibraryTracks.Count;
    public int OnDiskIndexedTrackCount => _onDiskIndexedTrackCount;
    public int IndexedCatalogCount => _indexedCatalogCount;
    public int StaleIndexedCount => _staleIndexedCount;
    public int DesiredDownloadCount => _desiredDownloadCount;
    public int IngestionBacklogCount => _ingestionBacklogCount;
    public int AnalyzedTrackCount => LibraryTracks.Count(t => t.HasAnalysis);
    public int PendingTrackCount => LibraryTracks.Count(t => !t.HasAnalysis);
    public int QueueTrackCount => AnalysisQueue.Count;
    public int IncompleteAnalysisTrackCount => LibraryTracks.Count(t => t.HasIncompleteAnalysis);
    public bool HasIncompleteAnalysisTracks => IncompleteAnalysisTrackCount > 0;
    public bool HasIncompleteCorruptTracks => LibraryTracks.Any(t => t.HasIncompleteAnalysis && t.IsLikelyCorruptFile);
    public IReadOnlyList<AnalysisTrackItem> IncompleteAnalysisTracks => LibraryTracks
        .Where(t => t.HasIncompleteAnalysis)
        .OrderBy(t => t.Artist)
        .ThenBy(t => t.Title)
        .ToList();
    public int StemsReadyCount => LibraryTracks.Count(t => t.StemsReady);
    public bool HasQueueMetrics => QueueTrackCount > 0 || _completedAnalysisRuns > 0 || IsProcessing;
    public bool IsDeveloperMode => Debugger.IsAttached || string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);
    public string AvgAnalysisTimeDisplay => _completedAnalysisRuns == 0 ? "—" : NumFormat.Ui($"{_totalAnalysisSeconds / _completedAnalysisRuns:F1}s");
    public string ThroughputDisplay => _completedAnalysisRuns == 0 ? "—" : NumFormat.Ui($"{_completedAnalysisRuns / Math.Max(_analysisSessionStopwatch.Elapsed.TotalMinutes, 1.0 / 60.0):F1}/min");
    public string FilterPerformanceDisplay => NumFormat.Ui($"Filter {_lastFilterMilliseconds:F1} ms (avg {_averageFilterMilliseconds:F1} ms)");
    public string InteractionPerformanceDisplay => _lastQueueMutationMilliseconds <= 0
        ? "Queue update —"
        : NumFormat.Ui($"Queue update {_lastQueueMutationMilliseconds:F1} ms");
    public string PerformanceDiagnosticsSummary => $"{_filteredTrackCount}/{LibraryTracks.Count} visible • {FilterPerformanceDisplay} • {InteractionPerformanceDisplay}";
    public string? PerformanceProbeSummary
    {
        get => _performanceProbeSummary;
        private set => this.RaiseAndSetIfChanged(ref _performanceProbeSummary, value);
    }
    public bool HasPerformanceProbeRuns => PerformanceProbeRuns.Count > 0;
    public string ElapsedTimeDisplay => _analysisSessionStopwatch.Elapsed == TimeSpan.Zero
        ? "—"
        : _analysisSessionStopwatch.Elapsed.ToString(_analysisSessionStopwatch.Elapsed.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
    public string QueueMetricsSummary => $"{QueueTrackCount} queued • {AnalyzedTrackCount} analyzed • {PendingTrackCount} remaining";
    public string IncompleteAnalysisSummary => IncompleteAnalysisTrackCount == 0
        ? "All tracks have sufficient analysis coverage."
        : $"{IncompleteAnalysisTrackCount} track(s) have incomplete or insufficient analysis data.";
    public string LibraryCountDifferentiationSummary =>
        $"Wanted downloads: {DesiredDownloadCount} • Ingestion backlog: {IngestionBacklogCount} • Physical on-disk indexed: {OnDiskIndexedTrackCount} • Stale indexed rows: {StaleIndexedCount}";
    public string CompletionRateDisplay => TotalTrackCount == 0 ? "0%" : $"{(AnalyzedTrackCount * 100.0 / TotalTrackCount):F0}%";

    // ── Commands ─────────────────────────────────────────────────────────────

    /// <summary>Adds a track from the library to the analysis queue.</summary>
    public ReactiveCommand<AnalysisTrackItem, Unit> AddToQueueCommand { get; }

    /// <summary>Removes a track from the analysis queue.</summary>
    public ReactiveCommand<AnalysisTrackItem, Unit> RemoveFromQueueCommand { get; }

    /// <summary>Adds every unanalyzed, un-queued visible track to the analysis queue.</summary>
    public ReactiveCommand<Unit, Unit> QueueAllUnanalyzedCommand { get; }

    /// <summary>Starts sequential analysis of all queued tracks.</summary>
    public ReactiveCommand<Unit, Unit> StartAnalysisCommand { get; }
    public ReactiveCommand<Unit, Unit> RunPerformanceProbeCommand { get; }

    /// <summary>Re-queues a completed track and clears cached output.</summary>
    public ReactiveCommand<AnalysisTrackItem, Unit> ReanalyzeCommand { get; }

    /// <summary>Queues all tracks that currently have incomplete analysis data.</summary>
    public ReactiveCommand<Unit, Unit> ReanalyzeAllIncompleteCommand { get; }

    /// <summary>
    /// Queues every track in the library for re-analysis, regardless of current status.
    /// "Incomplete" only catches tracks missing BPM/key/cues/waveform — it has no way to know
    /// a track's stored analysis predates a pipeline improvement (e.g. better drop/phrase
    /// detection), since that track still looks perfectly "complete" by those checks. This is
    /// the deliberate override for "the detection got better, redo everything."
    /// </summary>
    public ReactiveCommand<Unit, Unit> ReanalyzeAllTracksCommand { get; }

    /// <summary>Force-queues every track in the selected playlist for a fresh analysis pass.</summary>
    public ReactiveCommand<Unit, Unit> ReanalyzePlaylistCommand { get; }

    /// <summary>
    /// Re-maps cue points for every already-analysed track in the library, without touching
    /// BPM/key/energy/vocal/embedding data — for picking up a CueGenerationService logic change
    /// across the whole library without paying for a full re-analysis pass on every track.
    /// </summary>
    public ReactiveCommand<Unit, Unit> RegenerateAllCuesCommand { get; }

    /// <summary>Copies the full analysis JSON for a track to the clipboard.</summary>
    public ReactiveCommand<AnalysisTrackItem, Unit> CopyAnalysisJsonCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyPerformanceSnapshotCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearPerformanceProbeHistoryCommand { get; }

    public ReactiveCommand<AnalysisPaneMode, Unit> SetPaneModeCommand { get; }

    /// <summary>Runs a full library corruption scan using FFmpeg probe + NAudio frame scan.</summary>
    public ReactiveCommand<Unit, Unit> ScanLibraryIntegrityCommand { get; }

    public bool IsIntegrityScanRunning
    {
        get => _isIntegrityScanRunning;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isIntegrityScanRunning, value);
            this.RaisePropertyChanged(nameof(CanScanIntegrity));
            this.RaisePropertyChanged(nameof(ShowIntegrityPanel));
        }
    }

    public int IntegrityScanProgress
    {
        get => _integrityScanProgress;
        private set => this.RaiseAndSetIfChanged(ref _integrityScanProgress, value);
    }

    public string IntegrityScanStatusText
    {
        get => _integrityScanStatusText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _integrityScanStatusText, value);
            this.RaisePropertyChanged(nameof(ShowIntegrityPanel));
        }
    }

    /// <summary>
    /// The integrity card only appears once a scan or remediation has something to say; the scan
    /// itself is started from the page's overflow menu, so a resting page has no empty panel.
    /// </summary>
    public bool ShowIntegrityPanel => IsIntegrityScanRunning
        || !string.IsNullOrEmpty(IntegrityScanStatusText)
        || !string.IsNullOrEmpty(RemediationStatusText)
        || HasScanIssues;

    public bool CanScanIntegrity => !_isIntegrityScanRunning && _corruptionScanService is not null;

    // ── Remediation state ────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> FixAllIssuesCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> FixCorruptCommand   { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> FixMissingCommand   { get; private set; } = null!;

    /// <summary>
    /// Deletes one track already flagged corrupt in the "Incomplete Analysis" list and resets it
    /// to Missing so the download queue re-acquires a clean copy — the same remediation the
    /// "Scan Library Integrity" flow performs, just reachable directly from a track this session's
    /// own analysis pass already flagged, instead of requiring a separate full-library rescan.
    /// </summary>
    public ReactiveCommand<AnalysisTrackItem, Unit> RemediateTrackCommand { get; private set; } = null!;

    /// <summary>Same as <see cref="RemediateTrackCommand"/>, applied to every currently-flagged corrupt track at once.</summary>
    public ReactiveCommand<Unit, Unit> RemediateAllIncompleteCorruptCommand { get; private set; } = null!;

    public bool IsRemediating
    {
        get => _isRemediating;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isRemediating, value);
            this.RaisePropertyChanged(nameof(CanRemediate));
            this.RaisePropertyChanged(nameof(CanRemediateFlaggedTrack));
        }
    }

    public string RemediationStatusText
    {
        get => _remediationStatusText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _remediationStatusText, value);
            this.RaisePropertyChanged(nameof(ShowIntegrityPanel));
        }
    }

    public IReadOnlyList<CorruptFileScanEntry> ScanIssues
    {
        get => _scanIssues;
        private set
        {
            _scanIssues = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(HasScanIssues));
            this.RaisePropertyChanged(nameof(ShowIntegrityPanel));
            this.RaisePropertyChanged(nameof(CorruptIssueCount));
            this.RaisePropertyChanged(nameof(MissingIssueCount));
            this.RaisePropertyChanged(nameof(CanRemediate));
        }
    }

    public bool HasScanIssues   => _scanIssues.Count > 0;
    public int  CorruptIssueCount  => _scanIssues.Count(e => !e.IsMissing);
    public int  MissingIssueCount  => _scanIssues.Count(e => e.IsMissing);
    public bool CanRemediate    => HasScanIssues && !_isRemediating && _remediationService is not null;

    /// <summary>
    /// Gate for <see cref="RemediateTrackCommand"/>/<see cref="RemediateAllIncompleteCorruptCommand"/> —
    /// deliberately doesn't require <see cref="HasScanIssues"/>, since those act on tracks this
    /// session's own analysis pass flagged (<see cref="AnalysisTrackItem.IsLikelyCorruptFile"/>),
    /// a separate detection path from the "Scan Library Integrity" issue list.
    /// </summary>
    public bool CanRemediateFlaggedTrack => !_isRemediating && _remediationService is not null;

    private string? _automixStatusMessage;

    /// <summary>Success / error message from the last automix generation attempt.</summary>
    public string? AutomixStatusMessage
    {
        get => _automixStatusMessage;
        private set => this.RaiseAndSetIfChanged(ref _automixStatusMessage, value);
    }

    public string? AnalysisStatusMessage => AutomixStatusMessage;

    // ── Constructor ──────────────────────────────────────────────────────────────

    /// <summary>"N tracks need a loudness measurement" card. A DI singleton shared with the Library
    /// tools menu, so it is not disposed here. Null in design/test construction (card hidden).</summary>
    public LoudnessBackfillViewModel? LoudnessBackfill { get; }

    /// <summary>Library metadata enrichment card (shared DI singleton, not disposed here). Null in tests.</summary>
    public EnrichmentViewModel? Enrichment { get; }
    public bool HasEnrichment => Enrichment != null;

    public AnalysisPageViewModel(
        IEventBus eventBus,
        ILifecycleProjectionService lifecycleProjectionService,
        ILibraryService? libraryService = null,
        IClipboardService? clipboardService = null,
        ILibraryCorruptionScanService? corruptionScanService = null,
        ICorruptFileRemediationService? remediationService = null,
        AnalyzeTrackStructureJob? cueStructureJob = null,
        AppConfig? config = null,
        ConfigManager? configManager = null,
        LoudnessBackfillViewModel? loudnessBackfill = null,
        EnrichmentViewModel? enrichment = null,
        AnalysisCoverageService? coverageService = null,
        IDialogService? dialogService = null)
    {
        _eventBus = eventBus;
        LoudnessBackfill = loudnessBackfill;
        Enrichment = enrichment;
        _coverageService = coverageService;
        _libraryService = libraryService;
        _clipboardService = clipboardService;
        _lifecycleProjectionService = lifecycleProjectionService;
        _corruptionScanService = corruptionScanService;
        _remediationService = remediationService;
        _dialogService = dialogService;
        _cueStructureJob = cueStructureJob;
        _config = config;
        _configManager = configManager;

        if (_libraryService is null)
            LoadMockData();
        else
            _ = LoadLibraryAsync();

        // Throttled UI refresh logic
        _refreshRequestSubject
            .Throttle(TimeSpan.FromMilliseconds(100), RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshComputedStateInternal())
            .DisposeWith(_disposables);

        _filterRequestSubject
            .Throttle(TimeSpan.FromMilliseconds(150), RxApp.MainThreadScheduler)
            .Subscribe(query => ApplyFilterDirect(query))
            .DisposeWith(_disposables);

        ApplyFilterDirect();
        RefreshComputedStateInternal();

        // ── Wire up commands ──────────────────────────────────────────────────
        AddToQueueCommand = ReactiveCommand.Create<AnalysisTrackItem>(AddToQueue);
        RemoveFromQueueCommand = ReactiveCommand.Create<AnalysisTrackItem>(RemoveFromQueue);
        QueueAllUnanalyzedCommand = ReactiveCommand.CreateFromTask(QueueAllUnanalyzedAsync);

        var canStart = this.WhenAnyValue(x => x.CanStartAnalysis);
        StartAnalysisCommand = ReactiveCommand.CreateFromTask(StartAnalysisAsync, canStart);
        RunPerformanceProbeCommand = ReactiveCommand.Create(RunPerformanceProbe);
        ReanalyzeCommand = ReactiveCommand.Create<AnalysisTrackItem>(Reanalyze);
        ReanalyzeAllIncompleteCommand = ReactiveCommand.CreateFromTask(ReanalyzeAllIncompleteAsync);
        ReanalyzeAllTracksCommand = ReactiveCommand.CreateFromTask(ReanalyzeAllTracksAsync);
        ReanalyzePlaylistCommand = ReactiveCommand.CreateFromTask(ReanalyzePlaylistAsync);
        RegenerateAllCuesCommand = ReactiveCommand.CreateFromTask(RegenerateAllCuesAsync);
        CopyAnalysisJsonCommand = ReactiveCommand.CreateFromTask<AnalysisTrackItem>(CopyAnalysisJsonAsync);
        CopyPerformanceSnapshotCommand = ReactiveCommand.CreateFromTask(CopyPerformanceSnapshotAsync);
        ClearPerformanceProbeHistoryCommand = ReactiveCommand.Create(ClearPerformanceProbeHistory);
        SetPaneModeCommand = ReactiveCommand.Create<AnalysisPaneMode>(SetPaneMode);
        RetryLoadLibraryCommand = ReactiveCommand.CreateFromTask(LoadLibraryAsync);

        var canScan = this.WhenAnyValue(x => x.CanScanIntegrity);
        ScanLibraryIntegrityCommand = ReactiveCommand.CreateFromTask(ScanLibraryIntegrityAsync, canScan);

        var canRemediate = this.WhenAnyValue(x => x.CanRemediate);
        FixAllIssuesCommand = ReactiveCommand.CreateFromTask(
            () => RemediateAsync(_scanIssues), canRemediate);
        FixCorruptCommand = ReactiveCommand.CreateFromTask(
            () => RemediateAsync(_scanIssues.Where(e => !e.IsMissing).ToList()), canRemediate);
        FixMissingCommand = ReactiveCommand.CreateFromTask(
            () => RemediateAsync(_scanIssues.Where(e => e.IsMissing).ToList()), canRemediate);

        var canRemediateFlaggedTrack = this.WhenAnyValue(x => x.CanRemediateFlaggedTrack);
        RemediateTrackCommand = ReactiveCommand.CreateFromTask<AnalysisTrackItem>(
            track => RemediateAsync(new[] { BuildScanEntry(track) }), canRemediateFlaggedTrack);
        RemediateAllIncompleteCorruptCommand = ReactiveCommand.CreateFromTask(
            () => RemediateAsync(IncompleteAnalysisTracks.Where(t => t.IsLikelyCorruptFile).Select(BuildScanEntry).ToList()),
            canRemediateFlaggedTrack);

        // Keep all computed dashboard metrics in sync as the collections change.
        LibraryTracks.CollectionChanged += (_, _) => RefreshComputedState();
        AnalysisQueue.CollectionChanged += (_, _) => RefreshComputedState();
        PerformanceProbeRuns.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasPerformanceProbeRuns));

        // ── Playlist coverage ────────────────────────────────────────────────
        RefreshCoverageCommand = ReactiveCommand.CreateFromTask(RefreshCoverageAsync);
        RunMissingForPlaylistCommand = ReactiveCommand.CreateFromTask(RunMissingForPlaylistAsync);
        RunMissingForTrackCommand = ReactiveCommand.Create<AnalysisTrackItem>(
            item => QueueEngines(new[] { item }, item.RunnableMissingEngines));
        RunEngineForPlaylistCommand = ReactiveCommand.Create<EngineCoverageRow>(
            row => QueueCoverageRequests(Coverage?.GetRequests(row.Engine) ?? Array.Empty<TrackCoverageRequest>()));

        _coverageRefreshSubject
            .Throttle(TimeSpan.FromMilliseconds(500), RxApp.MainThreadScheduler)
            .Subscribe(unit => { _ = RefreshCoverageAsync(); })
            .DisposeWith(_disposables);

        if (_coverageService is not null)
            _ = LoadCoveragePlaylistsAsync();

        // React to per-track progress events
        _eventBus.GetEvent<AnalysisProgressEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnAnalysisProgress)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<TrackAnalysisStartedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnTrackAnalysisStarted)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<TrackAnalysisFailedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt =>
            {
                _ = OnTrackAnalysisFailedAsync(evt);
            })
            .DisposeWith(_disposables);

        _eventBus.GetEvent<TrackAnalysisCompletedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt =>
            {
                _ = OnTrackAnalysisCompletedAsync(evt);
            })
            .DisposeWith(_disposables);

        _eventBus.GetEvent<AnalysisQueueStatusChangedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnAnalysisQueueStatusChanged)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<TrackAnalysisRequestedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnTrackAnalysisRequested)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<FileIngestionQueuedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnFileIngestionQueued)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<FileIngestionCompletedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnFileIngestionCompleted)
            .DisposeWith(_disposables);

        // A folder import adds tracks in one go and publishes one completion event: reload the
        // page's library once, so "Analyse N new tracks" finds them here instead of doing one
        // library lookup per requested track (OnTrackAnalysisRequested).
        _eventBus.GetEvent<LibraryImportCompletedEvent>()
            .Where(evt => evt.Result.ChangedLibrary && _libraryService is not null)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt => { _ = LoadLibraryAsync(); })
            .DisposeWith(_disposables);

        _eventBus.GetEvent<FileMissingDetectedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnFileMissingDetected)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<LibraryEntryDeletedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnLibraryEntryDeleted)
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(220), RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter())
            .DisposeWith(_disposables);

        UpdateLayoutMode(1280);
    }


    public void Dispose()
    {
        _cts.Cancel();
        _disposables.Dispose();
        _cts.Dispose();
    }

}
