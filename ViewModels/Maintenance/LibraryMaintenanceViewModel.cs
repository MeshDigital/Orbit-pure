using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library.Files;
using SLSKDONET.Services.Library.Maintenance;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Maintenance;

public enum MaintenanceStep { Choose, Scan, Review, Apply, Summary }

/// <summary>
/// Step-navigating wizard for Library Maintenance: choose tasks → dry-run scan → review → apply →
/// summary/undo. The dry run only reads; nothing writes until Apply. Whole-library scope for v1.
/// </summary>
public sealed class LibraryMaintenanceViewModel : ReactiveObject, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly ILogger<LibraryMaintenanceViewModel> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly LibraryIdentityIndexLoader _indexLoader;
    private readonly TagChangeService _tagChanges;
    private readonly StylePlaylistApplier _playlistApplier;
    private readonly StyleBackfillService _styleBackfill;
    private readonly GenreRefreshService? _genreRefresh;
    private readonly ReorganizePreviewService? _reorganizePreview;
    private readonly FileRelocationService? _relocation;
    private readonly AppConfig? _config;
    private GenreRefreshScan _genreScan = GenreRefreshScan.Empty;
    private Guid? _reorganizeBatchId;
    private readonly CompositeDisposable _disposables = new();
    private readonly CancellationTokenSource _cts = new();

    private Guid? _runId;
    private LibraryIdentityIndex _index = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    // ── Steps ────────────────────────────────────────────────────────────────
    private MaintenanceStep _currentStep = MaintenanceStep.Choose;
    public MaintenanceStep CurrentStep
    {
        get => _currentStep;
        private set
        {
            if (_currentStep == value) return;
            _currentStep = value;
            this.RaisePropertyChanged(nameof(CurrentStep));
            this.RaisePropertyChanged(nameof(IsChooseStep));
            this.RaisePropertyChanged(nameof(IsScanStep));
            this.RaisePropertyChanged(nameof(IsReviewStep));
            this.RaisePropertyChanged(nameof(IsApplyStep));
            this.RaisePropertyChanged(nameof(IsSummaryStep));
        }
    }

    public bool IsChooseStep => CurrentStep == MaintenanceStep.Choose;
    public bool IsScanStep => CurrentStep == MaintenanceStep.Scan;
    public bool IsReviewStep => CurrentStep == MaintenanceStep.Review;
    public bool IsApplyStep => CurrentStep == MaintenanceStep.Apply;
    public bool IsSummaryStep => CurrentStep == MaintenanceStep.Summary;

    // ── Task selection ───────────────────────────────────────────────────────
    private bool _fixTags = true;
    public bool FixTags { get => _fixTags; set => this.RaiseAndSetIfChanged(ref _fixTags, value); }

    private bool _enrich = true;
    public bool Enrich { get => _enrich; set => this.RaiseAndSetIfChanged(ref _enrich, value); }

    private bool _styleBackfillTask;
    public bool StyleBackfill { get => _styleBackfillTask; set => this.RaiseAndSetIfChanged(ref _styleBackfillTask, value); }

    private bool _refreshGenres = true;
    /// <summary>Re-fuse genres from stored analysis (GA10 #255): no audio, seconds for thousands of tracks.</summary>
    public bool RefreshGenres { get => _refreshGenres; set => this.RaiseAndSetIfChanged(ref _refreshGenres, value); }

    /// <summary>Offered only when the refresh service is available (it is in the app).</summary>
    public bool CanRefreshGenres => _genreRefresh is not null;

    private bool _genreConflicts = true;
    /// <summary>Propose (unticked) genre fixes where the tag and a strong audio genre disagree (GA9 #254).</summary>
    public bool GenreConflicts { get => _genreConflicts; set => this.RaiseAndSetIfChanged(ref _genreConflicts, value); }

    private bool _genreFill = true;
    public bool GenreFill { get => _genreFill; set => this.RaiseAndSetIfChanged(ref _genreFill, value); }

    private bool _stylePlaylists = true;
    public bool StylePlaylists { get => _stylePlaylists; set => this.RaiseAndSetIfChanged(ref _stylePlaylists, value); }

    private bool _reorganizeFiles;
    /// <summary>LF5 (#347): opt-in preview + undoable file reorganisation using the naming template.</summary>
    public bool ReorganizeFiles { get => _reorganizeFiles; set => this.RaiseAndSetIfChanged(ref _reorganizeFiles, value); }

    public bool CanReorganizeFiles => _reorganizePreview is not null && _relocation is not null;

    // ── Review data ──────────────────────────────────────────────────────────
    public ObservableCollection<ProposalRowViewModel> TagRows { get; } = new();
    public ObservableCollection<ProposalRowViewModel> GenreRows { get; } = new();
    public ObservableCollection<ReorganizeRowViewModel> ReorganizeRows { get; } = new();

    private string? _reorganizeSummaryText;
    public string? ReorganizeSummaryText
    {
        get => _reorganizeSummaryText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _reorganizeSummaryText, value);
            this.RaisePropertyChanged(nameof(HasReorganizePreview));
        }
    }

    public bool HasReorganizePreview => !string.IsNullOrEmpty(ReorganizeSummaryText);

    private IReadOnlyList<PlannedStylePlaylist> _stylePlan = Array.Empty<PlannedStylePlaylist>();
    public IReadOnlyList<PlannedStylePlaylist> StylePlan { get => _stylePlan; private set => this.RaiseAndSetIfChanged(ref _stylePlan, value); }

    private int _missingStyles;
    public int MissingStyles { get => _missingStyles; private set => this.RaiseAndSetIfChanged(ref _missingStyles, value); }

    private int _enrichmentCount;
    public int EnrichmentCount { get => _enrichmentCount; private set => this.RaiseAndSetIfChanged(ref _enrichmentCount, value); }

    private int _refreshableGenres;
    /// <summary>Tracks whose genre the refresh can bring up to date from stored analysis.</summary>
    public int RefreshableGenres { get => _refreshableGenres; private set => this.RaiseAndSetIfChanged(ref _refreshableGenres, value); }

    private string? _genreSummaryText;
    /// <summary>Review-step summary of the AI genre work, with time estimates; null when nothing was chosen.</summary>
    public string? GenreSummaryText
    {
        get => _genreSummaryText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _genreSummaryText, value);
            this.RaisePropertyChanged(nameof(HasGenreSummary));
        }
    }

    public bool HasGenreSummary => !string.IsNullOrEmpty(GenreSummaryText);

    // ── Run state ────────────────────────────────────────────────────────────
    private bool _isScanning;
    public bool IsScanning { get => _isScanning; private set => this.RaiseAndSetIfChanged(ref _isScanning, value); }

    private bool _isApplying;
    public bool IsApplying { get => _isApplying; private set => this.RaiseAndSetIfChanged(ref _isApplying, value); }

    private string? _statusText;
    public string? StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private string? _summaryText;
    public string? SummaryText { get => _summaryText; private set => this.RaiseAndSetIfChanged(ref _summaryText, value); }

    public event EventHandler? Closed;

    // ── Commands ─────────────────────────────────────────────────────────────
    public ICommand StartScanCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand FinishCommand { get; }
    /// <summary>Switches to the exact-duplicate review (the shell closes this wizard).</summary>
    public ICommand FindExactDuplicatesCommand { get; }

    public LibraryMaintenanceViewModel(
        IEventBus eventBus,
        ILogger<LibraryMaintenanceViewModel> logger,
        IDbContextFactory<AppDbContext> dbFactory,
        LibraryIdentityIndexLoader indexLoader,
        TagChangeService tagChanges,
        StylePlaylistApplier playlistApplier,
        StyleBackfillService styleBackfill,
        GenreRefreshService? genreRefresh = null,
        ReorganizePreviewService? reorganizePreview = null,
        FileRelocationService? relocation = null,
        AppConfig? config = null)
    {
        _eventBus = eventBus;
        _logger = logger;
        _dbFactory = dbFactory;
        _indexLoader = indexLoader;
        _tagChanges = tagChanges;
        _playlistApplier = playlistApplier;
        _styleBackfill = styleBackfill;
        _genreRefresh = genreRefresh;
        _reorganizePreview = reorganizePreview;
        _relocation = relocation;
        _config = config;

        StartScanCommand = new AsyncRelayCommand(StartScanAsync);
        CancelCommand = new RelayCommand(() => _cts.Cancel());
        ApplyCommand = new AsyncRelayCommand(ApplySelectedAsync);
        UndoCommand = new AsyncRelayCommand(UndoAsync);
        BackCommand = new RelayCommand(GoBack);
        FinishCommand = new RelayCommand(() => Close());
        FindExactDuplicatesCommand = new RelayCommand(() => _eventBus.Publish(new OpenExactDuplicatesRequestedEvent()));
    }

    /// <summary>Closes the wizard; the shell disposes this instance.</summary>
    public void Close() => Closed?.Invoke(this, EventArgs.Empty);

    private async Task StartScanAsync()
    {
        CurrentStep = MaintenanceStep.Scan;
        StatusText = "Scanning…";
        try
        {
            await BeginRunAsync(_cts.Token);
            await ScanAsync(_cts.Token);
            CurrentStep = MaintenanceStep.Review;
            StatusText = null;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
            CurrentStep = MaintenanceStep.Choose;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Library maintenance scan failed");
            StatusText = $"Scan failed: {ex.Message}";
            CurrentStep = MaintenanceStep.Choose;
        }
    }

    private async Task ApplySelectedAsync()
    {
        var tagFixes = TagRows.Where(r => r.IsSelected).Select(r => r.Proposal).ToList();
        var genreFills = GenreRows.Where(r => r.IsSelected).Select(r => r.Proposal).ToList();
        var playlists = StylePlaylists ? StylePlan : Array.Empty<PlannedStylePlaylist>();

        CurrentStep = MaintenanceStep.Apply;
        try
        {
            await ApplyAsync(tagFixes, genreFills, playlists, _cts.Token);
            CurrentStep = MaintenanceStep.Summary;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Apply cancelled.";
            CurrentStep = MaintenanceStep.Review;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Library maintenance apply failed");
            StatusText = $"Apply failed: {ex.Message}";
            CurrentStep = MaintenanceStep.Review;
        }
    }

    private async Task UndoAsync()
    {
        if (_runId is null) return;
        try
        {
            await RevertAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Library maintenance undo failed");
            SummaryText = $"Undo failed: {ex.Message}";
        }
    }

    private void GoBack()
    {
        if (CurrentStep == MaintenanceStep.Summary) CurrentStep = MaintenanceStep.Review;
        else if (CurrentStep == MaintenanceStep.Review) CurrentStep = MaintenanceStep.Choose;
    }

    // ── Orchestration ────────────────────────────────────────────────────────

    public async Task BeginRunAsync(CancellationToken ct = default)
    {
        _runId = await _tagChanges.BeginRunAsync(new MaintenanceRunOptions(), ct);
    }

    /// <summary>Dry run: builds the identity index, detects tag issues, plans genre fill and style playlists. Reads only.</summary>
    public async Task ScanAsync(CancellationToken ct = default)
    {
        IsScanning = true;
        try
        {
            _index = await _indexLoader.LoadAsync(ct);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            if (FixTags)
            {
                var rows = await db.LibraryEntries.AsNoTracking()
                    .Select(e => new { e.UniqueHash, e.Artist, e.Title, e.Album, e.Label, e.FilePath })
                    .ToListAsync(ct);

                var options = new TagIssueOptions();
                TagRows.Clear();
                foreach (var r in rows)
                {
                    var proposals = TagIssueDetector.Detect(
                        new TagIdentity(r.UniqueHash, r.Artist, r.Title, r.Album, r.Label, r.FilePath),
                        _index, options);
                    foreach (var p in proposals)
                        TagRows.Add(new ProposalRowViewModel(p));
                }
            }
            else
            {
                TagRows.Clear();
            }

            // Style candidates: unclassified = both Genres (any format) and PrimaryGenre empty.
            var genred = await db.LibraryEntries.AsNoTracking()
                .Where(e => !string.IsNullOrWhiteSpace(e.PrimaryGenre) ||
                            (e.Genres != null && e.Genres != string.Empty && e.Genres != "[]"))
                .Select(e => e.UniqueHash)
                .ToHashSetAsync(ct);

            var styles = await db.AudioFeatures.AsNoTracking()
                .Where(f => f.TopStyle != null)
                .Select(f => new { f.TrackUniqueHash, f.TopStyle, f.TopStyleConfidence })
                .ToListAsync(ct);

            var candidates = styles
                .Select(s => new StyleCandidate(s.TrackUniqueHash, s.TopStyle, s.TopStyleConfidence ?? 0f, genred.Contains(s.TrackUniqueHash)))
                .ToList();

            GenreRows.Clear();
            if (GenreFill)
            {
                foreach (var p in StylePlaylistPlanner.GenreFillProposals(candidates))
                    GenreRows.Add(new ProposalRowViewModel(p));
            }
            if (GenreConflicts)
            {
                // Tag vs audio conflicts (GA9 #254): review-only, never against a genre you set.
                foreach (var p in await GenreConflictRule.ScanAsync(db, ct))
                    GenreRows.Add(new ProposalRowViewModel(p));
            }

            var plan = StylePlaylistPlanner.Plan(candidates, new Dictionary<string, Guid>(), new StylePlanOptions());
            StylePlan = StylePlaylists ? plan.Playlists : Array.Empty<PlannedStylePlaylist>();
            MissingStyles = plan.MissingStyles;

            // AI genre (GA10 #255): one scan sorts every track into re-fuse (stored data) / needs analysis / current.
            _genreScan = GenreRefreshScan.Empty;
            if (_genreRefresh is not null && (RefreshGenres || StyleBackfill))
            {
                StatusText = "Checking AI genres…";
                _genreScan = await _genreRefresh.ScanAsync(ct);
                EnrichmentCount = _genreScan.NeedsInference;
            }
            else
            {
                EnrichmentCount = await _styleBackfill.CountTracksMissingStylesAsync(ct);
            }
            RefreshableGenres = _genreScan.Refuse.Count;
            GenreSummaryText = BuildGenreSummary();

            // LF5 (#347): Reorganise library files preview (reads only; nothing moved until Apply).
            ReorganizeRows.Clear();
            ReorganizeSummaryText = null;
            if (ReorganizeFiles && _reorganizePreview is not null)
            {
                StatusText = "Previewing file reorganisation…";
                var entries = await db.LibraryEntries.AsNoTracking()
                    .Where(e => e.FilePath != null && e.FilePath != string.Empty)
                    .Select(e => new { e.UniqueHash, e.FilePath, e.Artist, e.Title, e.Album, e.DurationSeconds, e.Bitrate })
                    .ToListAsync(ct);

                var featuresByHash = await db.AudioFeatures.AsNoTracking()
                    .ToDictionaryAsync(f => f.TrackUniqueHash, ct);

                var reorgInput = new List<ReorganizeTrack>(entries.Count);
                foreach (var e in entries)
                {
                    featuresByHash.TryGetValue(e.UniqueHash, out var f);
                    reorgInput.Add(new ReorganizeTrack(
                        e.UniqueHash,
                        e.FilePath!,
                        new Track
                        {
                            Artist = e.Artist,
                            Title = e.Title,
                            Album = e.Album,
                            BPM = f is { Bpm: > 0 } ? f.Bpm : null,
                            MusicalKey = f is { Key.Length: > 0 } ? f.Key : null,
                            Energy = f is not null ? f.Energy : null,
                            Filename = System.IO.Path.GetFileName(e.FilePath),
                            Length = e.DurationSeconds,
                            Bitrate = e.Bitrate,
                        }));
                }

                var preview = await _reorganizePreview.PreviewAsync(reorgInput, ct);
                foreach (var item in preview.Items)
                {
                    if (item.Status == "Unchanged") continue;
                    ReorganizeRows.Add(new ReorganizeRowViewModel(item));
                }

                ReorganizeSummaryText =
                    $"{preview.Movable} to move · {preview.Unchanged} unchanged · {preview.Blocked} blocked (locked, linked folder, or collision).";
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    private string? BuildGenreSummary()
    {
        var parts = new List<string>();
        if (RefreshGenres && _genreRefresh is not null)
            parts.Add(RefreshableGenres == 0
                ? "Every genre that can be refreshed from stored analysis is already current."
                : $"{SLSKDONET.Utils.CountFormat.N(RefreshableGenres)} tracks will refresh their genre from stored analysis " +
                  $"(about {FormatDuration(GenreRefreshService.EstimatedPerTrack * RefreshableGenres)}, no audio).");
        if (StyleBackfill && EnrichmentCount > 0)
            parts.Add($"{SLSKDONET.Utils.CountFormat.N(EnrichmentCount)} tracks need a new AI style analysis " +
                      $"(about {FormatDuration(StyleBackfillService.EstimatedPerTrack * EnrichmentCount)} in the background).");
        else if (RefreshGenres && EnrichmentCount > 0)
            parts.Add($"{SLSKDONET.Utils.CountFormat.N(EnrichmentCount)} more need a new AI style analysis first; " +
                      "tick \"Analyse tracks with missing or outdated AI style\" to queue them.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    internal static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))} s"
        : t.TotalMinutes < 90 ? $"{(int)Math.Ceiling(t.TotalMinutes)} min"
        : SLSKDONET.Utils.NumFormat.Ui($"{t.TotalHours:0.#} h");

    /// <summary>
    /// Applies the approved work in order: tag fixes → genre fill → playlists → style backfill.
    /// Never reports success for a failed write.
    /// </summary>
    public async Task ApplyAsync(
        IReadOnlyList<ProposedTagChange> tagFixes,
        IReadOnlyList<ProposedTagChange> genreFills,
        IReadOnlyList<PlannedStylePlaylist> playlists,
        CancellationToken ct = default)
    {
        if (_runId is null)
            throw new InvalidOperationException("BeginRunAsync must be called before ApplyAsync.");

        IsApplying = true;
        try
        {
            StatusText = "Applying tag fixes…";
            var tagReport = await _tagChanges.ApplyAsync(_runId.Value, tagFixes, null, ct);

            StatusText = "Applying genre fill…";
            var genreReport = await _tagChanges.ApplyAsync(_runId.Value, genreFills, null, ct);
            if (genreReport.Applied > 0)
                _eventBus.Publish(new SLSKDONET.Services.Genre.GenreLabelsChangedEvent()); // the personal genre head retrains (GA11 #256)

            StatusText = "Creating style playlists…";
            var playlistReport = await _playlistApplier.ApplyAsync(_runId.Value, playlists, ct);

            // "Analyse tracks missing AI style": every track whose style scores are missing or stale
            // (#255). It used to queue only the tracks this run fixed, so the option did nearly nothing.
            StatusText = "Queuing style backfill…";
            string? backfillReason = null;
            var queuedForStyle = 0;
            if (StyleBackfill)
            {
                var needing = await _styleBackfill.FindTracksNeedingStylesAsync(ct);
                backfillReason = await _styleBackfill.QueueStyleBackfillAsync(needing, ct);
                if (backfillReason is null) queuedForStyle = needing.Count;
            }

            // Genre refresh from stored analysis (GA10 #255). It returns its own honest report on cancel.
            GenreRefreshReport? genreRefresh = null;
            if (RefreshGenres && _genreRefresh is not null && _genreScan.Refuse.Count > 0)
            {
                StatusText = "Refreshing genres from stored analysis…";
                var progress = new Progress<(int Done, int Total)>(p =>
                    StatusText = $"Refreshing genres from stored analysis… {SLSKDONET.Utils.CountFormat.N(p.Done)} / {SLSKDONET.Utils.CountFormat.N(p.Total)}");
                genreRefresh = await _genreRefresh.RefuseAsync(_genreScan.Refuse, progress, ct);
            }

            // LF5 (#347): Reorganise selected files as one undoable relocation batch + clean up empty folders.
            RelocationOutcome? reorgOutcome = null;
            var removedEmptyFolders = 0;
            _reorganizeBatchId = null;
            if (ReorganizeFiles && _relocation is not null)
            {
                var selectedMoves = ReorganizeRows
                    .Where(r => r.IsSelected && r.IsMove)
                    .Select(r => new RelocationRequest(r.Hash, r.SourcePath, r.TargetPath))
                    .ToList();

                if (selectedMoves.Count > 0)
                {
                    StatusText = $"Reorganising {selectedMoves.Count} files…";
                    var plan = await _relocation.PlanAsync(selectedMoves, ct);
                    reorgOutcome = await _relocation.ExecuteAsync(plan, ct);
                    if (reorgOutcome.Moved > 0 && reorgOutcome.BatchId != Guid.Empty)
                        _reorganizeBatchId = reorgOutcome.BatchId;

                    var stopRoot = _config?.DownloadDirectory;
                    if (!string.IsNullOrWhiteSpace(stopRoot) && reorgOutcome.Moved > 0)
                    {
                        var failedHashes = reorgOutcome.Failures.Select(f => f.Hash).ToHashSet(StringComparer.Ordinal);
                        foreach (var move in plan.Moves)
                        {
                            if (failedHashes.Contains(move.Hash)) continue;
                            removedEmptyFolders += EmptyFolderCleanup.RemoveEmptyParents(move.SourcePath, stopRoot).Count;
                        }
                    }
                }
            }

            var summary = new MaintenanceRunSummary(
                Applied: tagReport.Applied + genreReport.Applied + (reorgOutcome?.Moved ?? 0),
                FileWritten: tagReport.FileWritten + genreReport.FileWritten + (reorgOutcome?.Moved ?? 0),
                DbOnly: tagReport.DbOnly + genreReport.DbOnly,
                Failed: tagReport.Failed + genreReport.Failed + playlistReport.Failed + (reorgOutcome?.Failures.Count ?? 0));

            await _tagChanges.CompleteRunAsync(_runId.Value, summary, ct);

            var reorgText = reorgOutcome is null
                ? string.Empty
                : $" Reorganised {reorgOutcome.Moved} files"
                  + (reorgOutcome.Failures.Count > 0 ? $" ({reorgOutcome.Failures.Count} failed)" : string.Empty)
                  + (removedEmptyFolders > 0 ? $", removed {removedEmptyFolders} empty folder(s)" : string.Empty)
                  + "."
                  + (reorgOutcome.Moved > 0 ? " If you use Rekordbox, re-export your playlists so track locations stay in sync." : string.Empty);

            SummaryText = (summary.Failed == 0
                ? $"Applied {summary.Applied} changes ({summary.FileWritten} files, {summary.DbOnly} DB only). {playlistReport.Created} playlists created, {playlistReport.Appended} extended."
                : $"Applied {summary.Applied} changes with {summary.Failed} failures. {playlistReport.Created} playlists created, {playlistReport.Appended} extended.")
                + (StyleBackfill
                    ? backfillReason is null
                        ? $" {SLSKDONET.Utils.CountFormat.N(queuedForStyle)} tracks queued for AI style analysis."
                        : $" AI style analysis not queued: {backfillReason}"
                    : string.Empty)
                + (genreRefresh is { } g ? " " + DescribeGenreRefresh(g) : string.Empty)
                + reorgText;

            if (!string.IsNullOrWhiteSpace(backfillReason))
                _logger.LogWarning("[Maintenance] Style backfill unavailable: {Reason}", backfillReason);
        }
        finally
        {
            IsApplying = false;
        }
    }

    internal static string DescribeGenreRefresh(GenreRefreshReport g)
    {
        var text = $"Genres refreshed for {SLSKDONET.Utils.CountFormat.N(g.Refused)} tracks ({SLSKDONET.Utils.CountFormat.N(g.Changed)} changed)";
        if (g.Failed > 0) text += $", {SLSKDONET.Utils.CountFormat.N(g.Failed)} failed (see the log)";
        if (g.Skipped > 0) text += $", {SLSKDONET.Utils.CountFormat.N(g.Skipped)} skipped";
        text += g.Cancelled ? "; cancelled before the end." : ".";
        if (g.Refused > 0) text += " This is analysis data: Undo doesn't revert it.";
        return text;
    }

    /// <summary>Undoes the run: tag changes, playlists, and any LF5 file moves, reporting edited-since skips.</summary>
    public async Task RevertAsync(CancellationToken ct = default)
    {
        if (_runId is null) return;
        var tagRevert = await _tagChanges.RevertRunAsync(_runId.Value, ct);
        var playlistRevert = await _playlistApplier.RevertAsync(_runId.Value, ct);
        var filesReverted = 0;
        if (_reorganizeBatchId is Guid batchId && _relocation is not null)
        {
            filesReverted = await _relocation.RevertBatchAsync(batchId, ct);
            _reorganizeBatchId = null;
        }

        SummaryText = $"Reverted {tagRevert.Reverted} tag changes (skipped {tagRevert.Skipped}), " +
                      $"deleted {playlistRevert.Deleted} playlists, removed {playlistRevert.TracksRemoved} tracks" +
                      (filesReverted > 0 ? $", restored {filesReverted} moved file(s)." : ".");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _disposables.Dispose();
    }
}
