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

namespace SLSKDONET.Services;

/// <summary>
/// Concrete implementation of ILibraryService.
/// Manages persistent library data (LibraryEntry, PlaylistJob, PlaylistTrack).
/// Now UI-agnostic and focused purely on data management.
/// </summary>
public class LibraryService : ILibraryService
{
    private readonly ILogger<LibraryService> _logger;
    private readonly DatabaseService _databaseService;
    private readonly AppConfig _appConfig;
    private readonly IEventBus _eventBus;
    private readonly LibraryCacheService _cache; // Session 1: Performance cache

    // Events now published via IEventBus (ProjectDeletedEvent, ProjectUpdatedEvent)



    public LibraryService(
        ILogger<LibraryService> logger, 
        DatabaseService databaseService, 
        AppConfig appConfig, 
        IEventBus eventBus,
        LibraryCacheService cache) // Session 1: Inject cache
    {
        _logger = logger;
        _databaseService = databaseService;
        _appConfig = appConfig;
        _eventBus = eventBus;
        _cache = cache;

        _logger.LogDebug("LibraryService initialized (Data Only) with caching enabled");
    }


    // ===== INDEX 1: LibraryEntry (Main Global Index - DB backed) =====

    public async Task<LibraryEntry?> FindLibraryEntryAsync(string uniqueHash)
    {
        var entity = await _databaseService.FindLibraryEntryAsync(uniqueHash).ConfigureAwait(false);
        return entity != null ? EntityToLibraryEntry(entity) : null;
    }

    public async Task<LibraryEntryEntity?> GetTrackEntityByHashAsync(string uniqueHash)
    {
        return await _databaseService.FindLibraryEntryAsync(uniqueHash).ConfigureAwait(false);
    }

    public async Task<List<LibraryEntry>> LoadAllLibraryEntriesAsync()
    {
        // Session 2: Use global cache
        var cached = _cache.GetGlobalLibrary();
        if (cached != null)
        {
            _logger.LogDebug("Global Cache HIT (Library Index)");
            return cached;
        }

        _logger.LogDebug("Global Cache MISS (Library Index), loading from DB");
        var entities = await _databaseService.GetAllLibraryEntriesAsync().ConfigureAwait(false);
        var entries = entities.Select(EntityToLibraryEntry).ToList();
        
        _cache.CacheGlobalLibrary(entries);
        return entries;
    }

    public async Task<List<LibraryEntry>> GetLibraryEntriesByHashesAsync(List<string> hashes)
    {
        var entities = await _databaseService.GetLibraryEntriesByHashesAsync(hashes).ConfigureAwait(false);
        return entities.Select(EntityToLibraryEntry).ToList();
    }

    public async Task<List<LibraryEntry>> SearchLibraryEntriesWithStatusAsync(string query, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<LibraryEntry>();
        var entities = await _databaseService.SearchLibraryEntriesWithStatusAsync(query, limit).ConfigureAwait(false);
        return entities.Select(EntityToLibraryEntry).ToList();
    }

    public async Task SaveOrUpdateLibraryEntryAsync(LibraryEntry entry)
    {
        try
        {
            // Concurrency Fix: Load existing entity first to attach to context
            var existingEntity = await _databaseService.FindLibraryEntryAsync(entry.UniqueHash).ConfigureAwait(false);
            
            if (existingEntity != null)
            {
                // Update existing entity fields
                existingEntity.Artist = entry.Artist;
                existingEntity.Title = entry.Title;
                existingEntity.Album = entry.Album;
                existingEntity.FilePath = entry.FilePath;
                existingEntity.Bitrate = entry.Bitrate;
                existingEntity.DurationSeconds = entry.DurationSeconds;
                existingEntity.Format = entry.Format;
                existingEntity.Label = entry.Label;
                existingEntity.Comments = entry.Comments;
                existingEntity.LastUsedAt = DateTime.UtcNow;
                
                // Preserve scientific data if input is empty (don't overwrite enrichment with nulls)
                if (!string.IsNullOrEmpty(entry.SpotifyTrackId))
                {
                    existingEntity.SpotifyTrackId = entry.SpotifyTrackId;
                    existingEntity.Energy = entry.Energy;
                    existingEntity.Danceability = entry.Danceability;
                    existingEntity.Valence = entry.Valence;
                    existingEntity.BPM = entry.BPM;
                    existingEntity.MusicalKey = entry.MusicalKey;
                }
                
                // Only update enrichment flag if true (don't regress)
                if (entry.IsEnriched) existingEntity.IsEnriched = true;

                await _databaseService.SaveLibraryEntryAsync(existingEntity).ConfigureAwait(false);
                _logger.LogDebug("Updated library entry: {Hash}", entry.UniqueHash);
            }
            else
            {
                var entity = LibraryEntryToEntity(entry);
                entity.LastUsedAt = DateTime.UtcNow;
                await _databaseService.SaveLibraryEntryAsync(entity).ConfigureAwait(false);
                _logger.LogDebug("Created library entry: {Hash}", entry.UniqueHash);
            }

            // Session 2: Invalidate global cache on any change
            _cache.InvalidateGlobalLibrary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save or update library entry");
            // Do not throw here to prevent crashing the download flow! 
            // The file is on disk, we just failed to index it.
            // A background scan can pick it up later.
        }
    }

