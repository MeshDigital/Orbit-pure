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

    public bool IsEnabled(string provider) => Enabled.Contains(provider);
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
/// <item><b>Politeness</b>: waits while the analysis queue has work, drops to one track at a
///   time while audio plays, and publishes <see cref="TracksEnrichedEvent"/> in batches.</item>
/// </list>
/// A DI singleton whose constructor never throws.
/// </summary>
public sealed class MetadataEnrichmentOrchestrator : IDisposable
{
    public const int DefaultWorkers = 4;
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
    private readonly int _workers;
    private readonly TimeSpan _busyPoll;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly ConcurrentQueue<string> _changed = new();
    private readonly object _runLock = new();
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private volatile bool _paused;
    private volatile bool _waitingForAnalysis;
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
        IAudioPlayerService? player = null)
        : this(factory, store, providers, eventBus, logger, () => EnrichmentSettings.From(config), artwork,
               () => analysisQueue?.PendingCount > 0, () => player?.IsPlaying == true, DefaultWorkers, TimeSpan.FromSeconds(3),
               startupResumeDelay: TimeSpan.FromSeconds(45))
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
        TimeSpan? startupResumeDelay)
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
        _workers = Math.Max(1, workers);
        _busyPoll = busyPoll;
        _resume.TrySetResult();

        try
        {
            _subscriptions.Add(_eventBus.GetEvent<LibraryImportCompletedEvent>().Subscribe(evt => _ = OnImportCompletedAsync(evt)));
            _subscriptions.Add(_eventBus.GetEvent<FileIngestionCompletedEvent>().Subscribe(evt => _ = OnTrackIngestedAsync(evt.TrackUniqueHash)));
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
            var added = await _store.EnqueueAsync(evt.Result.AddedTrackHashes, priority: 1).ConfigureAwait(false);
            _logger.LogInformation("[Enrichment] Folder import added {Count} tracks; {Added} queued for metadata", evt.Result.AddedTrackHashes.Count, added);
            Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Enrichment] Could not queue the imported tracks");
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

                if (_isAnalysisBusy())
                {
                    if (!_waitingForAnalysis) { _waitingForAnalysis = true; PublishProgress(force: true); }
                    await Task.Delay(_busyPoll, ct).ConfigureAwait(false);
                    continue;
                }
                if (_waitingForAnalysis) { _waitingForAnalysis = false; PublishProgress(force: true); }

                var parallel = _isPlaying() ? 1 : _workers;
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
            _waitingForAnalysis = false;
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
            var message = $"{_runEnriched:N0} of {_runDone:N0} tracks got new metadata.";
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
        var patch = new MetadataPatch(track.Hash);
        var ids = new KnownIds { Isrc = track.Isrc, MusicBrainzRecordingId = track.MusicBrainzId, SpotifyTrackId = track.SpotifyTrackId };
        var asked = new List<string>();
        DateTime? deferUntil = null;
        var wanted = WantedFields(track, settings);

        foreach (var provider in Ordered(settings))
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
    /// </summary>
    internal async Task WriteAsync(MetadataPatch patch, CancellationToken ct)
    {
        await using (var db = _factory.CreateDbContext())
        {
            var now = DateTime.UtcNow.ToString("O");
            var entry = await db.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == patch.Hash, ct).ConfigureAwait(false);
            if (entry != null)
            {
                entry.EnrichmentAttempts++;
                entry.LastEnrichmentAttempt = now;
                if (!patch.IsEmpty) entry.IsEnriched = true;
                if (patch.Artist != null) entry.Artist = patch.Artist;
                if (patch.Title != null) entry.Title = patch.Title;
                if (patch.Album != null) entry.Album = patch.Album;
                if (patch.Label != null) entry.Label = patch.Label;
                if (patch.Genres != null) entry.Genres = patch.Genres;
                if (patch.Isrc != null) entry.ISRC = patch.Isrc;
                if (patch.MusicBrainzId != null) entry.MusicBrainzId = patch.MusicBrainzId;
                if (patch.SpotifyTrackId != null) entry.SpotifyTrackId = patch.SpotifyTrackId;
                if (patch.AlbumArtUrl != null) entry.AlbumArtUrl = patch.AlbumArtUrl;
                if (patch.ReleaseDate != null) entry.ReleaseDate = patch.ReleaseDate;
                if (patch.DurationSeconds != null && entry.DurationSeconds is null or <= 0) entry.DurationSeconds = patch.DurationSeconds;
                if (patch.TagBpm != null && entry.TagBPM is null) entry.TagBPM = patch.TagBpm;
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
        }

        if (!patch.IsEmpty)
            await _store.UpsertProvenanceAsync(patch.Provenance.Values.ToList(), ct).ConfigureAwait(false);
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
        return new EnrichmentProgress(IsRunning, _paused, _waitingForAnalysis, _runTotal, _runDone, _runEnriched, _pending, providers);
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
