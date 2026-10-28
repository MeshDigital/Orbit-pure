using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Answers "how complete is the analysis in this playlist, and what exactly is missing?".
///
/// The page needs this per playlist because the useful unit of work for a DJ is a set, not the
/// whole library: a playlist that is 90% prepared is worth finishing, and the 10% is a list you
/// can act on track by track.
///
/// Reads only the data the pipeline actually wrote. It never treats a track as analysed because a
/// row exists — see <see cref="AnalysisCoverageAnalyzer"/> for the per-engine predicates.
/// </summary>
public sealed class AnalysisCoverageService
{
    private readonly IDbContextFactory<AppDbContext> _db;
    private readonly ILogger<AnalysisCoverageService> _logger;
    private readonly TrackFingerprintStore? _fingerprints;

    public AnalysisCoverageService(
        IDbContextFactory<AppDbContext> db,
        ILogger<AnalysisCoverageService> logger,
        TrackFingerprintStore? fingerprints = null)
    {
        _db = db;
        _logger = logger;
        _fingerprints = fingerprints;
    }

    /// <summary>The engines that count towards "fully analysed" on this page.</summary>
    public const AnalysisEngine CoverageMask = AnalysisEngine.Pipeline;

    /// <summary>
    /// Coverage for every track in a playlist that is available offline (downloaded and on disk).
    /// Tracks that are still missing are excluded: they cannot be analysed, and counting them
    /// would make a correctly-filled playlist look permanently incomplete.
    /// </summary>
    public async Task<PlaylistAnalysisCoverage> GetPlaylistCoverageAsync(
        Guid playlistId,
        string playlistName = "",
        CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var tracks = await db.PlaylistTracks
            .AsNoTracking()
            .Where(t => t.PlaylistId == playlistId)
            .Select(t => new Candidate(
                t.TrackUniqueHash,
                t.Artist,
                t.Title,
                t.Status == TrackStatus.Downloaded ||
                    t.AvailabilityState == TrackAvailabilityState.Ready ||
                    t.AvailabilityState == TrackAvailabilityState.LocalUnanalyzed,
                t.ResolvedFilePath))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var coverage = await BuildCoverageAsync(db, tracks, cancellationToken).ConfigureAwait(false);
        return new PlaylistAnalysisCoverage(playlistId, playlistName, coverage);
    }

    /// <summary>
    /// Coverage for the whole library. Used when no playlist is selected, so the page always has
    /// one honest number ("fully analysed in the library") instead of an all-tracks-are-done claim.
    /// </summary>
    public async Task<PlaylistAnalysisCoverage> GetLibraryCoverageAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var tracks = await db.LibraryEntries
            .AsNoTracking()
            .Select(e => new Candidate(
                e.UniqueHash,
                e.Artist,
                e.Title,
                e.AvailabilityState == TrackAvailabilityState.Ready ||
                    e.AvailabilityState == TrackAvailabilityState.LocalUnanalyzed,
                e.FilePath))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var coverage = await BuildCoverageAsync(db, tracks, cancellationToken).ConfigureAwait(false);
        return new PlaylistAnalysisCoverage(null, "Library", coverage);
    }

    private async Task<List<TrackCoverageEntry>> BuildCoverageAsync(
        AppDbContext db,
        List<Candidate> candidates,
        CancellationToken cancellationToken)
    {
        // Availability columns can be stale after a file is moved or deleted. A row only counts
        // towards offline coverage when its actual audio path still resolves to a file on disk.
        var offline = candidates
            .Where(c => c.AvailableOffline &&
                        !string.IsNullOrWhiteSpace(c.FilePath) &&
                        File.Exists(c.FilePath))
            .ToList();

        if (offline.Count == 0) return new List<TrackCoverageEntry>();

        var hashes = offline.Select(c => c.Hash).Distinct().ToList();

        var features = await db.AudioFeatures
            .AsNoTracking()
            .Where(f => hashes.Contains(f.TrackUniqueHash))
            .ToDictionaryAsync(f => f.TrackUniqueHash, cancellationToken)
            .ConfigureAwait(false);

        var cueCounts = await db.CuePoints
            .AsNoTracking()
            .Where(c => hashes.Contains(c.TrackUniqueHash))
            .GroupBy(c => c.TrackUniqueHash)
            .Select(g => new { Hash = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Hash, x => x.Count, cancellationToken)
            .ConfigureAwait(false);

        var result = new List<TrackCoverageEntry>(offline.Count);
        foreach (var candidate in offline)
        {
            features.TryGetValue(candidate.Hash, out var entity);
            var cueCount = cueCounts.TryGetValue(candidate.Hash, out var count) ? count : 0;
            var coverage = AnalysisCoverageAnalyzer.Analyze(entity, CoverageMask, cueCount, HasFingerprintFile(candidate.Hash));
            result.Add(new TrackCoverageEntry(candidate.Hash, candidate.Artist, candidate.Title, coverage));
        }

        _logger.LogDebug(
            "[AnalysisCoverage] {Offline} offline tracks of {Total} candidates, {Rows} feature rows",
            offline.Count, candidates.Count, features.Count);

        return result;
    }

    /// <summary>
    /// True when the track's audio is on disk and playable, which is the only state in which
    /// analysis output is meaningful. Mirrors the availability/state pair the rest of the app uses.
    /// </summary>
    public static bool IsAvailableOffline(TrackStatus status, TrackAvailabilityState availability) =>
        status == TrackStatus.Downloaded ||
        availability is TrackAvailabilityState.Ready or TrackAvailabilityState.LocalUnanalyzed;

    /// <summary>
    /// True when the fingerprint file for this track exists. The pipeline writes fingerprints to
    /// disk (TrackFingerprintStore) rather than into the features row, so this is the only honest
    /// way to tell whether the engine has run.
    /// </summary>
    private bool HasFingerprintFile(string trackHash)
    {
        if (_fingerprints is null) return false;

        try
        {
            return File.Exists(_fingerprints.GetStoragePath(trackHash));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogDebug(ex, "[AnalysisCoverage] Fingerprint lookup failed for {Hash}", trackHash);
            return false;
        }
    }

    private readonly record struct Candidate(
        string Hash,
        string Artist,
        string Title,
        bool AvailableOffline,
        string? FilePath);
}