    public async Task SyncLibraryEntriesFromTracksAsync()
    {
        try
        {
            _logger.LogInformation("Starting Library Entry Synchronization...");

            // 1. Get all completed playlist tracks that have a resolved file path
            var allTracks = await _databaseService.GetAllPlaylistTracksAsync();
            var completedTracks = allTracks
                .Where(t => t.Status == TrackStatus.Downloaded && !string.IsNullOrEmpty(t.ResolvedFilePath))
                .ToList();

            if (!completedTracks.Any())
            {
                _logger.LogInformation("No completed tracks found to sync.");
                return;
            }

            // 2. Get all existing library entry hashes directly
            var existingEntries = await _databaseService.GetAllLibraryEntriesAsync();
            var existingHashes = new HashSet<string>(existingEntries.Select(e => e.UniqueHash));

            // 3. Identify missing entries
            var missingTracks = completedTracks
                .Where(t => !existingHashes.Contains(t.TrackUniqueHash))
                .GroupBy(t => t.TrackUniqueHash) // Deduplicate by hash
                .Select(g => g.First())
                .ToList();

            if (!missingTracks.Any())
            {
                _logger.LogInformation("All completed tracks are already indexed in LibraryEntry.");
                return;
            }

            _logger.LogInformation("Found {Count} tracks missing from LibraryEntry index. Backfilling...", missingTracks.Count);

            // 4. Create and save missing entries
            int addedCount = 0;
            foreach (var track in missingTracks)
            {
                // Basic check to ensure file actually exists before indexing
                if (!System.IO.File.Exists(track.ResolvedFilePath))
                {
                    _logger.LogWarning("Skipping index for missing file: {Path}", track.ResolvedFilePath);
                    continue;
                }

                var entry = new LibraryEntry
                {
                    UniqueHash = track.TrackUniqueHash,
                    Artist = track.Artist,
                    Title = track.Title,
                    Album = track.Album,
                    FilePath = track.ResolvedFilePath,
                    Bitrate = track.Bitrate,
                    // Use canonical duration if available, otherwise 0
                    DurationSeconds = track.CanonicalDuration ?? 0, 
                    Format = System.IO.Path.GetExtension(track.ResolvedFilePath).TrimStart('.').ToLowerInvariant(),
                    AddedAt = track.AddedAt,

                    // Transfer Metadata
                    SpotifyTrackId = track.SpotifyTrackId,
                    BPM = track.BPM,
                    MusicalKey = track.MusicalKey,
                    Energy = track.Energy,
                    Danceability = track.Danceability,
                    Valence = track.Valence,
                    IsEnriched = track.IsEnriched,
                    Label = track.Label,
                    Comments = track.Comments
                };

                await SaveOrUpdateLibraryEntryAsync(entry);
                addedCount++;
            }

            _logger.LogInformation("Library Synchronization Completed. Added {Count} new entries.", addedCount);
            
            // Phase 5: Ensure Default Smart Playlists
            await InitializeDefaultPlaylistsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to synchronize Library Entries.");
        }
    }

