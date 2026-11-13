using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Library.Maintenance;

public sealed record ContentHashBackfillProgress(int Done, int Total, string? CurrentPath, int Hashed = 0, int Unreadable = 0, int Missing = 0);

public sealed record DuplicateContentGroup(string AudioContentHash, IReadOnlyList<string> TrackHashes);

/// <summary>One library entry inside an exact-duplicate group, with what the keeper choice weighs.</summary>
public sealed record ExactDuplicateEntry(
    string UniqueHash,
    string Artist,
    string Title,
    string Album,
    string FilePath,
    string Format,
    int Bitrate,
    DateTime AddedAt,
    int CueCount,
    int PlaylistCount,
    bool IsAnalyzed,
    bool FileExists);

public sealed record ExactDuplicateGroupDetails(
    string AudioContentHash,
    IReadOnlyList<ExactDuplicateEntry> Entries,
    string SuggestedKeeperHash);

/// <summary>
/// Picks the copy to keep in an exact-duplicate group (issue #238): a file that is on disk, then
/// the best quality tier, then the most cues and analysis, then the most playlist memberships,
/// then the oldest entry. The DJ can override it in the review.
/// </summary>
public static class ExactDuplicateKeeper
{
    /// <summary>3 lossless · 2 ≥ 320 kbps · 1 lower bitrate · 0 unknown (same tiers as the Library's quality chips).</summary>
    public static int QualityRank(string? format, int bitrate)
    {
        if (!string.IsNullOrWhiteSpace(format) &&
            SLSKDONET.Services.Audio.AudioFormats.LosslessTokens.Contains(format.Trim().TrimStart('.').ToLowerInvariant()))
            return 3;
        if (bitrate >= 320) return 2;
        return bitrate > 0 ? 1 : 0;
    }

    public static string QualityLabel(string? format, int bitrate) => QualityRank(format, bitrate) switch
    {
        3 => "Lossless",
        2 => "High",
        1 => "Low",
        _ => "Unknown",
    };

    public static ExactDuplicateEntry Suggest(IEnumerable<ExactDuplicateEntry> entries) =>
        entries
            .OrderByDescending(e => e.FileExists)
            .ThenByDescending(e => QualityRank(e.Format, e.Bitrate))
            .ThenByDescending(e => e.CueCount)
            .ThenByDescending(e => e.IsAnalyzed)
            .ThenByDescending(e => e.PlaylistCount)
            .ThenBy(e => e.AddedAt)
            .ThenBy(e => e.UniqueHash, StringComparer.Ordinal)
            .First();
}

