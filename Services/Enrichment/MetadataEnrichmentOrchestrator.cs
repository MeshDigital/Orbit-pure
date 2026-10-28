using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Events;
using SLSKDONET.Models;
using SLSKDONET.Views;

namespace SLSKDONET.Services.Enrichment;

/// <summary>Which providers run and in what order (from config.ini [Enrichment]).</summary>
public sealed record EnrichmentSettings(bool AutoRun, IReadOnlyList<string> Order, IReadOnlySet<string> Enabled)
{
    public static EnrichmentSettings From(SLSKDONET.Configuration.AppConfig config) => new(
        config.EnrichmentAutoRun,
        Split(config.EnrichmentProviderOrder),
        new HashSet<string>(Split(config.EnrichmentEnabledProviders), StringComparer.OrdinalIgnoreCase));

    public static List<string> Split(string? csv) =>
        (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).Distinct().ToList();

    /// <summary>File tags are free and local, so they are always on.</summary>
    public bool IsEnabled(string provider) =>
        Enabled.Contains(provider) || string.Equals(provider, MetadataMerger.TagsSource, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Result of enriching one track (exposed for tests).</summary>
public sealed record TrackEnrichmentOutcome(MetadataPatch Patch, DateTime? DeferUntil, IReadOnlyList<string> ProvidersAsked);

/// <summary>
/// Background library metadata enrichment for thousands of tracks without getting throttled.
/// <list type="bullet">
/// <item><b>Queue</b>: persistent and resumable (<c>EnrichmentTasks</c>); Processing rows left by
///   a crash go back to Queued on start.</item>
/// <item><b>Per track</b>: providers in the configured order (cheapest first); a provider is only
///   asked when it can fill a field that is still empty, and the walk stops once every wanted
///   field is filled. IDs found on the way (ISRC, MBIDs) are passed to later providers.</item>
/// <item><b>Pacing</b>: several tracks in flight, so providers work in parallel, each behind its
///   own rate limiter and circuit breaker (429 + Retry-After pauses it; repeated 403 disables it
///   for the session and the card says so). A track whose useful provider was paused is deferred,
///   not dropped.</item>
/// <item><b>Merge</b>: <see cref="MetadataMerger"/> (never analysis BPM/key/energy, never user
///   edits). Artwork is downloaded into <see cref="ArtworkCacheService"/> before its URL is stored.</item>
/// <item><b>Triggers</b>: a folder import completing, a download landing in the library, the
///   manual "Enrich library" action, and resuming a leftover queue at startup.</item>
/// <item><b>Politeness</b>: one track at a time while the analysis queue has work or audio
///   plays, two while a folder import is writing rows, and <see cref="TracksEnrichedEvent"/> in
///   batches. (It used to stop completely while analysis had work, so after "Analyse 3,000 new
///   tracks" enrichment sat at 0 for the whole analysis run.)</item>
/// <item><b>Imports</b>: an automatic run starts when an import completes. A run the user started
///   mid-import picks up each batch as it lands (<see cref="LibraryEntriesAddedEvent"/>); the
///   completion trigger then skips tracks it already did, so nothing is enriched twice.</item>
/// </list>
/// A DI singleton whose constructor never throws.
/// </summary>
public sealed class MetadataEnrichmentOrchestrator : IDisposable
{
    public const int DefaultWorkers = 4;
    /// <summary>Tracks in flight while a folder import is still adding rows.</summary>
    public const int WorkersDuringImport = 2;
    public const int MaxDeferrals = 3;
    public static readonly TimeSpan RetryCooldown = TimeSpan.FromDays(7);
    private static readonly TimeSpan EventFlushInterval = TimeSpan.FromSeconds(1.5);
    private const int EventFlushSize = 25;

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly EnrichmentStore _store;
    private readonly IReadOnlyList<IMetadataProvider> _providers;
    private readonly ArtworkCacheService? _artwork;
    private readonly IEventBus _eventBus;
    private readonly ILogger<MetadataEnrichmentOrchestrator> _logger;
    private readonly Func<EnrichmentSettings> _settings;
    private readonly Func<bool> _isAnalysisBusy;
    private readonly Func<bool> _isPlaying;
    private readonly Func<bool> _isImportRunning;
    private readonly int _workers;
    private readonly TimeSpan _busyPoll;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly ConcurrentQueue<string> _changed = new();
    private readonly object _runLock = new();
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private volatile bool _paused;
    private volatile bool _slowedForAnalysis;
    private TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTime _lastFlush = DateTime.UtcNow;
    private DateTime _lastProgress = DateTime.MinValue;
    private int _runTotal;
    private int _runDone;
    private int _runEnriched;
    private int _pending;
    private bool _manualRun;
    private volatile bool _running;

    public MetadataEnrichmentOrchestrator(
        IDbContextFactory<AppDbContext> factory,
        EnrichmentStore store,
        IEnumerable<IMetadataProvider> providers,
        IEventBus eventBus,
        ILogger<MetadataEnrichmentOrchestrator> logger,
        SLSKDONET.Configuration.AppConfig config,
        ArtworkCacheService? artwork = null,
        AnalysisQueueService? analysisQueue = null,
        IAudioPlayerService? player = null,
        LibraryFolderScannerService? scanner = null)
        : this(factory, store, providers, eventBus, logger, () => EnrichmentSettings.From(config), artwork,
               () => analysisQueue?.PendingCount > 0, () => player?.IsPlaying == true, DefaultWorkers, TimeSpan.FromSeconds(3),
               startupResumeDelay: TimeSpan.FromSeconds(45), isImportRunning: () => scanner?.IsAnyImportRunning == true)
    {
    }

    internal MetadataEnrichmentOrchestrator(
        IDbContextFactory<AppDbContext> factory,
        EnrichmentStore store,
        IEnumerable<IMetadataProvider> providers,
        IEventBus eventBus,
        ILogger<MetadataEnrichmentOrchestrator> logger,
        Func<EnrichmentSettings> settings,
        ArtworkCacheService? artwork,
        Func<bool> isAnalysisBusy,
        Func<bool> isPlaying,
        int workers,
        TimeSpan busyPoll,
        TimeSpan? startupResumeDelay,
        Func<bool>? isImportRunning = null)
    {
        _factory = factory;
        _store = store;
        _providers = providers.ToList();
        _eventBus = eventBus;
        _logger = logger;
        _settings = settings;
        _artwork = artwork;
        _isAnalysisBusy = isAnalysisBusy;
        _isPlaying = isPlaying;
        _isImportRunning = isImportRunning ?? (() => false);
        _workers = Math.Max(1, workers);
        _busyPoll = busyPoll;
        _resume.TrySetResult();

        try
        {
            _subscriptions.Add(_eventBus.GetEvent<LibraryImportCompletedEvent>().Subscribe(evt => _ = OnImportCompletedAsync(evt)));
            _subscriptions.Add(_eventBus.GetEvent<FileIngestionCompletedEvent>().Subscribe(evt => _ = OnTrackIngestedAsync(evt.TrackUniqueHash)));
            _subscriptions.Add(_eventBus.GetEvent<LibraryEntriesAddedEvent>().Subscribe(evt => _ = OnEntriesAddedAsync(evt)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Enrichment] Could not subscribe to import/download events; enrichment only runs when started manually.");
        }

        if (startupResumeDelay is { } delay)
            _ = ResumeAfterStartupAsync(delay);
    }

    public IReadOnlyList<IMetadataProvider> Providers => _providers;
    public bool IsRunning => _running;
    public bool IsPaused => _paused;

    // ── Triggers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Manual "Enrich library": queues every track still missing metadata (not attempted in the
    /// last <see cref="RetryCooldown"/>, unless <paramref name="includeRecent"/>) and starts.
    /// Returns how many tracks were queued.
    /// </summary>
    public async Task<int> EnrichLibraryAsync(bool includeRecent = false, CancellationToken ct = default)
    {
        var hashes = await FindTracksNeedingMetadataAsync(includeRecent, ct).ConfigureAwait(false);
        var added = await _store.EnqueueAsync(hashes, priority: 0, ct).ConfigureAwait(false);
        _logger.LogInformation("[Enrichment] Manual run: {Count} tracks need metadata, {Added} newly queued", hashes.Count, added);
        _manualRun = true;
        Start();
        return hashes.Count;
    }

    /// <summary>Queues specific tracks (e.g. a selection) with high priority and starts.</summary>
    public async Task<int> EnqueueTracksAsync(IEnumerable<string> hashes, int priority = 2, CancellationToken ct = default)
    {
        var added = await _store.EnqueueAsync(hashes, priority, ct).ConfigureAwait(false);
        Start();
        return added;
    }

    /// <summary>Tracks missing any wanted field (artwork, album, label, year, genre).</summary>
    public async Task<int> CountTracksNeedingMetadataAsync(CancellationToken ct = default) =>
        (await FindTracksNeedingMetadataAsync(includeRecent: false, ct).ConfigureAwait(false)).Count;

    private async Task<List<string>> FindTracksNeedingMetadataAsync(bool includeRecent, CancellationToken ct)
    {
        await using var db = _factory.CreateDbContext();
        var cutoff = (DateTime.UtcNow - RetryCooldown).ToString("O");
        var query = db.LibraryEntries.AsNoTracking().Where(e =>
            e.AlbumArtUrl == null || e.AlbumArtUrl == "" ||
            e.Album == null || e.Album == "" ||
            e.Label == null || e.Label == "" ||
            e.ReleaseDate == null ||
            e.Genres == null || e.Genres == "");
        if (!includeRecent)
            query = query.Where(e => e.LastEnrichmentAttempt == null || e.LastEnrichmentAttempt.CompareTo(cutoff) < 0);
        return await query.OrderByDescending(e => e.AddedAt).Select(e => e.UniqueHash).ToListAsync(ct).ConfigureAwait(false);
    }

    private async Task OnImportCompletedAsync(LibraryImportCompletedEvent evt)
    {
        try
        {
            if (!_settings().AutoRun || evt.Result.AddedTrackHashes.Count == 0) return;
            // A run started mid-import already did (or queued) part of these: skip what it finished.
            var added = await _store.EnqueueAsync(evt.Result.AddedTrackHashes, priority: 1, skipFinishedWithin: RetryCooldown).ConfigureAwait(false);
            _logger.LogInformation("[Enrichment] Folder import added {Count} tracks; {Added} queued for metadata", evt.Result.AddedTrackHashes.Count, added);
            Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Enrichment] Could not queue the imported tracks");
        }
    }

    /// <summary>
    /// A batch of rows landed while an import is still running. Only followed when a run is
    /// active (the user pressed Start mid-import, or a run was already going); otherwise the
    /// completion trigger queues the whole import once, so enrichment never fights the import.
    /// </summary>
    private async Task OnEntriesAddedAsync(LibraryEntriesAddedEvent evt)
    {
        try
        {
            if (evt.TrackHashes.Count == 0 || !(_running || _paused)) return;
            var added = await _store.EnqueueAsync(evt.TrackHashes, priority: 1, skipFinishedWithin: RetryCooldown).ConfigureAwait(false);
            if (added > 0)
            {
                Interlocked.Add(ref _runTotal, added);
                _logger.LogInformation("[Enrichment] {Added} tracks from the running import joined the current run", added);
            }
            Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Enrichment] Could not queue tracks from the running import");
        }
    }

    private async Task OnTrackIngestedAsync(string hash)
    {
        try
        {
            if (!_settings().AutoRun || string.IsNullOrWhiteSpace(hash)) return;
            await _store.EnqueueAsync(new[] { hash }, priority: 2).ConfigureAwait(false);
            Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Enrichment] Could not queue downloaded track {Hash}", hash);
        }
    }

