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

namespace SLSKDONET.Services;

/// <summary>
/// Fixes tracks stuck showing "FILE MISSING" (<see cref="TrackAvailabilityState.Ghost"/>) in the
/// Track Inspector even though the file genuinely exists on disk and has already been analyzed
/// (waveform/BPM/energy curve all present) — a visible contradiction reported directly by the user.
///
/// Root cause: <c>DownloadManager.ProcessTrackAsync</c> has three "file already exists, skip the
/// actual download" fast paths (already-on-disk dedup, already-downloaded-in-project, and
/// cross-project dedup reuse) that set <c>Status = Downloaded</c> but historically didn't always
/// carry <c>AvailabilityState</c> past <see cref="TrackAvailabilityState.Ghost"/> the way the full
/// download-completion path does — leaving existing rows stuck. This does not touch tracks that are
/// genuinely missing (no file on disk) — those correctly stay Ghost.
/// </summary>
public sealed class AvailabilityStateReconciliationService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<AvailabilityStateReconciliationService> _logger;

    public AvailabilityStateReconciliationService(
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<AvailabilityStateReconciliationService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<ReconciliationResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var candidates = await db.PlaylistTracks
            .Where(t => t.AvailabilityState == TrackAvailabilityState.Ghost)
            .Select(t => new { t.Id, t.TrackUniqueHash, t.ResolvedFilePath, t.BPM })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        int fixedToReady = 0, fixedToUnanalyzed = 0;
        var fixedHashes = new List<string>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(candidate.ResolvedFilePath) || !File.Exists(candidate.ResolvedFilePath))
                continue; // Genuinely missing — correctly stays Ghost.

            // Already has real analysis data (BPM populated) → the file was fully processed before
            // AvailabilityState got stuck; jump straight to Ready rather than forcing a wasteful
            // re-analysis. Otherwise it genuinely hasn't been analyzed yet — LocalUnanalyzed lets
            // the existing AnalysisQueueService pick it up normally.
            var target = candidate.BPM > 0 ? TrackAvailabilityState.Ready : TrackAvailabilityState.LocalUnanalyzed;

            await db.PlaylistTracks
                .Where(t => t.Id == candidate.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.AvailabilityState, target), cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(candidate.TrackUniqueHash))
            {
                await db.Tracks
                    .Where(t => t.GlobalId == candidate.TrackUniqueHash)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.AvailabilityState, target), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (target == TrackAvailabilityState.Ready) fixedToReady++;
            else fixedToUnanalyzed++;
            fixedHashes.Add(candidate.TrackUniqueHash);
        }

        _logger.LogInformation(
            "[AvailabilityReconcile] Checked {Checked} Ghost-flagged tracks — {Ready} corrected to Ready, " +
            "{Unanalyzed} corrected to LocalUnanalyzed (file exists but never analyzed), {Untouched} genuinely missing",
            candidates.Count, fixedToReady, fixedToUnanalyzed, candidates.Count - fixedToReady - fixedToUnanalyzed);

        return new ReconciliationResult(candidates.Count, fixedToReady, fixedToUnanalyzed, fixedHashes);
    }
}

public record ReconciliationResult(
    int Checked,
    int FixedToReady,
    int FixedToUnanalyzed,
    IReadOnlyList<string> FixedHashes)
{
    public int TotalFixed => FixedToReady + FixedToUnanalyzed;
    public string Summary =>
        TotalFixed == 0
            ? $"Checked {Checked} track(s) marked missing — all genuinely have no file on disk."
            : $"Fixed {TotalFixed} track(s) incorrectly shown as missing ({FixedToReady} ready, {FixedToUnanalyzed} queued for analysis) out of {Checked} checked.";
}
