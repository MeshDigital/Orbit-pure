using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Views;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>A track whose stored loudness is unusable and whose file is on disk.</summary>
public sealed record LoudnessBackfillCandidate(string TrackUniqueHash, string FilePath);

/// <summary>Outcome of one bulk backfill run.</summary>
public sealed record LoudnessBackfillResult(
    int Total,
    int Measured,
    int NoLoudness,
    int Failed,
    int SyncedCopies,
    bool Cancelled,
    TimeSpan Elapsed)
{
    public int Done => Measured + NoLoudness + Failed;
}

/// <summary>Measures one file's integrated loudness; null when it decodes but nothing is above the
/// −70 LUFS gate. Throws when the file can't be read.</summary>
public delegate Task<double?> LoudnessFileMeasurer(string filePath, CancellationToken cancellationToken);

/// <summary>
/// User-triggered, lightweight loudness backfill. Tracks analysed before
/// <see cref="IntegratedLoudnessMeter"/> existed carry Essentia's 0–1 <c>average_loudness</c> (or the
/// column's 0 default) in <c>LoudnessLUFS</c>, which <see cref="IntegratedLoudnessMeter.AsLufs"/>
/// rejects, so they show "—" and play at unity gain. A full re-analysis would fix that but costs
/// minutes per track; this measures just the loudness:
///
/// <list type="bullet">
/// <item>Selection: <c>audio_features</c> rows whose LoudnessLUFS fails <c>AsLufs</c> (≥ 0, below
///   −70, non-finite) and whose file (PlaylistTracks → LibraryEntries → Tracks path, the analysis
///   queue's order) exists. Re-running only finds what is still unmeasured, so the job resumes.</item>
/// <item>Decode: FFmpeg, the same 44.1 kHz stereo float conversion analysis uses, streamed into the
///   meter (<see cref="AudioIngestionPipeline.DecodePcmStreamAsync"/>), so no temp WAV and no
///   whole-track buffer. True peak is left alone: nothing in the app measures it yet.</item>
/// <item>Write-back: every stored copy, exactly as analysis leaves them: audio_features.LoudnessLUFS
///   (0 = no gated loudness), IsDynamicCompressed re-derived from it, and Loudness on PlaylistTracks,
///   LibraryEntries and Tracks. Results are committed every <see cref="WriteBatchSize"/> tracks so a
///   cancel or crash loses at most one batch.</item>
/// <item>Throttling: <see cref="DefaultParallelism"/> files at a time on below-normal-priority
///   threads, and the run waits whenever the analysis queue has work.</item>
/// </list>
///
/// Never throws from its constructor (DI singleton); failures are logged and counted.
/// </summary>
public sealed class LoudnessBackfillService : IDisposable
{
    public const int DefaultParallelism = 2;
    public const int WriteBatchSize = 16;
    private const float AbsoluteGateLufs = -70f;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IEventBus _eventBus;
    private readonly ILogger<LoudnessBackfillService> _logger;
    private readonly LoudnessFileMeasurer _measure;
    private readonly Func<bool> _isAnalysisBusy;
    private readonly TimeSpan _busyPollInterval;
    private readonly int _parallelism;
    private readonly IDisposable? _requestSubscription;

    // Decoded fine but nothing above the gate (silence, a sub-400 ms file). Analysis stores 0 for
    // those too, so they still look unmeasured; remembering them keeps a re-run this session from
    // decoding them again. Read failures are not remembered: a NAS that was offline may be back.
    private readonly ConcurrentDictionary<string, byte> _noLoudness = new(StringComparer.Ordinal);

    private readonly object _runLock = new();
    private CancellationTokenSource? _runCts;
    private Task<LoudnessBackfillResult>? _runTask;
    private int _running;
    private LoudnessBackfillProgressEvent _progress = new(0, 0, 0, 0, false, false);

