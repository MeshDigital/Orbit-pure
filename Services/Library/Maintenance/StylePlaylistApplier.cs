using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.Library;

namespace SLSKDONET.Services.Library.Maintenance;

public sealed record StylePlaylistApplyReport(int Created, int Appended, int Failed, IReadOnlyList<string> Errors);
public sealed record StylePlaylistRevertReport(int Deleted, int KeptEdited, int TracksRemoved, IReadOnlyList<string> Errors);

/// <summary>
/// Applies and reverts style-playlist plans. Every action writes a <see cref="MaintenancePlaylistLogEntity"/>
/// row so a run can be undone; undo never deletes a playlist the user edited after the run.
/// </summary>
public sealed class StylePlaylistApplier
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILibraryService _library;
    private readonly ILogger<StylePlaylistApplier> _logger;

    public StylePlaylistApplier(
        IDbContextFactory<AppDbContext> dbFactory,
        ILibraryService library,
        ILogger<StylePlaylistApplier> logger)
    {
        _dbFactory = dbFactory;
        _library = library;
        _logger = logger;
    }

    public async Task<StylePlaylistApplyReport> ApplyAsync(Guid runId, IReadOnlyList<PlannedStylePlaylist> approved, CancellationToken ct = default)
    {
        int created = 0, appended = 0, failed = 0;
        var errors = new List<string>();

        foreach (var plan in approved)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var tracks = await ResolveTracksAsync(plan.Hashes, ct).ConfigureAwait(false);
                if (tracks.Count == 0)
                {
                    failed++;
                    errors.Add($"{plan.DisplayName}: no resolvable tracks");
                    continue;
                }

                Guid playlistId;
                var isNew = plan.ExistingPlaylistId is null;
                if (plan.ExistingPlaylistId is { } existing)
                {
                    playlistId = existing;
                    await _library.AddTracksToProjectAsync(tracks, playlistId).ConfigureAwait(false);
                    appended++;
                }
                else
                {
                    var job = await PlaylistCombinePersistence.SaveAsync(_library, plan.DisplayName, tracks).ConfigureAwait(false);
                    playlistId = job.Id;
                    created++;
                }

                await LogAsync(runId, playlistId, isNew, plan.Hashes, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{plan.DisplayName}: {ex.Message}");
                _logger.LogError(ex, "[StylePlaylist] Apply failed for {Playlist}", plan.DisplayName);
            }
        }

        _logger.LogInformation("[StylePlaylist] Apply complete: created {Created}, appended {Appended}, failed {Failed}", created, appended, failed);
        return new StylePlaylistApplyReport(created, appended, failed, errors);
    }

    public async Task<StylePlaylistRevertReport> RevertAsync(Guid runId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.MaintenancePlaylistLog
            .AsNoTracking()
            .Where(r => r.RunId == runId.ToString())
            .ToListAsync(ct).ConfigureAwait(false);

        int deleted = 0, keptEdited = 0, tracksRemoved = 0;
        var errors = new List<string>();

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var addedHashes = JsonSerializer.Deserialize<List<string>>(row.AddedTrackIdsJson) ?? new List<string>();
                if (addedHashes.Count == 0 || !Guid.TryParse(row.PlaylistId, out var playlistId)) continue;

                var current = await _library.LoadPlaylistTracksAsync(playlistId).ConfigureAwait(false);
                var currentHashes = current
                    .Where(t => !string.IsNullOrEmpty(t.TrackUniqueHash))
                    .Select(t => t.TrackUniqueHash!)
                    .ToHashSet(StringComparer.Ordinal);

                if (row.Created == 1)
                {
                    // Delete only when the track set is unchanged since the run (no user edits).
                    if (currentHashes.SetEquals(addedHashes))
                    {
                        await _library.DeletePlaylistJobAsync(playlistId).ConfigureAwait(false);
                        deleted++;
                    }
                    else
                    {
                        await RemoveHashesAsync(playlistId, addedHashes).ConfigureAwait(false);
                        keptEdited++;
                    }
                }
                else
                {
                    await RemoveHashesAsync(playlistId, addedHashes).ConfigureAwait(false);
                    tracksRemoved++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{row.PlaylistId}: {ex.Message}");
                _logger.LogError(ex, "[StylePlaylist] Revert failed for playlist log {Id}", row.Id);
            }
        }

        _logger.LogInformation("[StylePlaylist] Revert complete: deleted {Deleted}, kept-edited {Kept}, tracks-removed {Removed}",
            deleted, keptEdited, tracksRemoved);
        return new StylePlaylistRevertReport(deleted, keptEdited, tracksRemoved, errors);
    }

    private async Task LogAsync(Guid runId, Guid playlistId, bool created, IReadOnlyList<string> hashes, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.MaintenancePlaylistLog.Add(new MaintenancePlaylistLogEntity
        {
            RunId = runId.ToString(),
            PlaylistId = playlistId.ToString(),
            Created = created ? 1 : 0,
            AddedTrackIdsJson = JsonSerializer.Serialize(hashes),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task RemoveHashesAsync(Guid playlistId, IReadOnlyCollection<string> hashes)
    {
        var tracks = await _library.LoadPlaylistTracksAsync(playlistId).ConfigureAwait(false);
        var ids = tracks
            .Where(t => hashes.Contains(t.TrackUniqueHash ?? string.Empty))
            .Select(t => t.Id)
            .ToList();
        if (ids.Count > 0)
            await _library.RemoveTracksFromPlaylistAsync(playlistId, ids).ConfigureAwait(false);
    }

    private async Task<List<PlaylistTrack>> ResolveTracksAsync(IReadOnlyList<string> hashes, CancellationToken ct)
    {
        if (hashes.Count == 0) return new List<PlaylistTrack>();

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entries = await db.LibraryEntries
            .AsNoTracking()
            .Where(e => hashes.Contains(e.UniqueHash))
            .ToListAsync(ct).ConfigureAwait(false);

        // Keep the caller's order (confidence-descending), not DB order.
        var byHash = entries.ToDictionary(e => e.UniqueHash, StringComparer.Ordinal);
        var result = new List<PlaylistTrack>();
        foreach (var hash in hashes)
        {
            if (!byHash.TryGetValue(hash, out var e)) continue;
            var fileExists = !string.IsNullOrWhiteSpace(e.FilePath) && File.Exists(e.FilePath);
            result.Add(new PlaylistTrack
            {
                TrackUniqueHash = e.UniqueHash,
                Artist = e.Artist,
                Title = e.Title,
                Album = e.Album,
                Status = fileExists ? TrackStatus.Downloaded : TrackStatus.Missing,
                ResolvedFilePath = e.FilePath,
                PrimaryGenre = e.PrimaryGenre,
                Genres = e.Genres,
                Label = e.Label,
                ReleaseDate = e.ReleaseDate,
                MusicalKey = e.MusicalKey,
                BPM = e.BPM,
            });
        }
        return result;
    }
}