    public async Task AddTrackToLibraryIndexAsync(PlaylistTrack track, string finalPath)
    {
        try
        {
            var entry = new LibraryEntry
            {
                UniqueHash = track.TrackUniqueHash,
                Artist = track.Artist,
                Title = track.Title,
                Album = track.Album,
                FilePath = finalPath,
                Bitrate = track.Bitrate ?? 0,
                DurationSeconds = track.CanonicalDuration ?? 0,
                Format = System.IO.Path.GetExtension(finalPath).TrimStart('.').ToLowerInvariant(),
                AddedAt = DateTime.UtcNow,

                // Map Scientific Metadata
                SpotifyTrackId = track.SpotifyTrackId,
                BPM = track.BPM,
                MusicalKey = track.MusicalKey,
                Energy = track.Energy,
                Danceability = track.Danceability,
                Valence = track.Valence,
                IsEnriched = track.IsEnriched,
                Label = track.Label,
                Comments = track.Comments
            };

            await SaveOrUpdateLibraryEntryAsync(entry);
            
            // Session 2: Invalidate global cache
            _cache.InvalidateGlobalLibrary();

            _logger.LogInformation("Indexed track for All Tracks view: {Title}", track.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index completed track {Title} for All Tracks view", track.Title);
        }
    }

    public async Task RemoveTrackFromLibraryAsync(string trackHash)
    {
        try
        {
            _logger.LogInformation("Removing track from global library index: {Hash}", trackHash);
            var entryEntity = await _databaseService.FindLibraryEntryAsync(trackHash);
            if (entryEntity != null)
            {
                // We use DatabaseService directly or via repository
                // The DatabaseService usually has methods for specific entities
                await _databaseService.RemoveTrackAsync(trackHash); // Wait, RemoveTrackAsync might be for TrackEntity
            }
            
            // Actually, we should check if DatabaseService has a RemoveLibraryEntryAsync
            // Looking at the outline, it has RemoveTrackAsync which takes globalId. 
            // In AppDbContext, TrackEntity.GlobalId is the UniqueHash.
            // But LibraryEntryEntity.UniqueHash is also the UniqueHash.
            
            // I'll check DatabaseService.RemoveTrackAsync implementation.
            await _databaseService.RemoveTrackAsync(trackHash);
            
            _cache.InvalidateGlobalLibrary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove track {Hash} from library", trackHash);
        }
    }

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
                SmartCriteriaJson = job.SmartCriteriaJson
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
        var job = new PlaylistJob
        {
            Id = Guid.NewGuid(),
            SourceTitle = title,
            SourceType = "User",
            CreatedAt = DateTime.UtcNow,
            PlaylistTracks = new List<PlaylistTrack>(),
            TotalTracks = 0
        };

        // Persist and update reactive collection
        await SavePlaylistJobWithTracksAsync(job).ConfigureAwait(false);
        
        return job;
    }