    public LoudnessBackfillService(
        IDbContextFactory<AppDbContext> dbFactory,
        AudioIngestionPipeline ingestion,
        IEventBus eventBus,
        ILogger<LoudnessBackfillService> logger,
        AnalysisQueueService? analysisQueue = null)
        : this(dbFactory, eventBus, logger,
               (path, ct) => MeasureWithFfmpegAsync(ingestion, path, ct),
               () => analysisQueue?.PendingCount > 0,
               DefaultParallelism,
               TimeSpan.FromSeconds(2))
    {
    }

    internal LoudnessBackfillService(
        IDbContextFactory<AppDbContext> dbFactory,
        IEventBus eventBus,
        ILogger<LoudnessBackfillService> logger,
        LoudnessFileMeasurer measure,
        Func<bool> isAnalysisBusy,
        int parallelism,
        TimeSpan busyPollInterval)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
        _logger = logger;
        _measure = measure;
        _isAnalysisBusy = isAnalysisBusy;
        _parallelism = Math.Max(1, parallelism);
        _busyPollInterval = busyPollInterval;

        try
        {
            _requestSubscription = _eventBus.GetEvent<MeasureTrackLoudnessRequestedEvent>()
                // Off the publisher's (UI) thread: EF's SQLite "async" queries run synchronously.
                .Subscribe(evt => _ = Task.Run(() => MeasureTrackAsync(evt.TrackGlobalId)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LoudnessBackfill] Could not subscribe to single-track measure requests; the inspector's Measure link won't work.");
        }
    }

    /// <summary>True while a bulk run is in progress.</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>The latest progress snapshot (also published as <see cref="LoudnessBackfillProgressEvent"/>).</summary>
    public LoudnessBackfillProgressEvent Progress => Volatile.Read(ref _progress);

    // ── Selection ────────────────────────────────────────────────────────────

    /// <summary>Tracks with unusable stored loudness whose file exists, in a stable order.</summary>
    public async Task<IReadOnlyList<LoudnessBackfillCandidate>> FindCandidatesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // SQL narrows to "not in [-70, 0)"; AsLufs then applies the exact rule (non-finite included).
        var rows = await db.AudioFeatures.AsNoTracking()
            .Where(f => !(f.LoudnessLUFS < 0f && f.LoudnessLUFS >= AbsoluteGateLufs)
                        && !(f.LoudnessLUFS <= IntegratedLoudnessMeter.SilentMarker + 0.5f && f.LoudnessLUFS >= IntegratedLoudnessMeter.SilentMarker - 0.5f))
            .Select(f => new { f.TrackUniqueHash, f.LoudnessLUFS })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var hashes = rows
            .Where(r => !string.IsNullOrEmpty(r.TrackUniqueHash)
                        && !IntegratedLoudnessMeter.IsMeasured(r.LoudnessLUFS)
                        && !_noLoudness.ContainsKey(r.TrackUniqueHash))
            .Select(r => r.TrackUniqueHash)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();
        if (hashes.Count == 0) return Array.Empty<LoudnessBackfillCandidate>();

