using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Analysis.GenreFusion;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Similarity;

namespace SLSKDONET.Services.Library.Maintenance;

/// <summary>Why a track's genre is out of date (GA10 #255).</summary>
public enum GenreStaleness
{
    /// <summary>Fused by the current engine from the current library genre.</summary>
    Fresh,
    /// <summary>The stored model outputs are current, but the genre was fused by an older engine (or never),
    /// or the library genre / its source changed since: re-fuse from stored data, no audio.</summary>
    Refuse,
    /// <summary>Style scores, embedding or rhythm are missing or from an older model: needs a Mood-only
    /// analysis (audio decode + ONNX), which <see cref="StyleBackfillService"/> queues.</summary>
    NeedsInference,
}

/// <summary>The counts a refresh scan found.</summary>
/// <param name="Refuse">Tracks whose genre can be re-fused from stored data.</param>
/// <param name="NeedsInference">Tracks that need a Mood-only analysis first.</param>
public sealed record GenreRefreshScan(IReadOnlyList<string> Refuse, int NeedsInference, int Fresh)
{
    public static readonly GenreRefreshScan Empty = new(Array.Empty<string>(), 0, 0);
}

/// <summary>An honest account of a refresh run (CLAUDE.md: never report success for a failure).</summary>
/// <param name="Changed">Re-fused tracks whose audio label or resolved genre changed.</param>
/// <param name="Unchanged">Re-fused tracks whose labels stayed the same (the evidence is now current).</param>
/// <param name="Skipped">Tracks that had no features row any more when their chunk ran.</param>
/// <param name="Failed">Tracks that couldn't be re-fused or saved.</param>
public sealed record GenreRefreshReport(int Changed, int Unchanged, int Skipped, int Failed, bool Cancelled, TimeSpan Elapsed)
{
    public int Refused => Changed + Unchanged;
}

/// <summary>
/// Brings existing rows up to the current genre engine without decoding audio (GA10 #255). Rows whose stored
/// inputs (Discogs-400 style scores, the EffNet embedding for the Jamendo head, BPM, rhythm signature) are
/// current but whose genre came from an older <see cref="GenreFusionEngine.FusionVersion"/> — or from a library
/// genre that has changed since — are re-fused in chunks: thousands of tracks a minute on the CPU. Rows that need
/// fresh model output are counted here and left to <see cref="StyleBackfillService"/>.
/// <para>Writes only the genre columns (<see cref="GenreFeatureWriter"/>), never the embedding, style scores,
/// BPM or file tags. Never throws from its constructor (DI singleton); a missing Jamendo head degrades to
/// Discogs-400-only fusion.</para>
/// </summary>
public sealed class GenreRefreshService
{
    /// <summary>Tracks per load/fuse/save round trip.</summary>
    public const int ChunkSize = 200;

    /// <summary>Measured cost of re-fusing one track (Jamendo head + fusion + save), for the wizard's estimate.</summary>
    public static readonly TimeSpan EstimatedPerTrack = TimeSpan.FromMilliseconds(3);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly StyleBackfillService _styleBackfill;
    private readonly IEventBus _eventBus;
    private readonly ILogger<GenreRefreshService> _logger;
    private readonly IJamendoGenreHead? _jamendo;
    private readonly DiscogsStyleLabels? _styleLabels;
    private readonly Func<PersonalGenreModel?> _personalModel;

    public GenreRefreshService(
        IDbContextFactory<AppDbContext> dbFactory,
        StyleBackfillService styleBackfill,
        IEventBus eventBus,
        ILogger<GenreRefreshService> logger,
        IJamendoGenreHead? jamendo = null,
        DiscogsStyleLabels? styleLabels = null,
        Genre.PersonalGenreHeadService? personalGenre = null,
        Func<PersonalGenreModel?>? personalModel = null)
    {
        _personalModel = personalModel ?? (() => personalGenre?.ActiveModel);
        _dbFactory = dbFactory;
        _styleBackfill = styleBackfill;
        _eventBus = eventBus;
        _logger = logger;
        _jamendo = jamendo;
        _styleLabels = styleLabels;
    }

    /// <summary>The per-row facts staleness is decided from (one projected SQL row per features row).</summary>
    public sealed class StalenessRow
    {
        public string Hash { get; set; } = string.Empty;
        public long? FusionVersion { get; set; }
        public string? EvidenceGenres { get; set; }
        public string? EvidenceSource { get; set; }
        public bool HasEvidence { get; set; }
        public string? PersonalHead { get; set; }
        public string? EmbeddingModelTag { get; set; }
        public bool HasEmbedding { get; set; }
    }