/// <summary>
/// One playlist's analysis completeness: per-engine rows plus the per-track detail behind them.
/// </summary>
public sealed class PlaylistAnalysisCoverage
{
    public PlaylistAnalysisCoverage(
        Guid? playlistId,
        string playlistName,
        IReadOnlyList<TrackCoverageEntry> tracks)
    {
        PlaylistId = playlistId;
        PlaylistName = playlistName;
        Tracks = tracks;
        EngineRows = AnalysisCoverageAnalyzer.Summarize(tracks.Select(t => t.Coverage));
    }

    public Guid? PlaylistId { get; }

    public string PlaylistName { get; }

    /// <summary>Per-track coverage, keyed by track hash, in playlist order.</summary>
    public IReadOnlyList<TrackCoverageEntry> Tracks { get; }

    /// <summary>One row per engine, in catalog order, with complete/missing counts.</summary>
    public IReadOnlyList<EngineCoverageSummary> EngineRows { get; }

    public int TotalTracks => Tracks.Count;

    /// <summary>Tracks with every engine present.</summary>
    public int FullyAnalyzedTracks => Tracks.Count(t => t.Coverage.IsComplete);

    /// <summary>Tracks with nothing at all — never analysed.</summary>
    public int UnanalysedTracks => Tracks.Count(t => !t.Coverage.HasAnyOutput);

    /// <summary>Tracks with some output but at least one gap.</summary>
    public int PartiallyAnalyzedTracks => TotalTracks - FullyAnalyzedTracks - UnanalysedTracks;

    /// <summary>True when every track in the playlist has every engine.</summary>
    public bool IsComplete => TotalTracks > 0 && FullyAnalyzedTracks == TotalTracks;

    /// <summary>
    /// Every engine missing anywhere in the playlist — the mask for "fill what this playlist lacks".
    /// </summary>
    public AnalysisEngine MissingAnywhere =>
        AnalysisCoverageAnalyzer.MissingUnion(Tracks.Select(t => t.Coverage));

    /// <summary>
    /// The exact per-track work needed for this selection. Complete engines and tracks outside
    /// the selection are never included; duplicate playlist entries collapse into one request.
    /// </summary>
    public IReadOnlyList<TrackCoverageRequest> GetRequests(AnalysisEngine engines) =>
        Tracks
            .Select(t => new TrackCoverageRequest(t.TrackHash, t.Coverage.RunnableMissing & engines))
            .Where(r => r.Engines != AnalysisEngine.None)
            .GroupBy(r => r.TrackHash, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TrackCoverageRequest(
                g.Key,
                g.Aggregate(AnalysisEngine.None, (mask, request) => mask | request.Engines)))
            .ToList();

    /// <summary>Coverage for one track, or null when the track is not in this selection.</summary>
    public TrackAnalysisCoverage? ForTrack(string trackHash) =>
        Tracks.FirstOrDefault(t => string.Equals(t.TrackHash, trackHash, StringComparison.OrdinalIgnoreCase))?.Coverage;
}

/// <summary>
/// A track's identity plus its coverage, so the page can match rows by hash instead of by
/// position — the library list and a playlist are different populations in different orders.
/// </summary>
/// <param name="TrackHash">The track's unique hash.</param>
/// <param name="Artist">Artist, for the track list.</param>
/// <param name="Title">Title, for the track list.</param>
/// <param name="Coverage">What the track has and what it lacks.</param>
public sealed record TrackCoverageEntry(
    string TrackHash,
    string Artist,
    string Title,
    TrackAnalysisCoverage Coverage);

/// <summary>One track and only the requested engines it is still missing.</summary>
public sealed record TrackCoverageRequest(string TrackHash, AnalysisEngine Engines);
