using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Data.Entities;
using SLSKDONET.Utils;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Views;

namespace SLSKDONET.Services;


/// <summary>
/// PlaylistJob, PlaylistFolder, PlaylistTrack CRUD, lazy loading, and compatibility methods (#307).
/// </summary>
public partial class LibraryService
{
    // ===== INDEX 2: PlaylistJob (Playlist Headers - Database Backed) =====

    public async Task LogPlaylistActivityAsync(Guid playlistId, string action, string details)
    {
        try
        {
            var log = new PlaylistActivityLogEntity
            {
                Id = Guid.NewGuid(),
                PlaylistId = playlistId,
                Action = action,
                Details = details,
                Timestamp = DateTime.UtcNow
            };
            await _databaseService.LogActivityAsync(log).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log playlist activity");
        }
    }

    public async Task<bool> UndoLastActivityAsync(Guid playlistId, string action)
    {
        try
        {
            var lastLog = await _databaseService.GetLastPlaylistActivityAsync(playlistId, action).ConfigureAwait(false);
            if (lastLog == null || string.IsNullOrEmpty(lastLog.Details)) return false;

            // Details field stores JSON array of Track GUIDs for batch operations
            if (lastLog.Action == "SmartFill")
            {
                var trackIds = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(lastLog.Details);
                if (trackIds != null && trackIds.Any())
                {
                    await _databaseService.BatchDeletePlaylistTracksAsync(trackIds).ConfigureAwait(false);
                    await _databaseService.DeleteActivityLogAsync(lastLog.Id).ConfigureAwait(false);
                    
                    // Invalidate cache
                    _cache.InvalidateProject(playlistId);
                    _eventBus.Publish(new ProjectUpdatedEvent(playlistId));
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to undo last playlist activity");
            return false;
        }
    }

    public async Task<List<PlaylistJob>> GetHistoricalJobsAsync()
    {
        try 
        {
            var entities = await _databaseService.LoadAllPlaylistJobsAsync().ConfigureAwait(false);
            return entities.Select(EntityToPlaylistJob).OrderByDescending(j => j.CreatedAt).ToList();
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to load historical jobs");
             return new List<PlaylistJob>();
        }
    }

    public async Task<List<PlaylistJob>> LoadAllPlaylistJobsAsync()
    {
        try
        {
            var entities = await _databaseService.LoadAllPlaylistJobsAsync().ConfigureAwait(false);
            return entities.Select(EntityToPlaylistJob).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load playlist jobs from database");
            return new List<PlaylistJob>();
        }
    }

    public async Task<PlaylistJob?> FindPlaylistJobAsync(Guid playlistId)
    {
        try
        {
            // Session 1: Try cache first
            var cached = _cache.GetProject(playlistId);
            if (cached != null)
            {
                _logger.LogDebug("Cache HIT for project {Id}", playlistId);
                return cached;
            }
            
            // Cache miss - load from database
            _logger.LogDebug("Cache MISS for project {Id}, loading from database", playlistId);
            var entity = await _databaseService.LoadPlaylistJobAsync(playlistId).ConfigureAwait(false);
            
            if (entity != null)
            {
                var job = EntityToPlaylistJob(entity);
                _cache.CacheProject(job); // Cache for next time
                return job;
            }
            
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load playlist job {Id}", playlistId);
            return null;
        }
    }

    public async Task<PlaylistJob?> FindPlaylistJobBySourceTypeAsync(string sourceType)
    {
        try
        {
            // Efficiency: Loading all jobs to filter in memory isn't ideal but acceptable for small number of playlists.
            // A dedicated DB query would be better long term.
            var entities = await _databaseService.LoadAllPlaylistJobsAsync().ConfigureAwait(false);
            var entity = entities.FirstOrDefault(e => e.SourceType == sourceType);
            return entity != null ? EntityToPlaylistJob(entity) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find playlist job by source type {Type}", sourceType);
            return null;
        }
    }

    public async Task<PlaylistJob?> FindPlaylistJobBySourceUrlAsync(string sourceUrl)
    {
        if (string.IsNullOrEmpty(sourceUrl)) return null;

        try
        {
            var entities = await _databaseService.LoadAllPlaylistJobsAsync().ConfigureAwait(false);
            
            // Normalize searching URL: 
            // 1. Trim whitespace
            // 2. Replace backslashes with forward slashes
            // 3. Remove query parameters
            // 4. Remove trailing slashes
            // 5. ToLower
            string Normalize(string input)
            {
                if (string.IsNullOrEmpty(input)) return string.Empty;
                var s = input.Trim().Replace('\\', '/');
                if (s.Contains('?')) s = s.Split('?')[0];
                return s.TrimEnd('/').ToLowerInvariant();
            }

            var cleanSearch = Normalize(sourceUrl);
            _logger.LogInformation("Checking for duplicate job with normalized URL: '{Normalized}' (Original: '{Original}')", cleanSearch, sourceUrl);

            // Search logic
            return entities
                .Select(EntityToPlaylistJob)
                .FirstOrDefault(job => 
                {
                    if (string.IsNullOrEmpty(job.SourceUrl)) return false;
                    var cleanSource = Normalize(job.SourceUrl);
                    var match = string.Equals(cleanSearch, cleanSource, StringComparison.OrdinalIgnoreCase);
                    
                    if (match)
                    {
                        _logger.LogInformation("MATCH: Job '{Title}' ({Id}) matches normalized URL", job.SourceTitle, job.Id);
                    }
                    else if (cleanSource.Contains(cleanSearch) || cleanSearch.Contains(cleanSource))
                    {
                         // Partial match debug logging
                         _logger.LogDebug("Partial Mismatch: '{Search}' vs '{Source}'", cleanSearch, cleanSource);
                    }
                    
                    return match;
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find playlist job by source URL {Url}", sourceUrl);
            return null;
        }
    }

    public async Task SavePlaylistJobAsync(PlaylistJob job)
    {
        try
        {
            var entity = new PlaylistJobEntity
            {
                Id = job.Id,
                SourceTitle = job.SourceTitle,
                SourceType = job.SourceType,
                DestinationFolder = job.DestinationFolder,
                CreatedAt = job.CreatedAt,
                TotalTracks = job.TotalTracks > 0 ? job.TotalTracks : job.OriginalTracks.Count,
                SuccessfulCount = job.SuccessfulCount,
                FailedCount = job.FailedCount,
                MissingCount = job.MissingCount,

                AlbumArtUrl = job.AlbumArtUrl,
                SourceUrl = job.SourceUrl,
                
                // Phase 2.5: Persistence
                IsUserPaused = job.IsUserPaused,
                DateStarted = job.DateStarted,
                DateUpdated = job.DateUpdated,

                // Phase 20
                IsSmartPlaylist = job.IsSmartPlaylist,
                SmartCriteriaJson = job.SmartCriteriaJson,

                FolderId = job.FolderId,
                ManualSortOrder = job.ManualSortOrder
            };

            await _databaseService.SavePlaylistJobAsync(entity).ConfigureAwait(false);
            
            // Session 1: Invalidate cache on save
            _cache.InvalidateProject(job.Id);
            _logger.LogInformation("Saved playlist job: {Title} ({Id}), cache invalidated", job.SourceTitle, job.Id);

            // Notify listeners (UI updates)
            // Legacy event removed: PlaylistAdded?.Invoke(this, job);
            _eventBus.Publish(new ProjectAddedEvent(job.Id));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save playlist job");
            throw;
        }
    }

    public async Task SavePlaylistJobWithTracksAsync(PlaylistJob job)
    {
        try
        {
            // 1. Save Header + Tracks to DB atomically
            await _databaseService.SavePlaylistJobWithTracksAsync(job).ConfigureAwait(false);
            
            // 2. Invalidate Cache
            _cache.InvalidateProject(job.Id);

            // 3. Notify listeners
            // Legacy event removed: PlaylistAdded?.Invoke(this, job);
            _eventBus.Publish(new ProjectAddedEvent(job.Id));
            _logger.LogInformation("Saved playlist job with tracks and notified listeners: {Title}", job.SourceTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save playlist job with tracks {Title}", job.SourceTitle);
            throw;
        }
    }

    public async Task DeletePlaylistJobAsync(Guid playlistId)
    {
        try
        {
            // With soft delete, we just set the flag
            await _databaseService.SoftDeletePlaylistJobAsync(playlistId).ConfigureAwait(false);
            
            // Session 1: Invalidate cache on delete
            _cache.InvalidateProject(playlistId);
            _logger.LogInformation("Deleted playlist job: {Id}, cache invalidated", playlistId);

            // Emit the event so subscribers (like LibraryViewModel) can react.
            _eventBus.Publish(new ProjectDeletedEvent(playlistId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete playlist job");
            throw;
        }
    }

    public async Task<List<PlaylistJob>> LoadDeletedPlaylistJobsAsync()
    {
        try
        {
            var entities = await _databaseService.LoadDeletedPlaylistJobsAsync().ConfigureAwait(false);
            return entities.Select(EntityToPlaylistJob).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load deleted playlist jobs");
            return new List<PlaylistJob>();
        }
    }

    public async Task RestorePlaylistJobAsync(Guid playlistId)
    {
        try
        {
            await _databaseService.RestorePlaylistJobAsync(playlistId).ConfigureAwait(false);
            
            // Invalidate cache
            _cache.InvalidateProject(playlistId);
            
            // Notify listeners that a project was added (restored)
            _eventBus.Publish(new ProjectAddedEvent(playlistId));
            _logger.LogInformation("Restored playlist job: {Id}", playlistId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore playlist job {Id}", playlistId);
            throw;
        }
    }

    public async Task<PlaylistJob> CreateEmptyPlaylistAsync(string title)
    {
        // Explicit, low-frequency user action (New Playlist / Combine Playlists) — no uniqueness
        // check existed before, so combining twice with the same auto-suggested name (or naming
        // two playlists identically) silently created two indistinguishable playlists.
        var existingTitles = (await LoadAllPlaylistJobsAsync().ConfigureAwait(false))
            .Select(p => p.SourceTitle)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var finalTitle = title;
        var suffix = 2;
        while (existingTitles.Contains(finalTitle))
        {
            finalTitle = $"{title} ({suffix++})";
        }

        var job = new PlaylistJob
        {
            Id = Guid.NewGuid(),
            SourceTitle = finalTitle,
            SourceType = "User",
            CreatedAt = DateTime.UtcNow,
            PlaylistTracks = new List<PlaylistTrack>(),
            TotalTracks = 0
        };

        // Persist and update reactive collection
        await SavePlaylistJobWithTracksAsync(job).ConfigureAwait(false);
        
        return job;
    }

    // ===== Playlist Folders =====

    public async Task<List<PlaylistFolder>> LoadAllPlaylistFoldersAsync()
    {
        try
        {
            var entities = await _databaseService.LoadAllPlaylistFoldersAsync().ConfigureAwait(false);
            return entities.Select(EntityToPlaylistFolder).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load playlist folders");
            return new List<PlaylistFolder>();
        }
    }

    public async Task<PlaylistFolder> CreatePlaylistFolderAsync(string name, Guid? parentFolderId)
    {
        var folder = new PlaylistFolder
        {
            Id = Guid.NewGuid(),
            Name = name,
            ParentFolderId = parentFolderId,
            CreatedAt = DateTime.UtcNow
        };

        await _databaseService.SavePlaylistFolderAsync(PlaylistFolderToEntity(folder)).ConfigureAwait(false);
        _logger.LogInformation("Created playlist folder: {Name} ({Id})", folder.Name, folder.Id);
        return folder;
    }

    public async Task RenamePlaylistFolderAsync(Guid folderId, string newName)
    {
        var folders = await _databaseService.LoadAllPlaylistFoldersAsync().ConfigureAwait(false);
        var entity = folders.FirstOrDefault(f => f.Id == folderId);
        if (entity == null) return;

        entity.Name = newName;
        await _databaseService.SavePlaylistFolderAsync(entity).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves a folder under a new parent (or to root if null). Rejects moves that would
    /// create a cycle (moving a folder into itself or one of its own descendants).
    /// </summary>
    public async Task<bool> MovePlaylistFolderAsync(Guid folderId, Guid? newParentFolderId)
    {
        if (folderId == newParentFolderId) return false;

        var folders = await _databaseService.LoadAllPlaylistFoldersAsync().ConfigureAwait(false);
        var byId = folders.ToDictionary(f => f.Id);

        // Walk up from the proposed new parent; if we hit the folder being moved, it's a cycle.
        var cursor = newParentFolderId;
        while (cursor.HasValue)
        {
            if (cursor.Value == folderId) return false;
            cursor = byId.TryGetValue(cursor.Value, out var parent) ? parent.ParentFolderId : null;
        }

        var entity = byId.GetValueOrDefault(folderId);
        if (entity == null) return false;

        entity.ParentFolderId = newParentFolderId;
        await _databaseService.SavePlaylistFolderAsync(entity).ConfigureAwait(false);
        return true;
    }

    public async Task DeletePlaylistFolderAsync(Guid folderId)
    {
        await _databaseService.DeletePlaylistFolderAsync(folderId).ConfigureAwait(false);
    }

    public async Task MovePlaylistToFolderAsync(Guid playlistId, Guid? folderId)
    {
        await _databaseService.SetPlaylistFolderAsync(playlistId, folderId).ConfigureAwait(false);
        _cache.InvalidateProject(playlistId);
        _eventBus.Publish(new ProjectUpdatedEvent(playlistId));
    }

    private static PlaylistFolder EntityToPlaylistFolder(Data.PlaylistFolderEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        ParentFolderId = entity.ParentFolderId,
        SortOrder = entity.SortOrder,
        CreatedAt = entity.CreatedAt
    };

    private static Data.PlaylistFolderEntity PlaylistFolderToEntity(PlaylistFolder folder) => new()
    {
        Id = folder.Id,
        Name = folder.Name,
        ParentFolderId = folder.ParentFolderId,
        SortOrder = folder.SortOrder,
        CreatedAt = folder.CreatedAt
    };

    /// <summary>
    /// Persists a playlist's track order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the single write path for playlist order, and it enforces the invariant every reader
    /// relies on: after the call, the playlist's rows carry <c>SortOrder</c> 1..N — one row per
    /// position, no gaps, no ties. Callers used to write only the rows they had arranged (the
    /// virtualized grid's loaded page, the downloaded subset, the staged automix), leaving every
    /// other row on its old — often equal — position; <c>ORDER BY SortOrder</c> then had no defined
    /// answer for the duplicates, so the grid, Cue Forge's browser and the player could each show a
    /// different order, and paged reads could even repeat or skip rows.
    /// </para>
    /// <para>
    /// Two shapes of input are supported. A complete list (all rows of the playlist) is the new
    /// order outright, as Flow Builder's "Save order" and the Combine flow provide. A partial list
    /// — only the tracks an arrange operation could process — is treated as a permutation: those
    /// rows are reordered among the positions they already occupy, and every other row keeps its
    /// place. Either way, the whole playlist is renumbered 1..N.
    /// </para>
    /// <para>
    /// Only <c>SortOrder</c>/<c>TrackNumber</c> are written (see
    /// <c>TrackRepository.SavePlaylistTrackOrderAsync</c>): a reorder must never round-trip the
    /// rest of a row through the model mapping, which would blank columns it doesn't carry.
    /// </para>
    /// </remarks>
    public async Task SaveTrackOrderAsync(Guid playlistId, IEnumerable<PlaylistTrack> tracks)
    {
        var requested = (tracks ?? Enumerable.Empty<PlaylistTrack>()).ToList();
        if (requested.Count == 0) return;

        try
        {
            var current = playlistId == Guid.Empty
                ? new List<PlaylistTrack>()
                : await LoadPlaylistTracksAsync(playlistId).ConfigureAwait(false);

            var ordered = BuildTotalPlaylistOrder(current, requested);

            var orderedIds = ordered.Select(t => t.Id).ToList();
            await _databaseService.SavePlaylistTrackOrderAsync(playlistId, orderedIds).ConfigureAwait(false);

            // Every surface that renders this playlist (Library grid, Cue Forge browser, player
            // queue) re-reads the order on this instead of keeping the snapshot it loaded earlier.
            _eventBus.Publish(new PlaylistTrackOrderChangedEvent(playlistId, orderedIds));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist track order for playlist {Id}", playlistId);
            throw;
        }
    }

    /// <summary>
    /// Turns "the caller's order for some or all of the playlist's rows" into one total order for
    /// all of them. See <see cref="SaveTrackOrderAsync"/> for the two supported shapes.
    /// </summary>
    internal static List<PlaylistTrack> BuildTotalPlaylistOrder(
        IReadOnlyList<PlaylistTrack> current, IReadOnlyList<PlaylistTrack> requested)
    {
        // No membership loaded (an "All Tracks" id, or a failed read): the caller's list is all we
        // have, so persist it as given rather than dropping the reorder on the floor.
        if (current.Count == 0) return requested.ToList();

        var currentById = current.ToDictionary(t => t.Id);
        var requestedRows = new List<PlaylistTrack>(requested.Count);
        var requestedIds = new HashSet<Guid>();
        foreach (var track in requested)
        {
            // Only rows that really belong to this playlist take part; bridge tracks the caller
            // hasn't added yet (or a stale row deleted meanwhile) must not create phantom slots.
            if (currentById.TryGetValue(track.Id, out var currentRow) && requestedIds.Add(track.Id))
                requestedRows.Add(currentRow);
        }

        if (requestedRows.Count == 0) return current.ToList();
        if (requestedRows.Count == current.Count) return requestedRows;

        // Partial arrange: keep the playlist's shape (and any tracks that couldn't be arranged
        // exactly where they were) and permute only the arranged rows among their own positions.
        var movedIds = requestedIds;
        var result = current.ToList();
        var slots = new List<int>(requestedRows.Count);
        for (var i = 0; i < current.Count; i++)
        {
            if (movedIds.Contains(current[i].Id)) slots.Add(i);
        }

        for (var i = 0; i < slots.Count && i < requestedRows.Count; i++)
            result[slots[i]] = requestedRows[i];

        return result;
    }

    // ===== INDEX 3: PlaylistTrack (Relational Index - Database Backed) =====

    public async Task<List<PlaylistTrack>> LoadPlaylistTracksAsync(Guid playlistId)
    {
        try
        {
            var entities = await _databaseService.LoadPlaylistTracksAsync(playlistId).ConfigureAwait(false);
            return entities.Select(EntityToPlaylistTrack).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load playlist tracks for {PlaylistId}", playlistId);
            return new List<PlaylistTrack>();
        }
    }

    public async Task<List<PlaylistTrack>> LoadLibraryOverviewTracksAsync(bool likedOnly = false)
    {
        await using var db = new AppDbContext();
        return await Library.LibraryOverviewQuery.LoadAsync(db, likedOnly).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, string>> GetMetadataProvenanceAsync(string trackHash)
    {
        await using var db = new AppDbContext();
        var rows = await db.MetadataProvenance.AsNoTracking()
            .Where(p => p.TrackUniqueHash == trackHash)
            .Select(p => new { p.Field, p.Source })
            .ToListAsync().ConfigureAwait(false);
        return rows.ToDictionary(r => r.Field, r => r.Source, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<Engine.Analysis.GenreFusion.TrackGenreContext?> GetTrackGenreContextAsync(string trackHash)
    {
        try
        {
            await using var db = new AppDbContext();
            return await AudioAnalysis.TrackGenreContextQuery.LoadAsync(db, trackHash).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LibraryService] Couldn't read the library genre for {Hash}; using the audio genre", trackHash);
            return null;
        }
    }

    public async Task<PlaylistTrack?> GetPlaylistTrackByHashAsync(Guid playlistId, string trackHash)
    {
        try
        {
            var entity = await _databaseService.GetPlaylistTrackByHashAsync(playlistId, trackHash).ConfigureAwait(false);
            return entity != null ? EntityToPlaylistTrack(entity) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load playlist track by hash for {PlaylistId}/{Hash}", playlistId, trackHash);
            return null;
        }
    }

    public async Task<List<PlaylistTrack>> GetAllPlaylistTracksAsync()
    {
        try
        {
            var entities = await _databaseService.GetAllPlaylistTracksAsync().ConfigureAwait(false);
            return entities.Select(EntityToPlaylistTrack).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load all playlist tracks");
            return new List<PlaylistTrack>();
        }
    }

    public async Task<int> GetTrackCountAsync(Guid playlistId, string? filter = null, bool? downloadedOnly = null, IEnumerable<string>? hashFilter = null, string? camelotKeyFilter = null, string? qualityTier = null)
    {
        try
        {
            var count = playlistId == Guid.Empty
                ? await _databaseService.GetTotalLibraryTrackCountAsync(filter, downloadedOnly, hashFilter, camelotKeyFilter, qualityTier).ConfigureAwait(false)
                : await _databaseService.GetPlaylistTrackCountAsync(playlistId, filter, downloadedOnly, hashFilter, camelotKeyFilter, qualityTier).ConfigureAwait(false);
            _lastTrackQueryError = null;
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get track count for {PlaylistId}", playlistId);
            _lastTrackQueryError = ex.Message;
            return 0;
        }
    }

    public async Task<List<PlaylistTrack>> GetPagedPlaylistTracksAsync(Guid playlistId, int skip, int take, string? filter = null, bool? downloadedOnly = null, IEnumerable<string>? hashFilter = null, string? camelotKeyFilter = null, TrackSortColumn sortColumn = TrackSortColumn.Default, bool sortDescending = false, string? qualityTier = null)
    {
        try
        {
            var entities = playlistId == Guid.Empty
                ? await _databaseService.GetPagedAllTracksAsync(skip, take, filter, downloadedOnly, hashFilter, camelotKeyFilter, sortColumn, sortDescending, qualityTier).ConfigureAwait(false)
                : await _databaseService.GetPagedPlaylistTracksAsync(playlistId, skip, take, filter, downloadedOnly, hashFilter, camelotKeyFilter, sortColumn, sortDescending, qualityTier).ConfigureAwait(false);
            var tracks = entities.Select(EntityToPlaylistTrack).ToList();
            _lastTrackQueryError = null;
            return tracks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load paged playlist tracks for {PlaylistId}", playlistId);
            _lastTrackQueryError = ex.Message;
            return new List<PlaylistTrack>();
        }
    }

    public async Task<List<PlaylistTrack>> GetOrderedPlaylistTracksAsync(Guid playlistId, string? filter = null, bool? downloadedOnly = null, IEnumerable<string>? hashFilter = null, string? camelotKeyFilter = null, TrackSortColumn sortColumn = TrackSortColumn.Default, bool sortDescending = false, string? qualityTier = null)
    {
        try
        {
            var entities = playlistId == Guid.Empty
                ? await _databaseService.GetOrderedAllTracksAsync(filter, downloadedOnly, hashFilter, camelotKeyFilter, sortColumn, sortDescending, qualityTier).ConfigureAwait(false)
                : await _databaseService.GetOrderedPlaylistTracksAsync(playlistId, filter, downloadedOnly, hashFilter, camelotKeyFilter, sortColumn, sortDescending, qualityTier).ConfigureAwait(false);
            var tracks = entities.Select(EntityToPlaylistTrack).ToList();
            _lastTrackQueryError = null;
            return tracks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ordered playlist tracks for {PlaylistId}", playlistId);
            _lastTrackQueryError = ex.Message;
            return new List<PlaylistTrack>();
        }
    }

    public async Task SavePlaylistTrackAsync(PlaylistTrack track)
    {
        try
        {
            var entity = PlaylistTrackToEntity(track);
            await _databaseService.SavePlaylistTrackAsync(entity).ConfigureAwait(false);
            _logger.LogDebug("Saved playlist track: {PlaylistId}/{Hash}", track.PlaylistId, track.TrackUniqueHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save playlist track");
            throw;
        }
    }

    public async Task DeletePlaylistTracksAsync(Guid jobId)
    {
         await _databaseService.DeletePlaylistTracksAsync(jobId).ConfigureAwait(false);
    }
    
    public async Task DeletePlaylistTrackAsync(Guid playlistTrackId)
    {
        await _databaseService.DeleteSinglePlaylistTrackAsync(playlistTrackId).ConfigureAwait(false);
    }

    public async Task UpdatePlaylistTrackAsync(PlaylistTrack track)
    {
        try
        {
            var entity = PlaylistTrackToEntity(track);
            await _databaseService.SavePlaylistTrackAsync(entity).ConfigureAwait(false);
            _logger.LogDebug("Updated playlist track status: {Hash} = {Status}", track.TrackUniqueHash, track.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update playlist track");
            throw;
        }
    }
    
    public async Task UpdateLikeStatusAsync(string trackHash, bool isLiked)
    {
        try
        {
            await _databaseService.UpdateLikeStatusAsync(trackHash, isLiked).ConfigureAwait(false);
            _logger.LogDebug("Updated like status globally for hash {Hash}: {IsLiked}", trackHash, isLiked);
            
            // Invalidate cache since library items might have changed
            _cache.InvalidateGlobalLibrary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update global like status for {Hash}", trackHash);
            throw; // callers revert their optimistic UI and tell the user
        }
    }

    public async Task UpdateRatingAsync(string trackHash, int rating)
    {
        try
        {
            await _databaseService.UpdateRatingAsync(trackHash, rating).ConfigureAwait(false);
            _logger.LogDebug("Updated rating globally for hash {Hash}: {Rating}", trackHash, rating);
            
            // Invalidate cache
            _cache.InvalidateGlobalLibrary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update global rating for {Hash}", trackHash);
            throw; // callers revert their optimistic UI and tell the user
        }
    }

    public async Task UpdateColorTagAsync(string trackHash, string? colorTag)
    {
        try
        {
            await _databaseService.UpdateColorTagAsync(trackHash, colorTag).ConfigureAwait(false);
            _logger.LogDebug("Updated colour tag globally for hash {Hash}: {ColorTag}", trackHash, colorTag ?? "(none)");

            // Invalidate cache
            _cache.InvalidateGlobalLibrary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update global colour tag for {Hash}", trackHash);
            throw; // callers revert their optimistic UI and tell the user
        }
    }

    public async Task UpdateBpmAsync(string trackHash, double bpm)
    {
        await _databaseService.UpdateBpmAsync(trackHash, bpm).ConfigureAwait(false);
        _cache.InvalidateGlobalLibrary();
    }

    public async Task SavePlaylistTracksAsync(List<PlaylistTrack> tracks)
    {
        try
        {
            var entities = tracks.Select(PlaylistTrackToEntity).ToList();
            await _databaseService.SavePlaylistTracksAsync(entities).ConfigureAwait(false);
            _logger.LogInformation("Saved {Count} playlist tracks", tracks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save playlist tracks");
            throw;
        }
    }
    
    // ===== Phase 1: Heavy Data Lazy Loading =====

    public async Task<TrackTechnicalEntity?> GetTechnicalDetailsAsync(Guid playlistTrackId)
    {
        try
        {
            var entity = await _databaseService.GetTrackTechnicalDetailsAsync(playlistTrackId).ConfigureAwait(false);
            if (entity != null) return entity;

            // Fallback for Library Tracks (whose details live directly on LibraryEntryEntity)
            using var context = new AppDbContext();
            var libraryEntry = await context.LibraryEntries
                .AsNoTracking()
                .Include(le => le.AudioFeatures)
                .FirstOrDefaultAsync(le => le.Id == playlistTrackId)
                .ConfigureAwait(false);

            if (libraryEntry != null)
            {
                // TrackTechnicalEntity's own waveform columns were dead (dropped in
                // SchemaMigratorService's patch #27) — waveform bands are resolved from
                // AudioFeaturesEntity.WaveformBlob via EntityToLibraryEntry/EntityToPlaylistTrack's
                // own ResolveWaveformBands call instead, not through this synthesized entity.
                return new TrackTechnicalEntity
                {
                    PlaylistTrackId = playlistTrackId,
                    CuePointsJson = !string.IsNullOrWhiteSpace(libraryEntry.CuePointsJson)
                        ? libraryEntry.CuePointsJson
                        : libraryEntry.AudioFeatures?.CuePointsJson,
                    IsPrepared = libraryEntry.IsPrepared,
                    PrimaryGenre = libraryEntry.PrimaryGenre,
                    LastUpdated = libraryEntry.LastUsedAt
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load technical details for track {Id}", playlistTrackId);
            return null;
        }
    }
    
    public async Task SaveTechnicalDetailsAsync(TrackTechnicalEntity details)
    {
        try
        {
             await _databaseService.SaveTechnicalDetailsAsync(details).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save technical details for track {Id}", details.PlaylistTrackId);
        }
    }

    // ===== Legacy / Compatibility Methods =====

    public async Task<List<LibraryEntry>> LoadDownloadedTracksAsync()
    {
        // This now directly loads from the database. The old JSON method is gone.
        var entities = await _databaseService.GetAllLibraryEntriesAsync().ConfigureAwait(false);
        return entities.Select(EntityToLibraryEntry).ToList();
    }

    public async Task AddTrackAsync(Track track, string actualFilePath, Guid sourcePlaylistId)
    {
        try
        {
            var entry = new LibraryEntry
            {
                UniqueHash = track.UniqueHash,
                Artist = track.Artist ?? "Unknown",
                Title = track.Title ?? "Unknown",
                Album = track.Album ?? "Unknown",
                FilePath = actualFilePath,
                Bitrate = track.Bitrate,
                DurationSeconds = track.Length,
                Format = SLSKDONET.Models.AudioFileFormat.Resolve(track.Format, actualFilePath) ?? string.Empty
            };

            await SaveOrUpdateLibraryEntryAsync(entry).ConfigureAwait(false);
            _logger.LogDebug("Saved/updated track in library: {Hash}", entry.UniqueHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add track");
            throw;
        }
    }

}