    /// <summary>The personal head id a row fused now would record: the active model's, when its embedding fits.</summary>
    public static string? ExpectedPersonalHead(StalenessRow row, PersonalGenreModel? active) =>
        active is { Enabled: true, BlendWeight: > 0 } && row.HasEmbedding
        && string.Equals(active.EmbeddingModelTag, row.EmbeddingModelTag, StringComparison.Ordinal)
            ? active.Id : null;

    /// <summary>
    /// Pure staleness rule for a row whose model inputs are current (inference staleness is
    /// <see cref="StyleBackfillService"/>'s): stale when there is no evidence yet, the fusion version is older,
    /// or the library genre text or its source differs from what the evidence was fused with.
    /// </summary>
    public static GenreStaleness Classify(StalenessRow row, TrackGenreContext? current, int currentFusionVersion = GenreFusionEngine.FusionVersion,
        string? expectedPersonalHead = null)
    {
        if (!row.HasEvidence || row.FusionVersion is not { } version || version < currentFusionVersion)
            return GenreStaleness.Refuse;
        // A personal head was (re)trained, enabled or switched off since this row was fused (GA11 #256).
        if (!string.Equals(row.PersonalHead, expectedPersonalHead, StringComparison.Ordinal)) return GenreStaleness.Refuse;
        var genres = string.IsNullOrWhiteSpace(current?.Genres) ? null : current!.Genres!.Trim();
        var source = genres is null ? null : current!.Source;
        if (!string.Equals(genres, row.EvidenceGenres, StringComparison.Ordinal)) return GenreStaleness.Refuse;
        if (!string.Equals(source, row.EvidenceSource, StringComparison.Ordinal)) return GenreStaleness.Refuse;
        return GenreStaleness.Fresh;
    }

