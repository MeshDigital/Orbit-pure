using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;

namespace SLSKDONET.Services;

/// <summary>
/// Backfills basic technical metadata (duration, bitrate, format) for already-existing library
/// tracks that never got it — two independent, stacked gaps found via direct DB inspection while
/// investigating "Track Details shows '—'/'UNKNOWN' in the Inspector even after Analyse Track":
///
/// 1. Duration: mostly Soulseek downloads that predate <see cref="PostDownloadDurationCaptureService"/>,
///    which only captures duration going forward for new downloads. ~1,100 of ~1,570 tracks with a
///    real file on disk had a NULL/zero duration despite Bitrate/Format being populated for nearly
///    all of them at the LibraryEntries level.
///
/// 2. Bitrate/Format on PlaylistTracks specifically: DatabaseService.SavePlaylistJobWithTracksAsync
///    inherits BPM/Energy/Danceability/Valence/MusicalKey/CanonicalDuration from the matched
///    LibraryEntries row when the track's own value is missing, but was missing Bitrate/Format from
///    that inherit-if-possible treatment entirely (now fixed going forward) — every PlaylistTracks
///    row created before that fix silently kept Bitrate=0/Format='', even for tracks whose matching
///    LibraryEntries row (the same physical file) has the real values. This is why a track can show
///    correct Bitrate/Format under "All Tracks" (LibraryEntries-backed) but "—"/"UNKNOWN" for the
///    exact same file inside a specific project (PlaylistTracks-backed).
///
/// Same reconcile-forward-and-backward-separately shape as AvailabilityStateReconciliationService.
/// </summary>
public sealed class DurationBackfillService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<DurationBackfillService> _logger;

    public DurationBackfillService(IDbContextFactory<AppDbContext> dbFactory, ILogger<DurationBackfillService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<DurationBackfillResult> BackfillAsync(CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var candidates = await db.LibraryEntries
            .Where(e => (e.DurationSeconds == null || e.DurationSeconds == 0) && e.FilePath != null && e.FilePath != "")
            .Select(e => new { e.UniqueHash, e.FilePath })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        int fixedCount = 0, failedCount = 0;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(candidate.FilePath!)) { failedCount++; continue; }

            int durationSeconds;
            try
            {
                using var file = TagLib.File.Create(candidate.FilePath!);
                durationSeconds = (int)file.Properties.Duration.TotalSeconds;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[DurationBackfill] TagLib probe failed for '{Path}'", candidate.FilePath);
                failedCount++;
                continue;
            }

            if (durationSeconds <= 0) { failedCount++; continue; }

            var canonicalMs = durationSeconds * 1000;

            await db.LibraryEntries
                .Where(e => e.UniqueHash == candidate.UniqueHash)
                .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.DurationSeconds, durationSeconds), cancellationToken)
                .ConfigureAwait(false);

            await db.PlaylistTracks
                .Where(t => t.TrackUniqueHash == candidate.UniqueHash && (t.CanonicalDuration == null || t.CanonicalDuration == 0))
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.CanonicalDuration, canonicalMs), cancellationToken)
                .ConfigureAwait(false);

            await db.Tracks
                .Where(t => t.GlobalId == candidate.UniqueHash && (t.CanonicalDuration == null || t.CanonicalDuration == 0))
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.CanonicalDuration, canonicalMs), cancellationToken)
                .ConfigureAwait(false);

            fixedCount++;
        }

        _logger.LogInformation(
            "[DurationBackfill] Checked {Checked} track(s) with missing duration — {Fixed} fixed, {Failed} could not be read (file missing/unreadable)",
            candidates.Count, fixedCount, failedCount);

        var syncedCount = await SyncPlaylistTrackBitrateFormatAsync(db, cancellationToken).ConfigureAwait(false);
        var durationSyncedCount = await SyncPlaylistTrackDurationAsync(db, cancellationToken).ConfigureAwait(false);

        return new DurationBackfillResult(candidates.Count, fixedCount, failedCount, syncedCount, durationSyncedCount);
    }

    /// <summary>
    /// Copies CanonicalDuration from LibraryEntries into any PlaylistTracks row missing it, for the
    /// same physical file (matched by TrackUniqueHash). Pure DB sync, no file I/O — same shape as
    /// SyncPlaylistTrackBitrateFormatAsync below, just for Duration instead of Bitrate/Format.
    /// Found via direct DB inspection: after the TagLib probe pass above ran, 144 Status=Downloaded
    /// PlaylistTracks rows still had NULL/0 CanonicalDuration — every one of them had a matching
    /// LibraryEntries row whose DurationSeconds was already populated. The probe pass above only
    /// catches gaps where LibraryEntries itself is missing the duration; it never inherits an
    /// already-known LibraryEntries duration into PlaylistTracks, so this closes that separate gap.
    /// </summary>
    private async Task<int> SyncPlaylistTrackDurationAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var gaps = await db.PlaylistTracks
            .Where(t => t.CanonicalDuration == null || t.CanonicalDuration == 0)
            .Select(t => new { t.Id, t.TrackUniqueHash })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        int syncedCount = 0;

        foreach (var group in gaps.GroupBy(t => t.TrackUniqueHash))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var libDurationSeconds = await db.LibraryEntries
                .Where(e => e.UniqueHash == group.Key && e.DurationSeconds != null && e.DurationSeconds > 0)
                .Select(e => e.DurationSeconds)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (libDurationSeconds is null or <= 0) continue; // nothing better to inherit from

            var canonicalMs = libDurationSeconds.Value * 1000;

            foreach (var row in group)
            {
                await db.PlaylistTracks
                    .Where(t => t.Id == row.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.CanonicalDuration, canonicalMs), cancellationToken)
                    .ConfigureAwait(false);
                syncedCount++;
            }
        }

        _logger.LogInformation(
            "[DurationBackfill] Synced CanonicalDuration from LibraryEntries into {Synced} PlaylistTracks row(s) out of {Checked} missing it",
            syncedCount, gaps.Count);

        return syncedCount;
    }

    /// <summary>
    /// Copies Bitrate/Format from LibraryEntries into any PlaylistTracks row missing them, for the
    /// same physical file (matched by TrackUniqueHash). Pure DB sync, no file I/O — the data already
    /// exists, it just never got inherited at PlaylistTracks-creation time (see class doc, gap #2).
    /// </summary>
    private async Task<int> SyncPlaylistTrackBitrateFormatAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var gaps = await db.PlaylistTracks
            .Where(t => t.Bitrate == 0 || t.Format == null || t.Format == "")
            .Select(t => new { t.Id, t.TrackUniqueHash, t.Bitrate, t.Format })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        int syncedCount = 0;

        foreach (var group in gaps.GroupBy(t => t.TrackUniqueHash))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var libEntry = await db.LibraryEntries
                .Where(e => e.UniqueHash == group.Key && (e.Bitrate > 0 || (e.Format != null && e.Format != "")))
                .Select(e => new { e.Bitrate, e.Format })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (libEntry == null) continue; // nothing better to inherit from

            foreach (var row in group)
            {
                var newBitrate = row.Bitrate > 0 ? row.Bitrate : libEntry.Bitrate;
                var newFormat = !string.IsNullOrEmpty(row.Format) ? row.Format : libEntry.Format;
                if (newBitrate == row.Bitrate && newFormat == row.Format) continue; // nothing to change

                await db.PlaylistTracks
                    .Where(t => t.Id == row.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(t => t.Bitrate, newBitrate)
                        .SetProperty(t => t.Format, newFormat), cancellationToken)
                    .ConfigureAwait(false);
                syncedCount++;
            }
        }

        _logger.LogInformation(
            "[DurationBackfill] Synced Bitrate/Format from LibraryEntries into {Synced} PlaylistTracks row(s) out of {Checked} missing one or both",
            syncedCount, gaps.Count);

        return syncedCount;
    }
}

public record DurationBackfillResult(int Checked, int Fixed, int Failed, int MetadataSynced, int DurationSynced = 0)
{
    public int TotalFixed => Fixed + MetadataSynced + DurationSynced;
    public string Summary =>
        TotalFixed == 0
            ? "All tracks already have known duration, bitrate and format."
            : $"Backfilled duration for {Fixed} of {Checked} track(s) checked" +
              (Failed > 0 ? $" ({Failed} skipped — file missing or unreadable)" : "") +
              (MetadataSynced > 0 ? $"; synced bitrate/format for {MetadataSynced} playlist track(s)" : "") +
              (DurationSynced > 0 ? $"; synced duration for {DurationSynced} playlist track(s) from already-known library data" : "") +
              ".";
}