/// <summary>
/// Backfills <c>LibraryEntryEntity.AudioContentHash</c> (issue #238) one file at a time, and
/// groups entries that share the same tag-independent hash.
/// <para>
/// The backfill only touches entries without a hash, so a cancelled run simply picks up where it
/// stopped next time. It never starts on its own: the exact-duplicate review starts it.
/// <see cref="TryStart"/> runs it in the background and publishes
/// <see cref="ContentHashBackfillProgressEvent"/> for the review and the activity chip.
/// </para>
/// </summary>
public class LibraryContentHashBackfillService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly AudioContentHasher _hasher;
    private readonly ILogger<LibraryContentHashBackfillService> _logger;
    private readonly IEventBus? _eventBus;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public LibraryContentHashBackfillService(
        IDbContextFactory<AppDbContext> factory,
        AudioContentHasher hasher,
        ILogger<LibraryContentHashBackfillService> logger,
        IEventBus? eventBus = null)
    {
        _factory = factory;
        _hasher = hasher;
        _logger = logger;
        _eventBus = eventBus;
    }

    public bool IsRunning => _gate.CurrentCount == 0 || _runTask is { IsCompleted: false };

    /// <summary>The latest progress of the background run (null before the first run).</summary>
    public ContentHashBackfillProgressEvent? LastProgress { get; private set; }

    /// <summary>Starts the backfill in the background. False when one is already running.</summary>
    public bool TryStart()
    {
        lock (_runLock)
        {
            if (IsRunning) return false;
            var cts = new CancellationTokenSource();
            _runCts = cts;
            Publish(new ContentHashBackfillProgressEvent(true, 0, 0, 0, 0, 0, false, null));
            _runTask = Task.Run(() => RunInBackgroundAsync(cts));
            return true;
        }
    }

    /// <summary>Stops the background run after the current file; the next run resumes.</summary>
    public void Cancel()
    {
        lock (_runLock) _runCts?.Cancel();
    }

    /// <summary>Waits for the background run (tests).</summary>
    internal Task WaitForRunAsync() => _runTask ?? Task.CompletedTask;

    private async Task RunInBackgroundAsync(CancellationTokenSource cts)
    {
        ContentHashBackfillProgress? last = null;
        var progress = new InlineProgress(p =>
        {
            last = p;
            Publish(new ContentHashBackfillProgressEvent(true, p.Done, p.Total, p.Hashed, p.Unreadable, p.Missing, false, null));
        });

        try
        {
            await BackfillAsync(progress, cts.Token).ConfigureAwait(false);
            Publish(Finished(last, cancelled: false, error: null));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Content-hash backfill cancelled after {Done} entries", last?.Done ?? 0);
            Publish(Finished(last, cancelled: true, error: null));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Content-hash backfill failed");
            Publish(Finished(last, cancelled: false, error: ex.Message));
        }
        finally
        {
            lock (_runLock)
            {
                if (ReferenceEquals(_runCts, cts)) _runCts = null;
            }
            cts.Dispose();
        }
    }

    private static ContentHashBackfillProgressEvent Finished(ContentHashBackfillProgress? p, bool cancelled, string? error) =>
        new(false, p?.Done ?? 0, p?.Total ?? 0, p?.Hashed ?? 0, p?.Unreadable ?? 0, p?.Missing ?? 0, cancelled, error);

    private void Publish(ContentHashBackfillProgressEvent e)
    {
        LastProgress = e;
        try { _eventBus?.Publish(e); }
        catch (Exception ex) { _logger.LogWarning(ex, "Content-hash backfill: publishing progress failed"); }
    }

    public async Task<int> BackfillAsync(IProgress<ContentHashBackfillProgress>? progress, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return 0; // already running; the caller can resume later

        try
        {
            List<(string Hash, string Path)> pending;
            await using (var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                pending = await db.LibraryEntries
                    .AsNoTracking()
                    .Where(e => (e.AudioContentHash == null || e.AudioContentHash == "") &&
                                e.FilePath != null && e.FilePath != "")
                    .OrderBy(e => e.AddedAt)
                    .Select(e => new ValueTuple<string, string>(e.UniqueHash, e.FilePath!))
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
            }

            int done = 0, updated = 0, unreadable = 0, missing = 0;
            progress?.Report(new ContentHashBackfillProgress(0, pending.Count, null));
            foreach (var item in pending)
            {
                ct.ThrowIfCancellationRequested();

                if (!File.Exists(item.Path))
                {
                    missing++;
                }
                else
                {
                    var hash = await _hasher.ComputeAsync(item.Path, ct).ConfigureAwait(false);
                    if (hash is null)
                    {
                        _logger.LogWarning("Could not hash library file {Path}; leaving AudioContentHash null", item.Path);
                        unreadable++;
                    }
                    else
                    {
                        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
                        var entry = await db.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == item.Hash, ct).ConfigureAwait(false);
                        if (entry != null)
                        {
                            entry.AudioContentHash = hash.Value;
                            await db.SaveChangesAsync(ct).ConfigureAwait(false);
                            updated++;
                        }
                    }
                }

                done++;
                progress?.Report(new ContentHashBackfillProgress(done, pending.Count, item.Path, updated, unreadable, missing));
            }

            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Entries with a file path but no content hash yet (what a backfill would look at).</summary>
    public async Task<int> CountUnhashedAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.LibraryEntries.AsNoTracking()
            .CountAsync(e => (e.AudioContentHash == null || e.AudioContentHash == "") &&
                             e.FilePath != null && e.FilePath != "", ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DuplicateContentGroup>> FindDuplicateGroupsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.LibraryEntries
            .AsNoTracking()
            .Where(e => e.AudioContentHash != null && e.AudioContentHash != "")
            .Select(e => new { e.UniqueHash, Hash = e.AudioContentHash! })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.Hash, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateContentGroup(g.Key, g.Select(r => r.UniqueHash).ToList()))
            .OrderByDescending(g => g.TrackHashes.Count)
            .ThenBy(g => g.AudioContentHash, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Every exact-duplicate group with the details the review shows (path, quality, cues,
    /// playlists, date added) and a suggested keeper.
    /// </summary>
    public async Task<IReadOnlyList<ExactDuplicateGroupDetails>> LoadGroupDetailsAsync(CancellationToken ct = default)
    {
        var groups = await FindDuplicateGroupsAsync(ct).ConfigureAwait(false);
        if (groups.Count == 0) return Array.Empty<ExactDuplicateGroupDetails>();

        var hashes = groups.SelectMany(g => g.TrackHashes).Distinct(StringComparer.Ordinal).ToList();

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entries = new Dictionary<string, LibraryEntryEntity>(StringComparer.Ordinal);
        var cueCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var playlistCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        // SQLite caps bound parameters; read in chunks.
        foreach (var chunk in hashes.Chunk(400))
        {
            var rows = await db.LibraryEntries.AsNoTracking()
                .Where(e => chunk.Contains(e.UniqueHash))
                .Select(e => new LibraryEntryEntity
                {
                    UniqueHash = e.UniqueHash, Artist = e.Artist, Title = e.Title, Album = e.Album,
                    FilePath = e.FilePath, Format = e.Format, Bitrate = e.Bitrate, AddedAt = e.AddedAt,
                    BPM = e.BPM, MusicalKey = e.MusicalKey, AnalysisStatus = e.AnalysisStatus,
                })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var r in rows) entries[r.UniqueHash] = r;

            var cues = await db.CuePoints.AsNoTracking()
                .Where(c => chunk.Contains(c.TrackUniqueHash))
                .GroupBy(c => c.TrackUniqueHash)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var c in cues) cueCounts[c.Key] = c.Count;

            var playlists = await db.PlaylistTracks.AsNoTracking()
                .Where(t => chunk.Contains(t.TrackUniqueHash))
                .Select(t => new { t.TrackUniqueHash, t.PlaylistId })
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var p in playlists.GroupBy(p => p.TrackUniqueHash))
                playlistCounts[p.Key] = p.Count();
        }

        var result = new List<ExactDuplicateGroupDetails>(groups.Count);
        foreach (var group in groups)
        {
            var details = group.TrackHashes
                .Where(entries.ContainsKey)
                .Select(h =>
                {
                    var e = entries[h];
                    return new ExactDuplicateEntry(
                        e.UniqueHash, e.Artist ?? string.Empty, e.Title ?? string.Empty, e.Album ?? string.Empty,
                        e.FilePath ?? string.Empty, e.Format ?? string.Empty, e.Bitrate, e.AddedAt,
                        cueCounts.GetValueOrDefault(h), playlistCounts.GetValueOrDefault(h),
                        e.AnalysisStatus == AnalysisStatus.Completed || e.BPM is > 0 || !string.IsNullOrWhiteSpace(e.MusicalKey),
                        FileExistsSafe(e.FilePath));
                })
                .ToList();
            if (details.Count < 2) continue;

            result.Add(new ExactDuplicateGroupDetails(group.AudioContentHash, details, ExactDuplicateKeeper.Suggest(details).UniqueHash));
        }

        return result;
    }

    private bool FileExistsSafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check whether {Path} exists", path);
            return false;
        }
    }

    /// <summary>IProgress that reports on the calling thread (Progress&lt;T&gt; would post to a context).</summary>
    private sealed class InlineProgress : IProgress<ContentHashBackfillProgress>
    {
        private readonly Action<ContentHashBackfillProgress> _report;
        public InlineProgress(Action<ContentHashBackfillProgress> report) => _report = report;
        public void Report(ContentHashBackfillProgress value) => _report(value);
    }
}