    /// <summary>Sorts every library track into fresh / re-fuse / needs-inference. Reads only.</summary>
    public async Task<GenreRefreshScan> ScanAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var needsInference = (await _styleBackfill.FindTracksNeedingStylesAsync(ct).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var library = await db.LibraryEntries.AsNoTracking().Select(e => e.UniqueHash).ToListAsync(ct).ConfigureAwait(false);
        var candidates = library.Where(h => !string.IsNullOrEmpty(h) && !needsInference.Contains(h))
            .Distinct(StringComparer.Ordinal).ToList();

        // json_extract keeps the (multi-KB) evidence JSON in SQLite; json_valid guards hand-edited or corrupt rows.
        var rows = await db.Database.SqlQueryRaw<StalenessRow>(@"
            SELECT ""TrackUniqueHash"" AS ""Hash"",
                   CASE WHEN json_valid(""GenreEvidenceJson"") THEN json_extract(""GenreEvidenceJson"", '$.FusionVersion') END AS ""FusionVersion"",
                   CASE WHEN json_valid(""GenreEvidenceJson"") THEN json_extract(""GenreEvidenceJson"", '$.MetadataText') END AS ""EvidenceGenres"",
                   CASE WHEN json_valid(""GenreEvidenceJson"") THEN json_extract(""GenreEvidenceJson"", '$.MetadataSource') END AS ""EvidenceSource"",
                   COALESCE(json_valid(""GenreEvidenceJson""), 0) AS ""HasEvidence"",
                   CASE WHEN json_valid(""GenreEvidenceJson"") THEN json_extract(""GenreEvidenceJson"", '$.PersonalHead') END AS ""PersonalHead"",
                   ""EmbeddingModelTag"" AS ""EmbeddingModelTag"",
                   (""EmbeddingBlob"" IS NOT NULL) AS ""HasEmbedding""
            FROM ""audio_features""").ToListAsync(ct).ConfigureAwait(false);
        var rowByHash = new Dictionary<string, StalenessRow>(StringComparer.Ordinal);
        foreach (var row in rows) rowByHash.TryAdd(row.Hash, row);

        var contexts = await TrackGenreContextQuery.LoadManyAsync(db, candidates, ct).ConfigureAwait(false);

        var active = _personalModel();
        var refuse = new List<string>();
        var fresh = 0;
        foreach (var hash in candidates)
        {
            // No features row at all is "missing styles", which the style backfill already counted.
            if (!rowByHash.TryGetValue(hash, out var row)) continue;
            if (Classify(row, contexts.GetValueOrDefault(hash), expectedPersonalHead: ExpectedPersonalHead(row, active)) == GenreStaleness.Refuse)
                refuse.Add(hash);
            else fresh++;
        }

        _logger.LogInformation("[GenreRefresh] Scan in {Ms} ms: {Refuse} to re-fuse, {Inference} need analysis, {Fresh} current",
            sw.ElapsedMilliseconds, refuse.Count, needsInference.Count, fresh);
        return new GenreRefreshScan(refuse, needsInference.Count, fresh);
    }

    /// <summary>
    /// Re-fuses the given tracks from stored data, <see cref="ChunkSize"/> at a time (one SaveChanges per chunk,
    /// cancellation between chunks). Publishes <see cref="TrackAnalysisUpdatedEvent"/> for each track whose
    /// audio label or resolved genre changed, so open views refresh.
    /// </summary>
    public async Task<GenreRefreshReport> RefuseAsync(IReadOnlyList<string> hashes, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        int changed = 0, unchanged = 0, skipped = 0, failed = 0, done = 0;
        var keys = _styleLabels is { IsUsable: true } labels ? labels.Keys : null;
        var jamendoAvailable = _jamendo?.IsAvailable == true;
        var personal = _personalModel();
        if (!jamendoAvailable)
            _logger.LogInformation("[GenreRefresh] Jamendo head unavailable; re-fusing on the Discogs-400 styles alone");

        var list = hashes.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.Ordinal).ToList();
        progress?.Report((0, list.Count));
        try
        {
            foreach (var chunk in list.Chunk(ChunkSize))
            {
                ct.ThrowIfCancellationRequested();
                var changedInChunk = new List<string>();
                var fusedInChunk = 0;
                try
                {
                    await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                    var rows = await db.AudioFeatures.Where(f => chunk.Contains(f.TrackUniqueHash)).ToListAsync(ct).ConfigureAwait(false);
                    var contexts = await TrackGenreContextQuery.LoadManyAsync(db, chunk, ct).ConfigureAwait(false);
                    var byHash = rows.GroupBy(r => r.TrackUniqueHash).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

                    foreach (var hash in chunk)
                    {
                        if (!byHash.TryGetValue(hash, out var features)) { skipped++; continue; }
                        try
                        {
                            var before = TrackGenreResolver.Resolve(contexts.GetValueOrDefault(hash), AudioGenreOpinion.FromFeatures(features));
                            var audioBefore = features.DetectedSubGenre;
                            var jamendo = jamendoAvailable && features.Embedding is { } embedding
                                ? _jamendo!.PredictJamendo(embedding) ?? Array.Empty<GenreLabelScore>()
                                : (IReadOnlyList<GenreLabelScore>)Array.Empty<GenreLabelScore>();
                            var rhythm = AudioGenreOpinion.TryDeserialize<RhythmSignature>(features.RhythmSignatureJson);
                            var after = GenreFeatureWriter.Apply(features, jamendo,
                                GenreFeatureWriter.DiscogsVotes(features.StyleScores, keys), rhythm, contexts.GetValueOrDefault(hash), personal).Resolved;
                            fusedInChunk++;
                            // Changed = what a view shows changed: the audio label or the resolved genre.
                            if (!string.Equals(features.DetectedSubGenre, audioBefore, StringComparison.Ordinal)
                                || after.Bucket != before.Bucket || !string.Equals(after.DisplayText, before.DisplayText, StringComparison.Ordinal)
                                || !string.Equals(after.Subgenre, before.Subgenre, StringComparison.Ordinal))
                                changedInChunk.Add(hash);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            failed++;
                            db.Entry(features).State = EntityState.Unchanged; // don't save a half-written row
                            _logger.LogWarning(ex, "[GenreRefresh] Couldn't re-fuse {Hash}", hash);
                        }
                    }

                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    changed += changedInChunk.Count;
                    unchanged += fusedInChunk - changedInChunk.Count;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The chunk's save failed: none of its re-fused rows were written.
                    failed += fusedInChunk;
                    changedInChunk.Clear();
                    _logger.LogError(ex, "[GenreRefresh] Saving a chunk of {Count} tracks failed", chunk.Length);
                }

                foreach (var hash in changedInChunk)
                    _eventBus.Publish(new TrackAnalysisUpdatedEvent(hash, DateTime.UtcNow));
                done += chunk.Length;
                progress?.Report((done, list.Count));
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[GenreRefresh] Cancelled after {Done}/{Total} tracks", done, list.Count);
            return new GenreRefreshReport(changed, unchanged, skipped, failed, Cancelled: true, sw.Elapsed);
        }

        _logger.LogInformation("[GenreRefresh] Re-fused {Refused} tracks in {Ms} ms ({Changed} changed genre, {Skipped} skipped, {Failed} failed)",
            changed + unchanged, sw.ElapsedMilliseconds, changed, skipped, failed);
        return new GenreRefreshReport(changed, unchanged, skipped, failed, Cancelled: false, sw.Elapsed);
    }
}