    private async Task ResumeAfterStartupAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
            var reset = await _store.ResetInterruptedAsync(_lifetime.Token).ConfigureAwait(false);
            var pending = await _store.CountPendingAsync(_lifetime.Token).ConfigureAwait(false);
            if (pending > 0 && _settings().AutoRun)
            {
                _logger.LogInformation("[Enrichment] Resuming {Pending} queued tracks ({Reset} interrupted)", pending, reset);
                Start();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Enrichment] Could not resume the enrichment queue at startup");
        }
    }

    // ── Control ──────────────────────────────────────────────────────────────

    /// <summary>Starts the worker loop if it isn't running.</summary>
    public void Start()
    {
        lock (_runLock)
        {
            if (_runTask is { IsCompleted: false }) return;
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var token = _runCts.Token;
            _runTask = Task.Run(() => RunAsync(token), token);
        }
    }

    public void Pause()
    {
        if (_paused) return;
        _paused = true;
        _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PublishProgress(force: true);
    }

    public void Resume()
    {
        if (!_paused) { Start(); return; }
        _paused = false;
        _resume.TrySetResult();
        Start();
        PublishProgress(force: true);
    }

    /// <summary>Stops after the tracks in flight and drops the rest of the queue.</summary>
    public async Task CancelAsync()
    {
        Task? running;
        lock (_runLock)
        {
            _runCts?.Cancel();
            running = _runTask;
        }
        if (_paused) { _paused = false; _resume.TrySetResult(); }
        if (running != null)
        {
            try { await running.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        try
        {
            var dropped = await _store.ClearQueuedAsync().ConfigureAwait(false);
            await _store.ResetInterruptedAsync().ConfigureAwait(false);
            await _store.ClearQueuedAsync().ConfigureAwait(false);
            _logger.LogInformation("[Enrichment] Cancelled; {Dropped} queued tracks dropped", dropped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Enrichment] Could not clear the queue after cancel");
        }
        PublishProgress(force: true);
    }

    /// <summary>Waits for the current run (tests).</summary>
    internal Task WaitForIdleAsync()
    {
        lock (_runLock) return _runTask ?? Task.CompletedTask;
    }

    // ── Loop ─────────────────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        _running = true;
        var faulted = false;
        _runDone = 0;
        _runEnriched = 0;
        try
        {
            await _store.ResetInterruptedAsync(ct).ConfigureAwait(false);
            _pending = await _store.CountPendingAsync(ct).ConfigureAwait(false);
            _runTotal = _pending;
            PublishProgress(force: true);

            while (!ct.IsCancellationRequested)
            {
                if (_paused)
                {
                    PublishProgress(force: true);
                    await _resume.Task.WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                // Network-bound enrichment and CPU-bound analysis can share the machine: slow down
                // to one track at a time instead of stopping for the whole analysis run.
                var analysisBusy = _isAnalysisBusy();
                if (analysisBusy != _slowedForAnalysis) { _slowedForAnalysis = analysisBusy; PublishProgress(force: true); }

                var parallel = _isPlaying() || analysisBusy ? 1 : _isImportRunning() ? Math.Min(_workers, WorkersDuringImport) : _workers;
                var batch = await _store.ClaimAsync(parallel * 2, DateTime.UtcNow, ct).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    var next = await _store.NextDeferredAsync(ct).ConfigureAwait(false);
                    if (next == null) break;
                    var wait = next.Value - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait, ct).ConfigureAwait(false);
                    continue;
                }

                using var gate = new SemaphoreSlim(parallel, parallel);
                var work = batch.Select(async task =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try { await ProcessTaskAsync(task, ct).ConfigureAwait(false); }
                    finally { gate.Release(); }
                }).ToList();
                await Task.WhenAll(work).ConfigureAwait(false);

                _pending = await _store.CountPendingAsync(ct).ConfigureAwait(false);
                if (_runDone + _pending > _runTotal) _runTotal = _runDone + _pending;
                FlushChanged(force: false);
                PublishProgress(force: false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            faulted = true;
            _logger.LogError(ex, "[Enrichment] The enrichment run stopped on an error");
            _eventBus.Publish(new ToastRequestedEvent("Library enrichment stopped", ex.Message, NotificationType.Error, null));
        }
        finally
        {
            FlushChanged(force: true);
            _slowedForAnalysis = false;
            _running = false;
            try { _pending = await _store.CountPendingAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Enrichment] Could not count the remaining queue"); }
            PublishProgress(force: true);
            ReportFinished(ct.IsCancellationRequested);

            // Tracks queued while this run was winding down would otherwise wait for the next trigger.
            if (_pending > 0 && !faulted && !ct.IsCancellationRequested && !_paused)
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(500, _lifetime.Token).ConfigureAwait(false); Start(); }
                    catch (OperationCanceledException) { }
                });
        }
    }

    private void ReportFinished(bool cancelled)
    {
        if (_runDone == 0) return;
        _logger.LogInformation("[Enrichment] Run finished: {Done} tracks processed, {Enriched} got new metadata{Cancelled}",
            _runDone, _runEnriched, cancelled ? " (cancelled)" : string.Empty);
        if (_manualRun || _runDone >= 25)
        {
            var blocked = _providers.Where(p => p.Runtime.Breaker.IsDisabledForSession).Select(p => p.DisplayName).ToList();
            var message = $"{SLSKDONET.Utils.CountFormat.N(_runEnriched)} of {SLSKDONET.Utils.CountFormat.N(_runDone)} tracks got new metadata.";
            if (blocked.Count > 0) message += $" {string.Join(", ", blocked)} refused access and was switched off for this session.";
            _eventBus.Publish(new ToastRequestedEvent(cancelled ? "Enrichment cancelled" : "Library enrichment finished", message,
                blocked.Count > 0 ? NotificationType.Warning : NotificationType.Success, null));
        }
        _manualRun = false;
    }

    private async Task ProcessTaskAsync(EnrichmentTaskEntity task, CancellationToken ct)
    {
        try
        {
            var track = await LoadTrackAsync(task.TrackId, ct).ConfigureAwait(false);
            if (track == null)
            {
                // A download's LibraryEntries row can land a moment after the event; try once more.
                if (task.RetryCount == 0) await _store.DeferAsync(task.Id, DateTime.UtcNow.AddMinutes(1), "library entry not found yet", ct).ConfigureAwait(false);
                else await _store.CompleteAsync(task.Id, ct).ConfigureAwait(false);
                return;
            }

            var outcome = await EnrichTrackAsync(track, ct).ConfigureAwait(false);
            await WriteAsync(outcome.Patch, ct).ConfigureAwait(false);
            if (!outcome.Patch.IsEmpty)
            {
                Interlocked.Increment(ref _runEnriched);
                _changed.Enqueue(track.Hash);
            }

            if (outcome.DeferUntil is { } until && task.RetryCount < MaxDeferrals)
                await _store.DeferAsync(task.Id, until, "a provider was rate limited", ct).ConfigureAwait(false);
            else
            {
                await _store.CompleteAsync(task.Id, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _runDone);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Enrichment] Track {Hash} failed", task.TrackId);
            Interlocked.Increment(ref _runDone);
            try { await _store.FailAsync(task.Id, ex.Message, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception inner) { _logger.LogError(inner, "[Enrichment] Could not mark task {Id} failed", task.Id); }
        }
    }

    /// <summary>The wanted fields for a track under the current settings.</summary>
    internal MetadataField WantedFields(EnrichmentTrack track, EnrichmentSettings settings)
    {
        var wanted = MetadataField.Artwork | MetadataField.Album | MetadataField.Label | MetadataField.ReleaseDate |
                     MetadataField.Isrc | MetadataField.Genre;
        if (track.HasWeakIdentity) wanted |= MetadataField.Identity;
        // Style-level genres only when a provider that has them (Discogs) can actually run;
        // otherwise every track would keep "missing" a field nobody can fill.
        if (_providers.Any(p => p.Supplies.HasFlag(MetadataField.Style) && settings.IsEnabled(p.Name) &&
                                p.GetAvailability() == ProviderAvailability.Ready))
            wanted |= MetadataField.Style;
        return wanted;
    }

    /// <summary>
    /// Walks the providers for one track (cheapest first, early stop) and returns the merged
    /// patch. No database writes; internal for tests.
    /// </summary>
    internal async Task<TrackEnrichmentOutcome> EnrichTrackAsync(EnrichmentTrack track, CancellationToken ct)
    {
        var settings = _settings();
        var provenance = await _store.GetProvenanceAsync(track.Hash, ct).ConfigureAwait(false);
        var patch = new MetadataPatch(track.Hash) { GenresBefore = track.Genres };
        var ids = new KnownIds { Isrc = track.Isrc, MusicBrainzRecordingId = track.MusicBrainzId, SpotifyTrackId = track.SpotifyTrackId };
        var asked = new List<string>();
        DateTime? deferUntil = null;
        var wanted = WantedFields(track, settings);

        var ordered = Ordered(settings).ToList();
        // A file with no usable artist/title: ask the identifiers (AcoustID) right after the tags,
        // so the text-search providers further down can use the identity they recover.
        if (track.HasWeakIdentity)
            ordered = ordered
                .OrderBy(p => p.Name == MetadataMerger.TagsSource ? 0 : p.Supplies.HasFlag(MetadataField.Identity) ? 1 : 2)
                .ToList();

        foreach (var provider in ordered)
        {
            var missing = MetadataMerger.Missing(track, wanted, provenance);
            if (missing == MetadataField.None) break; // early stop: everything wanted is filled
            if ((provider.Supplies & missing) == MetadataField.None) continue;
            if (provider.GetAvailability() != ProviderAvailability.Ready) continue;

            if (provider.Runtime.Breaker.OpenUntil is { } openUntil)
            {
                deferUntil = Min(deferUntil, openUntil);
                continue;
            }

            asked.Add(provider.Name);
            ProviderLookup lookup;
            try
            {
                lookup = await provider.LookupAsync(track, ids, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                provider.Runtime.LastError = ex.Message;
                _logger.LogWarning(ex, "[Enrichment] {Provider} lookup threw for {Hash}", provider.Name, track.Hash);
                continue;
            }

            switch (lookup.Status)
            {
                case LookupStatus.Matched when lookup.Candidate is { } candidate:
                    await PrepareArtworkAsync(candidate, MetadataMerger.Missing(track, wanted, provenance), ct).ConfigureAwait(false);
                    MetadataMerger.Apply(track, provenance, candidate, provider.Name, patch);
                    ids.Isrc ??= MetadataMerger.NormalizeIsrc(candidate.Isrc);
                    ids.MusicBrainzRecordingId ??= candidate.MusicBrainzRecordingId;
                    ids.MusicBrainzReleaseId ??= candidate.MusicBrainzReleaseId;
                    ids.DeezerTrackId ??= candidate.DeezerTrackId;
                    ids.SpotifyTrackId ??= candidate.SpotifyTrackId;
                    break;
                case LookupStatus.RateLimited:
                    deferUntil = Min(deferUntil, DateTime.UtcNow + (lookup.RetryAfter is { } ra && ra > TimeSpan.Zero ? ra : TimeSpan.FromSeconds(30)));
                    break;
                case LookupStatus.Failed:
                    _logger.LogDebug("[Enrichment] {Provider} failed for {Hash}: {Error}", provider.Name, track.Hash, lookup.Error);
                    break;
            }
        }

        // Only defer when something is still missing (a paused provider might have filled it).
        if (MetadataMerger.Missing(track, wanted, provenance) == MetadataField.None) deferUntil = null;
        return new TrackEnrichmentOutcome(patch, deferUntil, asked);
    }

    private IEnumerable<IMetadataProvider> Ordered(EnrichmentSettings settings)
    {
        var byName = _providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in settings.Order)
            if (byName.TryGetValue(name, out var p) && settings.IsEnabled(name) && seen.Add(name)) yield return p;
        foreach (var p in _providers)
            if (settings.IsEnabled(p.Name) && seen.Add(p.Name)) yield return p;
    }

    /// <summary>Embedded bytes → a cached file path; remote URL → downloaded first, dropped if dead.</summary>
    private async Task PrepareArtworkAsync(MetadataCandidate candidate, MetadataField missing, CancellationToken ct)
    {
        if (!missing.HasFlag(MetadataField.Artwork))
        {
            candidate.ArtworkBytes = null;
            candidate.ArtworkUrl = null;
            return;
        }
        if (_artwork == null)
        {
            candidate.ArtworkBytes = null;
            return;
        }
        if (candidate.ArtworkBytes is { Length: > 0 } bytes)
        {
            candidate.ArtworkUrl = await _artwork.StoreEmbeddedArtworkAsync(bytes, ct).ConfigureAwait(false);
            candidate.ArtworkBytes = null;
            return;
        }
        if (!string.IsNullOrWhiteSpace(candidate.ArtworkUrl) && !await _artwork.PrefetchAsync(candidate.ArtworkUrl, ct).ConfigureAwait(false))
            candidate.ArtworkUrl = null;
    }

    private static DateTime Min(DateTime? a, DateTime b) => a is { } x && x < b ? x : b;

    // ── Persistence ──────────────────────────────────────────────────────────

    private async Task<EnrichmentTrack?> LoadTrackAsync(string hash, CancellationToken ct)
    {
        await using var db = _factory.CreateDbContext();
        var e = await db.LibraryEntries.AsNoTracking()
            .Where(x => x.UniqueHash == hash)
            .Select(x => new
            {
                x.UniqueHash, x.Artist, x.Title, x.Album, x.FilePath, x.DurationSeconds, x.CanonicalDuration, x.Label,
                x.ReleaseDate, x.Genres, x.AlbumArtUrl, x.ISRC, x.MusicBrainzId, x.SpotifyTrackId, x.TagBPM,
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (e == null) return null;
        return new EnrichmentTrack
        {
            Hash = e.UniqueHash,
            Artist = e.Artist ?? string.Empty,
            Title = e.Title ?? string.Empty,
            Album = e.Album,
            FilePath = e.FilePath,
            DurationSeconds = e.DurationSeconds is > 0 ? e.DurationSeconds : e.CanonicalDuration is > 0 ? e.CanonicalDuration / 1000.0 : null,
            Label = e.Label,
            ReleaseDate = e.ReleaseDate,
            Genres = e.Genres,
            AlbumArtUrl = e.AlbumArtUrl,
            Isrc = e.ISRC,
            MusicBrainzId = e.MusicBrainzId,
            SpotifyTrackId = e.SpotifyTrackId,
            TagBpm = e.TagBPM,
        };
    }

    /// <summary>
    /// Writes a patch to every stored copy of the track (LibraryEntries, PlaylistTracks, Tracks)
    /// and records provenance. Marks the attempt even when nothing was found, so the 7-day
    /// cooldown applies.
    /// <para>
    /// The patch was worked out from a snapshot read seconds (or, behind a rate limit, minutes)
    /// earlier, while the import, analysis, the tag editor or another enrichment pass may have
    /// written the same row since. So the write re-reads the row inside one IMMEDIATE
    /// transaction (it holds the write lock from the first read, so no other writer can slip in
    /// between) and applies fill-empty against the current values: a field somebody filled in
    /// the meantime is kept, and only fields actually written get provenance. It used to assign
    /// every patch field unconditionally. BPM/key/energy are never in a patch.
    /// </para>
    /// </summary>
    internal async Task WriteAsync(MetadataPatch patch, CancellationToken ct)
    {
        var written = await SqliteBusyRetry.RunAsync(() => WriteOnceAsync(patch, ct), _logger, "enrichment write", ct).ConfigureAwait(false);
        if (written.Count > 0)
        {
            var rows = patch.Provenance.Values.Where(p => written.Contains(p.Field)).ToList();
            await _store.UpsertProvenanceAsync(rows, ct).ConfigureAwait(false); // retries busy itself
        }
    }

    /// <summary>One attempt; returns the provenance fields that were really written to LibraryEntries.</summary>
    private async Task<HashSet<string>> WriteOnceAsync(MetadataPatch patch, CancellationToken ct)
    {
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var db = _factory.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false); // BEGIN IMMEDIATE on SQLite
        var now = DateTime.UtcNow.ToString("O");
        var entry = await db.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == patch.Hash, ct).ConfigureAwait(false);
        if (entry != null)
        {
            entry.EnrichmentAttempts++;
            entry.LastEnrichmentAttempt = now;

            bool Fill(string field, bool isEmpty)
            {
                if (!isEmpty || !patch.Provenance.ContainsKey(field)) return false;
                written.Add(field);
                return true;
            }

            if (patch.Artist != null && Fill(MetadataMerger.Fields.Artist, EnrichmentTrack.IsPlaceholderArtist(entry.Artist))) entry.Artist = patch.Artist;
            if (patch.Title != null && Fill(MetadataMerger.Fields.Title, string.IsNullOrWhiteSpace(entry.Title) ||
                    (!string.IsNullOrWhiteSpace(entry.FilePath) && string.Equals(entry.Title.Trim(), System.IO.Path.GetFileNameWithoutExtension(entry.FilePath), StringComparison.OrdinalIgnoreCase))))
                entry.Title = patch.Title;
            if (patch.Album != null && Fill(MetadataMerger.Fields.Album, string.IsNullOrWhiteSpace(entry.Album))) entry.Album = patch.Album;
            if (patch.Label != null && Fill(MetadataMerger.Fields.Label, string.IsNullOrWhiteSpace(entry.Label))) entry.Label = patch.Label;
            // Styles may replace a broad web genre, but only the exact value the merger saw.
            if (patch.Genres != null && Fill(MetadataMerger.Fields.Genre, string.IsNullOrWhiteSpace(entry.Genres) ||
                    (patch.Provenance.ContainsKey(MetadataMerger.Fields.Style) && patch.GenresBefore != null && string.Equals(entry.Genres, patch.GenresBefore, StringComparison.Ordinal))))
            {
                entry.Genres = patch.Genres;
                if (patch.Provenance.ContainsKey(MetadataMerger.Fields.Style)) written.Add(MetadataMerger.Fields.Style);
            }
            if (patch.Isrc != null && Fill(MetadataMerger.Fields.Isrc, string.IsNullOrWhiteSpace(entry.ISRC))) entry.ISRC = patch.Isrc;
            if (patch.MusicBrainzId != null && Fill(MetadataMerger.Fields.MusicBrainzId, string.IsNullOrWhiteSpace(entry.MusicBrainzId))) entry.MusicBrainzId = patch.MusicBrainzId;
            if (patch.SpotifyTrackId != null && Fill(MetadataMerger.Fields.SpotifyId, string.IsNullOrWhiteSpace(entry.SpotifyTrackId))) entry.SpotifyTrackId = patch.SpotifyTrackId;
            if (patch.AlbumArtUrl != null && Fill(MetadataMerger.Fields.Artwork, string.IsNullOrWhiteSpace(entry.AlbumArtUrl))) entry.AlbumArtUrl = patch.AlbumArtUrl;
            if (patch.ReleaseDate != null && Fill(MetadataMerger.Fields.ReleaseDate, entry.ReleaseDate == null)) entry.ReleaseDate = patch.ReleaseDate;
            if (patch.DurationSeconds != null && Fill(MetadataMerger.Fields.Duration, entry.DurationSeconds is null or <= 0)) entry.DurationSeconds = patch.DurationSeconds;
            if (patch.TagBpm != null && Fill(MetadataMerger.Fields.TagBpm, entry.TagBPM is null)) entry.TagBPM = patch.TagBpm;
            if (written.Count > 0) entry.IsEnriched = true;
        }

        if (!patch.IsEmpty)
        {
            foreach (var pt in await db.PlaylistTracks.Where(p => p.TrackUniqueHash == patch.Hash).ToListAsync(ct).ConfigureAwait(false))
            {
                pt.IsEnriched = true;
                if (patch.Artist != null && EnrichmentTrack.IsPlaceholderArtist(pt.Artist)) pt.Artist = patch.Artist;
                if (patch.Title != null && string.IsNullOrWhiteSpace(pt.Title)) pt.Title = patch.Title;
                if (patch.Album != null && string.IsNullOrWhiteSpace(pt.Album)) pt.Album = patch.Album;
                if (patch.Label != null && string.IsNullOrWhiteSpace(pt.Label)) pt.Label = patch.Label;
                if (patch.Genres != null && string.IsNullOrWhiteSpace(pt.Genres)) pt.Genres = patch.Genres;
                if (patch.Isrc != null && string.IsNullOrWhiteSpace(pt.ISRC)) pt.ISRC = patch.Isrc;
                if (patch.MusicBrainzId != null && string.IsNullOrWhiteSpace(pt.MusicBrainzId)) pt.MusicBrainzId = patch.MusicBrainzId;
                if (patch.AlbumArtUrl != null && string.IsNullOrWhiteSpace(pt.AlbumArtUrl)) pt.AlbumArtUrl = patch.AlbumArtUrl;
                if (patch.ReleaseDate != null && pt.ReleaseDate == null) pt.ReleaseDate = patch.ReleaseDate;
            }
            foreach (var t in await db.Tracks.Where(t => t.GlobalId == patch.Hash).ToListAsync(ct).ConfigureAwait(false))
            {
                t.IsEnriched = true;
                if (patch.Label != null && string.IsNullOrWhiteSpace(t.Label)) t.Label = patch.Label;
                if (patch.Genres != null && string.IsNullOrWhiteSpace(t.Genres)) t.Genres = patch.Genres;
                if (patch.Isrc != null && string.IsNullOrWhiteSpace(t.ISRC)) t.ISRC = patch.Isrc;
                if (patch.MusicBrainzId != null && string.IsNullOrWhiteSpace(t.MusicBrainzId)) t.MusicBrainzId = patch.MusicBrainzId;
                if (patch.AlbumArtUrl != null && string.IsNullOrWhiteSpace(t.AlbumArtUrl)) t.AlbumArtUrl = patch.AlbumArtUrl;
                if (patch.ReleaseDate != null && t.ReleaseDate == null) t.ReleaseDate = patch.ReleaseDate;
            }
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return written;
    }

    // ── Events ───────────────────────────────────────────────────────────────

    private void FlushChanged(bool force)
    {
        if (_changed.IsEmpty) return;
        if (!force && _changed.Count < EventFlushSize && DateTime.UtcNow - _lastFlush < EventFlushInterval) return;
        var batch = new List<string>();
        while (_changed.TryDequeue(out var h)) batch.Add(h);
        _lastFlush = DateTime.UtcNow;
        if (batch.Count == 0) return;
        try
        {
            _eventBus.Publish(new TracksEnrichedEvent(batch.Distinct(StringComparer.Ordinal).ToList()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Enrichment] A TracksEnrichedEvent subscriber threw");
        }
    }

    public EnrichmentProgress CurrentProgress()
    {
        var settings = _settings();
        var providers = _providers.Select(p =>
        {
            var availability = settings.IsEnabled(p.Name) ? p.GetAvailability() : ProviderAvailability.Disabled;
            if (p.Runtime.Breaker.IsDisabledForSession) availability = ProviderAvailability.Blocked;
            return p.Runtime.Snapshot(p.DisplayName, availability);
        }).ToList();
        return new EnrichmentProgress(IsRunning, _paused, _slowedForAnalysis, Volatile.Read(ref _runTotal), Volatile.Read(ref _runDone),
            Volatile.Read(ref _runEnriched), _pending, providers);
    }

    private void PublishProgress(bool force)
    {
        if (!force && DateTime.UtcNow - _lastProgress < TimeSpan.FromMilliseconds(500)) return;
        _lastProgress = DateTime.UtcNow;
        try
        {
            _eventBus.Publish(new EnrichmentProgressEvent(CurrentProgress()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Enrichment] A progress subscriber threw");
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
    }
}