    public async Task SaveTrackOrderAsync(Guid playlistId, IEnumerable<PlaylistTrack> tracks)
    {
        try
        {
            // Convert to models and persist batch
            var entities = tracks.Select(PlaylistTrackToEntity).ToList();
            await _databaseService.SavePlaylistTracksAsync(entities).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist track order for playlist {Id}", playlistId);
            throw;
        }
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

    public async Task<int> GetTrackCountAsync(Guid playlistId, string? filter = null, bool? downloadedOnly = null)
    {
        try
        {
            // Use repository through databaseService if possible, or add to databaseService
            // For now, I'll assume databaseService exposes it or I'll add it there.
            if (playlistId == Guid.Empty)
            {
                // FIX: Guid.Empty means "All Tracks" (Global Library Index)
                return await _databaseService.GetTotalLibraryTrackCountAsync(filter, downloadedOnly).ConfigureAwait(false);
            }

            return await _databaseService.GetPlaylistTrackCountAsync(playlistId, filter, downloadedOnly).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get track count for {PlaylistId}", playlistId);
            return 0;
        }
    }

    public async Task<List<PlaylistTrack>> GetPagedPlaylistTracksAsync(Guid playlistId, int skip, int take, string? filter = null, bool? downloadedOnly = null)
    {
        try
        {
            if (playlistId == Guid.Empty)
            {
                 // FIX: Guid.Empty means "All Tracks" (Global Library Index)
                 var globalEntities = await _databaseService.GetPagedAllTracksAsync(skip, take, filter, downloadedOnly).ConfigureAwait(false);
                 return globalEntities.Select(EntityToPlaylistTrack).ToList();
            }

            var entities = await _databaseService.GetPagedPlaylistTracksAsync(playlistId, skip, take, filter, downloadedOnly).ConfigureAwait(false);
            return entities.Select(EntityToPlaylistTrack).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load paged playlist tracks for {PlaylistId}", playlistId);
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
        }
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
            return entity;
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
                Format = track.Format ?? "Unknown"
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

    // ===== Helper Conversion Methods =====

    private PlaylistJob EntityToPlaylistJob(PlaylistJobEntity entity)
    {
        var playlistTracks = entity.Tracks?.Select(EntityToPlaylistTrack).ToList() ?? new List<PlaylistTrack>();

        var originalTracks = new ObservableCollection<Track>(playlistTracks.Select(pt => new Track {
            Artist = pt.Artist,
            Title = pt.Title,
            Album = pt.Album,
        }));

        var job = new PlaylistJob
        {
            Id = entity.Id,
            SourceTitle = entity.SourceTitle,
            SourceType = entity.SourceType,
            DestinationFolder = entity.DestinationFolder,
            CreatedAt = entity.CreatedAt,
            OriginalTracks = originalTracks,
            PlaylistTracks = playlistTracks,
            SuccessfulCount = entity.SuccessfulCount,
            FailedCount = entity.FailedCount,

            AlbumArtUrl = entity.AlbumArtUrl,
            SourceUrl = entity.SourceUrl,
            
            // Phase 2.5
            IsUserPaused = entity.IsUserPaused,
            DateStarted = entity.DateStarted,
            DateUpdated = entity.DateUpdated,
            IsDeleted = entity.IsDeleted,
            DeletedAt = entity.DeletedAt,

            // Phase 20
            IsSmartPlaylist = entity.IsSmartPlaylist,
            SmartCriteriaJson = entity.SmartCriteriaJson
        };

        job.MissingCount = entity.TotalTracks - entity.SuccessfulCount - entity.FailedCount;
        job.RefreshStatusCounts();

        return job;
    }

    private PlaylistTrack EntityToPlaylistTrack(PlaylistTrackEntity entity)
    {
        return new PlaylistTrack
        {
            Id = entity.Id,
            PlaylistId = entity.PlaylistId,
            Artist = entity.Artist,
            Title = entity.Title,
            Album = entity.Album,
            TrackUniqueHash = entity.TrackUniqueHash,
            Status = entity.Status,
            ResolvedFilePath = entity.ResolvedFilePath,
            TrackNumber = entity.TrackNumber,
            Rating = entity.Rating,
            IsLiked = entity.IsLiked,
            PlayCount = entity.PlayCount,
            LastPlayedAt = entity.LastPlayedAt,
            AddedAt = entity.AddedAt,
            SortOrder = entity.SortOrder,
            PreferredFormats = entity.PreferredFormats,
            MinBitrateOverride = entity.MinBitrateOverride,
            Format = entity.Format,
            
            // Spotify Metadata
            SpotifyTrackId = entity.SpotifyTrackId,
            ISRC = entity.ISRC,
            MusicBrainzId = entity.MusicBrainzId,
            SpotifyAlbumId = entity.SpotifyAlbumId,
            SpotifyArtistId = entity.SpotifyArtistId,
            AlbumArtUrl = entity.AlbumArtUrl,
            // Load waveform bands from TechnicalDetails (Lazy loaded via Include in Repository)
            WaveformData = entity.TechnicalDetails?.WaveformData ?? Array.Empty<byte>(), 
            RmsData = entity.TechnicalDetails?.RmsData ?? Array.Empty<byte>(),
            LowData = entity.TechnicalDetails?.LowData ?? Array.Empty<byte>(),
            MidData = entity.TechnicalDetails?.MidData ?? Array.Empty<byte>(),
            HighData = entity.TechnicalDetails?.HighData ?? Array.Empty<byte>(),
            ArtistImageUrl = entity.ArtistImageUrl,
            Genres = entity.Genres,
            Popularity = entity.Popularity,
            CanonicalDuration = entity.CanonicalDuration,
            ReleaseDate = entity.ReleaseDate,

            // Musical Intelligence
            MusicalKey = entity.MusicalKey,
            BPM = entity.BPM,
            Energy = entity.Energy,
            Danceability = entity.Danceability,
            Valence = entity.Valence,
            Label = entity.Label,
            Comments = entity.Comments,
            MoodTag = entity.MoodTag,
            PrimaryGenre = entity.PrimaryGenre,

            // Phase 21: AI Brain - Mapped below via AudioFeatures
            // Sadness = entity.Sadness, // Removed
            // VectorEmbedding = entity.VectorEmbedding, // Removed

            AnalysisOffset = entity.AnalysisOffset,
            BitrateScore = entity.BitrateScore,
            Bitrate = entity.Bitrate,
            
            // Dual-Truth
            SpotifyBPM = entity.SpotifyBPM,
            SpotifyKey = entity.SpotifyKey,
            ManualBPM = entity.ManualBPM,
            ManualKey = entity.ManualKey,
            
            IsEnriched = entity.IsEnriched,
            
            // Sonic Integrity
            Integrity = entity.Integrity,
            IsTrustworthy = entity.IsTrustworthy,
            QualityConfidence = entity.QualityConfidence,
            FrequencyCutoff = entity.FrequencyCutoff,
            QualityDetails = entity.QualityDetails,
            
            // Phase 17: Technical Audio Analysis
            Loudness = entity.Loudness,
            TruePeak = entity.TruePeak,
            DynamicRange = entity.DynamicRange,
            
            // Phase 15
            DetectedSubGenre = entity.DetectedSubGenre,
            InstrumentalProbability = entity.InstrumentalProbability, // Phase 18.2

            // Phase 21: AI Brain (Mapped from AudioFeatures)
            Sadness = entity.AudioFeatures?.Sadness,
            VectorEmbedding = entity.AudioFeatures?.VectorEmbedding
        };
    }

    private PlaylistTrackEntity PlaylistTrackToEntity(PlaylistTrack track)
    {
        return new PlaylistTrackEntity
        {
            Id = track.Id,
            PlaylistId = track.PlaylistId,
            Artist = track.Artist,
            Title = track.Title,
            Album = track.Album,
            TrackUniqueHash = track.TrackUniqueHash,
            Status = track.Status,
            ResolvedFilePath = track.ResolvedFilePath,
            TrackNumber = track.TrackNumber,
            Rating = track.Rating,
            IsLiked = track.IsLiked,
            PlayCount = track.PlayCount,
            LastPlayedAt = track.LastPlayedAt,
            AddedAt = track.AddedAt,
            SortOrder = track.SortOrder,
            PreferredFormats = track.PreferredFormats,
            MinBitrateOverride = track.MinBitrateOverride,
            Format = track.Format,
            
            // Spotify Metadata
            SpotifyTrackId = track.SpotifyTrackId,
            ISRC = track.ISRC,
            MusicBrainzId = track.MusicBrainzId,
            SpotifyAlbumId = track.SpotifyAlbumId,
            SpotifyArtistId = track.SpotifyArtistId,
            AlbumArtUrl = track.AlbumArtUrl,
            // HEAVY DATA REFACTOR: Managed via TechnicalDetails
            ArtistImageUrl = track.ArtistImageUrl,
            Genres = track.Genres,
            Popularity = track.Popularity,
            CanonicalDuration = track.CanonicalDuration,
            ReleaseDate = track.ReleaseDate,

            // Musical Intelligence
            MusicalKey = track.MusicalKey,
            BPM = track.BPM,
            Energy = track.Energy,
            DetectedSubGenre = track.DetectedSubGenre,
            Valence = track.Valence,
            Label = track.Label,
            Comments = track.Comments,
            MoodTag = track.MoodTag,
            PrimaryGenre = track.PrimaryGenre,
            AnalysisOffset = track.AnalysisOffset,
            BitrateScore = track.BitrateScore,
            Bitrate = track.Bitrate ?? 0,
            
            // Dual-Truth
            SpotifyBPM = track.SpotifyBPM,
            SpotifyKey = track.SpotifyKey,
            ManualBPM = track.ManualBPM,
            ManualKey = track.ManualKey,
            
            IsEnriched = track.IsEnriched,
            
            // Sonic Integrity
            Integrity = track.Integrity,
            IsTrustworthy = track.IsTrustworthy,
            QualityConfidence = track.QualityConfidence,
            FrequencyCutoff = track.FrequencyCutoff,

            QualityDetails = track.QualityDetails,
            
            // Phase 17: Technical Audio Analysis
            Loudness = track.Loudness,
            TruePeak = track.TruePeak,
            DynamicRange = track.DynamicRange,
            InstrumentalProbability = track.InstrumentalProbability // Phase 18.2
            
            // Phase 21: AI Brain - READ ONLY via AudioFeatures link
            // We do not set Sadness/Vector on the PlaylistTrackEntity directly.
            // They are stored in AudioFeaturesEntity linked by TrackUniqueHash.
        };
    }

    // ===== Private Helper Methods (JSON - LibraryEntry only) =====
    
    private LibraryEntry EntityToLibraryEntry(LibraryEntryEntity entity)
    {
        return new LibraryEntry
        {
            Id = entity.Id,
            UniqueHash = entity.UniqueHash,
            Artist = entity.Artist,
            Title = entity.Title,
            Album = entity.Album,
            FilePath = entity.FilePath,
            Bitrate = entity.Bitrate,
            DurationSeconds = entity.DurationSeconds,
            Format = entity.Format,
            AddedAt = entity.AddedAt,
            
            // Scientific Fields
            SpotifyTrackId = entity.SpotifyTrackId,
            ISRC = entity.ISRC,
            MusicBrainzId = entity.MusicBrainzId,
            Energy = entity.Energy,
            Danceability = entity.Danceability,
            Valence = entity.Valence,
            BPM = entity.BPM,
            MusicalKey = entity.MusicalKey,
            Label = entity.Label,
            Comments = entity.Comments,
            MoodTag = entity.MoodTag,
            PrimaryGenre = entity.PrimaryGenre,

            // Phase 21: AI Brain
            Sadness = entity.AudioFeatures?.Sadness,
            VectorEmbedding = entity.AudioFeatures?.VectorEmbedding,

            IsEnriched = entity.IsEnriched,
            
            // Phase 17: Technical Audio Analysis
            Loudness = entity.Loudness,
            TruePeak = entity.TruePeak,
            DynamicRange = entity.DynamicRange,
            
            WaveformData = entity.WaveformData ?? Array.Empty<byte>(),
            RmsData = entity.RmsData ?? Array.Empty<byte>(),
            LowData = entity.LowData ?? Array.Empty<byte>(),
            MidData = entity.MidData ?? Array.Empty<byte>(),
            HighData = entity.HighData ?? Array.Empty<byte>(),
            
            // Dual-Truth
            SpotifyBPM = entity.SpotifyBPM,
            SpotifyKey = entity.SpotifyKey,
            ManualBPM = entity.ManualBPM,
            ManualKey = entity.ManualKey,
            
            InstrumentalProbability = entity.InstrumentalProbability // Phase 18.2
        };
    }

    private LibraryEntryEntity LibraryEntryToEntity(LibraryEntry entry)
    {
        var entity = new LibraryEntryEntity();
        entity.UniqueHash = entry.UniqueHash;
        entity.Artist = entry.Artist;
        entity.Title = entry.Title;
        entity.Album = entry.Album;
        entity.FilePath = entry.FilePath;
        entity.Bitrate = entry.Bitrate;
        entity.DurationSeconds = entry.DurationSeconds;
        entity.Format = entry.Format;
        
        // Scientific Fields
        entity.SpotifyTrackId = entry.SpotifyTrackId;
        entity.ISRC = entry.ISRC;
        entity.MusicBrainzId = entry.MusicBrainzId;
        entity.Energy = entry.Energy;
        entity.Danceability = entry.Danceability;
        entity.Valence = entry.Valence;
        entity.BPM = entry.BPM;
        entity.MusicalKey = entry.MusicalKey;
        entity.IsEnriched = entry.IsEnriched;
        entity.Label = entry.Label;
        entity.Comments = entry.Comments;
        entity.MoodTag = entry.MoodTag;
        entity.PrimaryGenre = entry.PrimaryGenre;
        
        // Phase 17: Technical Audio Analysis
        entity.Loudness = entry.Loudness;
        entity.TruePeak = entry.TruePeak;
        entity.DynamicRange = entry.DynamicRange;
        
        entity.WaveformData = entry.WaveformData;
        entity.RmsData = entry.RmsData;
        entity.LowData = entry.LowData;
        entity.MidData = entry.MidData;
        entity.HighData = entry.HighData;
        
        // Dual-Truth
        entity.SpotifyBPM = entry.SpotifyBPM;
        entity.SpotifyKey = entry.SpotifyKey;
        entity.ManualBPM = entry.ManualBPM;
        entity.ManualBPM = entry.ManualBPM;
        entity.ManualKey = entry.ManualKey;
        
        entity.InstrumentalProbability = entry.InstrumentalProbability; // Phase 18.2
        
        // Phase 21: AI Brain - Read Only from AudioFeatures
        // We do not set entity.Sadness directly as it lives in AudioFeaturesEntity
        // entity.Sadness = entry.Sadness; 
        // entity.VectorEmbedding = entry.VectorEmbedding;

        if (entry.AddedAt == default)
        {
            entity.AddedAt = DateTime.UtcNow;
        }
        return entity;
    }




    /// <summary>
    /// Updates the file path for a library entry and persists the change.
    /// </summary>
    public async Task UpdateLibraryEntryPathAsync(string uniqueHash, string newPath)
    {
        try
        {
            var entity = await _databaseService.FindLibraryEntryAsync(uniqueHash);
            if (entity != null)
            {
                entity.FilePath = newPath;
                await _databaseService.SaveLibraryEntryAsync(entity);
                _logger.LogInformation("Updated file path for {Hash}: {NewPath}", uniqueHash, newPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update library entry path");
            throw;
        }
    }

    // Phase 16.2: Vibe Match
    public async Task<List<AudioFeaturesEntity>> GetAllAudioFeaturesAsync()
    {
        return await _databaseService.LoadAllAudioFeaturesAsync().ConfigureAwait(false);
    }

    // Phase 15
    public async Task<List<StyleDefinitionEntity>> GetStyleDefinitionsAsync()
    {
        try
        {
            return await _databaseService.LoadAllStyleDefinitionsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load style definitions");
            return new List<StyleDefinitionEntity>();
        }
    }

    // Phase 11.5: Verification logic
    public async Task MarkTrackAsVerifiedAsync(string trackHash)
    {
        try
        {
            await _databaseService.MarkTrackAsVerifiedAsync(trackHash).ConfigureAwait(false);
            _logger.LogInformation("Marked track verified: {Hash}", trackHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify track");
            throw;
        }
    }
    /// <summary>
    /// Creates a physical clone of a track, duplicating its database entry and decoupling its identity.
    /// This allows for multiple versions of the same file (e.g., Radio Edit vs Extended) with independent cues.
    /// </summary>
    public async Task<PlaylistTrack> CreatePhysicalCloneAsync(PlaylistTrack source, string newPath)
    {
        try
        {
            // 1. Create a deep copy of the track model
            var clone = EntityToPlaylistTrack(PlaylistTrackToEntity(source));
            
            // 2. Assign a new unique identity
            clone.Id = Guid.NewGuid();
            clone.ResolvedFilePath = newPath;
            
            // 3. Decouple the Hash (Critical to avoid collision in the "All Tracks" view)
            // We append a clone suffix to ensure it doesn't merge with the original
            clone.TrackUniqueHash = $"{source.TrackUniqueHash}_CLONE_{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            
            // 4. Reset preparation state for the new copy
            clone.IsPrepared = false;
            clone.CuePointsJson = null; // Fresh start for cues
            clone.Status = TrackStatus.Downloaded; // Immediately ready
            clone.AddedAt = DateTime.UtcNow;

            // 5. Persist to PlaylistTracks table
            await SavePlaylistTrackAsync(clone).ConfigureAwait(false);

            // 6. Index in the global LibraryEntry table
            await AddTrackToLibraryIndexAsync(clone, newPath).ConfigureAwait(false);

            // 7. Initialize fresh Technical Details
            var tech = new TrackTechnicalEntity
            {
                PlaylistTrackId = clone.Id,
                IsPrepared = false,
                LastUpdated = DateTime.UtcNow
            };
            await SaveTechnicalDetailsAsync(tech).ConfigureAwait(false);

            _logger.LogInformation("Physical clone created: {Hash} at {Path}", clone.TrackUniqueHash, newPath);
            return clone;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create physical clone for {Title}", source.Title);
            throw;
        }
    }

    public async Task<AudioFeaturesEntity?> GetAudioFeaturesByHashAsync(string uniqueHash)
    {
        return await _databaseService.GetAudioFeaturesByHashAsync(uniqueHash);
    }

    public async Task<List<LibraryEntry>> GetTracksAddedSinceAsync(DateTime since)
    {
        var all = await LoadAllLibraryEntriesAsync();
        return all.Where(e => e.AddedAt >= since).ToList();
    }

    public async Task AddTracksToProjectAsync(IEnumerable<PlaylistTrack> tracks, Guid targetProjectId)
    {
        try
        {
            var project = await FindPlaylistJobAsync(targetProjectId);
            if (project == null) throw new InvalidOperationException("Target project not found");

            var newTracks = new List<PlaylistTrack>();
            foreach (var track in tracks)
            {
                // Create a clone of the relational entry for the new project
                var newTrack = new PlaylistTrack
                {
                    Id = Guid.NewGuid(),
                    PlaylistId = targetProjectId,
                    Artist = track.Artist,
                    Title = track.Title,
                    Album = track.Album,
                    TrackUniqueHash = track.TrackUniqueHash,
                    Status = track.Status,
                    ResolvedFilePath = track.ResolvedFilePath,
                    TrackNumber = track.TrackNumber,
                    AddedAt = DateTime.UtcNow,
                    
                    // Copy metadata
                    SpotifyTrackId = track.SpotifyTrackId,
                    ISRC = track.ISRC,
                    SpotifyAlbumId = track.SpotifyAlbumId,
                    SpotifyArtistId = track.SpotifyArtistId,
                    AlbumArtUrl = track.AlbumArtUrl,
                    ArtistImageUrl = track.ArtistImageUrl,
                    Genres = track.Genres,
                    Popularity = track.Popularity,
                    CanonicalDuration = track.CanonicalDuration,
                    ReleaseDate = track.ReleaseDate,
                    MusicalKey = track.MusicalKey,
                    BPM = track.BPM,
                    Bitrate = track.Bitrate,
                    Format = track.Format,
                    IsEnriched = track.IsEnriched,
                    IsPrepared = track.IsPrepared, // Phase 10
                    PrimaryGenre = track.PrimaryGenre,
                    DetectedSubGenre = track.DetectedSubGenre,
                    Label = track.Label,
                    Comments = track.Comments
                };
                newTracks.Add(newTrack);
            }

            if (newTracks.Any())
            {
                await SavePlaylistTracksAsync(newTracks);
                
                // Update project counts
                project.SuccessfulCount += newTracks.Count(t => t.Status == TrackStatus.Downloaded);
                project.FailedCount += newTracks.Count(t => t.Status == TrackStatus.Failed || t.Status == TrackStatus.Skipped);
                project.MissingCount += newTracks.Count(t => t.Status == TrackStatus.Missing);
                project.TotalTracks += newTracks.Count;
                
                await SavePlaylistJobAsync(project);
                
                _logger.LogInformation("Added {Count} tracks to project {Title}", newTracks.Count, project.SourceTitle);
                
                // Publish event so UI can refresh
                _eventBus.Publish(new ProjectUpdatedEvent(targetProjectId));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add tracks to project {Id}", targetProjectId);
            throw;
        }
    }

    public async Task UpdateTrackCuePointsAsync(string trackHash, string cuePointsJson)
    {
        using var db = new AppDbContext();
        
        // 1. Update Library Entry
        var entry = await db.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == trackHash);
        if (entry != null)
        {
            entry.CuePointsJson = cuePointsJson;
        }

        // 2. Update all Playlist Tracks
        var playlistTracks = await db.PlaylistTracks.Where(t => t.TrackUniqueHash == trackHash).ToListAsync();
        foreach (var track in playlistTracks)
        {
            track.CuePointsJson = cuePointsJson;
            
            // Also update technical details if they exist
            var tech = await db.TechnicalDetails.FirstOrDefaultAsync(td => td.PlaylistTrackId == track.Id);
            if (tech != null)
            {
                tech.CuePointsJson = cuePointsJson;
            }
        }

        await db.SaveChangesAsync();
    }

    public async Task UpdateAudioFeaturesAsync(AudioFeaturesEntity entity)
    {
        using var db = new AppDbContext();
        var existing = await db.AudioFeatures.FirstOrDefaultAsync(f => f.TrackUniqueHash == entity.TrackUniqueHash);
        if (existing != null)
        {
            existing.PhraseSegmentsJson = entity.PhraseSegmentsJson;
            existing.EnergyCurveJson = entity.EnergyCurveJson;
            existing.VocalDensityCurveJson = entity.VocalDensityCurveJson;
            existing.AnomaliesJson = entity.AnomaliesJson;
            existing.StructuralVersion = entity.StructuralVersion;
            existing.StructuralHash = entity.StructuralHash;
            existing.AnalysisReasoningJson = entity.AnalysisReasoningJson;
            
            await db.SaveChangesAsync();
        }
    }

    public async Task<List<PlaylistTrack>> SearchAllPlaylists(string query, int limit = 50)
    {
        try
        {
            var entities = await _databaseService.SearchPlaylistTracksAsync(query, limit).ConfigureAwait(false);
            return entities.Select(EntityToPlaylistTrack).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search all playlists for {Query}", query);
            return new List<PlaylistTrack>();
        }
    }

    public async Task<List<PlaylistTrack>> FindTrackInOtherProjectsAsync(string artist, string title, Guid currentProjectId)
    {
        try
        {
            var entities = await _databaseService
                .FindTracksInOtherProjectsAsync(artist, title, currentProjectId)
                .ConfigureAwait(false);

            return entities.Select(m => EntityToPlaylistTrack(m)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding cross-references for {Artist} - {Title}", artist, title);
            return new List<PlaylistTrack>();
        }
    }

    public async Task<List<TrackPhraseEntity>> GetPhrasesByHashAsync(string trackHash)
    {
        return await _databaseService.GetPhrasesByHashAsync(trackHash);
    }

    public async Task SavePhrasesAsync(List<TrackPhraseEntity> phrases)
    {
        await _databaseService.SavePhrasesAsync(phrases);
    }

    private async Task InitializeDefaultPlaylistsAsync()
    {
        try
        {
            var likedJob = await FindPlaylistJobBySourceTypeAsync("Smart:Liked");
            if (likedJob == null)
            {
                _logger.LogInformation("Creating default 'Liked Songs' smart playlist...");
                var criteria = new SmartPlaylistCriteria { IsLiked = true };
                var job = new PlaylistJob
                {
                    Id = Guid.NewGuid(),
                    SourceTitle = "Liked Songs",
                    SourceType = "Smart:Liked",
                    CreatedAt = DateTime.UtcNow,
                    IsSmartPlaylist = true,
                    SmartCriteriaJson = System.Text.Json.JsonSerializer.Serialize(criteria)
                };
                await SavePlaylistJobAsync(job);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize default playlists");
        }
    }
}
