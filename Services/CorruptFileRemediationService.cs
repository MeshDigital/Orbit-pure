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

namespace SLSKDONET.Services;

/// <summary>
/// Applies remediation actions to corrupt or missing library files.
/// For each affected track:
///   - Deletes the file from disk (if it exists and is corrupt)
///   - Removes the LibraryEntryEntity row
///   - Resets PlaylistTrackEntity → Missing so the download queue re-acquires it
///   - Clears LocalFilePath on TrackEntity
///   - Deletes AudioAnalysisEntity + AudioFeaturesEntity (re-generated after re-download)
/// </summary>
public sealed class CorruptFileRemediationService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<CorruptFileRemediationService> _logger;

    public CorruptFileRemediationService(
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<CorruptFileRemediationService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// Remediates the supplied entries. Returns a summary of what was done.
    /// </summary>
    public async Task<RemediationResult> RemediateAsync(
        IReadOnlyList<CorruptFileScanEntry> entries,
        IProgress<(int Done, int Total, string CurrentFile)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int filesDeleted = 0, libEntriesRemoved = 0, playlistTracksReset = 0, analysisCleaned = 0;
        var errors = new List<string>();

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((i + 1, entries.Count, Path.GetFileName(entry.FilePath)));

            try
            {
                // 1 — Delete file from disk (only if it actually exists)
                if (!entry.IsMissing && File.Exists(entry.FilePath))
                {
                    File.Delete(entry.FilePath);
                    filesDeleted++;
                    _logger.LogInformation("[Remediation] Deleted corrupt file: {File}", entry.FilePath);
                }

                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

                // 2 — Reset PlaylistTrack rows → Missing so the download queue re-acquires them
                var playlistTracks = await db.PlaylistTracks
                    .Where(t => t.TrackUniqueHash == entry.Hash)
                    .ToListAsync(cancellationToken);

                foreach (var pt in playlistTracks)
                {
                    pt.Status            = TrackStatus.Missing;
                    pt.ResolvedFilePath  = string.Empty;   // NOT NULL column — use empty string like ReconcilePhysicalFilesAsync
                    pt.AvailabilityState = TrackAvailabilityState.Ghost;
                    playlistTracksReset++;
                }

                // Save the PlaylistTrack reset before touching AudioFeatures/LibraryEntries below.
                // Previously all six steps shared one SaveChangesAsync call — once the AudioFeatures
                // row got loaded into the same context (step 6) and then removed, EF's relationship
                // fixup (PlaylistTrack -> AudioFeatures via TrackUniqueHash, configured IsRequired(false))
                // nulled out the already-tracked PlaylistTrack rows' TrackUniqueHash, violating its
                // NOT NULL constraint and failing remediation every time for the affected track.
                if (playlistTracks.Count > 0)
                {
                    await db.SaveChangesAsync(cancellationToken);
                }

                // 3 — Remove LibraryEntryEntity (re-created after successful re-download).
                // ExecuteDeleteAsync bypasses change tracking entirely, so it can't trigger the
                // fixup above even if more steps are added here later.
                var libDeleted = await db.LibraryEntries
                    .Where(e => e.UniqueHash == entry.Hash)
                    .ExecuteDeleteAsync(cancellationToken);
                if (libDeleted > 0) libEntriesRemoved++;

                // 4 — Clear LocalFilePath on TrackEntity
                await db.Tracks
                    .Where(t => t.GlobalId == entry.Hash)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(t => t.LocalFilePath, (string?)null)
                        .SetProperty(t => t.IsLocalFile, false), cancellationToken);

                // 5 — Remove AudioAnalysisEntity (regenerated after re-download + re-analysis)
                var analysisDeleted = await db.AudioAnalysis
                    .Where(a => a.TrackUniqueHash == entry.Hash)
                    .ExecuteDeleteAsync(cancellationToken);
                if (analysisDeleted > 0) analysisCleaned++;

                // 6 — Remove AudioFeaturesEntity
                await db.AudioFeatures
                    .Where(f => f.TrackUniqueHash == entry.Hash)
                    .ExecuteDeleteAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var msg = $"{entry.Artist} — {entry.Title}: {ex.Message}";
                errors.Add(msg);
                _logger.LogError(ex, "[Remediation] Failed for {Hash}", entry.Hash);
            }
        }

        _logger.LogInformation(
            "[Remediation] Complete — FilesDeleted:{FD} LibEntriesRemoved:{LE} PlaylistTracksReset:{PT} AnalysisCleaned:{AC}",
            filesDeleted, libEntriesRemoved, playlistTracksReset, analysisCleaned);

        return new RemediationResult(
            Processed:            entries.Count,
            FilesDeleted:         filesDeleted,
            LibEntriesRemoved:    libEntriesRemoved,
            PlaylistTracksReset:  playlistTracksReset,
            AnalysisCleaned:      analysisCleaned,
            Errors:               errors);
    }
}

public record RemediationResult(
    int Processed,
    int FilesDeleted,
    int LibEntriesRemoved,
    int PlaylistTracksReset,
    int AnalysisCleaned,
    IReadOnlyList<string> Errors)
{
    public bool HasErrors => Errors.Count > 0;
    public string Summary =>
        $"Fixed {Processed - Errors.Count}/{Processed}: " +
        $"{FilesDeleted} files deleted, {PlaylistTracksReset} tracks re-queued for download" +
        (HasErrors ? $", {Errors.Count} errors" : string.Empty);
}