        var paths = await ResolvePathsAsync(db, hashes, cancellationToken).ConfigureAwait(false);
        var candidates = new List<LoudnessBackfillCandidate>(hashes.Count);
        foreach (var hash in hashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (paths.TryGetValue(hash, out var options) && options.FirstOrDefault(File.Exists) is { } path)
                candidates.Add(new LoudnessBackfillCandidate(hash, path));
        }
        return candidates;
    }

    public async Task<int> CountCandidatesAsync(CancellationToken cancellationToken = default) =>
        (await FindCandidatesAsync(cancellationToken).ConfigureAwait(false)).Count;

    /// <summary>Every recorded path per hash, in the analysis queue's preference order.</summary>
    private static async Task<Dictionary<string, List<string>>> ResolvePathsAsync(
        AppDbContext db, IReadOnlyCollection<string> hashes, CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string hash, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!result.TryGetValue(hash, out var list)) result[hash] = list = new List<string>(2);
            if (!list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
        }

        foreach (var r in await db.PlaylistTracks.AsNoTracking()
                     .Where(t => hashes.Contains(t.TrackUniqueHash) && t.ResolvedFilePath != null && t.ResolvedFilePath != "")
                     .Select(t => new { t.TrackUniqueHash, t.ResolvedFilePath })
                     .ToListAsync(ct).ConfigureAwait(false))
            Add(r.TrackUniqueHash, r.ResolvedFilePath);

        foreach (var r in await db.LibraryEntries.AsNoTracking()
                     .Where(e => hashes.Contains(e.UniqueHash) && e.FilePath != "")
                     .Select(e => new { e.UniqueHash, e.FilePath })
                     .ToListAsync(ct).ConfigureAwait(false))
            Add(r.UniqueHash, r.FilePath);

        foreach (var r in await db.Tracks.AsNoTracking()
                     .Where(t => hashes.Contains(t.GlobalId) && t.LocalFilePath != null && t.LocalFilePath != "")
                     .Select(t => new { t.GlobalId, t.LocalFilePath })
                     .ToListAsync(ct).ConfigureAwait(false))
            Add(r.GlobalId, r.LocalFilePath);

        return result;
    }

    // ── Bulk run ─────────────────────────────────────────────────────────────

    /// <summary>Starts a bulk run in the background. False when one is already running.</summary>
    public bool TryStart()
    {
        lock (_runLock)
        {
            if (IsRunning) return false;
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;
            _runTask = Task.Run(() => RunAsync(token), CancellationToken.None);
            return true;
        }
    }

    /// <summary>Stops the running bulk job after the files in flight; what was measured is kept.</summary>
    public void Cancel()
    {
        lock (_runLock) _runCts?.Cancel();
    }

    /// <summary>
    /// Measures every candidate and writes the results. Cancellation stops it cleanly (results so
    /// far are committed and <see cref="LoudnessBackfillResult.Cancelled"/> is set); it never throws
    /// for unreadable files or a failed write, which are logged and counted as failed.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another run is already in progress.</exception>
    public async Task<LoudnessBackfillResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("A loudness backfill is already running.");

        var started = DateTime.UtcNow;
        int total = 0, done = 0, measured = 0, noLoudness = 0, failed = 0, synced = 0;
        bool cancelled = false;
        try
        {
            PublishProgress(0, 0, 0, 0, isRunning: true, waiting: false);

            synced = await SyncStaleCopiesAsync(cancellationToken).ConfigureAwait(false);
            var candidates = await FindCandidatesAsync(cancellationToken).ConfigureAwait(false);
            total = candidates.Count;
            PublishProgress(0, total, 0, 0, isRunning: true, waiting: false);
            _logger.LogInformation("[LoudnessBackfill] {Total} track(s) to measure ({Synced} stale copies synced from existing measurements)", total, synced);

            foreach (var batch in candidates.Chunk(WriteBatchSize))
            {
                if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }

                var results = new ConcurrentBag<(string Hash, double? Lufs)>();
                try
                {
                    await Parallel.ForEachAsync(batch,
                        new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = cancellationToken },
                        async (candidate, token) =>
                        {
                            await WaitWhileAnalysisBusyAsync(total, () => (Volatile.Read(ref done), Volatile.Read(ref failed), Volatile.Read(ref measured)), token).ConfigureAwait(false);
                            try
                            {
                                var lufs = await _measure(candidate.FilePath, token).ConfigureAwait(false);
                                results.Add((candidate.TrackUniqueHash, lufs));
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref failed);
                                _logger.LogWarning(ex, "[LoudnessBackfill] Could not measure {File}; skipped.", Path.GetFileName(candidate.FilePath));
                                _eventBus.Publish(new TrackLoudnessMeasuredEvent(candidate.TrackUniqueHash, null, false, ex.Message));
                            }
                            Interlocked.Increment(ref done);
                            PublishProgress(Volatile.Read(ref done), total, Volatile.Read(ref failed), Volatile.Read(ref measured) + results.Count(r => r.Lufs is not null), isRunning: true, waiting: false);
                        }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                }

                // Commit what this batch measured, even when cancelled mid-batch.
                if (!results.IsEmpty)
                {
                    var written = await WriteResultsAsync(results.ToList()).ConfigureAwait(false);
                    if (written is null)
                    {
                        Interlocked.Add(ref failed, results.Count);
                    }
                    else
                    {
                        foreach (var (hash, stored) in written)
                        {
                            if (stored is null) { noLoudness++; _noLoudness.TryAdd(hash, 0); }
                            else measured++;
                            _eventBus.Publish(new TrackLoudnessMeasuredEvent(hash, stored, true));
                            _eventBus.Publish(new TrackAnalysisUpdatedEvent(hash, DateTime.UtcNow));
                        }
                    }
                }

                if (cancelled) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LoudnessBackfill] Run aborted");
            _eventBus.Publish(new ToastRequestedEvent("Loudness measurement failed",
                $"Stopped after {done.ToString("N0", CultureInfo.InvariantCulture)} tracks: {ex.Message}", NotificationType.Error, null));
            var aborted = new LoudnessBackfillResult(total, measured, noLoudness, failed, synced, cancelled, DateTime.UtcNow - started);
            Finish(aborted);
            return aborted;
        }

        var result = new LoudnessBackfillResult(total, measured, noLoudness, failed, synced, cancelled, DateTime.UtcNow - started);
        _logger.LogInformation(
            "[LoudnessBackfill] {State}: {Measured} measured, {NoLoudness} without gated loudness, {Failed} failed of {Total} in {Elapsed:0.0}s",
            cancelled ? "Cancelled" : "Done", measured, noLoudness, failed, total, result.Elapsed.TotalSeconds);

        if (measured + noLoudness > 0 || synced > 0)
            _eventBus.Publish(new SLSKDONET.Events.LibraryUpdatedEvent("Loudness backfill"));
        // Always report, so a start from the Library menu with nothing to do isn't a silent no-op.
        _eventBus.Publish(new ToastRequestedEvent(
            cancelled ? "Loudness measurement stopped" : "Loudness measured",
            BuildSummary(result),
            failed > 0 ? NotificationType.Warning
                : total == 0 && synced == 0 ? NotificationType.Information
                : NotificationType.Success,
            null));

        Finish(result);
        return result;
    }

    private void Finish(LoudnessBackfillResult result)
    {
        Volatile.Write(ref _running, 0);
        PublishProgress(result.Done, result.Total, result.Failed, result.Measured, isRunning: false, waiting: false);
    }

    /// <summary>"2,397 of 2,400 tracks measured · 3 failed", invariant digits.</summary>
    public static string BuildSummary(LoudnessBackfillResult r)
    {
        static string N(int v) => v.ToString("N0", CultureInfo.InvariantCulture);
        var text = r.Cancelled
            ? $"{N(r.Done)} of {N(r.Total)} tracks done before stopping. Run it again to continue."
            : r.Total == 0
                ? "Nothing needed measuring."
                : $"{N(r.Measured)} of {N(r.Total)} tracks measured.";
        if (r.SyncedCopies > 0) text += $" {N(r.SyncedCopies)} stale copies updated from existing measurements.";
        if (r.NoLoudness > 0) text += $" {N(r.NoLoudness)} silent or too short.";
        if (r.Failed > 0) text += $" {N(r.Failed)} failed (unreadable file; see the log).";
        return text;
    }

    private async Task WaitWhileAnalysisBusyAsync(int total, Func<(int Done, int Failed, int Measured)> snapshot, CancellationToken ct)
    {
        bool announced = false;
        while (SafeIsAnalysisBusy())
        {
            if (!announced)
            {
                var (d, f, m) = snapshot();
                PublishProgress(d, total, f, m, isRunning: true, waiting: true);
                announced = true;
            }
            await Task.Delay(_busyPollInterval, ct).ConfigureAwait(false);
        }
    }

    private bool SafeIsAnalysisBusy()
    {
        try { return _isAnalysisBusy(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LoudnessBackfill] Analysis-busy probe failed; continuing.");
            return false;
        }
    }

    private void PublishProgress(int done, int total, int failed, int measured, bool isRunning, bool waiting)
    {
        var progress = new LoudnessBackfillProgressEvent(done, total, failed, measured, isRunning, waiting);
        Volatile.Write(ref _progress, progress);
        _eventBus.Publish(progress);
    }

    // ── Single track ─────────────────────────────────────────────────────────

    /// <summary>
    /// Measures one track now (the inspector's "Measure" link), regardless of the analysis queue,
    /// and writes it to every stored copy. Never throws; the outcome is returned and published as
    /// <see cref="TrackLoudnessMeasuredEvent"/>.
    /// </summary>
    public async Task<TrackLoudnessMeasuredEvent> MeasureTrackAsync(string trackHash, CancellationToken cancellationToken = default)
    {
        TrackLoudnessMeasuredEvent outcome;
        try
        {
            string? path;
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                var paths = await ResolvePathsAsync(db, new[] { trackHash }, cancellationToken).ConfigureAwait(false);
                path = paths.TryGetValue(trackHash, out var options) ? options.FirstOrDefault(File.Exists) : null;
            }

            if (path is null)
            {
                outcome = new TrackLoudnessMeasuredEvent(trackHash, null, false, "The audio file isn't on disk.");
            }
            else
            {
                var lufs = await _measure(path, cancellationToken).ConfigureAwait(false);
                var written = await WriteResultsAsync(new List<(string, double?)> { (trackHash, lufs) }).ConfigureAwait(false);
                outcome = written is null
                    ? new TrackLoudnessMeasuredEvent(trackHash, null, false, "Couldn't save the result to the library.")
                    : new TrackLoudnessMeasuredEvent(trackHash, written[0].Stored, true);
                if (written is not null && lufs is null) _noLoudness.TryAdd(trackHash, 0);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = new TrackLoudnessMeasuredEvent(trackHash, null, false, "Cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LoudnessBackfill] Single-track measurement failed for {Hash}", trackHash);
            outcome = new TrackLoudnessMeasuredEvent(trackHash, null, false, ex.Message);
        }

        _eventBus.Publish(outcome);
        if (outcome.Success)
            _eventBus.Publish(new TrackAnalysisUpdatedEvent(trackHash, DateTime.UtcNow));
        else
            _eventBus.Publish(new ToastRequestedEvent("Couldn't measure loudness", outcome.Error ?? "Unknown error.", NotificationType.Error, null));
        return outcome;
    }

    // ── Write-back ───────────────────────────────────────────────────────────

    /// <summary>
    /// Writes measurements to every stored copy in one SaveChanges, mirroring what analysis does:
    /// audio_features.LoudnessLUFS as float (0 = not measurable), IsDynamicCompressed re-derived from
    /// it, and <c>AsLufs</c> of that float on PlaylistTracks / LibraryEntries / Tracks. Returns the
    /// value stored on the track rows per hash, or null when the write failed (logged).
    /// </summary>
    internal async Task<List<(string Hash, double? Stored)>?> WriteResultsAsync(IReadOnlyList<(string Hash, double? Lufs)> results)
    {
        var featureValue = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (hash, lufs) in results)
            featureValue[hash] = lufs is double v && double.IsFinite(v) ? (float)v : IntegratedLoudnessMeter.SilentMarker;
        var stored = featureValue.ToDictionary(kv => kv.Key, kv => IntegratedLoudnessMeter.AsLufs(kv.Value), StringComparer.Ordinal);
        var hashes = featureValue.Keys.ToList();

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

            foreach (var f in await db.AudioFeatures.Where(f => hashes.Contains(f.TrackUniqueHash)).ToListAsync().ConfigureAwait(false))
                ApplyToFeatures(f, featureValue[f.TrackUniqueHash]);
            foreach (var row in await db.PlaylistTracks.Where(t => hashes.Contains(t.TrackUniqueHash)).ToListAsync().ConfigureAwait(false))
                row.Loudness = stored[row.TrackUniqueHash];
            foreach (var row in await db.LibraryEntries.Where(e => hashes.Contains(e.UniqueHash)).ToListAsync().ConfigureAwait(false))
                row.Loudness = stored[row.UniqueHash];
            foreach (var row in await db.Tracks.Where(t => hashes.Contains(t.GlobalId)).ToListAsync().ConfigureAwait(false))
                row.Loudness = stored[row.GlobalId];

            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LoudnessBackfill] Writing {Count} loudness result(s) failed; they stay unmeasured and a re-run retries them.", hashes.Count);
            return null;
        }

        return hashes.Select(h => (h, stored[h])).ToList();
    }

    private static void ApplyToFeatures(AudioFeaturesEntity features, float lufs)
    {
        features.LoudnessLUFS = lufs;
        // EnergyScoringService's rule, which a legacy 0–1 value made true for nearly every track
        // with low dynamic complexity. Only re-derived when Essentia ran (DynamicComplexity set).
        if (lufs < 0f && features.DynamicComplexity > 0f)
            features.IsDynamicCompressed = features.DynamicComplexity < 2.0f && lufs > -7f;
    }

    /// <summary>
    /// Pure DB pass, no decoding: copies an already valid audio_features loudness onto track rows
    /// that still hold a legacy value or none (e.g. a playlist row added after analysis).
    /// </summary>
    private async Task<int> SyncStaleCopiesAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var valid = (await db.AudioFeatures.AsNoTracking()
                .Where(f => f.LoudnessLUFS < 0f && f.LoudnessLUFS >= AbsoluteGateLufs)
                .Select(f => new { f.TrackUniqueHash, f.LoudnessLUFS })
                .ToListAsync(ct).ConfigureAwait(false))
            .Where(r => !string.IsNullOrEmpty(r.TrackUniqueHash))
            .GroupBy(r => r.TrackUniqueHash, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => IntegratedLoudnessMeter.AsLufs(g.First().LoudnessLUFS), StringComparer.Ordinal);
        if (valid.Count == 0) return 0;
        var keys = valid.Keys.ToList();

        int synced = 0;
        foreach (var row in await db.PlaylistTracks
                     .Where(t => keys.Contains(t.TrackUniqueHash) && (t.Loudness == null || !(t.Loudness < 0 && t.Loudness >= -70)))
                     .ToListAsync(ct).ConfigureAwait(false))
        { row.Loudness = valid[row.TrackUniqueHash]; synced++; }
        foreach (var row in await db.LibraryEntries
                     .Where(e => keys.Contains(e.UniqueHash) && (e.Loudness == null || !(e.Loudness < 0 && e.Loudness >= -70)))
                     .ToListAsync(ct).ConfigureAwait(false))
        { row.Loudness = valid[row.UniqueHash]; synced++; }
        foreach (var row in await db.Tracks
                     .Where(t => keys.Contains(t.GlobalId) && (t.Loudness == null || !(t.Loudness < 0 && t.Loudness >= -70)))
                     .ToListAsync(ct).ConfigureAwait(false))
        { row.Loudness = valid[row.GlobalId]; synced++; }

        if (synced > 0) await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return synced;
    }

    // ── Decode ───────────────────────────────────────────────────────────────

    private static async Task<double?> MeasureWithFfmpegAsync(AudioIngestionPipeline ingestion, string path, CancellationToken ct)
    {
        var meter = new IntegratedLoudnessMeter.Accumulator(AudioIngestionPipeline.TargetSampleRate, AudioIngestionPipeline.TargetChannels);
        await ingestion.DecodePcmStreamAsync(path, chunk => meter.Add(chunk), ct).ConfigureAwait(false);
        if (meter.SegmentCount == 0)
            throw new InvalidOperationException("FFmpeg decoded no audio from this file.");
        return meter.GetIntegratedLufs();
    }

    public void Dispose()
    {
        _requestSubscription?.Dispose();
        lock (_runLock)
        {
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = null;
        }
    }
}
