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

                // 3 — Remove LibraryEntryEntity (re-created after successful re-download)
                var libEntry = await db.LibraryEntries.FindAsync(new object?[] { entry.Hash }, cancellationToken);
                if (libEntry is not null)
                {
                    db.LibraryEntries.Remove(libEntry);
                    libEntriesRemoved++;
                }

                // 4 — Clear LocalFilePath on TrackEntity
                var trackEntity = await db.Tracks
                    .FirstOrDefaultAsync(t => t.GlobalId == entry.Hash, cancellationToken);
                if (trackEntity is not null)
                {
                    trackEntity.LocalFilePath = null;
                    trackEntity.IsLocalFile   = false;
                }

                // 5 — Remove AudioAnalysisEntity (regenerated after re-download + re-analysis)
                var analysis = await db.AudioAnalysis
                    .FirstOrDefaultAsync(a => a.TrackUniqueHash == entry.Hash, cancellationToken);
                if (analysis is not null)
                {
                    db.AudioAnalysis.Remove(analysis);
                    analysisCleaned++;
                }

                // 6 — Remove AudioFeaturesEntity
                var features = await db.AudioFeatures
                    .FirstOrDefaultAsync(f => f.TrackUniqueHash == entry.Hash, cancellationToken);
                if (features is not null)
                    db.AudioFeatures.Remove(features);

                await db.SaveChangesAsync(cancellationToken);
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
