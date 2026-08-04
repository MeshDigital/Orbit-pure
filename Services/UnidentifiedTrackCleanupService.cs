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
/// Finds and fully removes "ID" placeholder tracks — DJ-tracklist shorthand for a track whose
/// identity was never established (e.g. "Basstripper - ID"). <see cref="Utils.CommentTracklistParser"/>
/// drops these at parse time for new imports, but that filter can't retroactively clean tracklists
/// that were imported before it existed, so they can still be sitting in the library.
///
/// Unlike <see cref="CorruptFileRemediationService"/> (which resets a track to Missing so the
/// download queue re-acquires a clean copy), this removes the track entirely: "ID" carries no real
/// identity to redownload against, so whatever file got matched to it was very likely the wrong
/// track in the first place.
/// </summary>
public sealed class UnidentifiedTrackCleanupService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IEventBus _eventBus;
    private readonly ILogger<UnidentifiedTrackCleanupService> _logger;

    public UnidentifiedTrackCleanupService(
        IDbContextFactory<AppDbContext> dbFactory,
        IEventBus eventBus,
        ILogger<UnidentifiedTrackCleanupService> logger)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
        _logger = logger;
    }

    /// <summary>
    /// Removes every track whose title (playlist row or library entry) is exactly "ID" once
    /// trimmed, case-insensitively. Deletes the physical file(s), every PlaylistTrack row across
    /// every playlist, the LibraryEntry, and all cached analysis data for that track.
    /// </summary>
    public async Task<UnidentifiedCleanupResult> RemoveAllAsync(CancellationToken cancellationToken = default)
    {
        // Hop off the calling thread immediately — same reasoning as CorruptFileRemediationService:
        // synchronous File.Delete calls below shouldn't block the UI thread this is usually invoked from.
        await Task.Yield();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Check both surfaces — a track's PlaylistTrack.Title and LibraryEntry.Title can drift out
        // of sync after a manual metadata edit, so a hash could carry "ID" on only one of them.
        var hashesFromPlaylists = await db.PlaylistTracks
            .Where(t => t.Title != null && t.Title.Trim().ToLower() == "id")
            .Select(t => t.TrackUniqueHash)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var hashesFromLibrary = await db.LibraryEntries
            .Where(e => e.Title != null && e.Title.Trim().ToLower() == "id")
            .Select(e => e.UniqueHash)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var hashes = hashesFromPlaylists
            .Concat(hashesFromLibrary)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        int filesDeleted = 0, playlistRowsRemoved = 0, libraryEntriesRemoved = 0;
        var errors = new List<string>();

        foreach (var hash in hashes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // 1 — Collect file paths + row ids BEFORE deleting (ExecuteDeleteAsync below
                // bypasses change tracking, so the rows won't be readable afterward).
                var playlistRows = await db.PlaylistTracks
                    .Where(t => t.TrackUniqueHash == hash)
                    .Select(t => new { t.Id, t.ResolvedFilePath })
                    .ToListAsync(cancellationToken).ConfigureAwait(false);

                foreach (var row in playlistRows)
                {
                    if (TryDeleteFile(row.ResolvedFilePath)) filesDeleted++;
                }

                var libraryEntry = await db.LibraryEntries
                    .Where(e => e.UniqueHash == hash)
                    .Select(e => new { e.FilePath })
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

                if (libraryEntry != null && TryDeleteFile(libraryEntry.FilePath)) filesDeleted++;

                // 2 — Child rows first: TechnicalDetails is keyed by PlaylistTrackId, not hash.
                var playlistRowIds = playlistRows.Select(r => r.Id).ToList();
                if (playlistRowIds.Count > 0)
                {
                    await db.TechnicalDetails
                        .Where(td => playlistRowIds.Contains(td.PlaylistTrackId))
                        .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                }

                // 3 — Remove the PlaylistTrack rows themselves (every playlist referencing this
                // hash), not just a status reset — there's no real identity to redownload against.
                var removed = await db.PlaylistTracks
                    .Where(t => t.TrackUniqueHash == hash)
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                playlistRowsRemoved += removed;

                // 4 — Remove the LibraryEntry.
                var libDeleted = await db.LibraryEntries
                    .Where(e => e.UniqueHash == hash)
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                if (libDeleted > 0) libraryEntriesRemoved++;

                // 5 — Clear the master Tracks row's local-file pointers (no Tracks rows carried
                // Title="ID" at the time this was written, but a hash could still be referenced
                // there independently of title, so clear defensively rather than assume).
                await db.Tracks
                    .Where(t => t.GlobalId == hash)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(t => t.LocalFilePath, (string?)null)
                        .SetProperty(t => t.IsLocalFile, false), cancellationToken).ConfigureAwait(false);

                // 6 — Remove cached analysis artifacts (waveform, cues, embeddings, etc.).
                await db.AudioAnalysis
                    .Where(a => a.TrackUniqueHash == hash)
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.AudioFeatures
                    .Where(f => f.TrackUniqueHash == hash)
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.CuePoints
                    .Where(c => c.TrackUniqueHash == hash)
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

                // 7 — Tell any open view still holding this track's data to drop it.
                _eventBus.Publish(new LibraryEntryDeletedEvent(hash));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{hash}: {ex.Message}");
                _logger.LogError(ex, "[IdCleanup] Failed to remove unidentified track {Hash}", hash);
            }
        }

        _logger.LogInformation(
            "[IdCleanup] Removed {Count} unidentified (\"ID\") track(s) — {Files} file(s) deleted, " +
            "{PlaylistRows} playlist row(s) removed, {LibEntries} library entries removed",
            hashes.Count, filesDeleted, playlistRowsRemoved, libraryEntriesRemoved);

        return new UnidentifiedCleanupResult(hashes.Count, filesDeleted, playlistRowsRemoved, libraryEntriesRemoved, errors);
    }

    private bool TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IdCleanup] Could not delete file {Path}", path);
            return false;
        }
    }
}

public record UnidentifiedCleanupResult(
    int TracksRemoved,
    int FilesDeleted,
    int PlaylistRowsRemoved,
    int LibraryEntriesRemoved,
    IReadOnlyList<string> Errors)
{
    public bool HasErrors => Errors.Count > 0;
    public string Summary =>
        $"Removed {TracksRemoved} unidentified track(s) — {FilesDeleted} file(s) deleted, " +
        $"{PlaylistRowsRemoved} playlist row(s) cleared" +
        (HasErrors ? $", {Errors.Count} error(s)" : string.Empty);
}
