using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library.Maintenance;

namespace SLSKDONET.ViewModels.Maintenance;

/// <summary>
/// Headless orchestration for the Library Maintenance wizard: choose tasks → dry-run scan → review →
/// apply → summary with undo. The View binds to this and drives the steps; nothing here writes to
/// files or the DB until <see cref="ApplyAsync"/> is called, and the dry-run (<see cref="ScanAsync"/>)
/// only reads.
/// </summary>
public sealed class LibraryMaintenanceViewModel : IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly ILogger<LibraryMaintenanceViewModel> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly LibraryIdentityIndexLoader _indexLoader;
    private readonly TagChangeService _tagChanges;
    private readonly StylePlaylistApplier _playlistApplier;
    private readonly StyleBackfillService _styleBackfill;
    private readonly CompositeDisposable _disposables = new();

    private Guid? _runId;
    private LibraryIdentityIndex _index = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public ObservableCollection<ProposedTagChange> TagProposals { get; } = new();
    public ObservableCollection<ProposedTagChange> GenreProposals { get; } = new();
    public IReadOnlyList<PlannedStylePlaylist> StylePlan { get; private set; } = Array.Empty<PlannedStylePlaylist>();
    public int MissingStyles { get; private set; }
    public int EnrichmentCount { get; private set; }

    public bool IsScanning { get; private set; }
    public bool IsApplying { get; private set; }
    public string? StatusText { get; private set; }

    /// <summary>Raised when the wizard overlay closes (via <see cref="Close"/>).</summary>
    public event EventHandler? Closed;

    /// <summary>Closes the wizard; the shell disposes this instance.</summary>
    public void Close() => Closed?.Invoke(this, EventArgs.Empty);

    public LibraryMaintenanceViewModel(
        IEventBus eventBus,
        ILogger<LibraryMaintenanceViewModel> logger,
        IDbContextFactory<AppDbContext> dbFactory,
        LibraryIdentityIndexLoader indexLoader,
        TagChangeService tagChanges,
        StylePlaylistApplier playlistApplier,
        StyleBackfillService styleBackfill)
    {
        _eventBus = eventBus;
        _logger = logger;
        _dbFactory = dbFactory;
        _indexLoader = indexLoader;
        _tagChanges = tagChanges;
        _playlistApplier = playlistApplier;
        _styleBackfill = styleBackfill;
    }

    /// <summary>Creates the <see cref="MaintenanceRunEntity"/> header for this run.</summary>
    public async Task BeginRunAsync(CancellationToken ct = default)
    {
        _runId = await _tagChanges.BeginRunAsync(new MaintenanceRunOptions(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Dry run: builds the identity index, detects tag issues, plans genre fill and style playlists.
    /// Reads only — no file or DB write.
    /// </summary>
    public async Task ScanAsync(CancellationToken ct = default)
    {
        IsScanning = true;
        StatusText = "Scanning…";
        try
        {
            _index = await _indexLoader.LoadAsync(ct).ConfigureAwait(false);

            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var rows = await db.LibraryEntries.AsNoTracking()
                .Select(e => new { e.UniqueHash, e.Artist, e.Title, e.Album, e.Label, e.FilePath })
                .ToListAsync(ct).ConfigureAwait(false);

            var options = new TagIssueOptions();
            TagProposals.Clear();
            foreach (var r in rows)
            {
                var proposals = TagIssueDetector.Detect(
                    new TagIdentity(r.UniqueHash, r.Artist, r.Title, r.Album, r.Label, r.FilePath),
                    _index, options);
                foreach (var p in proposals) TagProposals.Add(p);
            }

            // Style candidates: unclassified = both Genres (any format) and PrimaryGenre empty.
            var genred = await db.LibraryEntries.AsNoTracking()
                .Where(e => (!string.IsNullOrWhiteSpace(e.PrimaryGenre)) ||
                            (e.Genres != null && e.Genres != string.Empty && e.Genres != "[]"))
                .Select(e => e.UniqueHash)
                .ToHashSetAsync(ct).ConfigureAwait(false);

            var styles = await db.AudioFeatures.AsNoTracking()
                .Where(f => f.TopStyle != null)
                .Select(f => new { f.TrackUniqueHash, f.TopStyle, f.TopStyleConfidence })
                .ToListAsync(ct).ConfigureAwait(false);

            var candidates = styles.Select(s =>
                new StyleCandidate(s.TrackUniqueHash, s.TopStyle, s.TopStyleConfidence ?? 0f, genred.Contains(s.TrackUniqueHash)))
                .ToList();

            var genreProposals = StylePlaylistPlanner.GenreFillProposals(candidates);
            GenreProposals.Clear();
            foreach (var p in genreProposals) GenreProposals.Add(p);

            StylePlan = StylePlaylistPlanner.Plan(candidates, new Dictionary<string, Guid>(), new StylePlanOptions()).Playlists;
            MissingStyles = StylePlaylistPlanner.Plan(candidates, new Dictionary<string, Guid>(), new StylePlanOptions()).MissingStyles;

            EnrichmentCount = await _styleBackfill.CountTracksMissingStylesAsync(ct).ConfigureAwait(false);
            StatusText = $"Found {TagProposals.Count} tag fixes, {GenreProposals.Count} genre fills, {StylePlan.Count} playlists.";
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// Applies the approved work in the documented order: tag fixes → genre fill → playlists →
    /// style backfill (background). Each stage reports failures; nothing is reported as success
    /// when it failed.
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
            var tagReport = await _tagChanges.ApplyAsync(_runId.Value, tagFixes, null, ct).ConfigureAwait(false);

            StatusText = "Applying genre fill…";
            var genreReport = await _tagChanges.ApplyAsync(_runId.Value, genreFills, null, ct).ConfigureAwait(false);

            StatusText = "Creating style playlists…";
            var playlistReport = await _playlistApplier.ApplyAsync(_runId.Value, playlists, ct).ConfigureAwait(false);

            StatusText = "Queuing style backfill…";
            var backfillHashes = tagFixes.Concat(genreFills).Select(p => p.Hash)
                .Concat(playlists.SelectMany(p => p.Hashes))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var backfillReason = await _styleBackfill.QueueStyleBackfillAsync(backfillHashes, ct).ConfigureAwait(false);

            var summary = new MaintenanceRunSummary(
                Applied: tagReport.Applied + genreReport.Applied,
                FileWritten: tagReport.FileWritten + genreReport.FileWritten,
                DbOnly: tagReport.DbOnly + genreReport.DbOnly,
                Failed: tagReport.Failed + genreReport.Failed + playlistReport.Failed);

            await _tagChanges.CompleteRunAsync(_runId.Value, summary, ct).ConfigureAwait(false);
            StatusText = summary.Failed == 0
                ? $"Applied {summary.Applied} changes ({summary.FileWritten} files, {summary.DbOnly} DB only)."
                : $"Applied {summary.Applied} changes with {summary.Failed} failures.";

            if (!string.IsNullOrWhiteSpace(backfillReason))
                _logger.LogWarning("[Maintenance] Style backfill unavailable: {Reason}", backfillReason);
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>Undoes the run: tag changes and playlists, reporting edited-since skips.</summary>
    public async Task RevertAsync(CancellationToken ct = default)
    {
        if (_runId is null) return;
        var tagRevert = await _tagChanges.RevertRunAsync(_runId.Value, ct).ConfigureAwait(false);
        var playlistRevert = await _playlistApplier.RevertAsync(_runId.Value, ct).ConfigureAwait(false);
        StatusText = $"Reverted {tagRevert.Reverted} tag changes (skipped {tagRevert.Skipped}), " +
                     $"deleted {playlistRevert.Deleted} playlists, removed {playlistRevert.TracksRemoved} tracks.";
    }

    public void Dispose() => _disposables.Dispose();
}
